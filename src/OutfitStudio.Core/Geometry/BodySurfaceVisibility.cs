using System.Numerics;

namespace OutfitStudio.Core.Geometry;

/// <summary>Identifies an opaque body's hidden overlapping component without
/// confusing the inward-facing side of another limb with an outer skin layer.</summary>
internal sealed class BodySurfaceVisibility
{
    private sealed record Face(SurfaceTriangle Geometry, int Component, bool HasNormals);
    private readonly Dictionary<SurfaceTriangle, Face> uvFaces = [];
    private readonly Dictionary<SurfaceTriangle, Face> spatialFaces = [];
    internal TriangleIndex UvIndex { get; }
    private readonly TriangleIndex spatialIndex;

    public BodySurfaceVisibility(MdlDocument document, MdlDocument.Mesh[] meshes)
    {
        int componentOffset = 0;
        foreach (var mesh in meshes)
        {
            var parent = Enumerable.Range(0, mesh.VertexCount).ToArray();
            int Root(int vertex) { while (parent[vertex] != vertex) { parent[vertex] = parent[parent[vertex]]; vertex = parent[vertex]; } return vertex; }
            for (int i = 0; i < mesh.Indices.Length; i += 3)
                for (int corner = 1; corner < 3; corner++) parent[Root(mesh.Indices[i + corner])] = Root(mesh.Indices[i]);
            var normalElement = mesh.Elements.FirstOrDefault(e => e.Usage == 3);
            var normals = normalElement is null ? null : Enumerable.Range(0, mesh.VertexCount)
                .Select(i => MdlDocument.ReadVector(document.Data, mesh.Address(normalElement, i), normalElement.Type, true)).ToArray();
            if (normals is not null && normals.Any(n => !MdlDocument.Finite(n)))
                throw MdlDocument.Error("A target body contains a non-finite authored normal.");
            for (int i = 0; i < mesh.Indices.Length; i += 3)
            {
                int a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
                var cross = Vector3.Cross(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]);
                if (cross.LengthSquared() < 1e-18f) continue;
                var fallback = Vector3.Normalize(cross);
                var geometry = new SurfaceTriangle(mesh.Positions[a], mesh.Positions[b], mesh.Positions[c],
                    normals?[a] ?? fallback, normals?[b] ?? fallback, normals?[c] ?? fallback);
                var face = new Face(geometry, componentOffset + Root(a), normals is not null);
                spatialFaces.TryAdd(geometry, face);
                var uv = new SurfaceTriangle(new(mesh.Uvs![a], 0), new(mesh.Uvs[b], 0), new(mesh.Uvs[c], 0),
                    mesh.Positions[a], mesh.Positions[b], mesh.Positions[c]);
                if (Vector3.Cross(uv.B - uv.A, uv.C - uv.A).LengthSquared() >= 1e-20f) uvFaces.TryAdd(uv, face);
            }
            componentOffset += mesh.VertexCount;
        }
        UvIndex = new(uvFaces.Keys); spatialIndex = new(spatialFaces.Keys);
    }

    public Vector3[] VisibleCandidates(IEnumerable<TriangleIndex.Hit> hits, ref int hidden, ref int projected)
    {
        var visible = new List<Vector3>(); var lifted = new List<Vector3>();
        foreach (var hit in hits)
        {
            var face = uvFaces[hit.Triangle]; var point = hit.Triangle.SampleTarget(hit.Barycentric);
            var normal = face.Geometry.SampleTarget(hit.Barycentric);
            if (!face.HasNormals || normal.LengthSquared() < 1e-12f) { visible.Add(point); continue; }
            normal = Vector3.Normalize(normal);
            var current = point; int component = face.Component; float remaining = 0.05f; bool covered = false;
            for (int layer = 0; layer < 16 && remaining > 0.00001f; layer++)
            {
                var ray = spatialIndex.Raycast(current + normal * 0.00001f, normal, remaining, (triangle, bary) =>
                {
                    var other = spatialFaces[triangle];
                    if (!other.HasNormals || other.Component == component) return false;
                    var otherNormal = triangle.SampleTarget(bary);
                    return otherNormal.LengthSquared() > 1e-12f && Vector3.Dot(normal, Vector3.Normalize(otherNormal)) > 0.5f;
                });
                if (ray is null) break;
                var next = ray.Value.Triangle.Sample(ray.Value.Barycentric);
                remaining -= Vector3.Distance(current, next);
                current = next; component = spatialFaces[ray.Value.Triangle].Component; covered = true;
            }
            if (!covered) visible.Add(point);
            else { hidden++; lifted.Add(current); }
        }
        if (visible.Count > 0) return visible.ToArray();
        // Some inner atlas regions have no outer UV duplicate. Their outward ray
        // establishes the corresponding visible skin directly, rather than joining
        // the outfit to a hidden underlay or deleting outfit topology.
        projected += lifted.Count;
        return lifted.ToArray();
    }
}
