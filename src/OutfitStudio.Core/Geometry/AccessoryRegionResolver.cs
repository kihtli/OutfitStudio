using System.Numerics;

namespace OutfitStudio.Core.Geometry;

// Accessory equip slots describe replacement items, not where their geometry sits.
// Infer a single body region only when the authored surface provides clear evidence.
internal static class AccessoryRegionResolver
{
    private const float MaximumSurfaceDistance = 0.15f;
    private const float MinimumCoverage = 0.8f;
    private const float MinimumSeparation = 0.003f;

    public static string? Resolve(byte[] accessory, IReadOnlyDictionary<string, byte[]> bodyReferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessory);
        ArgumentNullException.ThrowIfNull(bodyReferences);
        cancellationToken.ThrowIfCancellationRequested();
        var samples = Samples(MdlDocument.Parse(accessory), cancellationToken);
        if (samples.Count == 0 || bodyReferences.Count == 0) return null;
        double totalWeight = samples.Sum(sample => sample.Weight);
        var scores = new List<(string Region, double Distance)>();
        foreach (var (region, bytes) in bodyReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = MdlDocument.Parse(bytes);
            var triangles = Triangles(body, true, cancellationToken).Select(triangle =>
                new SurfaceTriangle(triangle.A, triangle.B, triangle.C, triangle.A, triangle.B, triangle.C)).ToArray();
            if (triangles.Length == 0) continue;
            var surface = new TriangleIndex(triangles);
            double coverage = 0, squaredDistance = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var sample = samples[i];
                var hit = surface.Nearest(sample.Position, MaximumSurfaceDistance * MaximumSurfaceDistance);
                if (hit is not null) coverage += sample.Weight;
                squaredDistance += sample.Weight * (hit?.DistanceSquared ?? MaximumSurfaceDistance * MaximumSurfaceDistance);
            }
            // A tiny fragment near one region must not classify geometry spread
            // over several regions or a distant attachment with only a close tip.
            if (coverage / totalWeight < MinimumCoverage) continue;
            scores.Add((region, Math.Sqrt(squaredDistance / totalWeight)));
        }
        scores.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        if (scores.Count == 0) return null;
        if (scores.Count > 1 && scores[1].Distance - scores[0].Distance
            <= Math.Max(MinimumSeparation, scores[0].Distance * 0.25)) return null;
        return scores[0].Region;
    }

    private readonly record struct Sample(Vector3 Position, double Weight);
    private readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C, double Area);

    private static List<Sample> Samples(MdlDocument document, CancellationToken cancellationToken)
    {
        var samples = new List<Sample>();
        foreach (var triangle in Triangles(document, false, cancellationToken))
        {
            // Area-weighted interior quadrature avoids bias toward a densely
            // tessellated clasp or chain and ignores unused shape/stub vertices.
            var sum = triangle.A + triangle.B + triangle.C;
            double weight = triangle.Area / 3;
            samples.Add(new(sum / 6 + triangle.A / 2, weight));
            samples.Add(new(sum / 6 + triangle.B / 2, weight));
            samples.Add(new(sum / 6 + triangle.C / 2, weight));
        }
        return samples;
    }

    private static IEnumerable<Triangle> Triangles(MdlDocument document, bool bodyReference,
        CancellationToken cancellationToken)
    {
        foreach (var mesh in document.Meshes.Where(mesh => mesh.Lod == 0))
        {
            // Match the converter's exclusions for non-skin reference meshes.
            if (bodyReference && IsReferenceAdornment(document.Materials[mesh.Material])) continue;
            for (int i = 0; i < mesh.Indices.Length; i += 3)
            {
                if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var a = mesh.Positions[mesh.Indices[i]];
                var b = mesh.Positions[mesh.Indices[i + 1]];
                var c = mesh.Positions[mesh.Indices[i + 2]];
                float squaredArea = Vector3.Cross(b - a, c - a).LengthSquared();
                if (!float.IsFinite(squaredArea) || squaredArea < 1e-16f) continue;
                yield return new(a, b, c, Math.Sqrt(squaredArea) / 2);
            }
        }
    }

    private static bool IsReferenceAdornment(string material)
    {
        material = material.ToLowerInvariant();
        return material.Contains("piercing") || material.Contains("pube") || material.Contains("undies")
            || material.Contains("underwear") || material.Contains("nail");
    }
}
