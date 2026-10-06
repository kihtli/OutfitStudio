using System.Numerics;

namespace OutfitStudio.Core.Geometry;

/// <summary>
/// Continuous offset transport across connected reference triangles. The exact
/// surface correspondence stays piecewise linear; only the off-surface frame and
/// clearance direction are interpolated from area-weighted vertex frames.
/// Separate rotation and stretch means keep the transport orientation preserving.
/// </summary>
internal sealed class GarmentDeformationField
{
    // Match SkinSeamBindings' export-rounding tolerance, but require agreement in
    // both bodies so overlapping layers or different correspondences stay apart.
    private const float WeldTolerance = 0.000001f;
    private readonly Dictionary<SurfaceTriangle, (int A, int B, int C)> corners = new(ReferenceEqualityComparer.Instance);
    private readonly VertexFrame[] frames;

    private sealed record VertexFrame(Matrix4x4 Stretch, Matrix4x4 RotationScatter, Vector3 Normal);

    private sealed class Accumulator(Vector3 source, Vector3 target)
    {
        public readonly Vector3 Source = source;
        public readonly Vector3 Target = target;
        private readonly double[] components = new double[28];
        private double weight;

        public void Add(Matrix4x4 matrix, Matrix4x4 scatter, Vector3 normal, double area)
        {
            weight += area;
            components[0] += area * matrix.M11; components[1] += area * matrix.M12; components[2] += area * matrix.M13;
            components[3] += area * matrix.M21; components[4] += area * matrix.M22; components[5] += area * matrix.M23;
            components[6] += area * matrix.M31; components[7] += area * matrix.M32; components[8] += area * matrix.M33;
            components[9] += area * scatter.M11; components[10] += area * scatter.M12;
            components[11] += area * scatter.M13; components[12] += area * scatter.M14;
            components[13] += area * scatter.M21; components[14] += area * scatter.M22;
            components[15] += area * scatter.M23; components[16] += area * scatter.M24;
            components[17] += area * scatter.M31; components[18] += area * scatter.M32;
            components[19] += area * scatter.M33; components[20] += area * scatter.M34;
            components[21] += area * scatter.M41; components[22] += area * scatter.M42;
            components[23] += area * scatter.M43; components[24] += area * scatter.M44;
            components[25] += area * normal.X; components[26] += area * normal.Y; components[27] += area * normal.Z;
        }

        public VertexFrame Finish()
        {
            float Value(int index) => (float)(components[index] / weight);
            return new(new(Value(0), Value(1), Value(2), 0, Value(3), Value(4), Value(5), 0,
                Value(6), Value(7), Value(8), 0, 0, 0, 0, 1),
                new(Value(9), Value(10), Value(11), Value(12), Value(13), Value(14), Value(15), Value(16),
                    Value(17), Value(18), Value(19), Value(20), Value(21), Value(22), Value(23), Value(24)),
                new(Value(25), Value(26), Value(27)));
        }
    }

    public GarmentDeformationField(IEnumerable<SurfaceTriangle> triangles, CancellationToken cancellationToken = default)
    {
        var vertices = new List<Accumulator>();
        var cells = new Dictionary<(long X, long Y, long Z), List<int>>();
        int triangleIndex = 0;
        foreach (var triangle in triangles)
        {
            if ((triangleIndex++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var sourceCross = Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A);
            var targetCross = Vector3.Cross(triangle.TargetB - triangle.TargetA, triangle.TargetC - triangle.TargetA);
            float sourceArea = sourceCross.Length(), targetArea = targetCross.Length();
            if (!float.IsFinite(sourceArea) || !float.IsFinite(targetArea) || sourceArea < 1e-8f || targetArea < 1e-9f)
                throw MdlDocument.Error("The garment deformation field contains an invalid reference triangle.");
            var sourceNormal = sourceCross / sourceArea;
            var targetNormal = targetCross / targetArea;
            var sourceBasis = Basis(triangle.B - triangle.A, triangle.C - triangle.A, sourceNormal);
            var targetBasis = Basis(triangle.TargetB - triangle.TargetA, triangle.TargetC - triangle.TargetA, targetNormal);
            if (!LinearTransform.TryInvert(sourceBasis, out var inverse, out _))
                throw MdlDocument.Error("The garment reference frame cannot be inverted.");
            var transform = inverse * targetBasis;
            PolarTransport.Decompose(transform, out var stretch, out var scatter);
            int a = Vertex(triangle.A, triangle.TargetA), b = Vertex(triangle.B, triangle.TargetB), c = Vertex(triangle.C, triangle.TargetC);
            vertices[a].Add(stretch, scatter, targetNormal, sourceArea);
            vertices[b].Add(stretch, scatter, targetNormal, sourceArea);
            vertices[c].Add(stretch, scatter, targetNormal, sourceArea);
            corners.Add(triangle, (a, b, c));
        }
        frames = vertices.Select(vertex => vertex.Finish()).ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        int Vertex(Vector3 source, Vector3 target)
        {
            long Cell(float value)
            {
                double cell = Math.Floor((double)value / WeldTolerance);
                if (!double.IsFinite(cell) || cell <= long.MinValue + 2.0 || cell >= long.MaxValue - 2.0)
                    throw MdlDocument.Error("Reference coordinates are outside the garment field's supported range.");
                return (long)cell;
            }
            var key = (X: Cell(source.X), Y: Cell(source.Y), Z: Cell(source.Z));
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (cells.TryGetValue((key.X + dx, key.Y + dy, key.Z + dz), out var nearby))
                            foreach (int index in nearby)
                                if (Vector3.DistanceSquared(vertices[index].Source, source) <= WeldTolerance * WeldTolerance
                                    && Vector3.DistanceSquared(vertices[index].Target, target) <= WeldTolerance * WeldTolerance)
                                    return index;
            if (!cells.TryGetValue(key, out var cellVertices)) cells[key] = cellVertices = [];
            int next = vertices.Count;
            vertices.Add(new(source, target));
            cellVertices.Add(next);
            return next;
        }
    }

    public void Evaluate(SurfaceTriangle triangle, Vector3 barycentric, out Matrix4x4 transform, out Vector3 clearanceNormal)
    {
        var (a, b, c) = corners[triangle];
        var first = frames[a]; var second = frames[b]; var third = frames[c];
        // Difference form preserves a constant frame exactly, even if floating
        // barycentric components sum to slightly more or less than one.
        var stretch = first.Stretch + (second.Stretch - first.Stretch) * barycentric.Y
            + (third.Stretch - first.Stretch) * barycentric.Z;
        var scatter = first.RotationScatter + (second.RotationScatter - first.RotationScatter) * barycentric.Y
            + (third.RotationScatter - first.RotationScatter) * barycentric.Z;
        transform = PolarTransport.Compose(stretch, scatter);
        var normal = first.Normal + (second.Normal - first.Normal) * barycentric.Y
            + (third.Normal - first.Normal) * barycentric.Z;
        float length = normal.LengthSquared();
        if (!MdlDocument.Finite(normal) || length < 1e-16f)
            throw MdlDocument.Error("The interpolated garment clearance direction is undefined; the body correspondence folds over itself.");
        clearanceNormal = normal / MathF.Sqrt(length);
    }

    private static Matrix4x4 Basis(Vector3 x, Vector3 y, Vector3 z)
        => new(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
}
