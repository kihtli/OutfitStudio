using System.Numerics;

namespace OutfitStudio.Core.Geometry;

internal static class UvContinuity
{
    // A one-pass confidence decision can lock neighboring vertices onto different
    // branches of a folded atlas. Revisit all multi-candidate vertices after the
    // surface is anchored. Each update lowers local displacement-gradient energy;
    // because graph edges are symmetric, the whole surface energy also decreases.
    public static int Refine(Vector3[] source, Vector3[] destination, IReadOnlyDictionary<int, Vector3[]> choices,
        HashSet<int>[] neighbors, CancellationToken ct)
    {
        var ordered = choices.OrderBy(p => p.Key).ToArray();
        var refined = new HashSet<int>();
        for (int iteration = 0; iteration < 32 && ordered.Length > 0; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            int changes = 0, visited = 0;
            foreach (var (vertex, candidates) in ordered)
            {
                if ((visited++ & 255) == 0) ct.ThrowIfCancellationRequested();
                var best = destination[vertex]; double bestCost = Cost(best);
                foreach (var candidate in candidates)
                {
                    double candidateCost = Cost(candidate);
                    if (candidateCost < bestCost - Math.Max(1e-8, bestCost * 1e-6)) { best = candidate; bestCost = candidateCost; }
                }
                if (Vector3.DistanceSquared(best, destination[vertex]) < 1e-12f) continue;
                destination[vertex] = best; refined.Add(vertex); changes++;
                double Cost(Vector3 candidate)
                {
                    var delta = candidate - source[vertex]; double sum = 0;
                    foreach (int neighbor in neighbors[vertex])
                    {
                        float edgeLengthSquared = Vector3.DistanceSquared(source[vertex], source[neighbor]);
                        var gradient = delta - (destination[neighbor] - source[neighbor]);
                        sum += gradient.LengthSquared() / MathF.Max(1e-8f, edgeLengthSquared);
                    }
                    return sum;
                }
            }
            if (changes == 0) break;
        }
        return refined.Count;
    }
}
