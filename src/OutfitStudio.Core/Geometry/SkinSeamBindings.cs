using System.Numerics;

namespace OutfitStudio.Core.Geometry;

/// <summary>Unifies only export-rounding differences in an existing skin seam.</summary>
internal sealed class SkinSeamBindings
{
    private const float Tolerance = 0.000001f;
    private readonly Dictionary<(int Lod, string Material, int X, int Y, int Z), List<Vector3>> cells = [];

    public Vector3 Canonical(int lod, string material, Vector3 position)
    {
        int x = (int)MathF.Floor(position.X / Tolerance);
        int y = (int)MathF.Floor(position.Y / Tolerance);
        int z = (int)MathF.Floor(position.Z / Tolerance);
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    if (cells.TryGetValue((lod, material, x + dx, y + dy, z + dz), out var nearby))
                        foreach (var candidate in nearby)
                            if (Vector3.DistanceSquared(candidate, position) <= Tolerance * Tolerance)
                                return candidate;
        var key = (lod, material, x, y, z);
        if (!cells.TryGetValue(key, out var vertices)) cells[key] = vertices = [];
        vertices.Add(position);
        return position;
    }
}
