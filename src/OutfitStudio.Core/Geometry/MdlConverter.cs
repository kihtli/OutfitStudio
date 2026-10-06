using System.Numerics;
using OutfitStudio.Core.Models;

namespace OutfitStudio.Core.Geometry;

public static class MdlConverter
{
    public static PreparedConversion Combine(IEnumerable<PreparedConversion> conversions)
    {
        var items = conversions.ToArray();
        if (items.Length == 0) throw MdlDocument.Error("At least one body reference pair is required.");
        if (items.Any(c => c.Settings != items[0].Settings)) throw MdlDocument.Error("Combined references must use the same conversion settings.");
        if (items.Length == 1) return items[0];
        var triangles = items.SelectMany(c => c.Triangles).Distinct().ToArray();
        if (triangles.GroupBy(t => (t.A, t.B, t.C)).Any(g => g.Count() > 1))
            throw MdlDocument.Error("Combined references map the same source triangle to conflicting target positions.");
        return new PreparedConversion(new TriangleIndex(triangles), items[0].Settings,
            string.Join(" + ", items.Select(c => c.Method).Distinct()), items.SelectMany(c => c.Warnings).Distinct().ToArray(),
            items.SelectMany(c => c.BodyMaterials), BoneWeightTransfer.Combine(items.Select(c => c.WeightTransfer)),
            items.All(c => c.PreserveRigidParts));
    }
    public static ModelInspection Inspect(byte[] model) => MdlDocument.Parse(model).Inspection;

    public static ConversionResult Convert(byte[] sourceBody, byte[] targetBody, byte[] outfit,
        ConversionSettings settings, CancellationToken cancellationToken = default)
        => Prepare(sourceBody, targetBody, settings, cancellationToken).Convert(outfit, cancellationToken);

    public static PreparedConversion Prepare(byte[] sourceBody, byte[] targetBody, ConversionSettings settings,
        CancellationToken cancellationToken = default)
        => PrepareCore(sourceBody, targetBody, settings, null, cancellationToken);

    internal static PreparedConversion PrepareForAccessories(byte[] sourceBody, byte[] targetBody,
        IReadOnlyList<byte[]> accessories, ConversionSettings settings, CancellationToken cancellationToken = default)
        => PrepareCore(sourceBody, targetBody, settings, accessories, cancellationToken);

    private static PreparedConversion PrepareCore(byte[] sourceBody, byte[] targetBody, ConversionSettings settings,
        IReadOnlyList<byte[]>? accessories, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!float.IsFinite(settings.Strength) || settings.Strength is < 0 or > 1)
            throw MdlDocument.Error("Conversion strength must be between zero and one.");
        if (!float.IsFinite(settings.Clearance) || settings.Clearance is < 0 or > 0.1f)
            throw MdlDocument.Error("Clearance must be between zero and 0.1 native model units.");
        if (!float.IsFinite(settings.MaximumDistance) || settings.MaximumDistance is <= 0 or > 10)
            throw MdlDocument.Error("Maximum binding distance must be greater than zero and at most 10 native model units.");
        cancellationToken.ThrowIfCancellationRequested();
        var source = MdlDocument.Parse(sourceBody); var target = MdlDocument.Parse(targetBody);
        var warnings = new List<string>();
        var sourceMeshes = source.Meshes.Where(m => m.Lod == 0 && m.Indices.Length > 0 && !Accessory(source.Materials[m.Material])).ToArray();
        var targetMeshes = target.Meshes.Where(m => m.Lod == 0 && m.Indices.Length > 0 && !Accessory(target.Materials[m.Material])).ToArray();
        if (sourceMeshes.Length == 0 || targetMeshes.Length == 0) throw MdlDocument.Error("No body surface meshes were found in the selected references.");
        int ignored = source.Meshes.Count(m => m.Lod == 0 && m.Indices.Length > 0) - sourceMeshes.Length
            + target.Meshes.Count(m => m.Lod == 0 && m.Indices.Length > 0) - targetMeshes.Length;
        if (ignored != 0) warnings.Add($"Excluded {ignored} reference meshes whose material names identify piercings, pubes, underwear or nails.");
        var scopedIndices = accessories is null ? null : AccessoryReferenceScope.Select(sourceMeshes,
            accessories.Select(MdlDocument.Parse), settings.MaximumDistance, cancellationToken);
        if (scopedIndices is not null)
            warnings.Add("Localized accessory fitting checks the original nearest body faces and their adjacent surface; unrelated body regions are excluded from correspondence.");

        var triangles = new List<SurfaceTriangle>();
        var bodyMaterials = new HashSet<string>(StringComparer.Ordinal);
        int uvMapped = 0, topologyMapped = 0, extrapolated = 0, omitted = 0, degenerate = 0, continuityResolved = 0, hiddenLayers = 0, projectedLayers = 0;
        foreach (var sm in sourceMeshes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var indices = scopedIndices is null ? sm.Indices : scopedIndices[sm.Index];
            if (indices.Length == 0) continue;
            string sourceMaterial = MaterialKey(source.Materials[sm.Material]);
            var candidates = targetMeshes.Where(m => MaterialKey(target.Materials[m.Material]) == sourceMaterial).ToArray();
            if (candidates.Length == 0 && sourceMeshes.Length == 1 && targetMeshes.Length == 1)
            {
                candidates = targetMeshes;
                warnings.Add("Reference material names differ; using their single surface mesh. Confirm these references represent the same body region.");
            }
            if (candidates.Length == 0)
            {
                if (scopedIndices is not null) throw MdlDocument.Error("An accessory-bound source surface has no matching target material.");
                omitted++; continue;
            }
            bodyMaterials.Add(sourceMaterial);
            foreach (var candidate in candidates) bodyMaterials.Add(MaterialKey(target.Materials[candidate.Material]));
            var matching = candidates.Where(m => SameTopology(sm, m)).ToArray();
            Vector3[] destination;
            if (matching.Length == 1)
            {
                destination = matching[0].Positions;
                topologyMapped++;
            }
            else
            {
                if (!settings.AllowUvCorrespondence) throw MdlDocument.Error("Body vertex order/topology differs. Enable UV correspondence or select matching body variants.");
                if (sm.Uvs is null || candidates.Any(m => m.Uvs is null)) throw MdlDocument.Error("Different body topology requires primary UV coordinates on both references.");
                destination = MapUv(sm, indices, target, candidates, warnings, cancellationToken, ref extrapolated, ref continuityResolved, ref hiddenLayers, ref projectedLayers);
                uvMapped++;
            }
            for (int i = 0; i < indices.Length; i += 3)
            {
                int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                var sa = sm.Positions[a]; var sb = sm.Positions[b]; var sc = sm.Positions[c];
                var ta = destination[a]; var tb = destination[b]; var tc = destination[c];
                if (Vector3.Cross(sb - sa, sc - sa).LengthSquared() < 1e-16f) { degenerate++; continue; }
                if (Vector3.Cross(tb - ta, tc - ta).LengthSquared() < 1e-18f)
                {
                    if (scopedIndices is not null) throw MdlDocument.Error("An accessory-bound source face collapses on the target body; a custom correspondence is required.");
                    degenerate++; continue;
                }
                triangles.Add(new(sa, sb, sc, ta, tb, tc));
            }
        }
        if (omitted > 0) warnings.Add($"Excluded {omitted} source reference meshes without a matching target material.");
        if (degenerate > 0) warnings.Add($"Ignored {degenerate} degenerate reference triangles.");
        if (triangles.Count < 1) throw MdlDocument.Error("The selected references have no compatible surface triangles.");
        if (uvMapped > 0)
        {
            warnings.Add("UV correspondence is an approximate fit. Matching texture coordinates do not prove anatomical correspondence; inspect the result in the game before use.");
            if (extrapolated > 0) warnings.Add($"{extrapolated} body vertices used the nearest UV boundary within a 0.015 atlas-unit tolerance.");
            if (continuityResolved > 0) warnings.Add($"{continuityResolved} overlapping-UV vertices were resolved from neighboring body-surface displacement continuity.");
        }
        if (hiddenLayers > 0) warnings.Add($"Excluded {hiddenLayers} hidden overlapping body-surface candidates; {projectedLayers} candidates were projected along the authored outward normal onto the visible outer skin.");
        warnings.Add("Materials and topology are retained. Source-only body physics weights are adapted to the destination body when needed; garment-specific animation weights remain. Skin textures are not transferred.");
        var method = uvMapped > 0 ? topologyMapped > 0 ? "Topology and UV surface correspondence" : "UV surface correspondence" : "Matching topology";
        return new PreparedConversion(new TriangleIndex(triangles), settings, method, warnings, bodyMaterials,
            BoneWeightTransfer.Create(target, targetMeshes, cancellationToken, SourceBodyBones()), accessories is not null);

        IEnumerable<string> SourceBodyBones()
        {
            foreach (var mesh in sourceMeshes.Where(m => m.HasSkinning))
                for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
                {
                    if ((vertex & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    foreach (var influence in source.GetBoneWeights(mesh, vertex)) yield return influence.Bone;
                }
        }
    }

    private static bool SameTopology(MdlDocument.Mesh a, MdlDocument.Mesh b)
    {
        if (a.VertexCount != b.VertexCount || !a.Indices.AsSpan().SequenceEqual(b.Indices)) return false;
        // Equal index arrays alone do not establish vertex correspondence after a re-export.
        if (a.Uvs is null || b.Uvs is null) return true;
        return a.Indices.Distinct().All(i => Vector2.DistanceSquared(a.Uvs[i], b.Uvs[i]) <= 1e-8f);
    }

    internal static string MaterialKey(string material) => material.Replace('\\', '/').ToLowerInvariant();
    private static bool Accessory(string material)
    {
        material = material.ToLowerInvariant();
        return material.Contains("piercing") || material.Contains("pube") || material.Contains("undies") || material.Contains("underwear") || material.Contains("nail");
    }

    private static Vector3[] MapUv(MdlDocument.Mesh source, ushort[] indices, MdlDocument targetDocument, MdlDocument.Mesh[] targets, List<string> warnings, CancellationToken ct, ref int extrapolated, ref int continuityResolved, ref int hiddenLayers, ref int projectedLayers)
    {
        var visibleSurface = new BodySurfaceVisibility(targetDocument, targets);
        var index = visibleSurface.UvIndex;
        var vertices = indices.Select(vertex => (int)vertex).Distinct().ToArray();
        var uvOffset = UvTileAlignment.Select(source.Uvs!, vertices, index, ct);
        if (uvOffset != Vector2.Zero)
            warnings.Add(FormattableString.Invariant($"Aligned source body UV correspondence by a uniform tile offset ({uvOffset.X}, {uvOffset.Y}); stored mesh UV coordinates were retained."));
        var result = new Vector3[source.VertexCount];
        var resolved = new bool[source.VertexCount];
        var unresolved = new Dictionary<int, Vector3[]>();
        var choices = new Dictionary<int, Vector3[]>();
        foreach (int vertex in vertices)
        {
            if ((vertex & 255) == 0) ct.ThrowIfCancellationRequested();
            var uv = new Vector3(source.Uvs![vertex] + uvOffset, 0);
            var nearest = index.Nearest(uv, 0.015f * 0.015f)
                ?? throw MdlDocument.Error($"Source body vertex {vertex} has no target UV correspondence within 0.015 atlas units. These variants need a custom body mapping.");
            if (nearest.DistanceSquared > 1e-10f) extrapolated++;
            // Resolve duplicate atlas triangles only if the spatial match is unambiguous.
            // Shared triangle boundaries collapse to the same destination and are harmless.
            var hits = index.Within(uv, nearest.DistanceSquared + 1e-12f);
            var destinations = visibleSurface.VisibleCandidates(hits, ref hiddenLayers, ref projectedLayers)
                .OrderBy(p => Vector3.DistanceSquared(p, source.Positions[vertex])).ToArray();
            if (destinations.Length == 0) destinations = [nearest.Triangle.SampleTarget(nearest.Barycentric)];
            if (destinations.Any(p => Vector3.DistanceSquared(p, destinations[0]) > 1e-8f)) choices[vertex] = destinations;
            if (TryChoose(destinations, source.Positions[vertex], out var best))
            {
                result[vertex] = best; resolved[vertex] = true;
            }
            else unresolved[vertex] = destinations;
        }

        // UV folds can yield multiple local matches, especially at the thigh/hip seam.
        // Anchor those vertices to already unambiguous adjacent surface displacement,
        // never to an arbitrary atlas triangle or an ordering-dependent default.
        var neighbors = new HashSet<int>[source.VertexCount];
        for (int i = 0; i < neighbors.Length; i++) neighbors[i] = [];
        for (int i = 0; i < indices.Length; i += 3)
            for (int corner = 0; corner < 3; corner++)
            {
                int a = indices[i + corner], b = indices[i + (corner + 1) % 3];
                neighbors[a].Add(b); neighbors[b].Add(a);
            }
        for (int iteration = 0; iteration < 8 && unresolved.Count > 0; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            var accepted = new List<(int Vertex, Vector3 Position)>();
            foreach (var (vertex, candidates) in unresolved)
            {
                var known = neighbors[vertex].Where(n => resolved[n]).ToArray();
                if (known.Length < 2) continue;
                Vector3 displacement = Vector3.Zero; float totalWeight = 0;
                foreach (int neighbor in known)
                {
                    float weight = 1 / MathF.Max(1e-6f, Vector3.Distance(source.Positions[vertex], source.Positions[neighbor]));
                    displacement += (result[neighbor] - source.Positions[neighbor]) * weight;
                    totalWeight += weight;
                }
                var expected = source.Positions[vertex] + displacement / totalWeight;
                if (TryChoose(candidates, expected, out var best)) accepted.Add((vertex, best));
            }
            if (accepted.Count == 0) break;
            foreach (var (vertex, position) in accepted) { result[vertex] = position; resolved[vertex] = true; unresolved.Remove(vertex); continuityResolved++; }
        }
        if (unresolved.Count > 0)
            throw MdlDocument.Error($"Source body vertex {unresolved.Keys.First()} maps to overlapping target UV islands with ambiguous positions; {unresolved.Count} vertices could not be resolved by neighboring surface continuity. A custom correspondence map is required.");
        continuityResolved += UvContinuity.Refine(source.Positions, result, choices, neighbors, ct);
        return result;

        static bool TryChoose(Vector3[] destinations, Vector3 expected, out Vector3 best)
        {
            var ordered = destinations.OrderBy(p => Vector3.DistanceSquared(p, expected)).ToArray();
            best = ordered[0];
            float bestDistance = Vector3.Distance(best, expected);
            foreach (var other in ordered.Skip(1))
            {
                if (Vector3.DistanceSquared(best, other) <= 0.005f * 0.005f) continue;
                float alternative = Vector3.Distance(other, expected);
                // Require a 20% distance preference, with a 0.001 model-unit floor.
                if (alternative - bestDistance < MathF.Max(0.001f, bestDistance * 0.2f)) return false;
            }
            return true;
        }
    }
}

/// <summary>Reusable, immutable body correspondence. Safe to share across independent outfit model conversions.</summary>
public sealed class PreparedConversion
{
    private readonly TriangleIndex surface;
    private readonly ConversionSettings settings;
    private readonly HashSet<string> bodyMaterials;
    private readonly bool identitySurface;
    private GarmentDeformationField? garmentField;
    internal IEnumerable<string> BodyMaterials => bodyMaterials;
    internal BoneWeightTransfer WeightTransfer { get; }
    internal bool PreserveRigidParts { get; }
    public string Method { get; }
    public IReadOnlyList<string> Warnings { get; }
    internal ConversionSettings Settings => settings;
    internal IEnumerable<SurfaceTriangle> Triangles => surface.Triangles;

    internal PreparedConversion(TriangleIndex surface, ConversionSettings settings, string method, IReadOnlyList<string> warnings, IEnumerable<string>? bodyMaterials = null, BoneWeightTransfer? weightTransfer = null,
        bool preserveRigidParts = false)
    {
        this.surface = surface; this.settings = settings; Method = method; Warnings = warnings.ToArray();
        identitySurface = surface.Triangles.All(triangle => triangle.A == triangle.TargetA
            && triangle.B == triangle.TargetB && triangle.C == triangle.TargetC);
        this.bodyMaterials = new HashSet<string>(bodyMaterials ?? [], StringComparer.Ordinal);
        WeightTransfer = weightTransfer ?? BoneWeightTransfer.Combine([]);
        PreserveRigidParts = preserveRigidParts;
    }

    public ConversionResult Convert(byte[] outfit, CancellationToken cancellationToken = default)
    {
        var model = MdlDocument.Parse(outfit);
        var output = (byte[])outfit.Clone();
        int changed = 0, unchanged = 0, unbound = 0;
        var warnings = Warnings.ToList();
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        float greatestDisplacement = 0, radius = 0;
        if (settings.Strength == 0)
            return new(output, 0, model.Meshes.Sum(m => m.VertexCount), Method, warnings);
        var skinBindings = new SkinSeamBindings();
        var boundVertices = model.Meshes.ToDictionary(m => m.Index, m => new bool[m.VertexCount]);
        int skinVertices = 0;
        foreach (var mesh in model.Meshes)
        {
            string material = MdlConverter.MaterialKey(model.Materials[mesh.Material]);
            bool isSkin = bodyMaterials.Contains(material);
            float clearance = isSkin ? 0 : settings.Clearance;
            if (isSkin) skinVertices += mesh.VertexCount;
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var originalPosition = mesh.Positions[i];
                // UV splits and separately indexed body sections may differ by only
                // export-rounding error. They must use one binding and shading frame.
                var position = skinBindings.Canonical(mesh.Lod, material, originalPosition);
                var hit = surface.Nearest(position, settings.MaximumDistance * settings.MaximumDistance);
                boundVertices[mesh.Index][i] = hit is not null;
                var result = position;
                if (hit is null) { unchanged++; unbound++; }
                else if (clearance == 0 && (isSkin || identitySurface) && hit.Value.Triangle.A == hit.Value.Triangle.TargetA
                    && hit.Value.Triangle.B == hit.Value.Triangle.TargetB && hit.Value.Triangle.C == hit.Value.Triangle.TargetC)
                    { result = originalPosition; unchanged++; } // Exact identity must not acquire floating-point round-trip drift.
                else
                {
                    bool vertexChanged = false;
                    var triangle = hit.Value.Triangle;
                    Matrix4x4 transform;
                    Vector3 clearanceNormal;
                    float smoothFrame = isSkin ? 0 : GarmentBinding.SmoothFrameInfluence(hit.Value.DistanceSquared, settings.MaximumDistance);
                    if (smoothFrame < 1)
                    {
                        var sourceCross = Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A);
                        var targetCross = Vector3.Cross(triangle.TargetB - triangle.TargetA, triangle.TargetC - triangle.TargetA);
                        // Prepare has already rejected degenerate reference triangles.
                        // Fail explicitly if the stored reference is ever inconsistent;
                        // do not normalize zero vectors or silently leave vertices behind.
                        if (!MdlDocument.Finite(sourceCross) || !MdlDocument.Finite(targetCross)
                            || sourceCross.LengthSquared() < 1e-16f || targetCross.LengthSquared() < 1e-18f)
                            throw MdlDocument.Error($"The prepared reference triangle is invalid at mesh {mesh.Index}, vertex {i}. Rebuild the body correspondence before converting.");
                        var sourceNormal = Vector3.Normalize(sourceCross);
                        clearanceNormal = Vector3.Normalize(targetCross);
                        var sourceBasis = Basis(triangle.B - triangle.A, triangle.C - triangle.A, sourceNormal);
                        var targetBasis = Basis(triangle.TargetB - triangle.TargetA, triangle.TargetC - triangle.TargetA, clearanceNormal);
                        if (!LinearTransform.TryInvert(sourceBasis, out var inverse, out _)) throw MdlDocument.Error("Reference triangle cannot be inverted.");
                        transform = inverse * targetBasis;
                    }
                    else { transform = default; clearanceNormal = default; }
                    if (smoothFrame > 0)
                    {
                        // Build only for a conversion that actually contains cloth.
                        // Cached individual body references never need this field.
                        var field = garmentField;
                        if (field is null)
                        {
                            var created = new GarmentDeformationField(surface.Triangles, cancellationToken);
                            field = Interlocked.CompareExchange(ref garmentField, created, null) ?? created;
                        }
                        field.Evaluate(triangle, hit.Value.Barycentric, out var smoothTransform, out var smoothNormal);
                        if (smoothFrame == 1) { transform = smoothTransform; clearanceNormal = smoothNormal; }
                        else
                        {
                            PolarTransport.Decompose(transform, out var localStretch, out var localRotation);
                            PolarTransport.Decompose(smoothTransform, out var smoothStretch, out var smoothRotation);
                            transform = PolarTransport.Compose(Matrix4x4.Lerp(localStretch, smoothStretch, smoothFrame),
                                Matrix4x4.Lerp(localRotation, smoothRotation, smoothFrame));
                            clearanceNormal = Vector3.Lerp(clearanceNormal, smoothNormal, smoothFrame);
                            if (clearanceNormal.LengthSquared() < 1e-16f)
                                throw MdlDocument.Error("The garment clearance direction is ambiguous between the local and smooth reference frames.");
                            clearanceNormal = Vector3.Normalize(clearanceNormal);
                        }
                    }
                    var sourcePoint = triangle.Sample(hit.Value.Barycentric);
                    var targetPoint = triangle.SampleTarget(hit.Value.Barycentric);
                    var desired = targetPoint + Vector3.TransformNormal(position - sourcePoint, transform) + clearanceNormal * clearance;
                    float influence = isSkin ? settings.Strength : settings.Strength
                        * GarmentBinding.Influence(hit.Value.DistanceSquared, settings.MaximumDistance);
                    result = Vector3.Lerp(position, desired, influence);
                    if (!MdlDocument.Finite(result)) throw MdlDocument.Error("Deformation produced an invalid position.");
                    float displacement = Vector3.Distance(originalPosition, result);
                    if (result == originalPosition || displacement <= 1e-7f && !isSkin) result = originalPosition;
                    else
                    {
                        vertexChanged = true;
                        MdlDocument.WriteVector(output, mesh.Address(mesh.Position, i), mesh.Position.Type, result, false);
                        // Bounds must enclose the encoded coordinate (Half4 can round
                        // outward), rather than only the intermediate float value.
                        result = MdlDocument.ReadVector(output, mesh.Address(mesh.Position, i), mesh.Position.Type, false);
                        greatestDisplacement = MathF.Max(greatestDisplacement, Vector3.Distance(originalPosition, result));
                    }
                    var blended = Matrix4x4.Lerp(Matrix4x4.Identity, transform, influence);
                    // A vertex on a rotation axis can retain its position while its
                    // shading frame changes. Transform directions independently.
                    if (!NearlyIdentity(blended))
                    {
                        if (!LinearTransform.TryInvert(blended, out var inverseBlended, out double determinant))
                            throw MdlDocument.Error("Deformation collapses a vertex frame; choose compatible body references.");
                        if (determinant <= 0)
                            throw MdlDocument.Error("Deformation would turn a vertex frame inside out; choose a different strength or compatible body references.");
                        var normalMatrix = Matrix4x4.Transpose(inverseBlended);
                        foreach (var element in mesh.Elements.Where(e => e.Usage is 3 or 5 or 6))
                        {
                            int address = mesh.Address(element, i);
                            var old = MdlDocument.ReadVector(outfit, address, element.Type, true);
                            if (!MdlDocument.Finite(old)) throw MdlDocument.Error("Model contains a non-finite normal or tangent.");
                            var transformed = Vector3.TransformNormal(old, element.Usage == 3 ? normalMatrix : blended);
                            if (transformed.LengthSquared() > 1e-15f) transformed = Vector3.Normalize(transformed);
                            if (Vector3.DistanceSquared(old, transformed) > 1e-14f)
                            {
                                MdlDocument.WriteVector(output, address, element.Type, transformed, true);
                                vertexChanged = true;
                            }
                        }
                    }
                    if (vertexChanged) changed++; else unchanged++;
                }
                min = Vector3.Min(min, result); max = Vector3.Max(max, result); radius = MathF.Max(radius, result.Length());
            }
        }
        if (unbound == model.Meshes.Sum(m => m.VertexCount))
            throw MdlDocument.Error("No outfit vertices are within the maximum binding distance of the selected body reference.");
        var clearanceResult = GarmentClearance.Refine(model, output, bodyMaterials, settings.Clearance * settings.Strength, cancellationToken, boundVertices);
        var rigidResult = PreserveRigidParts && changed > 0
            ? RigidAccessoryParts.Apply(model, output, bodyMaterials, cancellationToken) : null;
        if (rigidResult is { Assemblies: > 0 })
            warnings.Add($"Preserved {rigidResult.Assemblies} rigid accessory assemblies from {rigidResult.Components} connected parts; {rigidResult.BlendedParts} connecting parts follow their authored attachment blends.");
        int transferredWeights = WeightTransfer.Apply(model, output, bodyMaterials, settings.MaximumDistance, boundVertices, cancellationToken, warnings);
        if (transferredWeights > 0)
            warnings.Add($"Adapted destination body weights on {transferredWeights} vertices to remove source-only body physics bones; embedded skin follows the destination and garment-specific animation influences are preserved.");
        if (clearanceResult.AdjustedVertices > 0 || clearanceResult.UnresolvedSamples > 0 || transferredWeights > 0 || rigidResult is not null)
        {
            if (clearanceResult.AdjustedVertices > 0 || clearanceResult.UnresolvedSamples > 0) warnings.Add($"Garment surface clearance adjusted {clearanceResult.AdjustedVertices} vertices by at most {clearanceResult.MaximumAdjustment:G5} model units across {clearanceResult.Samples} originally covered skin samples; {clearanceResult.UnresolvedSamples} samples remain below the requested clearance. This bounded rest-pose check does not cover animation or unsampled surface intersections.");
            changed = 0; unchanged = 0;
            min = new(float.PositiveInfinity); max = new(float.NegativeInfinity); radius = 0; greatestDisplacement = 0;
            foreach (var mesh in model.Meshes)
                for (int i = 0; i < mesh.VertexCount; i++)
                {
                    if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var p = MdlDocument.ReadVector(output, mesh.Address(mesh.Position, i), mesh.Position.Type, false);
                    min = Vector3.Min(min, p); max = Vector3.Max(max, p); radius = MathF.Max(radius, p.Length());
                    greatestDisplacement = MathF.Max(greatestDisplacement, Vector3.Distance(mesh.Positions[i], p));
                    bool different = mesh.Elements.Where(e => e.Usage is 0 or 1 or 2 or 3 or 5 or 6).Any(e =>
                        !output.AsSpan(mesh.Address(e, i), e.Size).SequenceEqual(outfit.AsSpan(mesh.Address(e, i), e.Size)));
                    if (!different && transferredWeights > 0 && mesh.HasSkinning)
                    {
                        var beforeWeights = model.GetBoneWeights(mesh, i);
                        var afterWeights = model.GetBoneWeights(mesh, i, output);
                        different = beforeWeights.Length != afterWeights.Length || beforeWeights.Any(a => !afterWeights.Any(b => a.Bone == b.Bone && a.Weight == b.Weight));
                    }
                    if (different) changed++; else unchanged++;
                }
        }
        if (skinVertices > 0 && settings.Clearance > 0)
            warnings.Add($"Clothing clearance was excluded from {skinVertices} embedded body vertices identified by the reference skin materials; coincident skin seams share one deformation binding.");
        if (unbound != 0) warnings.Add($"{unbound} vertices were outside the maximum binding distance and retained their original positions.");
        if (greatestDisplacement > 0 || transferredWeights > 0)
        {
            // Enlarge primary and model bounds. Bone boxes use an isotropic displacement
            // margin so this remains conservative regardless of local bone orientations.
            ExpandBounds(output, model.BoundsOffset, min, max);
            ExpandBounds(output, model.BoundsOffset + 32, min, max);
            for (int bone = 0; bone < model.BoneCount; bone++)
            {
                int offset = model.BoundsOffset + (4 + bone) * 32;
                var oldMin = MdlDocument.ReadVector(output, offset, 2, false);
                var oldMax = MdlDocument.ReadVector(output, offset + 16, 2, false);
                // Rebinding can attach a distant vertex to an existing bone even if its
                // position did not move. Enclose a full rest-model diameter in that case.
                float boneMargin = transferredWeights > 0 ? MathF.Max(greatestDisplacement, radius * 2) : greatestDisplacement;
                ExpandBounds(output, offset, oldMin - new Vector3(boneMargin), oldMax + new Vector3(boneMargin));
            }
            float oldRadius = MdlDocument.ReadFloat(output, model.ModelHeaderOffset);
            MdlDocument.WriteFloat(output, model.ModelHeaderOffset, MathF.Max(float.IsFinite(oldRadius) ? oldRadius : 0, radius));
        }
        else if (unbound == model.Meshes.Sum(m => m.VertexCount))
            throw MdlDocument.Error("No outfit vertices are within the maximum binding distance of the selected body reference.");
        if (greatestDisplacement > 0) AddFitDiagnostics(model, output, warnings, cancellationToken);
        return new(output, changed, unchanged, Method, warnings);
    }

    private static bool NearlyIdentity(Matrix4x4 m) => MathF.Abs(m.M11 - 1) < 1e-6f && MathF.Abs(m.M22 - 1) < 1e-6f && MathF.Abs(m.M33 - 1) < 1e-6f
        && MathF.Abs(m.M12) < 1e-6f && MathF.Abs(m.M13) < 1e-6f && MathF.Abs(m.M21) < 1e-6f && MathF.Abs(m.M23) < 1e-6f && MathF.Abs(m.M31) < 1e-6f && MathF.Abs(m.M32) < 1e-6f;

    private static void AddFitDiagnostics(MdlDocument original, byte[] output, List<string> warnings, CancellationToken ct)
    {
        int reversed = 0, collapsed = 0, considered = 0;
        foreach (var mesh in original.Meshes)
        {
            var positions = new Vector3[mesh.VertexCount];
            for (int i = 0; i < positions.Length; i++) positions[i] = MdlDocument.ReadVector(output, mesh.Address(mesh.Position, i), mesh.Position.Type, false);
            for (int i = 0; i < mesh.Indices.Length; i += 3)
            {
                if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
                int a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
                var before = Vector3.Cross(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]);
                var after = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                if (before.LengthSquared() < 1e-16f) continue;
                considered++;
                if (after.LengthSquared() < MathF.Max(1e-18f, before.LengthSquared() * 1e-8f)) collapsed++;
                else if (Vector3.Dot(before, after) < 0) reversed++;
            }
        }
        if (reversed > 0 || collapsed > 0)
            warnings.Add($"Fit diagnostic: {reversed} of {considered} nondegenerate triangles changed orientation by more than 90 degrees; {collapsed} collapsed. Review this fit before enabling it; UV mapping can require manual corrections.");
    }

    private static Matrix4x4 Basis(Vector3 x, Vector3 y, Vector3 z) => new(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
    private static void ExpandBounds(byte[] output, int offset, Vector3 min, Vector3 max)
    {
        var oldMin = MdlDocument.ReadVector(output, offset, 2, false);
        var oldMax = MdlDocument.ReadVector(output, offset + 16, 2, false);
        if (MdlDocument.Finite(oldMin)) min = Vector3.Min(oldMin, min);
        if (MdlDocument.Finite(oldMax)) max = Vector3.Max(oldMax, max);
        MdlDocument.WriteVector(output, offset, 2, min, false); MdlDocument.WriteVector(output, offset + 16, 2, max, false);
    }
}
