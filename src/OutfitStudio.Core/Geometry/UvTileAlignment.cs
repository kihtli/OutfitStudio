using System.Numerics;

namespace OutfitStudio.Core.Geometry;

/// <summary>Matches a uniformly translated UV tile without folding distinct islands together.</summary>
internal static class UvTileAlignment
{
    internal const float Tolerance = 0.015f;

    public static Vector2 Select(Vector2[] sourceUvs, IReadOnlyList<int> requiredVertices,
        TriangleIndex target, CancellationToken cancellationToken)
    {
        if (requiredVertices.Count == 0 || Covers(Vector2.Zero)) return Vector2.Zero;

        var sourceMin = new Vector2(float.PositiveInfinity);
        var sourceMax = new Vector2(float.NegativeInfinity);
        foreach (int vertex in requiredVertices)
        {
            sourceMin = Vector2.Min(sourceMin, sourceUvs[vertex]);
            sourceMax = Vector2.Max(sourceMax, sourceUvs[vertex]);
        }
        var targetMin = new Vector2(float.PositiveInfinity);
        var targetMax = new Vector2(float.NegativeInfinity);
        foreach (var triangle in target.Triangles)
        {
            targetMin = Vector2.Min(targetMin, new(triangle.Min.X, triangle.Min.Y));
            targetMax = Vector2.Max(targetMax, new(triangle.Max.X, triangle.Max.Y));
        }
        var difference = targetMin * 0.5f + targetMax * 0.5f - sourceMin * 0.5f - sourceMax * 0.5f;
        // Check nine nearby integer translations, regardless of atlas size. Very
        // large offsets no longer preserve the existing sub-texel tolerance in floats.
        if (!float.IsFinite(difference.X) || !float.IsFinite(difference.Y)
            || MathF.Abs(difference.X) > 65536 || MathF.Abs(difference.Y) > 65536)
            return Vector2.Zero;
        var center = new Vector2(MathF.Round(difference.X), MathF.Round(difference.Y));
        Vector2? accepted = null;
        for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
            {
                var candidate = center + new Vector2(x, y);
                if (candidate == Vector2.Zero || !Covers(candidate)) continue;
                if (accepted is not null)
                    throw MdlDocument.Error("Source body UVs fit multiple translated target atlas tiles. A custom correspondence map is required.");
                accepted = candidate;
            }
        // Let the normal UV correspondence check report the first unmapped
        // vertex when no whole-reference translation passes the same tolerance.
        return accepted ?? Vector2.Zero;

        bool Covers(Vector2 offset)
        {
            int visited = 0;
            foreach (int vertex in requiredVertices)
            {
                if ((visited++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var uv = new Vector3(sourceUvs[vertex] + offset, 0);
                if (target.Nearest(uv, Tolerance * Tolerance) is null) return false;
            }
            return true;
        }
    }
}
