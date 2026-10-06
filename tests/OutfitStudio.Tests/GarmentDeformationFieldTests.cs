using System.Numerics;
using OutfitStudio.Core.Geometry;
using OutfitStudio.Core.Models;

namespace OutfitStudio.Tests;

public sealed class GarmentDeformationFieldTests
{
    [Fact]
    public void OffSurfacePositionsAndClearanceHaveNoJumpAcrossSharedReferenceEdge()
    {
        var (upper, lower) = BentPair();
        var field = new GarmentDeformationField([upper, lower]);
        const float offset = .2f, epsilon = .000001f;
        var first = Evaluate(field, upper, new(.6f - epsilon, .4f, epsilon), new(.4f, epsilon, offset), .0025f);
        var second = Evaluate(field, lower, new(.6f - epsilon, epsilon, .4f), new(.4f, -epsilon, offset), .0025f);
        Assert.True(Vector3.Distance(first, second) < .00001f);
        // The previous constant-per-face frame has a finite jump as epsilon -> 0.
        var oldUpper = Vector3.Transform(new Vector3(.4f, epsilon, offset), Matrix4x4.CreateRotationX(.35f));
        Assert.True(Vector3.Distance(oldUpper, new(.4f, -epsilon, offset)) > .06f);
        field.Evaluate(upper, new(.6f, .4f, 0), out var a, out var na);
        field.Evaluate(lower, new(.6f, 0, .4f), out var b, out var nb);
        Assert.True(MatrixDistance(a, b) < .000001f);
        Assert.True(Vector3.Distance(na, nb) < .000001f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstantPlanarFramePreservesAuthoredOffsetsAndFolds(bool affine)
    {
        // The existing unit-normal convention reproduces planar tangential
        // affine transforms and rigid motion, not arbitrary 3D normal scaling.
        var rotation = Matrix4x4.CreateRotationX(.31f) * Matrix4x4.CreateRotationZ(-.17f);
        var expected = affine ? new Matrix4x4(1.3f, .2f, 0, 0, .4f, .9f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1) * rotation : rotation;
        var translation = new Vector3(.03f, -.02f, .09f);
        SurfaceTriangle Triangle(Vector3 a, Vector3 b, Vector3 c) => new(a, b, c,
            Vector3.TransformNormal(a, expected) + translation, Vector3.TransformNormal(b, expected) + translation,
            Vector3.TransformNormal(c, expected) + translation);
        var upper = Triangle(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
        var lower = Triangle(Vector3.Zero, -Vector3.UnitY, Vector3.UnitX);
        var field = new GarmentDeformationField([upper, lower]);
        Vector3[] folds = [new(.2f, .2f, .07f), new(.3f, -.2f, .15f), new(.35f, .1f, -.04f), new(.45f, -.1f, .02f)];
        var converted = new List<Vector3>();
        foreach (var point in folds)
        {
            var triangle = point.Y >= 0 ? upper : lower;
            var bary = TriangleIndex.ClosestBarycentric(point, triangle.A, triangle.B, triangle.C);
            var actual = Evaluate(field, triangle, bary, point, 0);
            Assert.True(Vector3.Distance(actual, Vector3.TransformNormal(point, expected) + translation) < .000002f);
            converted.Add(actual);
        }
        if (!affine)
            for (int i = 1; i < folds.Length; i++)
                Assert.Equal(Vector3.Distance(folds[i - 1], folds[i]), Vector3.Distance(converted[i - 1], converted[i]), 5);
    }

    [Fact]
    public void SurfacePointsRetainExactBarycentricCorrespondence()
    {
        var (upper, lower) = BentPair();
        var field = new GarmentDeformationField([upper, lower]);
        foreach (var triangle in new[] { upper, lower })
        foreach (var bary in new[] { Vector3.UnitX, new Vector3(.2f, .3f, .5f), new Vector3(.5f, .5f, 0) })
            Assert.Equal(triangle.SampleTarget(bary), Evaluate(field, triangle, bary, triangle.Sample(bary), 0));
    }

    [Fact]
    public void RoundedUvSplitsShareAFrameAcrossNeighboringWeldCells()
    {
        var (upper, lower) = BentPair(new(-.0000004f, 0, 0), new(-.0000004f, 0, 0));
        var field = new GarmentDeformationField([upper, lower]);
        field.Evaluate(upper, new(.5f, .5f, 0), out var a, out var na);
        field.Evaluate(lower, new(.5f, 0, .5f), out var b, out var nb);
        Assert.True(MatrixDistance(a, b) < .000001f);
        Assert.True(Vector3.Distance(na, nb) < .000001f);
    }

    [Fact]
    public void CoincidentSourcePointsWithDifferentTargetBranchesDoNotWeld()
    {
        var (upper, lower) = BentPair(default, new(0, 0, .000002f));
        var field = new GarmentDeformationField([upper, lower]);
        field.Evaluate(upper, new(.5f, .5f, 0), out var a, out var na);
        field.Evaluate(lower, new(.5f, 0, .5f), out var b, out var nb);
        Assert.True(MatrixDistance(a, b) > .2f);
        Assert.True(Vector3.Distance(na, nb) > .2f);
    }

    [Theory]
    [InlineData("/body.mtrl")]
    [InlineData("/dress.mtrl")]
    public void NeighborFramesDoNotChangeSkinOrCloseGarmentDetail(string material)
    {
        var (upper, lower) = BentPair();
        var fixture = WithPositions(material, [new(.1f, .1f, .01f), new(.6f, .1f, .01f), new(.1f, .6f, .01f)]);
        var isolated = new PreparedConversion(new TriangleIndex([upper]), new() { Clearance = .0025f }, "test", [], ["/body.mtrl"]);
        var withNeighbor = new PreparedConversion(new TriangleIndex([upper, lower]), new() { Clearance = .0025f }, "test", [], ["/body.mtrl"]);
        Assert.Equal(isolated.Convert(fixture.Bytes).ModelData, withNeighbor.Convert(fixture.Bytes).ModelData);
    }

    [Fact]
    public void IdentityGarmentConversionPreservesEveryByteIncludingShapeVertices()
    {
        var triangle = new SurfaceTriangle(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
        var fixture = GeometryTests.Fixture.Create(shape: true, offset: new(0, 0, .15f), material: "/dress.mtrl");
        var prepared = new PreparedConversion(new TriangleIndex([triangle]), new() { Clearance = 0 }, "test", [], ["/body.mtrl"]);
        Assert.Equal(fixture.Bytes, prepared.Convert(fixture.Bytes).ModelData);
    }

    [Fact]
    public void ActuallyUsedAmbiguousRotationFailsClosed()
    {
        // Equal rotations of 180 degrees about three orthogonal axes have no
        // unique mean rotation. Do not silently choose a discontinuous face.
        SurfaceTriangle Triangle(Vector3 a, Vector3 b) => new(Vector3.Zero, a, b, Vector3.Zero, -a, -b);
        var surface = new TriangleIndex([Triangle(Vector3.UnitX, Vector3.UnitY), Triangle(Vector3.UnitY, Vector3.UnitZ), Triangle(Vector3.UnitZ, Vector3.UnitX)]);
        var fixture = WithPositions("/dress.mtrl", [new(-.1f, -.1f, -.1f), new(-.2f, -.1f, -.1f), new(-.1f, -.2f, -.1f)]);
        var prepared = new PreparedConversion(surface, new() { Clearance = 0, MaximumDistance = .5f }, "test", [], ["/body.mtrl"]);
        var error = Assert.Throws<ModelConversionException>(() => prepared.Convert(fixture.Bytes));
        Assert.Contains("rotation is ambiguous", error.Message);
    }

    [Fact]
    public void UnequalOrthogonalRotationsRetainPositiveTransportDespiteNegativeMatrixMean()
    {
        SurfaceTriangle Triangle(Vector3 a, Vector3 b, float area) => new(Vector3.Zero, a * area, b,
            Vector3.Zero, -a * area, -b);
        var triangles = new[] { Triangle(Vector3.UnitY, Vector3.UnitZ, .4f),
            Triangle(Vector3.UnitZ, Vector3.UnitX, .35f), Triangle(Vector3.UnitX, Vector3.UnitY, .25f) };
        var field = new GarmentDeformationField(triangles);
        field.Evaluate(triangles[0], Vector3.UnitX, out var transform, out _);
        // Arithmetic matrix averaging yields diag(-.2,-.3,-.5), determinant -.03.
        Assert.True(LinearTransform.TryInvert(transform, out _, out double determinant));
        Assert.True(determinant > .99999);
        Assert.True(MatrixDistance(transform, Matrix4x4.CreateRotationX(MathF.PI)) < .00001f);
    }

    [Fact]
    public void RotationsStraddlingHalfTurnHaveContinuousSharedEdgeAndIgnoreQuaternionSign()
    {
        var firstRotation = Matrix4x4.CreateRotationX(MathF.PI - .03f);
        var secondRotation = Matrix4x4.CreateRotationX(-MathF.PI + .03f);
        var upper = new SurfaceTriangle(Vector3.Zero, Vector3.UnitX, Vector3.UnitY,
            Vector3.Zero, Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitY, firstRotation));
        var lower = new SurfaceTriangle(Vector3.Zero, -Vector3.UnitY, Vector3.UnitX,
            Vector3.Zero, Vector3.TransformNormal(-Vector3.UnitY, secondRotation), Vector3.UnitX);
        var field = new GarmentDeformationField([upper, lower]);
        field.Evaluate(upper, new(.6f, .4f, 0), out var a, out var normalA);
        field.Evaluate(lower, new(.6f, 0, .4f), out var b, out var normalB);
        Assert.True(MatrixDistance(a, b) < .000001f);
        Assert.True(MatrixDistance(a, Matrix4x4.CreateRotationX(MathF.PI)) < .000001f);
        Assert.True(Vector3.Distance(normalA, normalB) < .000001f);
        var q = Quaternion.CreateFromRotationMatrix(firstRotation);
        Assert.Equal(PolarTransport.Scatter(q), PolarTransport.Scatter(-q));
    }

    [Fact]
    public void PolarDecompositionRetainsNoncommutingStretchAndRotation()
    {
        var stretch = new Matrix4x4(1.3f, .2f, .1f, 0, .2f, .9f, .05f, 0, .1f, .05f, 1.1f, 0, 0, 0, 0, 1);
        foreach (float angle in new[] { 0, .7f, MathF.PI - .0001f, -MathF.PI + .0001f })
        {
            var expected = Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(new(1, 2, 3)), angle) * stretch;
            PolarTransport.Decompose(expected, out var actualStretch, out var scatter);
            Assert.True(MatrixDistance(stretch, actualStretch) < .000002f);
            Assert.True(MatrixDistance(expected, PolarTransport.Compose(actualStretch, scatter)) < .000003f);
        }
    }

    private static (SurfaceTriangle Upper, SurfaceTriangle Lower) BentPair(Vector3 lowerSourceShift = default, Vector3 lowerTargetShift = default)
    {
        var rotation = Matrix4x4.CreateRotationX(.35f);
        var upper = new SurfaceTriangle(Vector3.Zero, Vector3.UnitX, Vector3.UnitY,
            Vector3.Zero, Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitY, rotation));
        var lower = new SurfaceTriangle(lowerSourceShift, -Vector3.UnitY + lowerSourceShift, Vector3.UnitX + lowerSourceShift,
            lowerTargetShift, -Vector3.UnitY + lowerTargetShift, Vector3.UnitX + lowerTargetShift);
        return (upper, lower);
    }

    private static Vector3 Evaluate(GarmentDeformationField field, SurfaceTriangle triangle, Vector3 bary, Vector3 point, float clearance)
    {
        field.Evaluate(triangle, bary, out var transform, out var normal);
        return triangle.SampleTarget(bary) + Vector3.TransformNormal(point - triangle.Sample(bary), transform) + clearance * normal;
    }

    private static float MatrixDistance(Matrix4x4 a, Matrix4x4 b)
        => Vector3.Distance(new(a.M11, a.M12, a.M13), new(b.M11, b.M12, b.M13))
            + Vector3.Distance(new(a.M21, a.M22, a.M23), new(b.M21, b.M22, b.M23))
            + Vector3.Distance(new(a.M31, a.M32, a.M33), new(b.M31, b.M32, b.M33));

    private static GeometryTests.Fixture WithPositions(string material, Vector3[] positions)
    {
        var fixture = GeometryTests.Fixture.Create(material: material);
        for (int i = 0; i < positions.Length; i++)
        {
            int address = fixture.PositionOffsets[i];
            BitConverter.TryWriteBytes(fixture.Bytes.AsSpan(address), positions[i].X);
            BitConverter.TryWriteBytes(fixture.Bytes.AsSpan(address + 4), positions[i].Y);
            BitConverter.TryWriteBytes(fixture.Bytes.AsSpan(address + 8), positions[i].Z);
        }
        return fixture;
    }
}
