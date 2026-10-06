using System.Numerics;

namespace OutfitStudio.Core.Geometry;

/// <summary>Weighted least-squares proper rotation and translation, without scaling or reflection.</summary>
internal static class RigidAlignment
{
    internal static Matrix4x4 Fit(IReadOnlyList<Vector3> source, IReadOnlyList<Vector3> target,
        IReadOnlyList<float>? weights = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (source.Count == 0 || source.Count != target.Count || weights is not null && weights.Count != source.Count)
            throw new ArgumentException("Rigid alignment needs equally sized, nonempty point and weight lists.");

        double total = 0;
        int anchor = -1;
        for (int i = 0; i < source.Count; i++)
        {
            if (!Finite(source[i]) || !Finite(target[i]))
                throw new ArgumentException("Rigid alignment points must be finite.");
            double weight = weights?[i] ?? 1;
            if (!double.IsFinite(weight) || weight < 0)
                throw new ArgumentException("Rigid alignment weights must be finite and nonnegative.", nameof(weights));
            total += weight;
            if (anchor < 0 && weight > 0) anchor = i;
        }
        if (anchor < 0)
            throw new ArgumentException("Rigid alignment needs a positive total weight.", nameof(weights));

        // Accumulate offsets from a real point rather than large absolute coordinates.
        // Identical points then have an exactly zero centered position, even with weights.
        var sourceMean = Mean(source);
        var targetMean = Mean(target);
        var covariance = new double[3, 3];
        for (int i = 0; i < source.Count; i++)
        {
            double weight = weights?[i] ?? 1;
            if (weight == 0) continue;
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 3; column++)
                    covariance[row, column] += weight * (Coordinate(source[i], row) - sourceMean[row])
                        * (Coordinate(target[i], column) - targetMean[column]);
        }

        double scale = covariance.Cast<double>().Max(Math.Abs);
        double[] quaternion = [1, 0, 0, 0]; // Scalar first.
        if (scale > 0)
        {
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 3; column++) covariance[row, column] /= scale;

            var gram = new double[3, 3];
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 3; column++)
                    for (int k = 0; k < 3; k++) gram[row, column] += covariance[row, k] * covariance[column, k];
            var (values, vectors) = Eigensystem(gram);
            var order = Enumerable.Range(0, 3).OrderByDescending(i => values[i]).ToArray();
            if (values[order[1]] <= values[order[0]] * 1e-12)
            {
                // A line leaves rotation about that line unconstrained. Choose its
                // shortest arc, rather than an arbitrary twist from an eigensolver.
                var from = Enumerable.Range(0, 3).Select(i => vectors[i, order[0]]).ToArray();
                int signIndex = Enumerable.Range(0, 3).OrderByDescending(i => Math.Abs(from[i])).First();
                if (from[signIndex] < 0) for (int i = 0; i < 3; i++) from[i] = -from[i];
                var to = new double[3];
                for (int column = 0; column < 3; column++)
                    for (int row = 0; row < 3; row++) to[column] += from[row] * covariance[row, column];
                quaternion = ShortestArc(from, to);
            }
            else
            {
                double xx = covariance[0, 0], xy = covariance[0, 1], xz = covariance[0, 2];
                double yx = covariance[1, 0], yy = covariance[1, 1], yz = covariance[1, 2];
                double zx = covariance[2, 0], zy = covariance[2, 1], zz = covariance[2, 2];
                var horn = new double[,]
                {
                    { xx + yy + zz, yz - zy, zx - xz, xy - yx },
                    { yz - zy, xx - yy - zz, xy + yx, zx + xz },
                    { zx - xz, xy + yx, -xx + yy - zz, yz + zy },
                    { xy - yx, zx + xz, yz + zy, -xx - yy + zz },
                };
                var (eigenvalues, eigenvectors) = Eigensystem(horn);
                int best = Enumerable.Range(0, 4).OrderByDescending(i => eigenvalues[i]).First();
                quaternion = Enumerable.Range(0, 4).Select(i => eigenvectors[i, best]).ToArray();
            }
        }

        Normalize(quaternion);
        double w = quaternion[0], x = quaternion[1], y = quaternion[2], z = quaternion[3];
        // System.Numerics transforms row vectors: this is the transpose of the
        // conventional column-vector quaternion rotation matrix.
        var rotation = new double[,]
        {
            { 1 - 2 * (y * y + z * z), 2 * (x * y + w * z), 2 * (x * z - w * y) },
            { 2 * (x * y - w * z), 1 - 2 * (x * x + z * z), 2 * (y * z + w * x) },
            { 2 * (x * z + w * y), 2 * (y * z - w * x), 1 - 2 * (x * x + y * y) },
        };
        var translation = new double[3];
        for (int column = 0; column < 3; column++)
        {
            translation[column] = targetMean[column];
            for (int row = 0; row < 3; row++) translation[column] -= sourceMean[row] * rotation[row, column];
        }
        return new(
            Single(rotation[0, 0]), Single(rotation[0, 1]), Single(rotation[0, 2]), 0,
            Single(rotation[1, 0]), Single(rotation[1, 1]), Single(rotation[1, 2]), 0,
            Single(rotation[2, 0]), Single(rotation[2, 1]), Single(rotation[2, 2]), 0,
            Single(translation[0]), Single(translation[1]), Single(translation[2]), 1);

        double[] Mean(IReadOnlyList<Vector3> points)
        {
            var mean = new double[3];
            for (int i = 0; i < points.Count; i++)
                for (int axis = 0; axis < 3; axis++)
                    mean[axis] += (weights?[i] ?? 1) * (Coordinate(points[i], axis) - Coordinate(points[anchor], axis));
            for (int axis = 0; axis < 3; axis++) mean[axis] = Coordinate(points[anchor], axis) + mean[axis] / total;
            return mean;
        }
    }

    private static double[] ShortestArc(double[] from, double[] to)
    {
        Normalize(from); Normalize(to);
        double dot = Math.Clamp(from.Zip(to, (a, b) => a * b).Sum(), -1, 1);
        var cross = Cross(from, to);
        if (cross.Sum(value => value * value) < 1e-28)
        {
            if (dot >= 0) return [1, 0, 0, 0];
            // Opposite directions have many equally short half-turns. Pick the
            // least aligned coordinate axis, with stable X/Y/Z tie breaking.
            var basis = new double[3];
            basis[Enumerable.Range(0, 3).OrderBy(i => Math.Abs(from[i])).First()] = 1;
            cross = Cross(from, basis);
            Normalize(cross);
            return [0, cross[0], cross[1], cross[2]];
        }
        var result = new[] { 1 + dot, cross[0], cross[1], cross[2] };
        Normalize(result);
        return result;
    }

    private static double[] Cross(double[] a, double[] b)
        => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];

    private static void Normalize(double[] values)
    {
        double length = Math.Sqrt(values.Sum(value => value * value));
        for (int i = 0; i < values.Length; i++) values[i] /= length;
    }

    // Cyclic-free Jacobi iteration on tiny symmetric matrices (3x3 or 4x4).
    // Picking the largest remaining entry gives deterministic tie breaking and
    // avoids the zero-eigenvector failure of power iteration at half-turns.
    private static (double[] Values, double[,] Vectors) Eigensystem(double[,] input)
    {
        int count = input.GetLength(0);
        var matrix = (double[,])input.Clone();
        var vectors = new double[count, count];
        for (int i = 0; i < count; i++) vectors[i, i] = 1;
        double scale = matrix.Cast<double>().Max(Math.Abs);
        for (int iteration = 0; iteration < 128; iteration++)
        {
            int p = 0, q = 1;
            double largest = 0;
            for (int row = 0; row < count; row++)
                for (int column = row + 1; column < count; column++)
                    if (Math.Abs(matrix[row, column]) > largest)
                    { largest = Math.Abs(matrix[row, column]); p = row; q = column; }
            if (largest <= scale * 1e-14) break;
            double offDiagonal = matrix[p, q];
            double tau = (matrix[q, q] - matrix[p, p]) / (2 * offDiagonal);
            double tangent = Math.CopySign(1, tau) / (Math.Abs(tau) + Math.Sqrt(1 + tau * tau));
            double cosine = 1 / Math.Sqrt(1 + tangent * tangent), sine = tangent * cosine;
            matrix[p, p] -= tangent * offDiagonal;
            matrix[q, q] += tangent * offDiagonal;
            matrix[p, q] = matrix[q, p] = 0;
            for (int row = 0; row < count; row++)
            {
                if (row != p && row != q)
                {
                    double beforeP = matrix[row, p], beforeQ = matrix[row, q];
                    matrix[row, p] = matrix[p, row] = cosine * beforeP - sine * beforeQ;
                    matrix[row, q] = matrix[q, row] = sine * beforeP + cosine * beforeQ;
                }
                double vectorP = vectors[row, p], vectorQ = vectors[row, q];
                vectors[row, p] = cosine * vectorP - sine * vectorQ;
                vectors[row, q] = sine * vectorP + cosine * vectorQ;
            }
        }
        return (Enumerable.Range(0, count).Select(i => matrix[i, i]).ToArray(), vectors);
    }

    private static float Single(double value)
    {
        float result = (float)value;
        if (!float.IsFinite(result)) throw new InvalidOperationException("The rigid alignment cannot be represented by a finite transform.");
        return result;
    }
    private static double Coordinate(Vector3 value, int axis) => axis switch { 0 => value.X, 1 => value.Y, _ => value.Z };
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
