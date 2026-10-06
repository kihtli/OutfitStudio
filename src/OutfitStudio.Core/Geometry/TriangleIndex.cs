using System.Numerics;

namespace OutfitStudio.Core.Geometry;

// Keep triangles immutable and reference-backed. Returning a large aggregate of six
// Vector3 fields inside a nullable Hit produced corrupted coordinates on optimized
// Windows/Wine execution. A reference also avoids repeatedly copying 72-byte records
// during BVH sorting and nearest-point traversal.
internal sealed record SurfaceTriangle(Vector3 A, Vector3 B, Vector3 C, Vector3 TargetA, Vector3 TargetB, Vector3 TargetC)
{
    public Vector3 Min => Vector3.Min(A, Vector3.Min(B, C));
    public Vector3 Max => Vector3.Max(A, Vector3.Max(B, C));
    public Vector3 Center => (A + B + C) / 3;
    public Vector3 Sample(Vector3 barycentric) => A * barycentric.X + B * barycentric.Y + C * barycentric.Z;
    public Vector3 SampleTarget(Vector3 barycentric) => TargetA * barycentric.X + TargetB * barycentric.Y + TargetC * barycentric.Z;
}

internal sealed class TriangleIndex
{
    internal readonly record struct Hit(SurfaceTriangle Triangle, Vector3 Barycentric, float DistanceSquared);
    private sealed class Node
    {
        public Vector3 Min, Max;
        public int Start, Count;
        public Node? Left, Right;
    }
    private readonly SurfaceTriangle[] triangles;
    private readonly Node root;
    internal IEnumerable<SurfaceTriangle> Triangles => triangles;

    public TriangleIndex(IEnumerable<SurfaceTriangle> triangles)
    {
        this.triangles = triangles.ToArray();
        if (this.triangles.Length == 0) throw MdlDocument.Error("The selected body reference contains no usable surface triangles.");
        root = Build(0, this.triangles.Length);
    }

    private Node Build(int start, int count)
    {
        var node = new Node { Start = start, Count = count, Min = new(float.PositiveInfinity), Max = new(float.NegativeInfinity) };
        for (int i = start; i < start + count; i++) { node.Min = Vector3.Min(node.Min, triangles[i].Min); node.Max = Vector3.Max(node.Max, triangles[i].Max); }
        if (count <= 12) return node;
        var extent = node.Max - node.Min;
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        Array.Sort(triangles, start, count, Comparer<SurfaceTriangle>.Create((a, b) => Component(a.Center, axis).CompareTo(Component(b.Center, axis))));
        int half = count / 2;
        node.Left = Build(start, half); node.Right = Build(start + half, count - half);
        return node;
    }

    public Hit? Nearest(Vector3 point, float maxDistanceSquared, Func<SurfaceTriangle, bool>? accept = null)
    {
        Hit? closest = null;
        Search(root);
        return closest;
        void Search(Node node)
        {
            if (BoxDistance(point, node.Min, node.Max) > maxDistanceSquared) return;
            if (node.Left is null || node.Right is null)
            {
                for (int i = node.Start; i < node.Start + node.Count; i++)
                {
                    var t = triangles[i]; var bary = ClosestBarycentric(point, t.A, t.B, t.C);
                    if (accept is not null && !accept(t)) continue;
                    float dist = Vector3.DistanceSquared(point, t.Sample(bary));
                    if (dist <= maxDistanceSquared) { maxDistanceSquared = dist; closest = new(t, bary, dist); }
                }
                return;
            }
            float dl = BoxDistance(point, node.Left.Min, node.Left.Max), dr = BoxDistance(point, node.Right.Min, node.Right.Max);
            if (dl <= dr) { Search(node.Left); Search(node.Right); } else { Search(node.Right); Search(node.Left); }
        }
    }

    public List<Hit> Within(Vector3 point, float distanceSquared)
    {
        var results = new List<Hit>();
        Search(root);
        return results;
        void Search(Node node)
        {
            if (BoxDistance(point, node.Min, node.Max) > distanceSquared) return;
            if (node.Left is null || node.Right is null)
            {
                for (int i = node.Start; i < node.Start + node.Count; i++)
                {
                    var t = triangles[i]; var bary = ClosestBarycentric(point, t.A, t.B, t.C);
                    float dist = Vector3.DistanceSquared(point, t.Sample(bary));
                    if (dist <= distanceSquared) results.Add(new(t, bary, dist));
                }
            }
            else { Search(node.Left); Search(node.Right); }
        }
    }

    internal Hit? Raycast(Vector3 origin, Vector3 direction, float maximumDistance, Func<SurfaceTriangle, Vector3, bool> accept)
    {
        Hit? closest = null;
        Search(root);
        return closest;
        void Search(Node node)
        {
            float near = 0, far = maximumDistance;
            for (int axis = 0; axis < 3; axis++)
            {
                float d = Component(direction, axis), o = Component(origin, axis);
                if (MathF.Abs(d) < 1e-12f)
                {
                    if (o < Component(node.Min, axis) || o > Component(node.Max, axis)) return;
                    continue;
                }
                float a = (Component(node.Min, axis) - o) / d, b = (Component(node.Max, axis) - o) / d;
                near = MathF.Max(near, MathF.Min(a, b)); far = MathF.Min(far, MathF.Max(a, b));
                if (near > far) return;
            }
            if (node.Left is not null && node.Right is not null) { Search(node.Left); Search(node.Right); return; }
            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                var triangle = triangles[i]; var ab = triangle.B - triangle.A; var ac = triangle.C - triangle.A;
                var p = Vector3.Cross(direction, ac); double determinant = Vector3.Dot(ab, p);
                if (Math.Abs(determinant) < 1e-12) continue;
                var relative = origin - triangle.A; double u = Vector3.Dot(relative, p) / determinant;
                if (u < -1e-6 || u > 1 + 1e-6) continue;
                var q = Vector3.Cross(relative, ab); double v = Vector3.Dot(direction, q) / determinant;
                if (v < -1e-6 || u + v > 1 + 1e-6) continue;
                float distance = (float)(Vector3.Dot(ac, q) / determinant);
                if (distance < 0 || distance > maximumDistance) continue;
                var bary = new Vector3((float)(1 - u - v), (float)u, (float)v);
                if (!accept(triangle, bary)) continue;
                maximumDistance = distance; closest = new(triangle, bary, distance * distance);
            }
        }
    }

    private static float Component(Vector3 value, int axis) => axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;
    private static float BoxDistance(Vector3 point, Vector3 min, Vector3 max) => Vector3.DistanceSquared(point, Vector3.Clamp(point, min, max));

    // Voronoi-region closest-point test from Real-Time Collision Detection, chapter 5.
    internal static Vector3 ClosestBarycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return new(1, 0, 0);
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return new(0, 1, 0);
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) { float v = d1 / (d1 - d3); return new(1 - v, v, 0); }
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return new(0, 0, 1);
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) { float w = d2 / (d2 - d6); return new(1 - w, 0, w); }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) { float w = (d4 - d3) / (d4 - d3 + d5 - d6); return new(0, 1 - w, w); }
        float sum = va + vb + vc;
        if (MathF.Abs(sum) < 1e-25f) return new(1, 0, 0);
        float inverse = 1 / sum;
        return new(1 - vb * inverse - vc * inverse, vb * inverse, vc * inverse);
    }
}
