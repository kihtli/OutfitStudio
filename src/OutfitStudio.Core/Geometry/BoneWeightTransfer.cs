using System.Numerics;

namespace OutfitStudio.Core.Geometry;

/// <summary>Removes source-only body extension bindings without replacing authored garment animation.</summary>
internal sealed class BoneWeightTransfer
{
    private sealed record WeightedFace(SurfaceTriangle Triangle, MdlDocument.BoneInfluence[][] Weights);
    private sealed record Surface(string Material, TriangleIndex Index, Dictionary<SurfaceTriangle, WeightedFace> Faces);
    private readonly Surface[] surfaces;
    private readonly HashSet<string> targetBones;
    private readonly HashSet<string> bodyBones;
    private readonly bool supportsYab;
    private readonly bool supportsIvcs;

    private BoneWeightTransfer(Surface[] surfaces, IEnumerable<string> targetBones, IEnumerable<string>? sourceBodyBones = null)
    {
        this.surfaces = surfaces;
        this.targetBones = targetBones.ToHashSet(StringComparer.Ordinal);
        bodyBones = this.targetBones.Concat(sourceBodyBones ?? []).ToHashSet(StringComparer.Ordinal);
        supportsYab = this.targetBones.Any(n => n.StartsWith("ya_", StringComparison.Ordinal));
        supportsIvcs = supportsYab || this.targetBones.Any(n => n.StartsWith("iv_", StringComparison.Ordinal));
    }

    internal static BoneWeightTransfer Create(MdlDocument target, IEnumerable<MdlDocument.Mesh> meshes,
        CancellationToken ct = default, IEnumerable<string>? sourceBodyBones = null)
    {
        var surfaces = new List<Surface>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mesh in meshes.Where(m => m.HasSkinning))
        {
            var weights = new MdlDocument.BoneInfluence[mesh.VertexCount][];
            for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
            {
                if ((vertex & 255) == 0) ct.ThrowIfCancellationRequested();
                weights[vertex] = target.GetBoneWeights(mesh, vertex);
            }
            foreach (var influence in weights.SelectMany(w => w)) names.Add(influence.Bone);
            var faces = new Dictionary<SurfaceTriangle, WeightedFace>();
            for (int face = 0; face < mesh.Indices.Length; face += 3)
            {
                if ((face & 1023) == 0) ct.ThrowIfCancellationRequested();
                int a = mesh.Indices[face], b = mesh.Indices[face + 1], c = mesh.Indices[face + 2];
                if (Vector3.Cross(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]).LengthSquared() < 1e-18f) continue;
                var triangle = new SurfaceTriangle(mesh.Positions[a], mesh.Positions[b], mesh.Positions[c],
                    mesh.Positions[a], mesh.Positions[b], mesh.Positions[c]);
                faces.TryAdd(triangle, new(triangle, [weights[a], weights[b], weights[c]]));
            }
            if (faces.Count > 0) surfaces.Add(new(MdlConverter.MaterialKey(target.Materials[mesh.Material]), new(faces.Keys), faces));
        }
        return new(surfaces.ToArray(), names, sourceBodyBones);
    }

    internal static BoneWeightTransfer Combine(IEnumerable<BoneWeightTransfer> transfers)
    {
        var items = transfers.ToArray();
        return new(items.SelectMany(t => t.surfaces).ToArray(), items.SelectMany(t => t.targetBones), items.SelectMany(t => t.bodyBones));
    }

    internal int Apply(MdlDocument model, byte[] output, IReadOnlySet<string> bodyMaterials,
        float maximumDistance, IReadOnlyDictionary<int, bool[]> boundVertices, CancellationToken ct, ICollection<string>? warnings = null)
    {
        var replacements = new Dictionary<(int Mesh, int Vertex), MdlDocument.BoneInfluence[]>();
        var originals = new Dictionary<int, MdlDocument.BoneInfluence[][]>();
        int reducedVertices = 0;
        foreach (var mesh in model.Meshes.Where(m => m.HasSkinning))
        {
            ct.ThrowIfCancellationRequested();
            var original = new MdlDocument.BoneInfluence[mesh.VertexCount][];
            for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
            {
                if ((vertex & 255) == 0) ct.ThrowIfCancellationRequested();
                original[vertex] = model.GetBoneWeights(mesh, vertex);
            }
            originals.Add(mesh.Index, original);
            bool Unsupported(MdlDocument.BoneInfluence influence) =>
                influence.Bone.StartsWith("ya_", StringComparison.Ordinal) ? !supportsYab
                : influence.Bone.StartsWith("iv_", StringComparison.Ordinal) && !supportsIvcs;
            bool BodyInfluence(MdlDocument.BoneInfluence influence) =>
                !influence.Bone.StartsWith("j_sk", StringComparison.Ordinal)
                && (bodyBones.Contains(influence.Bone) || Unsupported(influence));
            if (!original.Any(w => w.Any(Unsupported))) continue;
            string material = MdlConverter.MaterialKey(model.Materials[mesh.Material]);
            bool skin = bodyMaterials.Contains(material);
            var candidates = skin ? surfaces.Where(s => s.Material == material).ToArray() : surfaces;
            if (candidates.Length == 0) candidates = surfaces;
            for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
            {
                if ((vertex & 255) == 0) ct.ThrowIfCancellationRequested();
                float missing = original[vertex].Where(Unsupported).Sum(w => w.Weight);
                float bodyMass = original[vertex].Where(BodyInfluence).Sum(w => w.Weight);
                // Affected skin is sampled consistently across the whole mesh, including seam and shape vertices.
                // Garments keep their skirt and other influences absent from both body references.
                // Replace the whole body-following mass, including vanilla-only neighboring vertices:
                // replacing only a 30% source breast influence through a 30% destination breast sample
                // would leave the fabric at 9% breast while its embedded skin follows at 30%.
                if (!skin && bodyMass <= 0) continue;
                if (!boundVertices[mesh.Index][vertex])
                {
                    if (missing > 0) throw MdlDocument.Error($"Mesh {mesh.Index}, vertex {vertex} uses source body physics bones absent from the destination, but is outside the body binding distance. Its weights cannot be converted safely.");
                    continue;
                }
                var position = MdlDocument.ReadVector(output, mesh.Address(mesh.Position, vertex), mesh.Position.Type, false);
                TriangleIndex.Hit? nearest = null; Surface? selected = null;
                float distanceSquared = maximumDistance * maximumDistance;
                foreach (var surface in candidates)
                {
                    var hit = surface.Index.Nearest(position, distanceSquared);
                    if (hit is null) continue;
                    if (nearest is not null && hit.Value.DistanceSquared >= nearest.Value.DistanceSquared) continue;
                    nearest = hit; selected = surface; distanceSquared = hit.Value.DistanceSquared;
                }
                if (nearest is null || selected is null)
                    throw MdlDocument.Error($"Mesh {mesh.Index}, vertex {vertex} has no nearby destination body skinning surface for replacing unavailable source physics bones.");
                var target = Sample(selected.Faces[nearest.Value.Triangle].Weights, nearest.Value.Barycentric);
                IEnumerable<MdlDocument.BoneInfluence> replacement = skin ? target
                    : original[vertex].Where(w => !BodyInfluence(w)).Concat(target.Select(w => new MdlDocument.BoneInfluence(w.Bone, w.Weight * bodyMass)));
                try
                {
                    bool reduced = false;
                    var encoded = skin ? model.CanonicalizeBoneWeights(mesh, replacement)
                        : CanonicalizeGarmentWeights(model, mesh, replacement, original[vertex].Where(w => !BodyInfluence(w)), out reduced);
                    if (reduced) reducedVertices++;
                    replacements.Add((mesh.Index, vertex), encoded);
                }
                catch (Models.ModelConversionException error)
                {
                    static string Describe(IEnumerable<MdlDocument.BoneInfluence> influences) => string.Join(", ", influences.Select(w => $"{w.Bone}={w.Weight:G9}"));
                    throw MdlDocument.Error($"{error.Message} Vertex {vertex} at {position}; preserved garment weights: [{Describe(original[vertex].Where(w => !BodyInfluence(w)))}]; destination body sample: [{Describe(target)}]; body weight mass: {bodyMass:G9}.");
                }
            }
        }
        if (replacements.Count == 0) return 0;
        // Resolve global names and every palette against the final influences of every shared
        // consumer before changing entries. A slot is reusable only when no unchanged mesh/LOD/shape needs it.
        var paletteRequirements = new Dictionary<int, HashSet<string>>();
        foreach (var paletteMeshes in model.Meshes.Where(m => m.HasSkinning).GroupBy(m => m.BonePaletteIndex))
        {
            ct.ThrowIfCancellationRequested();
            var required = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mesh in paletteMeshes)
                for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
                {
                    if ((vertex & 255) == 0) ct.ThrowIfCancellationRequested();
                    foreach (var influence in replacements.GetValueOrDefault((mesh.Index, vertex), originals[mesh.Index][vertex]))
                        required.Add(influence.Bone);
                }
            paletteRequirements.Add(paletteMeshes.Key, required);
        }
        model.EnsureBoneNames(output, paletteRequirements.Values.SelectMany(names => names));
        foreach (var (palette, required) in paletteRequirements)
            model.EnsureBonePalette(output, palette, required);
        int adjusted = 0;
        foreach (var mesh in model.Meshes.Where(m => m.HasSkinning))
            for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
            {
                if ((vertex & 255) == 0) ct.ThrowIfCancellationRequested();
                if (!replacements.TryGetValue((mesh.Index, vertex), out var replacement)) continue;
                var weight = mesh.Elements.Single(e => e.Usage == 1);
                var indices = mesh.Elements.Single(e => e.Usage == 2);
                var beforeWeights = output.AsSpan(mesh.Address(weight, vertex), weight.Size).ToArray();
                var beforeIndices = output.AsSpan(mesh.Address(indices, vertex), indices.Size).ToArray();
                model.WriteBoneWeights(output, mesh, vertex, replacement, canonicalized: true);
                // A palette change can rebind an otherwise identical blend index/weight byte sequence.
                bool changedBinding = !Equivalent(originals[mesh.Index][vertex], model.GetBoneWeights(mesh, vertex, output));
                if (changedBinding || !beforeWeights.AsSpan().SequenceEqual(output.AsSpan(mesh.Address(weight, vertex), weight.Size))
                    || !beforeIndices.AsSpan().SequenceEqual(output.AsSpan(mesh.Address(indices, vertex), indices.Size))) adjusted++;
            }
        if (reducedVertices > 0)
            warnings?.Add($"Limited destination body interpolation on {reducedVertices} garment vertices to fit their blend slots: at most one packed weight unit (1/255) was reassigned to another body joint per vertex. Authored garment and skirt influences were retained.");
        return adjusted;
    }

    private static MdlDocument.BoneInfluence[] CanonicalizeGarmentWeights(MdlDocument model, MdlDocument.Mesh mesh,
        IEnumerable<MdlDocument.BoneInfluence> replacement, IEnumerable<MdlDocument.BoneInfluence> preserved, out bool reduced)
    {
        reduced = false;
        var encoded = model.CanonicalizeBoneWeights(mesh, replacement, enforceCapacity: false);
        int excess = encoded.Length - model.BoneInfluenceCapacity(mesh);
        if (excess <= 0) return encoded;
        // Only packed weights have a one-byte representational tolerance. Float/half skinning
        // and any larger required change retain the ordinary capacity failure.
        if (mesh.Elements.Single(e => e.Usage == 1).Type is 3 or 14)
            return model.CanonicalizeBoneWeights(mesh, replacement);
        var retainedGarment = preserved.ToDictionary(w => w.Bone, w => (int)MathF.Round(w.Weight * 255), StringComparer.Ordinal);
        var body = encoded.Where(w => !retainedGarment.ContainsKey(w.Bone))
            .OrderBy(w => w.Weight).ThenBy(w => w.Bone, StringComparer.Ordinal).ToArray();
        var remove = body.Take(excess).ToArray();
        int removedUnits = remove.Sum(w => (int)MathF.Round(w.Weight * 255));
        if (remove.Length != excess || body.Length <= excess || removedUnits > 1
            || retainedGarment.Any(p => encoded.Where(w => w.Bone == p.Key).Sum(w => (int)MathF.Round(w.Weight * 255)) != p.Value))
            return model.CanonicalizeBoneWeights(mesh, replacement);
        var removedNames = remove.Select(w => w.Bone).ToHashSet(StringComparer.Ordinal);
        // Reassign that encoded unit within body mass only; no global normalization can
        // alter one of the original skirt/cape influences that consumed the other slots.
        string recipient = body.Where(w => !removedNames.Contains(w.Bone))
            .OrderByDescending(w => w.Weight).ThenBy(w => w.Bone, StringComparer.Ordinal).First().Bone;
        var result = encoded.Where(w => !removedNames.Contains(w.Bone))
            .Select(w => w.Bone == recipient ? new MdlDocument.BoneInfluence(w.Bone, (MathF.Round(w.Weight * 255) + removedUnits) / 255f) : w)
            .OrderByDescending(w => w.Weight).ThenBy(w => w.Bone, StringComparer.Ordinal).ToArray();
        reduced = true;
        return result;
    }

    private static bool Equivalent(MdlDocument.BoneInfluence[] a, MdlDocument.BoneInfluence[] b) =>
        a.Length == b.Length && a.All(left => b.Any(right => right.Bone == left.Bone && right.Weight == left.Weight));

    // A reference mesh uses only part of its skeleton. Known extension-family presence establishes
    // rig support even when that particular reference omits another bone in the same family.
    // These body extensions require their corresponding modded skeleton. Vanilla garment bones,
    // especially j_sk_* skirt chains, are not inferred to be missing merely because a nude body omits them.
    internal static bool IsBodyExtension(string name) => name.StartsWith("iv_", StringComparison.Ordinal)
        || name.StartsWith("ya_", StringComparison.Ordinal);

    private static MdlDocument.BoneInfluence[] Sample(MdlDocument.BoneInfluence[][] corners, Vector3 barycentric)
    {
        float[] bary = [barycentric.X, barycentric.Y, barycentric.Z];
        var weights = new Dictionary<string, float>(StringComparer.Ordinal);
        for (int corner = 0; corner < 3; corner++)
            foreach (var weight in corners[corner])
                weights[weight.Bone] = weights.GetValueOrDefault(weight.Bone) + weight.Weight * MathF.Max(0, bary[corner]);
        float sum = weights.Values.Sum();
        if (!float.IsFinite(sum) || sum <= 0) throw MdlDocument.Error("Destination body contains an unusable skinning sample.");
        return weights.Where(p => p.Value > 0).Select(p => new MdlDocument.BoneInfluence(p.Key, p.Value / sum)).ToArray();
    }
}
