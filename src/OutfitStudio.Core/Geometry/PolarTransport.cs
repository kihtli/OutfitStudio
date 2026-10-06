using System.Numerics;

namespace OutfitStudio.Core.Geometry;

/// <summary>
/// Interpolates proper rotations and positive stretches separately. Quaternion
/// scatter matrices do not depend on the sign chosen for a quaternion, including
/// rotations that straddle 180 degrees.
/// </summary>
internal static class PolarTransport
{
    public static void Decompose(Matrix4x4 transform, out Matrix4x4 stretch, out Matrix4x4 rotationScatter)
    {
        if (!LinearTransform.TryInvert(transform, out _, out double determinant) || determinant <= 0)
            throw MdlDocument.Error("The garment reference transport is not orientation preserving.");
        Span<double> j = stackalloc double[9]
        {
            transform.M11, transform.M12, transform.M13,
            transform.M21, transform.M22, transform.M23,
            transform.M31, transform.M32, transform.M33,
        };
        Span<double> gram = stackalloc double[9];
        for (int row = 0; row < 3; row++)
        for (int column = 0; column < 3; column++)
            gram[row * 3 + column] = j[row] * j[column] + j[3 + row] * j[3 + column] + j[6 + row] * j[6 + column];
        Span<double> eigenvalues = stackalloc double[3];
        Span<double> eigenvectors = stackalloc double[9];
        Diagonalize(gram, 3, eigenvalues, eigenvectors);
        Span<double> p = stackalloc double[9];
        Span<double> inverseP = stackalloc double[9];
        p.Clear();
        inverseP.Clear();
        for (int axis = 0; axis < 3; axis++)
        {
            if (!double.IsFinite(eigenvalues[axis]) || eigenvalues[axis] <= 0)
                throw MdlDocument.Error("The garment reference stretch cannot be decomposed safely.");
            double value = Math.Sqrt(eigenvalues[axis]);
            for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
            {
                double product = eigenvectors[row * 3 + axis] * eigenvectors[column * 3 + axis];
                p[row * 3 + column] += product * value;
                inverseP[row * 3 + column] += product / value;
            }
        }
        Span<double> r = stackalloc double[9];
        for (int row = 0; row < 3; row++)
        for (int column = 0; column < 3; column++)
            r[row * 3 + column] = j[row * 3] * inverseP[column]
                + j[row * 3 + 1] * inverseP[3 + column] + j[row * 3 + 2] * inverseP[6 + column];
        stretch = Matrix(p);
        var rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Matrix(r)));
        rotationScatter = Scatter(rotation);
    }

    internal static Matrix4x4 Scatter(Quaternion quaternion)
    {
        var q = Quaternion.Normalize(quaternion);
        return new(q.X * q.X, q.X * q.Y, q.X * q.Z, q.X * q.W,
            q.Y * q.X, q.Y * q.Y, q.Y * q.Z, q.Y * q.W,
            q.Z * q.X, q.Z * q.Y, q.Z * q.Z, q.Z * q.W,
            q.W * q.X, q.W * q.Y, q.W * q.Z, q.W * q.W);
    }

    public static Matrix4x4 Compose(Matrix4x4 stretch, Matrix4x4 scatter)
    {
        Span<double> values = stackalloc double[16]
        {
            scatter.M11, scatter.M12, scatter.M13, scatter.M14,
            scatter.M21, scatter.M22, scatter.M23, scatter.M24,
            scatter.M31, scatter.M32, scatter.M33, scatter.M34,
            scatter.M41, scatter.M42, scatter.M43, scatter.M44,
        };
        Span<double> eigenvalues = stackalloc double[4];
        Span<double> eigenvectors = stackalloc double[16];
        Diagonalize(values, 4, eigenvalues, eigenvectors);
        int largest = 0, second = 1;
        if (eigenvalues[second] > eigenvalues[largest]) (largest, second) = (second, largest);
        for (int index = 2; index < 4; index++)
            if (eigenvalues[index] > eigenvalues[largest]) (largest, second) = (index, largest);
            else if (eigenvalues[index] > eigenvalues[second]) second = index;
        // A repeated principal eigenvalue has no unique rotation. Refuse the
        // ambiguity instead of choosing a discontinuous face or quaternion arc.
        if (!double.IsFinite(eigenvalues[largest]) || eigenvalues[largest] <= 0
            || eigenvalues[largest] - eigenvalues[second] <= 1e-6 * eigenvalues[largest])
            throw MdlDocument.Error("The interpolated garment rotation is ambiguous; the body correspondence has conflicting rotations.");
        var rotation = Quaternion.Normalize(new((float)eigenvectors[largest], (float)eigenvectors[4 + largest],
            (float)eigenvectors[8 + largest], (float)eigenvectors[12 + largest]));
        return Matrix4x4.CreateFromQuaternion(rotation) * stretch;
    }

    private static Matrix4x4 Matrix(ReadOnlySpan<double> values)
        => new((float)values[0], (float)values[1], (float)values[2], 0,
            (float)values[3], (float)values[4], (float)values[5], 0,
            (float)values[6], (float)values[7], (float)values[8], 0, 0, 0, 0, 1);

    // Cyclic Jacobi for the small real symmetric matrices above. Double scalar
    // arithmetic avoids dependency on platform-specific SIMD/eigen libraries.
    private static void Diagonalize(Span<double> matrix, int size, Span<double> eigenvalues, Span<double> eigenvectors)
    {
        eigenvectors.Clear();
        for (int index = 0; index < size; index++) eigenvectors[index * size + index] = 1;
        for (int sweep = 0; sweep < 32; sweep++)
        {
            double diagonalScale = 0, offDiagonal = 0;
            for (int p = 0; p < size; p++)
            {
                diagonalScale = Math.Max(diagonalScale, Math.Abs(matrix[p * size + p]));
                for (int q = p + 1; q < size; q++) offDiagonal = Math.Max(offDiagonal, Math.Abs(matrix[p * size + q]));
            }
            if (!double.IsFinite(diagonalScale) || !double.IsFinite(offDiagonal))
                throw MdlDocument.Error("The garment transport contains non-finite rotation or stretch values.");
            if (offDiagonal <= 1e-14 * diagonalScale || offDiagonal == 0)
            {
                for (int index = 0; index < size; index++) eigenvalues[index] = matrix[index * size + index];
                return;
            }
            for (int p = 0; p < size; p++)
            for (int q = p + 1; q < size; q++)
            {
                double cross = matrix[p * size + q];
                if (cross == 0) continue;
                double angle = .5 * Math.Atan2(2 * cross, matrix[q * size + q] - matrix[p * size + p]);
                double c = Math.Cos(angle), s = Math.Sin(angle);
                double pp = matrix[p * size + p], qq = matrix[q * size + q];
                matrix[p * size + p] = c * c * pp - 2 * c * s * cross + s * s * qq;
                matrix[q * size + q] = s * s * pp + 2 * c * s * cross + c * c * qq;
                matrix[p * size + q] = matrix[q * size + p] = 0;
                for (int k = 0; k < size; k++)
                {
                    if (k != p && k != q)
                    {
                        double kp = matrix[k * size + p], kq = matrix[k * size + q];
                        matrix[k * size + p] = matrix[p * size + k] = c * kp - s * kq;
                        matrix[k * size + q] = matrix[q * size + k] = s * kp + c * kq;
                    }
                    double vp = eigenvectors[k * size + p], vq = eigenvectors[k * size + q];
                    eigenvectors[k * size + p] = c * vp - s * vq;
                    eigenvectors[k * size + q] = s * vp + c * vq;
                }
            }
        }
        throw MdlDocument.Error("The garment rotation and stretch decomposition did not converge.");
    }
}
