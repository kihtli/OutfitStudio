using System.Numerics;
using System.Text;
using OutfitStudio.Core.Geometry;
using OutfitStudio.Core.Models;

namespace OutfitStudio.Tests;

public sealed class GeometryTests
{
    [Fact]
    public void ScalarReciprocalBasisRecoversSkinnyRotatedTriangleCoordinates()
    {
        // Area stays above the body's usable-triangle threshold, but the nearly
        // parallel edges make cancellation in a general float inverse visible.
        for (int repeat = 0; repeat < 128; repeat++)
        {
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new(1, 2, 3)), repeat * 0.073f);
            var x = Vector3.Transform(new Vector3(0.003f, 0, 0), rotation);
            var y = Vector3.Transform(new Vector3(0.0029f, 0.00001f, 0), rotation);
            var normal = Vector3.Normalize(Vector3.Cross(x, y));
            var basis = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0,
                normal.X, normal.Y, normal.Z, 0, 0, 0, 0, 1);
            Assert.True(LinearTransform.TryInvert(basis, out var inverse, out double determinant));
            Assert.True(determinant > 0);
            Assert.True(Vector3.Distance(Vector3.TransformNormal(x, inverse), Vector3.UnitX) < 0.0001f);
            Assert.True(Vector3.Distance(Vector3.TransformNormal(y, inverse), Vector3.UnitY) < 0.0001f);
            var coordinates = new Vector3(0.2f, 0.7f, 0.00003f);
            var point = Vector3.TransformNormal(coordinates, basis);
            Assert.True(Vector3.Distance(Vector3.TransformNormal(point, inverse), coordinates) < 0.0001f);
        }
    }

    [Fact]
    public void ScalarLinearInverseRejectsSingularNonfiniteAndUnrepresentableFrames()
    {
        Assert.False(LinearTransform.TryInvert(Matrix4x4.CreateScale(0, 1, 1), out _, out _));
        Assert.False(LinearTransform.TryInvert(Matrix4x4.CreateScale(float.NaN, 1, 1), out _, out _));
        Assert.False(LinearTransform.TryInvert(Matrix4x4.CreateScale(float.Epsilon, 1, 1), out _, out _));
        Assert.True(LinearTransform.TryInvert(Matrix4x4.CreateScale(-2, 3, 4), out var inverse, out double determinant));
        Assert.Equal(-24, determinant);
        Assert.Equal(new Vector3(-0.5f, 1f / 3, 0.25f), Vector3.TransformNormal(Vector3.One, inverse));
    }

    [Fact]
    public void NearestSurfaceSearchRetainsAllReferenceCoordinatesAcrossRepeatedQueries()
    {
        var triangles = Enumerable.Range(0, 256).Select(i =>
        {
            var origin = new Vector3((i % 16) * 2 + 0.13f, (i / 16) * 2 + 0.27f, i * 0.017f + 0.19f);
            var a = origin; var b = origin + new Vector3(0.6f, 0.03f, 0.04f); var c = origin + new Vector3(0.04f, 0.7f, 0.09f);
            var delta = new Vector3(0.11f, -0.13f, 0.23f);
            return new SurfaceTriangle(a, b, c, a + delta, b + delta, c + delta);
        }).ToArray();
        var index = new TriangleIndex(triangles.Reverse());
        // Repeated traversal also exercises tiered/JIT-optimized nullable-hit returns.
        for (int repeat = 0; repeat < 32; repeat++)
            foreach (var expected in triangles)
            {
                var hit = index.Nearest(expected.Center, 0.1f);
                Assert.True(hit.HasValue);
                Assert.Equal(expected.A, hit.Value.Triangle.A); Assert.Equal(expected.B, hit.Value.Triangle.B); Assert.Equal(expected.C, hit.Value.Triangle.C);
                Assert.Equal(expected.TargetA, hit.Value.Triangle.TargetA); Assert.Equal(expected.TargetB, hit.Value.Triangle.TargetB); Assert.Equal(expected.TargetC, hit.Value.Triangle.TargetC);
                Assert.True(Vector3.DistanceSquared(expected.Center, hit.Value.Triangle.Sample(hit.Value.Barycentric)) < 1e-10f);
            }
    }

    [Fact]
    public void IdentityConversionPreservesEveryByte()
    {
        var body = Fixture.Create();
        var result = MdlConverter.Convert(body.Bytes, body.Bytes, body.Bytes, new());
        Assert.Equal(body.Bytes, result.ModelData);
        Assert.Equal(0, result.ConvertedVertices);
    }

    [Fact]
    public void ZeroStrengthPreservesEveryByte()
    {
        var source = Fixture.Create(); var target = Fixture.Create(offset: new(0, 0, 0.1f));
        var result = MdlConverter.Convert(source.Bytes, target.Bytes, source.Bytes, new() { Strength = 0 });
        Assert.Equal(source.Bytes, result.ModelData);
    }

    [Fact]
    public void TranslationConvertsEveryLodAndShapeVertexAndPreservesOtherBytes()
    {
        var source = Fixture.Create(); var target = Fixture.Create(offset: new(0, 0, 0.1f));
        var outfit = Fixture.Create(lods: 2, shape: true, offset: new(0, 0, 0.02f), material: "/dress.mtrl");
        byte[] original = (byte[])outfit.Bytes.Clone();
        // Keep every LOD/shape vertex inside the full-strength binding region;
        // the separate distance-support tests cover the loose outer region.
        var result = MdlConverter.Convert(source.Bytes, target.Bytes, outfit.Bytes, new() { Clearance = 0.01f, MaximumDistance = .5f });
        Assert.Equal(8, result.ConvertedVertices);
        Assert.Equal(2, MdlConverter.Inspect(result.ModelData).LodCount);
        Assert.Equal(1, MdlConverter.Inspect(result.ModelData).ShapeCount);
        Assert.Equal(original, outfit.Bytes);
        foreach (int address in outfit.PositionOffsets)
            Assert.Equal(0.11f, Float(result.ModelData, address + 8) - Float(original, address + 8), 5);
        for (int i = 0; i < original.Length; i++)
        {
            bool allowed = i >= outfit.HeaderOffset && i < outfit.HeaderOffset + 4
                || i >= outfit.BoundsOffset && i < outfit.BoundsOffset + 64
                || i >= outfit.BoundsOffset + 128 && i < outfit.BoundsOffset + 160
                || outfit.PositionOffsets.Any(p => i >= p && i < p + 28);
            if (!allowed) Assert.Equal(original[i], result.ModelData[i]);
        }
    }

    [Fact]
    public void UvCorrespondenceHandlesDifferentTriangulation()
    {
        var source = Fixture.Create(); var target = Fixture.Create(retessellated: true, offset: new(0, 0, 0.1f));
        var prepared = MdlConverter.Prepare(source.Bytes, target.Bytes, new());
        Assert.Equal("UV surface correspondence", prepared.Method);
        var output = prepared.Convert(source.Bytes);
        Assert.Equal(3, output.ConvertedVertices);
        foreach (int p in source.PositionOffsets) Assert.Equal(0.1f, Float(output.ModelData, p + 8), 5);
    }

    [Fact]
    public void AmbiguousOverlappingUvIslandsAreRejected()
    {
        var source = Fixture.Create(); var target = Fixture.Create(overlapping: true);
        var error = Assert.Throws<ModelConversionException>(() => MdlConverter.Prepare(source.Bytes, target.Bytes, new()));
        Assert.Contains("ambiguous", error.Message);
    }

    [Fact]
    public void DifferentTopologyCanBeExplicitlyDisabled()
    {
        var source = Fixture.Create(); var target = Fixture.Create(retessellated: true);
        Assert.Throws<ModelConversionException>(() => MdlConverter.Prepare(source.Bytes, target.Bytes, new() { AllowUvCorrespondence = false }));
    }

    [Fact]
    public void UnboundVerticesArePreservedAndReported()
    {
        var source = Fixture.Create(); var target = Fixture.Create(offset: new(0, 0, 0.1f));
        var outfit = Fixture.Create(shape: true, farShape: true);
        var result = MdlConverter.Convert(source.Bytes, target.Bytes, outfit.Bytes, new());
        Assert.Equal(3, result.ConvertedVertices); Assert.Equal(1, result.UnchangedVertices);
        Assert.Contains(result.Warnings, s => s.Contains("outside the maximum"));
        int address = outfit.PositionOffsets[^1];
        Assert.Equal(outfit.Bytes.AsSpan(address, 40).ToArray(), result.ModelData.AsSpan(address, 40).ToArray());
    }

    [Theory]
    [InlineData(0)] [InlineData(20)] [InlineData(70)] [InlineData(200)]
    public void TruncatedFilesAreRejected(int length)
    {
        var body = Fixture.Create();
        Assert.Throws<ModelConversionException>(() => MdlConverter.Inspect(body.Bytes[..length]));
    }

    [Fact]
    public void InvalidIndexIsRejected()
    {
        var body = Fixture.Create();
        BitConverter.GetBytes((ushort)65535).CopyTo(body.Bytes, body.IndexOffsets[0]);
        Assert.Throws<ModelConversionException>(() => MdlConverter.Inspect(body.Bytes));
    }

    [Fact]
    public void NonFiniteVertexIsRejected()
    {
        var body = Fixture.Create();
        BitConverter.GetBytes(float.NaN).CopyTo(body.Bytes, body.PositionOffsets[0]);
        Assert.Throws<ModelConversionException>(() => MdlConverter.Inspect(body.Bytes));
    }

    [Fact]
    public void CancellationIsObserved()
    {
        var body = Fixture.Create(); using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => MdlConverter.Prepare(body.Bytes, body.Bytes, new(), cts.Token));
    }

    [Fact]
    public void CombinedReferencesChooseNearestRegionAndNeverDoubleDeform()
    {
        var firstSource = Fixture.Create(); var firstTarget = Fixture.Create(offset: new(0, 0, 0.1f));
        var secondSource = Fixture.Create(offset: new(3, 0, 0)); var secondTarget = Fixture.Create(offset: new(3, 0, 0.2f));
        var first = MdlConverter.Prepare(firstSource.Bytes, firstTarget.Bytes, new());
        var second = MdlConverter.Prepare(secondSource.Bytes, secondTarget.Bytes, new());
        var combined = MdlConverter.Combine([first, second, second]);
        var result = combined.Convert(secondSource.Bytes);
        foreach (int address in secondSource.PositionOffsets) Assert.Equal(0.2f, Float(result.ModelData, address + 8), 5);
        Assert.Equal(second.Convert(secondSource.Bytes).ModelData, result.ModelData);
    }

    [Fact]
    public void CombinedReferencesRejectConflictingTargets()
    {
        var source = Fixture.Create(); var a = Fixture.Create(offset: new(0, 0, 0.1f)); var b = Fixture.Create(offset: new(0, 0, 0.2f));
        Assert.Throws<ModelConversionException>(() => MdlConverter.Combine([
            MdlConverter.Prepare(source.Bytes, a.Bytes, new()), MdlConverter.Prepare(source.Bytes, b.Bytes, new())]));
    }

    [Fact]
    public void NonUniformDeformationUsesTriangleCorrespondence()
    {
        var source = Fixture.Create(); var target = Fixture.Create();
        BitConverter.GetBytes(2f).CopyTo(target.Bytes, target.PositionOffsets[1]);
        var result = MdlConverter.Convert(source.Bytes, target.Bytes, source.Bytes, new());
        Assert.Equal(2f, Float(result.ModelData, source.PositionOffsets[1]), 5);
        Assert.Equal(1f, Float(result.ModelData, source.PositionOffsets[2] + 4), 5);
        Assert.Equal(1f, Float(result.ModelData, source.PositionOffsets[1] + 20), 5);
    }

    [Fact]
    public void NormalTransformsRemainPerpendicularToTiltedSurface()
    {
        var source = Fixture.Create(); var target = Fixture.Create();
        BitConverter.GetBytes(0.5f).CopyTo(target.Bytes, target.PositionOffsets[1] + 8);
        var outfit = Fixture.Create(offset: new(0, 0, 0.02f));
        var result = MdlConverter.Convert(source.Bytes, target.Bytes, outfit.Bytes, new());
        int p = outfit.PositionOffsets[1];
        var normal = new Vector3(Float(result.ModelData, p + 12), Float(result.ModelData, p + 16), Float(result.ModelData, p + 20));
        Assert.Equal(0f, Vector3.Dot(normal, new(1, 0, 0.5f)), 5);
        Assert.Equal(1f, normal.Length(), 5);
    }

    [Fact]
    public void StationaryVerticesStillReceiveRotatedNormals()
    {
        var source = Fixture.Create(); var target = Fixture.Create();
        BitConverter.GetBytes(0.5f).CopyTo(target.Bytes, target.PositionOffsets[1] + 8);
        var result = MdlConverter.Convert(source.Bytes, target.Bytes, source.Bytes, new());
        foreach (int vertex in new[] { 0, 2 })
        {
            int p = source.PositionOffsets[vertex];
            Assert.Equal(source.Bytes.AsSpan(p, 12).ToArray(), result.ModelData.AsSpan(p, 12).ToArray());
            var normal = new Vector3(Float(result.ModelData, p + 12), Float(result.ModelData, p + 16), Float(result.ModelData, p + 20));
            Assert.Equal(0f, Vector3.Dot(normal, new(1, 0, 0.5f)), 5);
        }
    }

    [Fact]
    public void ClearanceSeparatesClothingWithoutInflatingEmbeddedSkin()
    {
        var source = Fixture.Create(); var target = Fixture.Create(offset: new(0, 0, 0.1f));
        var skin = Fixture.Create(); var cloth = Fixture.Create(material: "/dress.mtrl");
        var prepared = MdlConverter.Prepare(source.Bytes, target.Bytes, new() { Clearance = 0.01f });
        var skinResult = prepared.Convert(skin.Bytes); var clothResult = prepared.Convert(cloth.Bytes);
        foreach (int address in skin.PositionOffsets) Assert.Equal(0.1f, Float(skinResult.ModelData, address + 8), 5);
        foreach (int address in cloth.PositionOffsets) Assert.Equal(0.11f, Float(clothResult.ModelData, address + 8), 5);
        Assert.Contains(skinResult.Warnings, message => message.Contains("embedded body vertices"));
    }

    [Fact]
    public void OriginallyCoincidentSkinSeamsSharePositionsAndShadingFrames()
    {
        // Two reference faces meet along Y, but bend differently in the target.
        var a = Vector3.Zero; var b = Vector3.UnitY;
        var prepared = new PreparedConversion(new TriangleIndex([
            new(a, -Vector3.UnitX, b, a, -Vector3.UnitX, b),
            new(a, b, Vector3.UnitX, a, b, new(1, 0, 1))
        ]), new() { Clearance = 0.0025f }, "test", [], ["/body.mtrl"]);
        var outfit = Fixture.Create(overlapping: true);
        for (int i = 0; i < 6; i++)
        {
            int address = outfit.PositionOffsets[i];
            var point = new Vector3(i < 3 ? 0.00000002f : -0.00000002f, 0.2f + (i % 3) * 0.2f, 0);
            MdlDocument.WriteVector(outfit.Bytes, address, 2, point, false);
        }
        var result = prepared.Convert(outfit.Bytes);
        for (int i = 0; i < 3; i++)
        {
            int first = outfit.PositionOffsets[i], second = outfit.PositionOffsets[i + 3];
            Assert.Equal(result.ModelData.AsSpan(first, 24).ToArray(), result.ModelData.AsSpan(second, 24).ToArray());
        }
    }

    [Fact]
    public void SkinSeamBindingNeverJoinsDifferentMaterialsOrLods()
    {
        var bindings = new SkinSeamBindings();
        var first = new Vector3(0.5f, 0.5f, 0.5f); var nearby = first + new Vector3(0.0000001f, 0, 0);
        Assert.Equal(first, bindings.Canonical(0, "skin", first));
        Assert.Equal(nearby, bindings.Canonical(1, "skin", nearby));
        Assert.Equal(nearby, bindings.Canonical(0, "cloth", nearby));
        Assert.Equal(first, bindings.Canonical(0, "skin", nearby));
    }

    [Fact]
    public void CoarseGarmentTriangleClearsInteriorSkinAndCarriesFarShapeCorner()
    {
        var model = ClearanceFixture(shape: true); var output = (byte[])model.Data.Clone();
        RaiseSkin(model, output, 0.008f);
        var result = GarmentClearance.Refine(model, output, new HashSet<string> { "/body.mtrl" }, 0.0025f, default);
        Assert.True(result.AdjustedVertices > 0); Assert.Equal(0, result.UnresolvedSamples);
        var cloth = model.Meshes[1];
        var vertices = Enumerable.Range(0, 4).Select(v => MdlDocument.ReadVector(output, cloth.Address(cloth.Position, v), 2, false)).ToArray();
        foreach (var point in model.Meshes[0].Positions)
        {
            var raised = point + new Vector3(0, 0, 0.008f);
            var bary = TriangleIndex.ClosestBarycentric(raised, vertices[0], vertices[1], vertices[2]);
            var nearest = vertices[0] * bary.X + vertices[1] * bary.Y + vertices[2] * bary.Z;
            var normal = Vector3.Normalize(Vector3.Cross(vertices[1] - vertices[0], vertices[2] - vertices[0]));
            Assert.True(Vector3.Dot(nearest - raised, normal) >= 0.00248f);
        }
        // Replacement is far from the surface; its actual base-corner binding wins.
        Assert.True(Vector3.Distance(vertices[3] - cloth.Positions[3], vertices[0] - cloth.Positions[0]) < 1e-6f);
        for (int i = 0; i < output.Length; i++)
        {
            bool geometry = Enumerable.Range(0, cloth.VertexCount).Any(v => i >= cloth.Address(cloth.Position, v) && i < cloth.Address(cloth.Position, v) + 28);
            bool raisedBodyPosition = model.Meshes[0].Positions.Select((_, v) => model.Meshes[0].Address(model.Meshes[0].Position, v)).Any(a => i >= a && i < a + 12);
            if (!geometry && !raisedBodyPosition) Assert.Equal(model.Data[i], output[i]);
        }
    }

    [Fact]
    public void ClearanceLeavesOriginalCutoutsAndZeroClearanceUnchanged()
    {
        var cutout = ClearanceFixture(cutout: true); var output = (byte[])cutout.Data.Clone(); RaiseSkin(cutout, output, 0.008f);
        var before = (byte[])output.Clone();
        var result = GarmentClearance.Refine(cutout, output, new HashSet<string> { "/body.mtrl" }, 0.0025f, default);
        Assert.Equal(0, result.Samples); Assert.Equal(before, output);
        var covered = ClearanceFixture(); output = (byte[])covered.Data.Clone(); RaiseSkin(covered, output, 0.008f); before = (byte[])output.Clone();
        GarmentClearance.Refine(covered, output, new HashSet<string> { "/body.mtrl" }, 0, default);
        Assert.Equal(before, output);
    }

    [Fact]
    public void ClearanceIsBoundedAndReportsUnresolvedSamples()
    {
        var model = ClearanceFixture(); var output = (byte[])model.Data.Clone(); RaiseSkin(model, output, 0.15f);
        var result = GarmentClearance.Refine(model, output, new HashSet<string> { "/body.mtrl" }, 0.02f, default);
        Assert.True(result.UnresolvedSamples > 0);
        Assert.InRange(result.MaximumAdjustment, 0.09f, 0.100001f);
    }

    [Fact]
    public void ClearanceDoesNotMoveUnboundGarmentFaces()
    {
        var model = ClearanceFixture(); var output = (byte[])model.Data.Clone(); RaiseSkin(model, output, 0.008f); var before = (byte[])output.Clone();
        var bound = model.Meshes.ToDictionary(m => m.Index, m => Enumerable.Repeat(m.Index == 0, m.VertexCount).ToArray());
        var result = GarmentClearance.Refine(model, output, new HashSet<string> { "/body.mtrl" }, 0.0025f, default, bound);
        Assert.Equal(0, result.AdjustedVertices); Assert.Equal(before, output);
    }

    [Fact]
    public void ClearanceUsesGarmentOrientationDespiteInwardTargetDimpleNormals()
    {
        var model = ClearanceFixture(); var output = (byte[])model.Data.Clone(); RaiseSkin(model, output, 0.008f);
        foreach (int v in Enumerable.Range(0, 3)) MdlDocument.WriteVector(output, model.Meshes[0].StreamOffsets[0] + v * 40 + 12, 2, -Vector3.UnitZ, true);
        var result = GarmentClearance.Refine(model, output, new HashSet<string> { "/body.mtrl" }, 0.0025f, default);
        Assert.True(result.AdjustedVertices > 0); Assert.Equal(0, result.UnresolvedSamples);
    }

    private static void RaiseSkin(MdlDocument model, byte[] output, float height)
    {
        var skin = model.Meshes[0];
        for (int i = 0; i < skin.VertexCount; i++) MdlDocument.WriteVector(output, skin.Address(skin.Position, i), 2, skin.Positions[i] + new Vector3(0, 0, height), false);
    }

    private static MdlDocument ClearanceFixture(bool shape = false, bool cutout = false)
    {
        var bodyPositions = new[] { new Vector3(-0.001f, 0, 0), new Vector3(0.001f, 0, 0), new Vector3(0, 0.001f, 0) };
        if (cutout) for (int i = 0; i < bodyPositions.Length; i++) bodyPositions[i] += Vector3.UnitX;
        var clothPositions = new List<Vector3> { new(-0.1f, -0.1f, 0.001f), new(0.1f, -0.1f, 0.001f), new(0, 0.1f, 0.001f) };
        if (shape) clothPositions.Add(new(-0.1f, -0.1f, 0.08f));
        var bytes = Enumerable.Repeat((byte)0xA5, (bodyPositions.Length + clothPositions.Count) * 40).ToArray();
        var meshes = new[] { MakeMesh(0, 0, bodyPositions), MakeMesh(1, 120, clothPositions.ToArray()) };
        foreach (var mesh in meshes)
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                MdlDocument.WriteVector(bytes, mesh.Address(mesh.Position, i), 2, mesh.Positions[i], false);
                MdlDocument.WriteVector(bytes, mesh.StreamOffsets[0] + i * 40 + 12, 2, Vector3.UnitZ, true);
                MdlDocument.WriteVector(bytes, mesh.StreamOffsets[0] + i * 40 + 24, 8, Vector3.UnitX, true);
            }
        return new() { Data = bytes, Version = 0x01000005, LodCount = 1, ModelHeaderOffset = 0, BoundsOffset = 0,
            BoneCount = 0, ShapeCount = shape ? 1 : 0, Meshes = meshes, Materials = ["/body.mtrl", "/dress.mtrl"],
            ShapeBindings = shape ? [new(1, 0, 3)] : [] };
        MdlDocument.Mesh MakeMesh(int index, int address, Vector3[] positions) => new() { Index = index, Lod = 0,
            VertexCount = positions.Length, Material = index, StartIndex = 0, StreamOffsets = [address, 0, 0], Strides = [40, 0, 0],
            Elements = [new(0, 0, 2, 0, 0), new(0, 12, 2, 3, 0), new(0, 24, 8, 6, 0)], Indices = [0, 1, 2], Positions = positions, Uvs = null };
    }

    [Fact]
    public void OverlappingBodyUvUsesVisibleOuterSkinInsteadOfNearerHiddenUnderlay()
    {
        var source = Fixture.Create(); var target = Fixture.Create(overlapping: true);
        for (int i = 0; i < 6; i++) BitConverter.GetBytes(i < 3 ? 0.01f : 0.02f).CopyTo(target.Bytes, target.PositionOffsets[i] + 8);
        var prepared = MdlConverter.Prepare(source.Bytes, target.Bytes, new());
        var result = prepared.Convert(source.Bytes);
        foreach (int address in source.PositionOffsets) Assert.Equal(0.02f, Float(result.ModelData, address + 8), 5);
        Assert.Contains(prepared.Warnings, w => w.Contains("hidden overlapping body-surface"));
    }

    [Fact]
    public void OpposingNearbyBodySurfacesDoNotCountAsOuterSkinLayers()
    {
        var source = Fixture.Create(); var target = Fixture.Create(overlapping: true);
        for (int i = 0; i < 6; i++)
        {
            BitConverter.GetBytes(i < 3 ? 0.01f : 0.02f).CopyTo(target.Bytes, target.PositionOffsets[i] + 8);
            if (i >= 3) BitConverter.GetBytes(-1f).CopyTo(target.Bytes, target.PositionOffsets[i] + 20);
        }
        var result = MdlConverter.Convert(source.Bytes, target.Bytes, source.Bytes, new());
        foreach (int address in source.PositionOffsets) Assert.Equal(0.01f, Float(result.ModelData, address + 8), 5);
    }

    [Fact]
    public void HiddenUvRegionProjectsThroughMultipleSkinLayersToVisibleEnvelope()
    {
        var source = Fixture.Create(); var target = Fixture.Create(overlapping: true, thirdLayer: true);
        for (int i = 0; i < 9; i++)
        {
            BitConverter.GetBytes(0.01f * (i / 3 + 1)).CopyTo(target.Bytes, target.PositionOffsets[i] + 8);
            // Only the inner component covers the original atlas region.
            if (i >= 3) BitConverter.GetBytes(Float(target.Bytes, target.PositionOffsets[i] + 28) + 2).CopyTo(target.Bytes, target.PositionOffsets[i] + 28);
        }
        var prepared = MdlConverter.Prepare(source.Bytes, target.Bytes, new());
        var result = prepared.Convert(source.Bytes);
        foreach (int address in source.PositionOffsets) Assert.Equal(0.03f, Float(result.ModelData, address + 8), 5);
        Assert.Contains(prepared.Warnings, w => w.Contains("projected along"));
    }

    [Fact]
    public void UvBranchRefinementRevisitsPreviouslyAcceptedDiscontinuousCandidates()
    {
        Vector3[] source = [new(0, 0, 0), new(0.0001f, 0, 0), new(0.001f, 0, 0)];
        Vector3[] destination = [source[0] + new Vector3(0, 0, 0.02f), source[1] + new Vector3(0, 0, 0.005f), source[2] + new Vector3(0, 0, 0.02f)];
        var continuous = source[1] + new Vector3(0, 0, 0.02f);
        var choices = new Dictionary<int, Vector3[]> { [1] = [destination[1], continuous] };
        HashSet<int>[] neighbors = [[1], [0, 2], [1]];
        Assert.Equal(1, UvContinuity.Refine(source, destination, choices, neighbors, default));
        Assert.Equal(continuous, destination[1]);
        Assert.True(Vector3.Distance(destination[0], destination[1]) < 0.00011f);
        Assert.Equal(0, UvContinuity.Refine(source, destination, choices, neighbors, default));
    }

    private static float Float(byte[] bytes, int offset) => BitConverter.ToSingle(bytes, offset);

    // Synthetic, independently encoded MDL v5 fixtures. No game assets are embedded.
    internal sealed record Fixture(byte[] Bytes, int HeaderOffset, int BoundsOffset, int[] PositionOffsets, int[] IndexOffsets)
    {
        public static Fixture Create(int lods = 1, bool shape = false, bool farShape = false, bool retessellated = false,
            bool overlapping = false, Vector3 offset = default, string material = "/body.mtrl", bool thirdLayer = false)
        {
            var vertices = new List<Vector3> { Vector3.Zero, Vector3.UnitX, Vector3.UnitY };
            var uv = new List<Vector2> { Vector2.Zero, Vector2.UnitX, Vector2.UnitY };
            ushort[] indices = [0, 1, 2];
            if (retessellated)
            {
                vertices.Add(new(1f / 3, 1f / 3, 0)); uv.Add(new(1f / 3, 1f / 3));
                indices = [0, 1, 3, 1, 2, 3, 2, 0, 3];
            }
            if (shape) { vertices.Add(new(0.25f, 0.25f, farShape ? 5 : 0.05f)); uv.Add(new(0.25f, 0.25f)); }
            if (overlapping)
            {
                for (int i = 0; i < 3; i++) vertices[i] += new Vector3(0, 0, -0.1f);
                vertices.AddRange([new(0, 0, 0.1f), new(1, 0, 0.1f), new(0, 1, 0.1f)]);
                uv.AddRange([Vector2.Zero, Vector2.UnitX, Vector2.UnitY]); indices = [0, 1, 2, 3, 4, 5];
            }
            if (thirdLayer)
            {
                vertices.AddRange([new(0, 0, 0.3f), new(1, 0, 0.3f), new(0, 1, 0.3f)]);
                uv.AddRange([Vector2.Zero, Vector2.UnitX, Vector2.UnitY]); indices = [0, 1, 2, 3, 4, 5, 6, 7, 8];
            }
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(new byte[68]);
            for (int lod = 0; lod < lods; lod++)
            {
                long declaration = stream.Position;
                foreach (var e in new[] { (0, 2, 0), (12, 2, 3), (24, 8, 6), (28, 1, 4), (36, 5, 1) })
                { w.Write((byte)0); w.Write((byte)e.Item1); w.Write((byte)e.Item2); w.Write((byte)e.Item3); w.Write(0u); }
                w.Write((byte)255); w.Write(new byte[136 - (int)(stream.Position - declaration)]);
            }
            byte[] strings = Encoding.UTF8.GetBytes(material + "\0bone\0shape\0");
            w.Write((ushort)3); w.Write((ushort)0); w.Write(strings.Length); w.Write(strings);
            int header = (int)stream.Position;
            w.Write(new byte[56]);
            int lodTable = (int)stream.Position; w.Write(new byte[180]);
            int meshTable = (int)stream.Position; w.Write(new byte[lods * 36]);
            w.Write(0u); w.Write((uint)Encoding.UTF8.GetByteCount(material) + 1); // material and bone string offsets
            w.Write(new byte[128]); w.Write(1u); // v5 bone table
            if (shape)
            {
                w.Write((uint)Encoding.UTF8.GetByteCount(material) + 6);
                for (int i = 0; i < 3; i++) w.Write((ushort)(i < lods ? i : 0));
                for (int i = 0; i < 3; i++) w.Write((ushort)(i < lods ? 1 : 0));
                for (int i = 0; i < lods; i++) { w.Write(0u); w.Write(1u); w.Write((uint)i); }
                for (int i = 0; i < lods; i++) { w.Write((ushort)0); w.Write((ushort)(vertices.Count - 1)); }
            }
            w.Write(0u); w.Write((byte)0);
            int bounds = (int)stream.Position;
            for (int i = 0; i < 5; i++)
            { w.Write(-1f); w.Write(-1f); w.Write(-1f); w.Write(123f); w.Write(1f); w.Write(1f); w.Write(1f); w.Write(456f); }
            int dataStart = (int)stream.Position;
            var vOffsets = new int[3]; var iOffsets = new int[3]; var positions = new List<int>();
            for (int lod = 0; lod < lods; lod++)
            {
                vOffsets[lod] = (int)stream.Position;
                for (int i = 0; i < vertices.Count; i++)
                {
                    positions.Add((int)stream.Position); var p = vertices[i] + offset;
                    w.Write(p.X); w.Write(p.Y); w.Write(p.Z);
                    w.Write(0f); w.Write(0f); w.Write(1f);
                    w.Write(new byte[] { 255, 128, 128, 255 });
                    w.Write(uv[i].X); w.Write(uv[i].Y);
                    w.Write(new byte[] { 255, 0, 0, 0 });
                }
                iOffsets[lod] = (int)stream.Position; foreach (ushort index in indices) w.Write(index);
            }
            stream.Position = 0; w.Write(0x01000005u); w.Write(lods * 136); w.Write(dataStart - 68 - lods * 136); w.Write((ushort)lods); w.Write((ushort)1);
            foreach (int value in vOffsets) w.Write(value); foreach (int value in iOffsets) w.Write(value);
            for (int i = 0; i < 3; i++) w.Write(i < lods ? vertices.Count * 40 : 0);
            for (int i = 0; i < 3; i++) w.Write(i < lods ? indices.Length * 2 : 0);
            w.Write((byte)lods); w.Write(new byte[3]);
            stream.Position = header; w.Write(1f); w.Write((ushort)lods); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)1); w.Write((ushort)1);
            w.Write((ushort)(shape ? 1 : 0)); w.Write((ushort)(shape ? lods : 0)); w.Write((ushort)(shape ? lods : 0)); w.Write((byte)lods);
            for (int lod = 0; lod < lods; lod++)
            {
                stream.Position = lodTable + lod * 60; w.Write((ushort)lod); w.Write((ushort)1);
                stream.Position = lodTable + lod * 60 + 44; w.Write(vertices.Count * 40); w.Write(indices.Length * 2); w.Write(vOffsets[lod]); w.Write(iOffsets[lod]);
                stream.Position = meshTable + lod * 36; w.Write((ushort)vertices.Count); w.Write((ushort)0); w.Write(indices.Length); w.Write(new byte[12]); w.Write(new byte[12]);
                w.Write((byte)40); w.Write((byte)0); w.Write((byte)0); w.Write((byte)1);
            }
            return new(stream.ToArray(), header, bounds, positions.ToArray(), iOffsets);
        }
    }
}
