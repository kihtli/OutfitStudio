using System.Numerics;
using System.Text;

namespace OutfitStudio.Core.Geometry;

/// <summary>Restores the authored rigid geometry of accessory components after fitting their placement.</summary>
internal static class RigidAccessoryParts
{
    private const float WeldTolerance = 0.000001f;
    internal sealed record Statistics(int Assemblies, int Components, int AdjustedVertices, float MaximumAdjustment)
    {
        public int BlendedParts { get; init; }
    }
    internal readonly record struct Vertex(MdlDocument.Mesh Mesh, int Index)
    {
        public Vector3 Position => Mesh.Positions[Index];
    }

    internal sealed class Part
    {
        public required int Lod { get; init; }
        public required Vertex[] Vertices { get; init; }
        public required Vector3[] SourceFitPoints { get; init; }
        public required Vector3[] TargetFitPoints { get; init; }
        public required float[] FitWeights { get; init; }
        public required string? UniformBoneSignature { get; init; }
        public required IReadOnlyDictionary<string, float> AverageBoneWeights { get; init; }
        public required IReadOnlySet<string> AuthoredBones { get; init; }
        public required Matrix4x4 Transform { get; set; }
    }

    public static Statistics Apply(MdlDocument model, byte[] output, IReadOnlySet<string> skinMaterials,
        CancellationToken cancellationToken = default)
    {
        var parts = FitParts(model, output, skinMaterials, cancellationToken, out int components);
        int blended = BlendAnchors(parts, cancellationToken);
        int adjusted = 0, visited = 0;
        float maximumAdjustment = 0;
        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var vertex in part.Vertices)
            {
                if ((visited++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var mesh = vertex.Mesh;
                int positionAddress = mesh.Address(mesh.Position, vertex.Index);
                var before = MdlDocument.ReadVector(output, positionAddress, mesh.Position.Type, false);
                var after = Vector3.Transform(vertex.Position, part.Transform);
                MdlDocument.WriteVector(output, positionAddress, mesh.Position.Type, after, false);
                after = MdlDocument.ReadVector(output, positionAddress, mesh.Position.Type, false);
                float adjustment = Vector3.Distance(before, after);
                if (adjustment > 1e-7f) adjusted++;
                maximumAdjustment = MathF.Max(maximumAdjustment, adjustment);
                // Use the authored vectors. Composing with the earlier nonrigid
                // normal transform would retain precisely the distortion removed here.
                foreach (var element in mesh.Elements.Where(element => element.Usage is 3 or 5 or 6))
                {
                    int address = mesh.Address(element, vertex.Index);
                    var original = MdlDocument.ReadVector(model.Data, address, element.Type, true);
                    var direction = Vector3.TransformNormal(original, part.Transform);
                    if (direction.LengthSquared() > 1e-15f) direction = Vector3.Normalize(direction);
                    MdlDocument.WriteVector(output, address, element.Type, direction, true);
                }
            }
        }
        return new(parts.Count, components, adjusted, maximumAdjustment) { BlendedParts = blended };
    }

    internal static List<Part> FitParts(MdlDocument model, byte[] output, IReadOnlySet<string> skinMaterials,
        CancellationToken cancellationToken, out int componentCount)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(skinMaterials);
        if (output.Length != model.Data.Length) throw MdlDocument.Error("Rigid fitting must retain the original model layout.");
        cancellationToken.ThrowIfCancellationRequested();
        var vertices = new List<Vertex>();
        var ids = new Dictionary<(int Mesh, int Vertex), int>();
        foreach (var mesh in model.Meshes.Where(mesh => !skinMaterials.Contains(MdlConverter.MaterialKey(model.Materials[mesh.Material]))))
            for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
            {
                ids.Add((mesh.Index, vertex), vertices.Count);
                vertices.Add(new(mesh, vertex));
            }
        var union = new Union(vertices.Count);
        var indexed = new bool[vertices.Count];
        var replacements = new bool[vertices.Count];
        foreach (var mesh in model.Meshes)
        {
            if (mesh.VertexCount == 0 || !ids.ContainsKey((mesh.Index, 0))) continue;
            for (int offset = 0; offset < mesh.Indices.Length; offset += 3)
            {
                if ((offset & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                int a = ids[(mesh.Index, mesh.Indices[offset])], b = ids[(mesh.Index, mesh.Indices[offset + 1])], c = ids[(mesh.Index, mesh.Indices[offset + 2])];
                union.Join(a, b); union.Join(a, c);
                indexed[a] = indexed[b] = indexed[c] = true;
            }
        }
        var cells = new Dictionary<(int Lod, long X, long Y, long Z), List<int>>();
        for (int id = 0; id < vertices.Count; id++)
        {
            if ((id & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!indexed[id]) continue;
            var vertex = vertices[id];
            var key = Cell(vertex);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (cells.TryGetValue((key.Lod, key.X + dx, key.Y + dy, key.Z + dz), out var nearby))
                            foreach (int other in nearby)
                                if (Vector3.DistanceSquared(vertex.Position, vertices[other].Position) <= WeldTolerance * WeldTolerance)
                                    union.Join(id, other);
            if (!cells.TryGetValue(key, out var cell)) cells[key] = cell = [];
            cell.Add(id);
        }
        foreach (var shape in model.ShapeBindings)
            if (ids.TryGetValue((shape.MeshIndex, shape.BaseVertex), out int baseId)
                && ids.TryGetValue((shape.MeshIndex, shape.ReplacementVertex), out int replacementId))
            {
                replacements[replacementId] = true;
                union.Join(baseId, replacementId);
            }
        var baseByLod = Enumerable.Range(0, vertices.Count).Where(id => indexed[id] && !replacements[id])
            .GroupBy(id => vertices[id].Mesh.Lod).ToDictionary(group => group.Key, group => group.ToArray());
        var anchoredRoots = baseByLod.Values.SelectMany(values => values).Select(union.Find).ToHashSet();
        // Unused stream vertices have no visible face. Keep their placement with
        // the nearest authored component; explicit shape bindings take precedence.
        for (int id = 0; id < vertices.Count; id++)
        {
            if ((id & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (anchoredRoots.Contains(union.Find(id)) || !baseByLod.TryGetValue(vertices[id].Mesh.Lod, out var candidates)) continue;
            int nearest = candidates.MinBy(candidate => Vector3.DistanceSquared(vertices[id].Position, vertices[candidate].Position));
            union.Join(nearest, id);
            anchoredRoots.Add(union.Find(id));
        }
        var components = Enumerable.Range(0, vertices.Count).GroupBy(union.Find)
            .Select(group => group.ToArray()).Where(group => group.Any(id => indexed[id] && !replacements[id])).ToArray();
        componentCount = components.Length;
        var signatures = new string?[vertices.Count];
        var boneWeights = new MdlDocument.BoneInfluence[vertices.Count][];
        for (int id = 0; id < vertices.Count; id++)
        {
            if ((id & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var vertex = vertices[id];
            boneWeights[id] = model.GetBoneWeights(vertex.Mesh, vertex.Index);
            signatures[id] = Signature(boneWeights[id]);
        }
        var assemblies = new Dictionary<(int Lod, string Signature), int>();
        foreach (var component in components)
        {
            string? signature = signatures[component[0]];
            if (signature is null || component.Any(id => signatures[id] != signature)) continue;
            var key = (vertices[component[0]].Mesh.Lod, signature);
            if (assemblies.TryGetValue(key, out int previous)) union.Join(previous, component[0]);
            else assemblies.Add(key, component[0]);
        }
        var parts = new List<Part>();
        foreach (var members in components.SelectMany(component => component).GroupBy(union.Find))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int[] memberIds = members.ToArray();
            int[] fitting = memberIds.Where(id => indexed[id] && !replacements[id]).ToArray();
            float[] fitWeights = Enumerable.Repeat(1f, fitting.Length).ToArray();
            var sourcePoints = fitting.Select(id => vertices[id].Position).ToArray();
            var targetPoints = fitting.Select(id =>
            {
                var vertex = vertices[id];
                return MdlDocument.ReadVector(output, vertex.Mesh.Address(vertex.Mesh.Position, vertex.Index), vertex.Mesh.Position.Type, false);
            }).ToArray();
            var average = new Dictionary<string, double>(StringComparer.Ordinal);
            double totalWeight = fitWeights.Sum(weight => (double)weight);
            for (int i = 0; i < fitting.Length; i++)
                foreach (var influence in boneWeights[fitting[i]])
                    average[influence.Bone] = average.GetValueOrDefault(influence.Bone) + influence.Weight * fitWeights[i] / totalWeight;
            string? uniform = signatures[memberIds[0]];
            if (memberIds.Any(id => signatures[id] != uniform)) uniform = null;
            parts.Add(new()
            {
                Lod = vertices[memberIds[0]].Mesh.Lod,
                Vertices = memberIds.Select(id => vertices[id]).ToArray(),
                SourceFitPoints = sourcePoints, TargetFitPoints = targetPoints, FitWeights = fitWeights,
                UniformBoneSignature = uniform,
                AverageBoneWeights = average.ToDictionary(pair => pair.Key, pair => (float)pair.Value, StringComparer.Ordinal),
                AuthoredBones = memberIds.SelectMany(id => boneWeights[id]).Select(influence => influence.Bone).ToHashSet(StringComparer.Ordinal),
                Transform = RigidAlignment.Fit(sourcePoints, targetPoints, fitWeights),
            });
        }
        return parts;
    }

    // A varying-weight link between two rigid anchors already encodes its place
    // between them. Interpolate their rigid placements rather than inheriting a
    // discontinuous center from independent nearest-surface deformation.
    internal static int BlendAnchors(IReadOnlyList<Part> parts, CancellationToken cancellationToken = default)
    {
        const float weightTolerance = 1f / 255 + 0.000001f;
        int blended = 0;
        foreach (var lod in parts.GroupBy(part => part.Lod))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var anchors = lod.Where(part => part.UniformBoneSignature is not null && part.AverageBoneWeights.Count > 0).ToArray();
            // More anchors need an explicit, unambiguous attachment graph.
            if (anchors.Length != 2) continue;
            var first = anchors[0]; var second = anchors[1];
            var supported = first.AuthoredBones.Concat(second.AuthoredBones).ToHashSet(StringComparer.Ordinal);
            var varying = supported.Where(bone => MathF.Abs(first.AverageBoneWeights.GetValueOrDefault(bone)
                - second.AverageBoneWeights.GetValueOrDefault(bone)) > 1e-6f).Order(StringComparer.Ordinal).ToArray();
            float firstTotal = varying.Sum(bone => first.AverageBoneWeights.GetValueOrDefault(bone));
            float secondTotal = varying.Sum(bone => second.AverageBoneWeights.GetValueOrDefault(bone));
            if (firstTotal <= 0 || secondTotal <= 0) continue;
            var a = varying.Select(bone => first.AverageBoneWeights.GetValueOrDefault(bone) / firstTotal).ToArray();
            var b = varying.Select(bone => second.AverageBoneWeights.GetValueOrDefault(bone) / secondTotal).ToArray();
            var delta = a.Zip(b, (left, right) => left - right).ToArray();
            float lengthSquared = delta.Sum(value => value * value);
            if (lengthSquared < 1e-12f) continue;
            var firstRotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(first.Transform));
            var secondRotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(second.Transform));
            foreach (var part in lod.Where(part => part.UniformBoneSignature is null && part.AverageBoneWeights.Count > 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (part.AuthoredBones.Any(bone => !supported.Contains(bone))) continue;
                float total = varying.Sum(bone => part.AverageBoneWeights.GetValueOrDefault(bone));
                if (total <= 0) continue;
                var values = varying.Select(bone => part.AverageBoneWeights.GetValueOrDefault(bone) / total).ToArray();
                float weight = values.Select((value, i) => (value - b[i]) * delta[i]).Sum() / lengthSquared;
                if (weight < -weightTolerance || weight > 1 + weightTolerance) continue;
                weight = Math.Clamp(weight, 0, 1);
                if (values.Where((value, i) => MathF.Abs(value - (b[i] + weight * delta[i])) > weightTolerance).Any()) continue;
                var center = part.SourceFitPoints.Aggregate(Vector3.Zero, (sum, point) => sum + point) / part.SourceFitPoints.Length;
                var placedCenter = Vector3.Lerp(Vector3.Transform(center, second.Transform), Vector3.Transform(center, first.Transform), weight);
                var rotation = Quaternion.Normalize(Quaternion.Slerp(secondRotation, firstRotation, weight));
                var transform = Matrix4x4.CreateFromQuaternion(rotation);
                transform.Translation = placedCenter - Vector3.TransformNormal(center, transform);
                part.Transform = transform;
                blended++;
            }
        }
        return blended;
    }

    private static string? Signature(MdlDocument.BoneInfluence[] influences)
    {
        if (influences.Length == 0) return null;
        var signature = new StringBuilder();
        foreach (var influence in influences.OrderBy(influence => influence.Bone, StringComparer.Ordinal))
            signature.Append(influence.Bone.Length).Append(':').Append(influence.Bone).Append(':')
                .Append(BitConverter.SingleToInt32Bits(influence.Weight).ToString("X8", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
        return signature.ToString();
    }

    private static (int Lod, long X, long Y, long Z) Cell(Vertex vertex)
    {
        return (vertex.Mesh.Lod, Coordinate(vertex.Position.X), Coordinate(vertex.Position.Y), Coordinate(vertex.Position.Z));
        static long Coordinate(float value)
        {
            double cell = Math.Floor((double)value / WeldTolerance);
            if (!double.IsFinite(cell) || cell <= long.MinValue + 2.0 || cell >= long.MaxValue - 2.0)
                throw MdlDocument.Error("Accessory coordinates are outside the rigid fitter's supported range.");
            return (long)cell;
        }
    }

    private sealed class Union(int count)
    {
        private readonly int[] parents = Enumerable.Range(0, count).ToArray();
        private readonly byte[] ranks = new byte[count];
        public int Find(int value)
        {
            while (parents[value] != value) { parents[value] = parents[parents[value]]; value = parents[value]; }
            return value;
        }
        public void Join(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a == b) return;
            if (ranks[a] < ranks[b]) (a, b) = (b, a);
            parents[b] = a;
            if (ranks[a] == ranks[b]) ranks[a]++;
        }
    }
}
