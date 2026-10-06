using System.Numerics;

namespace OutfitStudio.Core.Geometry;

/// <summary>Smooths the deformation field, preserving the garment's original surface detail.
/// Positive cotangent weights and lumped triangle areas make the smoothing scale independent
/// of tessellation. Contact patches follow the connected garment, never a nearby loose layer.</summary>
internal sealed class GarmentFairing
{
    private readonly MdlDocument.Mesh mesh;
    private readonly List<int>[] groups;
    private readonly int[] anchors;
    private readonly bool[] eligible;
    private readonly Dictionary<int, float>[] edges;
    private readonly Dictionary<int, float>[] weights;
    private readonly float[] mass;

    public GarmentFairing(MdlDocument.Mesh mesh, List<int>[] groups, bool[] eligible, CancellationToken ct)
    {
        this.mesh = mesh;
        this.groups = groups;
        this.eligible = eligible;
        anchors = Enumerable.Range(0, mesh.VertexCount).Where(v => groups[v][0] == v).ToArray();
        edges = Enumerable.Range(0, mesh.VertexCount).Select(_ => new Dictionary<int, float>()).ToArray();
        weights = Enumerable.Range(0, mesh.VertexCount).Select(_ => new Dictionary<int, float>()).ToArray();
        mass = new float[mesh.VertexCount];
        for (int face = 0; face < mesh.Indices.Length; face += 3)
        {
            if ((face & 1023) == 0) ct.ThrowIfCancellationRequested();
            int[] ids = Enumerable.Range(face, 3).Select(i => groups[mesh.Indices[i]][0]).ToArray();
            Vector3[] p = ids.Select(i => mesh.Positions[i]).ToArray();
            float area2 = Vector3.Cross(p[1] - p[0], p[2] - p[0]).Length();
            if (!float.IsFinite(area2)) throw MdlDocument.Error("Garment triangle is too large to fit safely.");
            if (area2 < 1e-12f) continue;
            for (int corner = 0; corner < 3; corner++)
            {
                mass[ids[corner]] += area2 / 6;
                int a = ids[(corner + 1) % 3], b = ids[(corner + 2) % 3];
                if (a == b) continue;
                float length = Vector3.Distance(mesh.Positions[a], mesh.Positions[b]);
                edges[a][b] = length;
                edges[b][a] = length;
                float weight = MathF.Max(0, Vector3.Dot(p[(corner + 1) % 3] - p[corner], p[(corner + 2) % 3] - p[corner]) / area2) * 0.5f;
                if (!float.IsFinite(weight)) throw MdlDocument.Error("Garment triangle has an invalid fitting weight.");
                weights[a][b] = weights[a].GetValueOrDefault(b) + weight;
                weights[b][a] = weights[b].GetValueOrDefault(a) + weight;
            }
        }
        if (mass.Any(value => !float.IsFinite(value)) || weights.Any(row => row.Values.Any(value => !float.IsFinite(value))))
            throw MdlDocument.Error("Garment fitting weights exceeded the supported range.");
        // A welded group is fixed if any of its members is an unbound or shared seam vertex.
        foreach (int anchor in anchors)
            if (groups[anchor].Any(v => !eligible[v]))
                foreach (int vertex in groups[anchor]) eligible[vertex] = false;
    }

    public void Smooth(Vector3[] before, Vector3[] positions, float radius, CancellationToken ct)
    {
        int count = mesh.VertexCount;
        float lambda = radius * radius;
        var displacement = Enumerable.Range(0, count).Select(i => before[i] - mesh.Positions[i]).ToArray();
        var diagonal = new double[count];
        foreach (int anchor in anchors)
            diagonal[anchor] = Math.Max(mass[anchor], 1e-10) + lambda * weights[anchor].Values.Sum(w => (double)w);
        // Solve (M + r²L)d = M*d_original, with fixed seam/binding constraints.
        // This smooths offsets only: original folds, cutouts and authored details remain.
        for (int axis = 0; axis < 3; axis++)
        {
            ct.ThrowIfCancellationRequested();
            var x = new double[count];
            var rhs = new double[count];
            var residual = new double[count];
            var preconditioned = new double[count];
            var direction = new double[count];
            var product = new double[count];
            float Component(Vector3 value) => axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;
            foreach (int anchor in anchors)
            {
                x[anchor] = Component(displacement[anchor]);
                rhs[anchor] = Math.Max(mass[anchor], 1e-10) * x[anchor];
                foreach (var (neighbor, weight) in weights[anchor])
                    if (!eligible[neighbor]) rhs[anchor] += lambda * weight * Component(displacement[neighbor]);
            }
            void Apply(double[] input, double[] result)
            {
                foreach (int anchor in anchors)
                {
                    if (!eligible[anchor]) continue;
                    double value = diagonal[anchor] * input[anchor];
                    foreach (var (neighbor, weight) in weights[anchor])
                        if (eligible[neighbor]) value -= lambda * weight * input[neighbor];
                    result[anchor] = value;
                }
            }
            Apply(x, product);
            double residualProduct = 0, initialError = 0;
            foreach (int anchor in anchors)
            {
                if (!eligible[anchor]) continue;
                residual[anchor] = rhs[anchor] - product[anchor];
                preconditioned[anchor] = residual[anchor] / diagonal[anchor];
                direction[anchor] = preconditioned[anchor];
                residualProduct += residual[anchor] * preconditioned[anchor];
                initialError += residual[anchor] * residual[anchor];
            }
            for (int iteration = 0; iteration < 240 && residualProduct > 1e-30; iteration++)
            {
                if ((iteration & 15) == 0) ct.ThrowIfCancellationRequested();
                Apply(direction, product);
                double denominator = 0;
                foreach (int anchor in anchors)
                    if (eligible[anchor]) denominator += direction[anchor] * product[anchor];
                if (denominator <= 1e-30) break;
                double alpha = residualProduct / denominator, error = 0, nextProduct = 0;
                foreach (int anchor in anchors)
                {
                    if (!eligible[anchor]) continue;
                    x[anchor] += alpha * direction[anchor];
                    residual[anchor] -= alpha * product[anchor];
                    error += residual[anchor] * residual[anchor];
                    preconditioned[anchor] = residual[anchor] / diagonal[anchor];
                    nextProduct += residual[anchor] * preconditioned[anchor];
                }
                if (error <= initialError * 1e-12) break;
                double beta = nextProduct / residualProduct;
                foreach (int anchor in anchors)
                    if (eligible[anchor]) direction[anchor] = preconditioned[anchor] + beta * direction[anchor];
                residualProduct = nextProduct;
            }
            foreach (int anchor in anchors)
            {
                if (!eligible[anchor]) continue;
                if (!double.IsFinite(x[anchor])) throw MdlDocument.Error("Garment smoothing produced an invalid displacement.");
                foreach (int vertex in groups[anchor])
                {
                    var value = positions[vertex];
                    if (axis == 0) value.X = mesh.Positions[vertex].X + (float)x[anchor];
                    else if (axis == 1) value.Y = mesh.Positions[vertex].Y + (float)x[anchor];
                    else value.Z = mesh.Positions[vertex].Z + (float)x[anchor];
                    if (!MdlDocument.Finite(value)) throw MdlDocument.Error("Garment smoothing produced an invalid position.");
                    positions[vertex] = value;
                }
            }
        }
        // Fairing removes local mapping spikes. Contact has its own smaller bound
        // around this smoothed surface; anchoring contact to the old spike restores it.
        const float maximumSmoothingShift = 0.05f;
        foreach (int anchor in anchors)
        {
            if (!eligible[anchor]) continue;
            var delta = positions[anchor] - before[anchor];
            if (delta.LengthSquared() > maximumSmoothingShift * maximumSmoothingShift)
                delta = Vector3.Normalize(delta) * maximumSmoothingShift;
            foreach (int vertex in groups[anchor]) positions[vertex] = before[vertex] + delta;
        }
    }

    public (int Vertex, float Weight)[] Patch(int a, int b, int c, Vector3 barycentric, float radius, CancellationToken ct)
    {
        var center = mesh.Positions[a] * barycentric.X + mesh.Positions[b] * barycentric.Y + mesh.Positions[c] * barycentric.Z;
        var distances = new Dictionary<int, float>();
        var queue = new PriorityQueue<int, float>();
        foreach (int corner in new[] { a, b, c })
        {
            int vertex = groups[corner][0];
            float distance = Vector3.Distance(center, mesh.Positions[vertex]);
            if (distance >= radius || distances.TryGetValue(vertex, out float old) && old <= distance) continue;
            distances[vertex] = distance;
            queue.Enqueue(vertex, distance);
        }
        int visited = 0;
        while (queue.TryDequeue(out int vertex, out float distance))
        {
            if ((visited++ & 255) == 0) ct.ThrowIfCancellationRequested();
            if (distances[vertex] < distance) continue;
            foreach (var (neighbor, length) in edges[vertex])
            {
                float next = distance + length;
                if (next >= radius || distances.TryGetValue(neighbor, out float old) && old <= next) continue;
                distances[neighbor] = next;
                queue.Enqueue(neighbor, next);
            }
        }
        // Compact C2 kernel: no abrupt correction edge at the patch boundary.
        return distances.Where(pair => eligible[pair.Key]).Select(pair =>
        {
            float r = pair.Value / radius, s = 1 - r;
            return (pair.Key, s * s * s * s * (4 * r + 1));
        }).ToArray();
    }
}
