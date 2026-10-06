using System.Numerics;

namespace OutfitStudio.Core.Geometry;

/// <summary>Scalar reciprocal bases for the linear part of a surface frame.</summary>
internal static class LinearTransform
{
    // These frames have no translation or projection. Their inverse needs only
    // nine cofactors, rather than the general SIMD 4x4 inverse. Use doubles for
    // the scalar cross/dot products so small body triangles retain their area.
    // The general Matrix4x4 inverse intermittently rejected a valid frame under
    // optimized Windows/Wine execution; keep this arithmetic platform-neutral.
    public static bool TryInvert(in Matrix4x4 matrix, out Matrix4x4 inverse, out double determinant)
    {
        double a = matrix.M11, b = matrix.M12, c = matrix.M13;
        double d = matrix.M21, e = matrix.M22, f = matrix.M23;
        double g = matrix.M31, h = matrix.M32, i = matrix.M33;
        double c11 = e * i - f * h, c12 = f * g - d * i, c13 = d * h - e * g;
        determinant = a * c11 + b * c12 + c * c13;
        inverse = default;
        if (!double.IsFinite(determinant) || determinant == 0
            || matrix.M14 != 0 || matrix.M24 != 0 || matrix.M34 != 0
            || matrix.M41 != 0 || matrix.M42 != 0 || matrix.M43 != 0 || matrix.M44 != 1)
            return false;
        double reciprocal = 1 / determinant;
        inverse = new(
            (float)(c11 * reciprocal), (float)((c * h - b * i) * reciprocal), (float)((b * f - c * e) * reciprocal), 0,
            (float)(c12 * reciprocal), (float)((a * i - c * g) * reciprocal), (float)((c * d - a * f) * reciprocal), 0,
            (float)(c13 * reciprocal), (float)((b * g - a * h) * reciprocal), (float)((a * e - b * d) * reciprocal), 0,
            0, 0, 0, 1);
        return float.IsFinite(inverse.M11) && float.IsFinite(inverse.M12) && float.IsFinite(inverse.M13)
            && float.IsFinite(inverse.M21) && float.IsFinite(inverse.M22) && float.IsFinite(inverse.M23)
            && float.IsFinite(inverse.M31) && float.IsFinite(inverse.M32) && float.IsFinite(inverse.M33);
    }
}
