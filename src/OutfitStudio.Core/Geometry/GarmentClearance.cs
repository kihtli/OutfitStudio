using System.Numerics;

namespace OutfitStudio.Core.Geometry;

/// <summary>Local surface clearance for skin that was covered in the input outfit.
/// This is a bounded rest-pose correction, not a cloth simulation.</summary>
internal static class GarmentClearance
{
    internal sealed record Statistics(int AdjustedVertices, int Samples, int UnresolvedSamples, float MaximumAdjustment);

    public static Statistics Refine(MdlDocument original, byte[] output, ISet<string> bodyMaterials,
        float clearance, CancellationToken ct, IReadOnlyDictionary<int, bool[]>? boundVertices = null)
    {
        if (clearance <= 0) return new(0, 0, 0, 0);
        int adjusted = 0, sampleCount = 0, unresolved = 0;
        float maximumAdjustment = 0;
        var skin = original.Meshes.Where(m => bodyMaterials.Contains(MdlConverter.MaterialKey(original.Materials[m.Material]))).ToArray();
        if (skin.Length == 0) return new(0, 0, 0, 0);
        var geometricSkinNormals = new Dictionary<int, Vector3[]>();
        var sharedSeams = SharedMeshSeams(original, ct);
        bool Bound(MdlDocument.Mesh mesh, int vertex) => boundVertices is null || boundVertices[mesh.Index][vertex];
        float coverageDistance = Math.Clamp(clearance * 4, 0.01f, 0.05f);
        float baseAdjustmentLimit = Math.Clamp(clearance * 8, 0.01f, 0.1f);
        foreach (var mesh in original.Meshes)
        {
            ct.ThrowIfCancellationRequested();
            string material = MdlConverter.MaterialKey(original.Materials[mesh.Material]);
            if (bodyMaterials.Contains(material) || Decoration(material) || mesh.Indices.Length == 0) continue;
            var normal = mesh.Elements.FirstOrDefault(e => e.Usage == 3);
            if (normal is null) continue; // No authored outward orientation is available.
            var eligible = Enumerable.Range(0, mesh.VertexCount).Select(v => Bound(mesh, v) && !sharedSeams.Contains((mesh.Index, v))).ToArray();
            var before = ReadPositions(mesh, output);
            var positions = (Vector3[])before.Clone();
            var sourceSurface = BuildSurface(mesh, mesh.Positions);
            if (sourceSurface is null) continue;
            var signs = new float[mesh.Indices.Length / 3];
            var sourceFaceNormals = new Vector3[signs.Length];
            for (int face = 0; face < mesh.Indices.Length; face += 3)
            {
                int a = mesh.Indices[face], b = mesh.Indices[face + 1], c = mesh.Indices[face + 2];
                var cross = Vector3.Cross(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]);
                var authored = MdlDocument.ReadVector(original.Data, mesh.Address(normal, a), normal.Type, true)
                    + MdlDocument.ReadVector(original.Data, mesh.Address(normal, b), normal.Type, true)
                    + MdlDocument.ReadVector(original.Data, mesh.Address(normal, c), normal.Type, true);
                signs[face / 3] = Vector3.Dot(cross, authored) < 0 ? -1 : 1;
                if (cross.LengthSquared() >= 1e-16f)
                    sourceFaceNormals[face / 3] = Vector3.Normalize(cross) * signs[face / 3];
            }
            // A thin closed ribbon has nearby, opposite-facing walls. Track
            // those walls and their contacts: restricting unrelated ordinary
            // panels can distort a legitimate curved hem.
            var pairedWalls = new bool[signs.Length];
            float shellDistance = MathF.Min(0.005f, coverageDistance / 2);
            foreach (var triangle in sourceSurface.Value.Index.Triangles)
            {
                int face = sourceSurface.Value.Faces[triangle];
                if ((face & 1023) == 0) ct.ThrowIfCancellationRequested();
                var outward = sourceFaceNormals[face / 3];
                var opposite = sourceSurface.Value.Index.Nearest(triangle.Center, shellDistance * shellDistance,
                    t => Vector3.Dot(sourceFaceNormals[sourceSurface.Value.Faces[t] / 3], outward) < -0.9f);
                if (opposite is null) continue;
                var bary = opposite.Value.Barycentric;
                if (MathF.Min(bary.X, MathF.Min(bary.Y, bary.Z)) >= 0.001f)
                {
                    pairedWalls[face / 3] = true;
                    pairedWalls[sourceSurface.Value.Faces[opposite.Value.Triangle] / 3] = true;
                }
            }
            // Contacts on the adjoining panel share the strap's local surface.
            // Include only the same bounded source-space neighborhood as the
            // wall-pair detection, rather than changing remote panel fitting.
            var pairedTriangles = sourceSurface.Value.Index.Triangles
                .Where(t => pairedWalls[sourceSurface.Value.Faces[t] / 3]).ToArray();
            var pairedSurface = pairedTriangles.Length > 0 ? new TriangleIndex(pairedTriangles) : null;
            var points = new Dictionary<Vector3, (int Face, float Direction, bool Layered, Vector3 SkinOutward)>();
            foreach (var body in skin.Where(m => m.Lod == mesh.Lod))
            {
                var skinNormal = body.Elements.FirstOrDefault(e => e.Usage == 3);
                if (skinNormal is null) continue;
                foreach (int vertex in body.Indices.Distinct())
                {
                    if ((vertex & 255) == 0) ct.ThrowIfCancellationRequested();
                    if (!Bound(body, vertex)) continue;
                    var outward = MdlDocument.ReadVector(original.Data, body.Address(skinNormal, vertex), skinNormal.Type, true);
                    if (!MdlDocument.Finite(outward) || outward.LengthSquared() < 1e-15f) continue;
                    outward = Vector3.Normalize(outward);
                    var hit = sourceSurface.Value.Index.Nearest(body.Positions[vertex], coverageDistance * coverageDistance);
                    if (hit is null) continue;
                    int sourceFace = sourceSurface.Value.Faces[hit.Value.Triangle];
                    if (Enumerable.Range(sourceFace, 3).Any(c => !eligible[mesh.Indices[c]])) continue;
                    var bary = hit.Value.Barycentric;
                    // An edge/vertex hit can be alongside an intentional cutout.
                    if (MathF.Min(bary.X, MathF.Min(bary.Y, bary.Z)) < 0.001f) continue;
                    var delta = hit.Value.Triangle.Sample(bary) - body.Positions[vertex];
                    float gap = Vector3.Dot(delta, outward);
                    if (gap < 0 || gap < delta.Length() * 0.25f) continue;
                    // Keep the authored coverage test even where skin normals
                    // turn sharply around a body detail. The skin-to-cloth
                    // direction identifies the covered side more reliably than
                    // requiring the garment normal to follow that skin normal.
                    var contactDirection = delta.LengthSquared() > 1e-16f ? Vector3.Normalize(delta) : outward;
                    float direction = 1;
                    bool layered = pairedWalls[sourceFace / 3]
                        || pairedSurface?.Nearest(hit.Value.Triangle.Sample(bary), shellDistance * shellDistance) is not null;
                    if (layered && Vector3.Dot(sourceFaceNormals[sourceFace / 3], contactDirection) < 0)
                    {
                        // If the outer wall projects onto a seam/cutout edge, keep
                        // the original covered sample and reverse its inner-side guide.
                        direction = -1;
                        var exterior = sourceSurface.Value.Index.Nearest(body.Positions[vertex], coverageDistance * coverageDistance,
                            t => Vector3.Dot(sourceFaceNormals[sourceSurface.Value.Faces[t] / 3], contactDirection) > 0.25f);
                        if (exterior is not null)
                        {
                            int exteriorFace = sourceSurface.Value.Faces[exterior.Value.Triangle];
                            var exteriorBary = exterior.Value.Barycentric;
                            if (MathF.Min(exteriorBary.X, MathF.Min(exteriorBary.Y, exteriorBary.Z)) >= 0.001f
                                && Enumerable.Range(exteriorFace, 3).All(c => eligible[mesh.Indices[c]]))
                            {
                                sourceFace = exteriorFace;
                                direction = 1;
                            }
                        }
                    }
                    var skinOutward = Vector3.Zero;
                    if (layered)
                    {
                        if (!geometricSkinNormals.TryGetValue(body.Index, out var normals))
                            geometricSkinNormals[body.Index] = normals = GeometricSkinNormals(original, body, output, ct);
                        skinOutward = normals[vertex];
                    }
                    points.TryAdd(MdlDocument.ReadVector(output, body.Address(body.Position, vertex), body.Position.Type, false),
                        (sourceFace, direction, layered, skinOutward));
                }
            }
            if (points.Count == 0) continue;
            sampleCount += points.Count;
            // Preserve pre-existing duplicate seams while moving triangle corners.
            var groups = new Dictionary<Vector3, List<int>>();
            var vertexGroup = new List<int>[mesh.VertexCount];
            var canonical = new SkinSeamBindings();
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                var key = canonical.Canonical(mesh.Lod, material, mesh.Positions[i]);
                if (!groups.TryGetValue(key, out var members)) groups[key] = members = [];
                members.Add(i); vertexGroup[i] = members;
            }
            var fairing = new GarmentFairing(mesh, vertexGroup, eligible, ct);
            fairing.Smooth(before, positions, 0.01f, ct);
            var contactBefore = (Vector3[])positions.Clone();
            // Clothing that already hugged the source skin should not inflate
            // merely because many nearby contact samples share a broad patch.
            // Give it a local movement allowance based on the initial contact
            // demand, while retaining the global safety bound below.
            var sourceSkinSurfaces = skin.Where(m => m.Lod == mesh.Lod)
                .Select(m => BuildSurface(m, m.Positions)).Where(s => s is not null).ToArray();
            var localLimits = Enumerable.Repeat(float.PositiveInfinity, mesh.VertexCount).ToArray();
            int groupIndex = 0;
            foreach (var members in groups.Values)
            {
                if ((groupIndex++ & 255) == 0) ct.ThrowIfCancellationRequested();
                int anchor = members[0];
                if (eligible[anchor] && sourceSkinSurfaces.Any(s =>
                        s!.Value.Index.Nearest(mesh.Positions[anchor], coverageDistance * coverageDistance) is not null))
                    foreach (int vertex in members) localLimits[vertex] = clearance;
            }
            // A fixed cap can be smaller than an already-covered target-body feature.
            // Measure that deficit on the smoothed surface and allow one clearance
            // margin for changing contact directions, retaining the global bound.
            var allowedContacts = new HashSet<(int Face, Vector3 Point)>();
            float initialDeficit = 0;
            var startSurface = BuildSurface(mesh, positions, eligible);
            if (startSurface is not null)
            {
                int initialIndex = 0;
                foreach (var (point, contact) in points)
                {
                    if ((initialIndex++ & 255) == 0) ct.ThrowIfCancellationRequested();
                    var guide = ContactGuide(contact);
                    var hit = startSurface.Value.Index.Nearest(point, baseAdjustmentLimit * baseAdjustmentLimit * 4,
                        t => FacesContact(t, startSurface.Value.Faces[t], guide, contact.Layered));
                    if (hit is null) continue;
                    var triangle = hit.Value.Triangle;
                    int face = startSurface.Value.Faces[triangle];
                    var outward = Vector3.Normalize(Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A)) * signs[face / 3];
                    float deficit = clearance - Vector3.Dot(triangle.Sample(hit.Value.Barycentric) - point, outward);
                    if (!float.IsFinite(deficit)) throw MdlDocument.Error("Garment contact produced an invalid initial clearance.");
                    initialDeficit = MathF.Max(initialDeficit, deficit);
                    if (deficit <= 0) continue;
                    AllowOriginalContact(face, point);
                }
            }
            float adjustmentLimit = Math.Clamp(MathF.Max(baseAdjustmentLimit, initialDeficit + clearance), baseAdjustmentLimit, 0.1f);
            for (int iteration = 0; iteration < 24; iteration++)
            {
                ct.ThrowIfCancellationRequested();
                var current = BuildSurface(mesh, positions, eligible);
                if (current is null) break;
                int violations = 0;
                int sampleIndex = 0;
                foreach (var (point, contact) in points)
                {
                    if ((sampleIndex++ & 255) == 0) ct.ThrowIfCancellationRequested();
                    var guide = ContactGuide(contact);
                    var hit = current.Value.Index.Nearest(point, adjustmentLimit * adjustmentLimit * 4,
                        t => FacesContact(t, current.Value.Faces[t], guide, contact.Layered));
                    if (hit is null) continue;
                    int face = current.Value.Faces[hit.Value.Triangle];
                    int a = mesh.Indices[face], b = mesh.Indices[face + 1], c = mesh.Indices[face + 2];
                    var cross = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                    if (cross.LengthSquared() < 1e-16f) continue;
                    var outward = Vector3.Normalize(cross) * signs[face / 3];
                    var bary = TriangleIndex.ClosestBarycentric(point, positions[a], positions[b], positions[c]);
                    var sample = positions[a] * bary.X + positions[b] * bary.Y + positions[c] * bary.Z;
                    float missing = clearance - Vector3.Dot(sample - point, outward);
                    if (missing <= 0.00001f) continue;
                    violations++;
                    // A thin, closed strap can move past its inner wall onto a
                    // neighboring panel. Grant that contact its original fitting
                    // allowance. Ordinary panel contacts retain their initial
                    // local bounds so the already fitted sides stay close.
                    if (contact.Layered) AllowOriginalContact(face, point);
                    // Spread contact over a connected cloth patch instead of forming
                    // a point at the three corners of a coarse triangle.
                    var (patch, response) = ContactPatch(mesh, fairing, vertexGroup, a, b, c, bary, ct);
                    if (response < 0.01f) continue;
                    var delta = outward * (missing / response);
                    foreach (var (vertex, weight) in patch) Move(vertex, delta * weight);
                }
                if (violations == 0) break;
            }
            var finalSurface = BuildSurface(mesh, positions, eligible);
            if (finalSurface is null) continue;
            int finalIndex = 0;
            foreach (var (point, contact) in points)
            {
                if ((finalIndex++ & 255) == 0) ct.ThrowIfCancellationRequested();
                var guide = ContactGuide(contact);
                var hit = finalSurface.Value.Index.Nearest(point, adjustmentLimit * adjustmentLimit * 4,
                    t => FacesContact(t, finalSurface.Value.Faces[t], guide, contact.Layered));
                if (hit is null) { unresolved++; continue; }
                var triangle = hit.Value.Triangle;
                int face = finalSurface.Value.Faces[triangle];
                var outward = Vector3.Normalize(Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A)) * signs[face / 3];
                if (Vector3.Dot(triangle.Sample(hit.Value.Barycentric) - point, outward) < clearance - 0.00002f) unresolved++;
            }
            var used = mesh.Indices.Select(v => (int)v).ToHashSet();
            var authoredRotations = SurfaceRotations(mesh, mesh.Positions, positions, vertexGroup, out var uvFrames);
            var correctionRotations = SurfaceRotations(mesh, before, positions, vertexGroup, out _);
            // Shape replacement vertices follow the actual MDL base-index mapping,
            // even when they are far from the base surface or overlap another layer.
            foreach (var shape in original.ShapeBindings.Where(b => b.MeshIndex == mesh.Index).GroupBy(b => b.ReplacementVertex))
            {
                int replacement = shape.Key;
                if (!eligible[replacement]) continue;
                var deltas = shape.Select(b => positions[b.BaseVertex] - before[b.BaseVertex]).ToArray();
                if (deltas.Any(d => Vector3.DistanceSquared(d, deltas[0]) > 1e-10f))
                    throw MdlDocument.Error("A shape replacement references base vertices with conflicting clothing-clearance corrections; this shape needs a custom fit.");
                if (used.Contains(replacement) && Vector3.DistanceSquared(positions[replacement] - before[replacement], deltas[0]) > 1e-10f)
                    throw MdlDocument.Error("A shape replacement also used by the base mesh needs conflicting clothing-clearance corrections.");
                positions[replacement] = before[replacement] + deltas[0];
                authoredRotations[replacement] = authoredRotations[shape.First().BaseVertex];
                correctionRotations[replacement] = correctionRotations[shape.First().BaseVertex];
                uvFrames[replacement] = uvFrames[shape.First().BaseVertex];
            }
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                if ((i & 255) == 0) ct.ThrowIfCancellationRequested();
                if (!MdlDocument.Finite(positions[i])) throw MdlDocument.Error("Garment fitting produced an invalid position.");
                bool vertexAdjusted = false;
                float movement = Vector3.Distance(before[i], positions[i]);
                if (movement > 1e-7f)
                {
                    MdlDocument.WriteVector(output, mesh.Address(mesh.Position, i), mesh.Position.Type, positions[i], false);
                    var encoded = MdlDocument.ReadVector(output, mesh.Address(mesh.Position, i), mesh.Position.Type, false);
                    movement = Vector3.Distance(before[i], encoded);
                    if (movement > 0) { vertexAdjusted = true; maximumAdjustment = MathF.Max(maximumAdjustment, movement); }
                }
                if (!Bound(mesh, i) || sharedSeams.Contains((mesh.Index, i)))
                {
                    if (vertexAdjusted) adjusted++;
                    continue;
                }
                foreach (var element in mesh.Elements.Where(e => e.Usage is 3 or 5 or 6))
                {
                    int address = mesh.Address(element, i);
                    var value = MdlDocument.ReadVector(output, address, element.Type, true);
                    // Transport the original authored frame through the actual final
                    // cloth geometry, not the already approximate body-field frame.
                    var basis = MdlDocument.ReadVector(original.Data, address, element.Type, true);
                    var rotated = Vector3.Transform(basis, authoredRotations[i]);
                    if (element.Usage is 5 or 6)
                    {
                        if (!uvFrames[i]) rotated = Vector3.Transform(value, correctionRotations[i]);
                        var authoredNormal = MdlDocument.ReadVector(original.Data, mesh.Address(normal, i), normal.Type, true);
                        var frameNormal = Vector3.Transform(authoredNormal, authoredRotations[i]);
                        if (frameNormal.LengthSquared() > 1e-15f)
                        {
                            frameNormal = Vector3.Normalize(frameNormal);
                            rotated -= frameNormal * Vector3.Dot(rotated, frameNormal);
                            if (rotated.LengthSquared() < 1e-15f)
                                rotated = Vector3.Cross(frameNormal, MathF.Abs(frameNormal.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY);
                        }
                    }
                    if (!MdlDocument.Finite(rotated) || !float.IsFinite(rotated.LengthSquared()))
                        throw MdlDocument.Error("Garment fitting produced an invalid authored frame.");
                    if (rotated.LengthSquared() > 1e-15f) rotated = Vector3.Normalize(rotated);
                    if (Vector3.DistanceSquared(value, rotated) > 1e-14f)
                    {
                        MdlDocument.WriteVector(output, address, element.Type, rotated, true);
                        vertexAdjusted |= MdlDocument.ReadVector(output, address, element.Type, true) != value;
                    }
                }
                if (vertexAdjusted) adjusted++;
            }

            Vector3 ContactGuide((int Face, float Direction, bool Layered, Vector3 SkinOutward) contact)
            {
                // Layered cloth should not rotate its own contact guide while
                // being corrected. Anchor it to the fitted skin's actual surface,
                // without relying on transferred target shading normals.
                if (contact.Layered && contact.SkinOutward.LengthSquared() > 1e-16f) return contact.SkinOutward;
                return ContactDirection(contact.Face) * contact.Direction;
            }

            // Ordinary panels and degenerate skin retain the garment guide so a
            // hem may legitimately rotate around a different body shape.
            Vector3 ContactDirection(int face)
            {
                var cross = Vector3.Cross(positions[mesh.Indices[face + 1]] - positions[mesh.Indices[face]],
                    positions[mesh.Indices[face + 2]] - positions[mesh.Indices[face]]);
                return cross.LengthSquared() >= 1e-16f ? Vector3.Normalize(cross) * signs[face / 3] : Vector3.Zero;
            }

            bool FacesContact(SurfaceTriangle triangle, int face, Vector3 guide, bool layered)
            {
                // A layered contact keeps its outward side even when its nearest
                // face migrates onto an unpaired edge or adjoining panel.
                if (!layered && !pairedWalls[face / 3]) return true;
                var cross = Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A);
                return cross.LengthSquared() >= 1e-16f
                    && Vector3.Dot(Vector3.Normalize(cross) * signs[face / 3], guide) > 0.25f;
            }

            void AllowOriginalContact(int face, Vector3 point)
            {
                if (!allowedContacts.Add((face, point))) return;
                int a = mesh.Indices[face], b = mesh.Indices[face + 1], c = mesh.Indices[face + 2];
                var cross = Vector3.Cross(contactBefore[b] - contactBefore[a], contactBefore[c] - contactBefore[a]);
                if (cross.LengthSquared() < 1e-16f) return;
                var outward = Vector3.Normalize(cross) * signs[face / 3];
                var bary = TriangleIndex.ClosestBarycentric(point, contactBefore[a], contactBefore[b], contactBefore[c]);
                var sample = contactBefore[a] * bary.X + contactBefore[b] * bary.Y + contactBefore[c] * bary.Z;
                float deficit = clearance - Vector3.Dot(sample - point, outward);
                if (deficit <= 0) return;
                var (patch, response) = ContactPatch(mesh, fairing, vertexGroup, a, b, c, bary, ct);
                if (response < 0.01f) return;
                foreach (var (vertex, weight) in patch)
                {
                    float allowance = 1.5f * deficit / response * weight + clearance;
                    if (!float.IsFinite(allowance)) throw MdlDocument.Error("Garment contact produced an invalid local fitting bound.");
                    foreach (int member in vertexGroup[vertex])
                        if (float.IsFinite(localLimits[member])) localLimits[member] = MathF.Max(localLimits[member], allowance);
                }
            }

            void Move(int vertex, Vector3 displacement)
            {
                if (!MdlDocument.Finite(displacement)) throw MdlDocument.Error("Garment contact produced an invalid displacement.");
                var members = vertexGroup[vertex];
                if (members.Any(v => !eligible[v])) return;
                int anchor = members[0];
                var total = positions[anchor] + displacement - contactBefore[anchor];
                float limit = MathF.Min(adjustmentLimit, localLimits[anchor]);
                if (total.LengthSquared() > limit * limit) total = Vector3.Normalize(total) * limit;
                foreach (int member in members)
                {
                    positions[member] = contactBefore[member] + total;
                    if (!MdlDocument.Finite(positions[member])) throw MdlDocument.Error("Garment contact produced an invalid position.");
                }
            }
        }
        return new(adjusted, sampleCount, unresolved, maximumAdjustment);
    }

    private static ((int Vertex, float Weight)[] Patch, float Response) ContactPatch(MdlDocument.Mesh mesh,
        GarmentFairing fairing, List<int>[] groups, int a, int b, int c, Vector3 bary, CancellationToken ct)
    {
        var center = mesh.Positions[a] * bary.X + mesh.Positions[b] * bary.Y + mesh.Positions[c] * bary.Z;
        float radius = MathF.Max(0.06f, 2 * MathF.Max(Vector3.Distance(center, mesh.Positions[a]),
            MathF.Max(Vector3.Distance(center, mesh.Positions[b]), Vector3.Distance(center, mesh.Positions[c]))));
        var patch = fairing.Patch(a, b, c, bary, radius, ct);
        float response = 0;
        foreach (var (vertex, weight) in patch)
        {
            if (vertex == groups[a][0]) response += bary.X * weight;
            if (vertex == groups[b][0]) response += bary.Y * weight;
            if (vertex == groups[c][0]) response += bary.Z * weight;
        }
        return (patch, response);
    }

    private static bool Decoration(string material) => material.Contains("piercing") || material.Contains("pube") || material.Contains("nail") || material.Contains("chain");
    private static Vector3[] ReadPositions(MdlDocument.Mesh mesh, byte[] data) => Enumerable.Range(0, mesh.VertexCount)
        .Select(i => MdlDocument.ReadVector(data, mesh.Address(mesh.Position, i), mesh.Position.Type, false)).ToArray();

    private static Vector3[] GeometricSkinNormals(MdlDocument original, MdlDocument.Mesh mesh, byte[] output, CancellationToken ct)
    {
        var result = new Vector3[mesh.VertexCount];
        var normal = mesh.Elements.FirstOrDefault(e => e.Usage == 3);
        if (normal is null) return result;
        var positions = ReadPositions(mesh, output);
        for (int face = 0; face < mesh.Indices.Length; face += 3)
        {
            if ((face & 1023) == 0) ct.ThrowIfCancellationRequested();
            int a = mesh.Indices[face], b = mesh.Indices[face + 1], c = mesh.Indices[face + 2];
            var sourceCross = Vector3.Cross(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]);
            var authored = MdlDocument.ReadVector(original.Data, mesh.Address(normal, a), normal.Type, true)
                + MdlDocument.ReadVector(original.Data, mesh.Address(normal, b), normal.Type, true)
                + MdlDocument.ReadVector(original.Data, mesh.Address(normal, c), normal.Type, true);
            float sign = Vector3.Dot(sourceCross, authored) < 0 ? -1 : 1;
            var cross = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]) * sign;
            if (!MdlDocument.Finite(cross)) throw MdlDocument.Error("The fitted skin produced an invalid geometric normal.");
            result[a] += cross; result[b] += cross; result[c] += cross;
        }
        for (int vertex = 0; vertex < result.Length; vertex++)
        {
            if ((vertex & 255) == 0) ct.ThrowIfCancellationRequested();
            float lengthSquared = result[vertex].LengthSquared();
            if (!MdlDocument.Finite(result[vertex]) || !float.IsFinite(lengthSquared))
                throw MdlDocument.Error("The fitted skin produced an invalid accumulated geometric normal.");
            result[vertex] = lengthSquared > 1e-16f ? Vector3.Normalize(result[vertex]) : Vector3.Zero;
        }
        return result;
    }

    private static (TriangleIndex Index, Dictionary<SurfaceTriangle, int> Faces)? BuildSurface(MdlDocument.Mesh mesh, Vector3[] positions, bool[]? eligible = null)
    {
        var triangles = new List<SurfaceTriangle>(); var faces = new Dictionary<SurfaceTriangle, int>();
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            if (eligible is not null && (!eligible[mesh.Indices[i]] || !eligible[mesh.Indices[i + 1]] || !eligible[mesh.Indices[i + 2]])) continue;
            var a = positions[mesh.Indices[i]]; var b = positions[mesh.Indices[i + 1]]; var c = positions[mesh.Indices[i + 2]];
            var cross = Vector3.Cross(b - a, c - a);
            if (!MdlDocument.Finite(a) || !MdlDocument.Finite(b) || !MdlDocument.Finite(c)
                || !MdlDocument.Finite(cross) || !float.IsFinite(cross.LengthSquared()))
                throw MdlDocument.Error("Garment fitting produced an invalid surface triangle.");
            if (cross.LengthSquared() < 1e-16f) continue;
            var triangle = new SurfaceTriangle(a, b, c, a, b, c);
            triangles.Add(triangle); faces[triangle] = i;
        }
        return triangles.Count == 0 ? null : (new TriangleIndex(triangles), faces);
    }

    private static HashSet<(int Mesh, int Vertex)> SharedMeshSeams(MdlDocument model, CancellationToken ct)
    {
        var canonical = new SkinSeamBindings();
        var groups = new Dictionary<(int Lod, string Material, Vector3 Position), List<(int Mesh, int Vertex)>>();
        foreach (var mesh in model.Meshes)
        {
            string material = MdlConverter.MaterialKey(model.Materials[mesh.Material]);
            for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
            {
                if ((vertex & 255) == 0) ct.ThrowIfCancellationRequested();
                var key = (mesh.Lod, material, canonical.Canonical(mesh.Lod, material, mesh.Positions[vertex]));
                if (!groups.TryGetValue(key, out var members)) groups[key] = members = [];
                members.Add((mesh.Index, vertex));
            }
        }
        return groups.Values.Where(g => g.Select(v => v.Mesh).Distinct().Skip(1).Any()).SelectMany(g => g).ToHashSet();
    }

    private static Quaternion[] SurfaceRotations(MdlDocument.Mesh mesh, Vector3[] before, Vector3[] after,
        List<int>[] groups, out bool[] uvFrames)
    {
        var oldNormals = new Vector3[mesh.VertexCount]; var newNormals = new Vector3[mesh.VertexCount];
        // Tangents stay separate at UV splits; only geometric normals share the seam weld.
        var oldTangents = new Vector3[mesh.VertexCount]; var newTangents = new Vector3[mesh.VertexCount];
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            int a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
            var oldEdge1 = before[b] - before[a]; var oldEdge2 = before[c] - before[a];
            var newEdge1 = after[b] - after[a]; var newEdge2 = after[c] - after[a];
            var old = Vector3.Cross(oldEdge1, oldEdge2); var next = Vector3.Cross(newEdge1, newEdge2);
            if (!MdlDocument.Finite(old) || !MdlDocument.Finite(next))
                throw MdlDocument.Error("Garment fitting produced an invalid shading frame.");
            foreach (int vertex in new[] { a, b, c })
            {
                oldNormals[groups[vertex][0]] += old;
                newNormals[groups[vertex][0]] += next;
            }
            if (mesh.Uvs is null) continue;
            var uv1 = mesh.Uvs[b] - mesh.Uvs[a]; var uv2 = mesh.Uvs[c] - mesh.Uvs[a];
            float determinant = uv1.X * uv2.Y - uv1.Y * uv2.X;
            if (MathF.Abs(determinant) < 1e-12f) continue;
            float weight = old.Length() / determinant;
            var oldTangent = (oldEdge1 * uv2.Y - oldEdge2 * uv1.Y) * weight;
            var newTangent = (newEdge1 * uv2.Y - newEdge2 * uv1.Y) * weight;
            foreach (int vertex in new[] { a, b, c })
            {
                oldTangents[vertex] += oldTangent;
                newTangents[vertex] += newTangent;
            }
        }
        var rotations = new Quaternion[mesh.VertexCount];
        uvFrames = new bool[mesh.VertexCount];
        for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
        {
            var oldNormal = oldNormals[groups[vertex][0]];
            var newNormal = newNormals[groups[vertex][0]];
            if (!MdlDocument.Finite(oldNormal) || !MdlDocument.Finite(newNormal)
                || !float.IsFinite(oldNormal.LengthSquared()) || !float.IsFinite(newNormal.LengthSquared()))
                throw MdlDocument.Error("Garment fitting produced an invalid accumulated shading frame.");
            rotations[vertex] = Rotation(oldNormal, newNormal);
            if (oldNormal.LengthSquared() < 1e-15f || newNormal.LengthSquared() < 1e-15f) continue;
            oldNormal = Vector3.Normalize(oldNormal); newNormal = Vector3.Normalize(newNormal);
            var oldTangent = oldTangents[vertex] - oldNormal * Vector3.Dot(oldTangents[vertex], oldNormal);
            var newTangent = newTangents[vertex] - newNormal * Vector3.Dot(newTangents[vertex], newNormal);
            if (!MdlDocument.Finite(oldTangent) || !MdlDocument.Finite(newTangent)
                || !float.IsFinite(oldTangent.LengthSquared()) || !float.IsFinite(newTangent.LengthSquared()))
                throw MdlDocument.Error("Garment fitting produced an invalid UV frame.");
            if (oldTangent.LengthSquared() < 1e-15f || newTangent.LengthSquared() < 1e-15f) continue;
            var source = Frame(Vector3.Normalize(oldTangent), oldNormal);
            var target = Frame(Vector3.Normalize(newTangent), newNormal);
            rotations[vertex] = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(source) * target));
            uvFrames[vertex] = true;
        }
        return rotations;

        static Matrix4x4 Frame(Vector3 tangent, Vector3 normal)
        {
            var bitangent = Vector3.Cross(normal, tangent);
            return new(tangent.X, tangent.Y, tangent.Z, 0, bitangent.X, bitangent.Y, bitangent.Z, 0,
                normal.X, normal.Y, normal.Z, 0, 0, 0, 0, 1);
        }
    }

    private static Quaternion Rotation(Vector3 before, Vector3 after)
    {
        if (before.LengthSquared() < 1e-15f || after.LengthSquared() < 1e-15f) return Quaternion.Identity;
        before = Vector3.Normalize(before); after = Vector3.Normalize(after);
        float dot = Math.Clamp(Vector3.Dot(before, after), -1, 1);
        if (dot > 1 - 1e-7f) return Quaternion.Identity;
        if (dot < -1 + 1e-6f)
        {
            var axis = Vector3.Normalize(Vector3.Cross(before, MathF.Abs(before.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));
            return Quaternion.CreateFromAxisAngle(axis, MathF.PI);
        }
        return Quaternion.Normalize(new(Vector3.Cross(before, after), 1 + dot));
    }
}
