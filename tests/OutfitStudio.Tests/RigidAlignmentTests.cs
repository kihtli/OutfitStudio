using System.Numerics;
using OutfitStudio.Core.Geometry;

namespace OutfitStudio.Tests;

public sealed class RigidAlignmentTests
{
    private static readonly Vector3[] Cloud = [new(-1, -2, -3), new(2, -1, 1), new(-2, 3, 1), new(3, 1, -2), new(1, 2, 4)];

    [Theory]
    [InlineData(0)]
    [InlineData(0.3f)]
    [InlineData(1.57079632679f)]
    [InlineData(3.14159265359f)]
    public void RecoversRotationAndTranslationIncludingHalfTurns(float angle)
    {
        var expected = Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(new(1, -2, 3)), angle)
            * Matrix4x4.CreateTranslation(8, -3, 7);
        var target = Cloud.Select(point => Vector3.Transform(point, expected)).ToArray();
        var actual = RigidAlignment.Fit(Cloud, target);
        AssertProper(actual);
        for (int i = 0; i < Cloud.Length; i++) AssertNear(target[i], Vector3.Transform(Cloud[i], actual));
    }

    [Fact]
    public void WeightedFitIgnoresAZeroWeightOutlier()
    {
        var expected = Matrix4x4.CreateRotationY(0.75f) * Matrix4x4.CreateTranslation(-4, 2, 6);
        var target = Cloud.Select(point => Vector3.Transform(point, expected)).ToArray();
        target[^1] = new(1000, -500, 300);
        var actual = RigidAlignment.Fit(Cloud, target, [2, 5, 1, 3, 0]);
        AssertProper(actual);
        for (int i = 0; i < Cloud.Length - 1; i++) AssertNear(target[i], Vector3.Transform(Cloud[i], actual));
    }

    [Fact]
    public void AnisotropicStretchFindsClosestProperRotationWithoutResizing()
    {
        Vector3[] source = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        var rigid = Matrix4x4.CreateRotationZ(0.6f) * Matrix4x4.CreateRotationX(-0.9f) * Matrix4x4.CreateTranslation(5, 6, 7);
        var target = source.Select(point => Vector3.Transform(point * new Vector3(2, 0.5f, 3), rigid)).ToArray();
        var fit = RigidAlignment.Fit(source, target);
        AssertProper(fit);
        for (int i = 0; i < source.Length; i++)
        {
            AssertNear(Vector3.Transform(source[i], rigid), Vector3.Transform(source[i], fit));
            for (int j = 0; j < source.Length; j++)
                Assert.InRange(MathF.Abs(Vector3.Distance(source[i], source[j])
                    - Vector3.Distance(Vector3.Transform(source[i], fit), Vector3.Transform(source[j], fit))), 0, 1e-5f);
        }
    }

    [Fact]
    public void MirroredTargetNeverCreatesAReflection()
    {
        Vector3[] source = [new(-1, -1, -1), new(-1, 1, 1), new(1, -1, 1), new(1, 1, -1)];
        var target = source.Select(point => new Vector3(-point.X, point.Y, point.Z)).ToArray();
        var fit = RigidAlignment.Fit(source, target);
        AssertProper(fit);
        float error = source.Select((point, index) => Vector3.DistanceSquared(Vector3.Transform(point, fit), target[index])).Sum();
        Assert.InRange(error, 15.9999f, 16.0001f);
    }

    [Fact]
    public void PlanarPointsRecoverTheirRotation()
    {
        Vector3[] source = [new(-1, -1, 0), new(-1, 1, 0), new(1, -1, 0), new(2, 3, 0)];
        var rotation = Matrix4x4.CreateRotationX(MathF.PI) * Matrix4x4.CreateRotationZ(0.5f) * Matrix4x4.CreateTranslation(1, 2, 3);
        var target = source.Select(point => Vector3.Transform(point, rotation)).ToArray();
        var fit = RigidAlignment.Fit(source, target);
        AssertProper(fit);
        for (int i = 0; i < source.Length; i++) AssertNear(target[i], Vector3.Transform(source[i], fit));
    }

    [Fact]
    public void LineUsesShortestArcWithoutAnArbitraryTwist()
    {
        Vector3[] source = [new(-2, 0, 0), Vector3.Zero, new(3, 0, 0)];
        var target = source.Select(point => new Vector3(4, point.X + 5, 6)).ToArray();
        var fit = RigidAlignment.Fit(source, target, [1, 3, 2]);
        AssertProper(fit);
        AssertNear(Vector3.UnitZ, Vector3.TransformNormal(Vector3.UnitZ, fit));
        AssertNear(Vector3.UnitY, Vector3.TransformNormal(Vector3.UnitX, fit));
        for (int i = 0; i < source.Length; i++) AssertNear(target[i], Vector3.Transform(source[i], fit));
    }

    [Fact]
    public void OppositeLineChoosesADeterministicHalfTurn()
    {
        Vector3[] source = [new(-1, 0, 0), new(1, 0, 0)];
        var target = source.Select(point => -point).ToArray();
        var fit = RigidAlignment.Fit(source, target);
        Assert.Equal(fit, RigidAlignment.Fit(source, target));
        AssertProper(fit);
        AssertNear(-Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitX, fit));
        AssertNear(Vector3.UnitZ, Vector3.TransformNormal(Vector3.UnitZ, fit));
    }

    [Fact]
    public void ARepeatedPointKeepsIdentityRotationAndTranslatesTheWeightedCenter()
    {
        Vector3[] source = [new(3, 5, 7), new(3, 5, 7), new(3, 5, 7)];
        Vector3[] target = [new(4, 7, 9), new(4, 7, 9), new(4, 7, 9)];
        var fit = RigidAlignment.Fit(source, target, [0.1f, 0.2f, 0.3f]);
        Assert.Equal(Matrix4x4.CreateTranslation(1, 2, 2), fit);
        var single = RigidAlignment.Fit([new(1, 2, 3)], [new(-2, 1, 4)]);
        Assert.Equal(Matrix4x4.CreateTranslation(-3, -1, 1), single);
    }

    [Fact]
    public void CollapsedTargetKeepsIdentityRotationAndAlignsWeightedCenters()
    {
        var target = Enumerable.Repeat(new Vector3(2, -4, 6), Cloud.Length).ToArray();
        float[] weights = [1, 2, 3, 4, 5];
        var fit = RigidAlignment.Fit(Cloud, target, weights);
        Assert.Equal(Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitX, fit));
        Assert.Equal(Vector3.UnitY, Vector3.TransformNormal(Vector3.UnitY, fit));
        Assert.Equal(Vector3.UnitZ, Vector3.TransformNormal(Vector3.UnitZ, fit));
        var center = Cloud.Select((point, index) => point * weights[index]).Aggregate(Vector3.Zero, (a, b) => a + b) / weights.Sum();
        AssertNear(target[0], Vector3.Transform(center, fit));
    }

    [Fact]
    public void UniformlyScalingWeightsDoesNotChangeTheFit()
    {
        var target = Cloud.Select(point => point * new Vector3(2, 0.6f, 1.2f) + new Vector3(2, 4, -3)).ToArray();
        var first = RigidAlignment.Fit(Cloud, target, [1, 3, 2, 4, 1]);
        var second = RigidAlignment.Fit(Cloud, target, [100, 300, 200, 400, 100]);
        foreach (var point in Cloud) AssertNear(Vector3.Transform(point, first), Vector3.Transform(point, second));
    }

    [Fact]
    public void InvalidInputIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => RigidAlignment.Fit(null!, [Vector3.Zero]));
        Assert.Throws<ArgumentNullException>(() => RigidAlignment.Fit([Vector3.Zero], null!));
        Assert.Throws<ArgumentException>(() => RigidAlignment.Fit([], []));
        Assert.Throws<ArgumentException>(() => RigidAlignment.Fit([Vector3.Zero], []));
        Assert.Throws<ArgumentException>(() => RigidAlignment.Fit([Vector3.Zero], [Vector3.Zero], []));
        Assert.Throws<ArgumentException>(() => RigidAlignment.Fit([Vector3.Zero], [Vector3.Zero], [-1]));
        Assert.Throws<ArgumentException>(() => RigidAlignment.Fit([Vector3.Zero], [Vector3.Zero], [0]));
        Assert.Throws<ArgumentException>(() => RigidAlignment.Fit([Vector3.Zero], [Vector3.Zero], [float.NaN]));
        Assert.Throws<ArgumentException>(() => RigidAlignment.Fit([Vector3.Zero], [Vector3.Zero], [float.PositiveInfinity]));
        Assert.Throws<ArgumentException>(() => RigidAlignment.Fit([new(float.NaN, 0, 0)], [Vector3.Zero]));
        Assert.Throws<ArgumentException>(() => RigidAlignment.Fit([Vector3.Zero], [new(0, float.PositiveInfinity, 0)]));
    }

    private static void AssertProper(Matrix4x4 matrix)
    {
        Assert.InRange(matrix.GetDeterminant(), 0.99999f, 1.00001f);
        var x = Vector3.TransformNormal(Vector3.UnitX, matrix);
        var y = Vector3.TransformNormal(Vector3.UnitY, matrix);
        var z = Vector3.TransformNormal(Vector3.UnitZ, matrix);
        Assert.InRange(MathF.Abs(x.LengthSquared() - 1), 0, 1e-5f);
        Assert.InRange(MathF.Abs(y.LengthSquared() - 1), 0, 1e-5f);
        Assert.InRange(MathF.Abs(z.LengthSquared() - 1), 0, 1e-5f);
        Assert.InRange(MathF.Abs(Vector3.Dot(x, y)), 0, 1e-5f);
        Assert.InRange(MathF.Abs(Vector3.Dot(x, z)), 0, 1e-5f);
        Assert.InRange(MathF.Abs(Vector3.Dot(y, z)), 0, 1e-5f);
    }
    private static void AssertNear(Vector3 expected, Vector3 actual)
        => Assert.InRange(Vector3.Distance(expected, actual), 0, 2e-5f);
}
