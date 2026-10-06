using System.Numerics;

namespace OutfitStudio.Core.Geometry;

/// <summary>Retains the original nearest body faces and their complete local shading neighborhoods.</summary>
internal static class AccessoryReferenceScope
{
    // GarmentDeformationField joins export-rounded source/target vertices within
    // this radius. Retain all possible source neighbors here; the field still
    // requires target agreement before joining their shading frames.
    private const float WeldTolerance = 0.000001f;

    public static IReadOnlyDictionary<int, ushort[]> Select(IReadOnlyList<MdlDocument.Mesh> bodyMeshes,
        IEnumerable<MdlDocument> accessories, float maximumDistance, CancellationToken ct)
    {
        var locations = new Dictionary<SurfaceTriangle, (int Mesh, int Offset)>(ReferenceEqualityComparer.Instance);
        foreach (var mesh in bodyMeshes)
            for (int offset = 0; offset < mesh.Indices.Length; offset += 3)
            {
                if ((offset & 255) == 0) ct.ThrowIfCancellationRequested();
                var a = mesh.Positions[mesh.Indices[offset]];
                var b = mesh.Positions[mesh.Indices[offset + 1]];
                var c = mesh.Positions[mesh.Indices[offset + 2]];
                if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-16f) continue;
                locations.Add(new(a, b, c, a, b, c), (mesh.Index, offset));
            }
        if (locations.Count == 0) throw MdlDocument.Error("The source body has no surface for accessory fitting.");
        var surface = new TriangleIndex(locations.Keys);
        var selected = new Dictionary<int, HashSet<int>>();
        var vertices = new Dictionary<(long X, long Y, long Z), HashSet<Vector3>>();
        int count = 0;
        foreach (var accessory in accessories)
            foreach (var mesh in accessory.Meshes)
                foreach (var position in mesh.Positions)
                {
                    if ((count++ & 255) == 0) ct.ThrowIfCancellationRequested();
                    var hit = surface.Nearest(position, maximumDistance * maximumDistance)
                        ?? throw MdlDocument.Error("Accessory geometry extends beyond the selected source body reference. Use explicit references for accessories spanning several regions.");
                    var location = locations[hit.Triangle];
                    if (!selected.TryGetValue(location.Mesh, out var faces)) selected[location.Mesh] = faces = [];
                    faces.Add(location.Offset);
                    AddVertex(hit.Triangle.A); AddVertex(hit.Triangle.B); AddVertex(hit.Triangle.C);
                }
        if (count == 0) throw MdlDocument.Error("The accessory has no vertices to fit.");
        // Include every incident face, including export-rounded UV splits, so
        // off-surface frame interpolation retains the original local neighborhood.
        foreach (var (triangle, location) in locations)
        {
            ct.ThrowIfCancellationRequested();
            if (!ContainsVertex(triangle.A) && !ContainsVertex(triangle.B) && !ContainsVertex(triangle.C)) continue;
            if (!selected.TryGetValue(location.Mesh, out var faces)) selected[location.Mesh] = faces = [];
            faces.Add(location.Offset);
        }
        return bodyMeshes.ToDictionary(mesh => mesh.Index, mesh => selected.TryGetValue(mesh.Index, out var faces)
            ? faces.Order().SelectMany(offset => mesh.Indices.Skip(offset).Take(3)).ToArray() : []);

        void AddVertex(Vector3 position)
        {
            var key = Cell(position);
            if (!vertices.TryGetValue(key, out var bucket)) vertices[key] = bucket = [];
            bucket.Add(position);
        }

        bool ContainsVertex(Vector3 position)
        {
            var key = Cell(position);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (vertices.TryGetValue((key.X + dx, key.Y + dy, key.Z + dz), out var bucket)
                            && bucket.Any(vertex => Vector3.DistanceSquared(vertex, position) <= WeldTolerance * WeldTolerance))
                            return true;
            return false;
        }

        static (long X, long Y, long Z) Cell(Vector3 position)
        {
            return (Coordinate(position.X), Coordinate(position.Y), Coordinate(position.Z));
            static long Coordinate(float value)
            {
                double cell = Math.Floor((double)value / WeldTolerance);
                if (!double.IsFinite(cell) || cell <= long.MinValue + 2.0 || cell >= long.MaxValue - 2.0)
                    throw MdlDocument.Error("Reference coordinates are outside the accessory neighborhood's supported range.");
                return (long)cell;
            }
        }
    }
}
