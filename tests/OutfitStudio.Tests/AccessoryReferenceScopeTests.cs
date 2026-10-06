using System.Numerics;
using OutfitStudio.Core.Geometry;
using OutfitStudio.Core.Models;
using Fixture = OutfitStudio.Tests.GeometryTests.Fixture;

namespace OutfitStudio.Tests;

public sealed class AccessoryReferenceScopeTests
{
    [Fact]
    public void UnrelatedDisconnectedUvGapDoesNotBlockLocalizedAccessory()
    {
        var source = Body(2);
        for (int i = 3; i < 6; i++) SetUv(source, i, new(4 + (i == 4 ? 1 : 0), i == 5 ? 1 : 0));
        var target = Fixture.Create(retessellated: true, offset: new(0, 0, .1f));
        var accessory = Accessory(Vector3.Zero);
        Assert.Throws<ModelConversionException>(() => MdlConverter.Prepare(source.Bytes, target.Bytes, new()));
        var localized = MdlConverter.PrepareForAccessories(source.Bytes, target.Bytes, [accessory.Bytes], new());
        Assert.Single(localized.Triangles);
        var converted = localized.Convert(accessory.Bytes);
        Assert.Equal(accessory.PositionOffsets.Length, converted.ConvertedVertices);
        foreach (int address in accessory.PositionOffsets)
            Assert.Equal(.1f, BitConverter.ToSingle(converted.ModelData, address + 8) - BitConverter.ToSingle(accessory.Bytes, address + 8), 5);
    }

    [Fact]
    public void MissingUvOnRequiredLocalSurfaceStillFails()
    {
        var source = Body(2);
        SetUv(source, 1, new(1.25f, 0));
        var target = Fixture.Create(retessellated: true, offset: new(0, 0, .1f));
        var error = Assert.Throws<ModelConversionException>(() => MdlConverter.PrepareForAccessories(
            source.Bytes, target.Bytes, [Accessory(Vector3.Zero).Bytes], new()));
        Assert.Contains("no target UV correspondence", error.Message);
    }

    [Fact]
    public void EveryLodAndUnusedShapeVertexContributesToScope()
    {
        var source = Body(3);
        var accessory = Fixture.Create(lods: 2, shape: true, material: "/metal.mtrl");
        for (int i = 0; i < 3; i++)
        {
            SetPosition(accessory, i, SmallPoint(i) + new Vector3(0, 0, .02f));
            SetPosition(accessory, i + 4, SmallPoint(i) + new Vector3(4, 0, .02f));
        }
        SetPosition(accessory, 3, new(8.2f, .2f, .02f));
        SetPosition(accessory, 7, new(8.3f, .2f, .02f));
        var scope = Scope(source, accessory);
        Assert.Equal(9, scope[0].Length);
        var prepared = MdlConverter.PrepareForAccessories(source.Bytes, Translate(source, new(0, 0, .1f)), [accessory.Bytes], new());
        var converted = prepared.Convert(accessory.Bytes);
        Assert.Equal(8, converted.ConvertedVertices);
        foreach (int address in accessory.PositionOffsets)
            Assert.Equal(.12f, BitConverter.ToSingle(converted.ModelData, address + 8), 5);
    }

    [Fact]
    public void ScopeIsUnionOfAllStyleModelsSharingReference()
    {
        var source = Body(3);
        var first = Accessory(Vector3.Zero);
        var second = Accessory(new(4, 0, 0));
        var scope = Scope(source, first, second);
        Assert.Equal(new ushort[] { 0, 1, 2, 3, 4, 5 }, scope[0]);
        var prepared = MdlConverter.PrepareForAccessories(source.Bytes, Translate(source, new(0, 0, .1f)),
            [first.Bytes, second.Bytes], new());
        Assert.Equal(2, prepared.Triangles.Count());
        foreach (var accessory in new[] { first, second })
        {
            var converted = prepared.Convert(accessory.Bytes);
            Assert.Equal(accessory.PositionOffsets.Length, converted.ConvertedVertices);
            Assert.DoesNotContain(converted.Warnings, warning => warning.Contains("outside the maximum"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutOfRangeShapeOrLodVertexCannotBeSilentlyLeftUnbound(bool otherLod)
    {
        var source = Body(1);
        var accessory = Fixture.Create(lods: otherLod ? 2 : 1, shape: !otherLod, material: "/metal.mtrl");
        SetPosition(accessory, accessory.PositionOffsets.Length - 1, new(9, 9, 9));
        var error = Assert.Throws<ModelConversionException>(() => MdlConverter.PrepareForAccessories(
            source.Bytes, Translate(source, new(0, 0, .1f)), [accessory.Bytes], new()));
        Assert.Contains("extends beyond", error.Message);
    }

    [Fact]
    public void AdjacentUvSplitFacesPreserveTheCompleteLocalSmoothFrame()
    {
        AssertLocalFramePreserved(0);
    }

    [Fact]
    public void ExportRoundedUvSplitFacesPreserveTheCompleteLocalSmoothFrame()
    {
        AssertLocalFramePreserved(.0000005f);
    }

    [Fact]
    public void DisconnectedRemoteSurfaceDoesNotChangeValidLocalConversion()
    {
        var source = Body(2);
        var target = Translate(source, new(0, 0, .1f));
        var accessory = Accessory(Vector3.Zero, .1f);
        var settings = new ConversionSettings { Clearance = .0025f };
        var full = MdlConverter.Prepare(source.Bytes, target, settings).Convert(accessory.Bytes);
        var localized = MdlConverter.PrepareForAccessories(source.Bytes, target, [accessory.Bytes], settings).Convert(accessory.Bytes);
        Assert.Equal(full.ModelData, localized.ModelData);
    }

    [Fact]
    public void EmptyAccessorySetAndCancelledScopeAreRejected()
    {
        var source = Body(1);
        Assert.Throws<ModelConversionException>(() => Scope(source));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => AccessoryReferenceScope.Select(
            MdlDocument.Parse(source.Bytes).Meshes, [MdlDocument.Parse(Accessory(Vector3.Zero).Bytes)], .25f, cancellation.Token));
    }

    [Fact]
    public void CollapsedRequiredTargetFaceCannotSilentlyRebindToAnotherSurface()
    {
        var source = Body(2);
        byte[] target = Translate(source, new(0, 0, .1f));
        for (int i = 0; i < 3; i++) MdlDocument.WriteVector(target, source.PositionOffsets[i], 2, Vector3.Zero, false);
        // A surviving surface must not conceal the collapsed face used by the
        // other accessory style, even though a full-body preparation can omit it.
        Assert.Single(MdlConverter.Prepare(source.Bytes, target, new()).Triangles);
        var error = Assert.Throws<ModelConversionException>(() => MdlConverter.PrepareForAccessories(source.Bytes, target,
            [Accessory(Vector3.Zero).Bytes, Accessory(new(4, 0, 0)).Bytes], new()));
        Assert.Contains("accessory-bound source face collapses", error.Message);
    }

    private static void AssertLocalFramePreserved(float splitOffset)
    {
        var source = Body(2);
        SetPosition(source, 3, new(splitOffset, 0, 0));
        SetPosition(source, 4, new(-1, 0, 0));
        SetPosition(source, 5, new(0, -1, 0));
        byte[] target = Translate(source, new(0, 0, .1f));
        MdlDocument.WriteVector(target, source.PositionOffsets[4], 2, new(-1, 0, .4f), false);
        MdlDocument.WriteVector(target, source.PositionOffsets[5], 2, new(0, -1, .4f), false);
        var accessory = Accessory(Vector3.Zero, .1f);
        Assert.Equal(6, Scope(source, accessory)[0].Length);
        var settings = new ConversionSettings { Clearance = .0025f };
        var full = MdlConverter.Prepare(source.Bytes, target, settings).Convert(accessory.Bytes);
        var prepared = MdlConverter.PrepareForAccessories(source.Bytes, target, [accessory.Bytes], settings);
        // Compare the continuous field before the separate rigid-part stage.
        // RigidAccessoryPartsTests verifies that stage's intentional geometry change.
        var local = new PreparedConversion(new TriangleIndex(prepared.Triangles), prepared.Settings,
            prepared.Method, prepared.Warnings, prepared.BodyMaterials, prepared.WeightTransfer).Convert(accessory.Bytes);
        Assert.Equal(full.ModelData, local.ModelData);
    }

    private static IReadOnlyDictionary<int, ushort[]> Scope(Fixture body, params Fixture[] accessories)
        => AccessoryReferenceScope.Select(MdlDocument.Parse(body.Bytes).Meshes,
            accessories.Select(a => MdlDocument.Parse(a.Bytes)), .25f, default);

    private static Fixture Body(int components)
    {
        var body = Fixture.Create(overlapping: components >= 2, thirdLayer: components == 3);
        for (int i = 0; i < body.PositionOffsets.Length; i++)
            SetPosition(body, i, new((i / 3) * 4 + (i % 3 == 1 ? 1 : 0), i % 3 == 2 ? 1 : 0, 0));
        return body;
    }

    private static Fixture Accessory(Vector3 offset, float height = .02f)
    {
        var accessory = Fixture.Create(material: "/metal.mtrl");
        for (int i = 0; i < 3; i++) SetPosition(accessory, i, SmallPoint(i) + offset + new Vector3(0, 0, height));
        return accessory;
    }

    private static Vector3 SmallPoint(int index) => new(.2f + (index == 1 ? .1f : 0), .2f + (index == 2 ? .1f : 0), 0);
    private static void SetPosition(Fixture fixture, int vertex, Vector3 position)
        => MdlDocument.WriteVector(fixture.Bytes, fixture.PositionOffsets[vertex], 2, position, false);
    private static void SetUv(Fixture fixture, int vertex, Vector2 uv)
    {
        MdlDocument.WriteFloat(fixture.Bytes, fixture.PositionOffsets[vertex] + 28, uv.X);
        MdlDocument.WriteFloat(fixture.Bytes, fixture.PositionOffsets[vertex] + 32, uv.Y);
    }
    private static byte[] Translate(Fixture fixture, Vector3 offset)
    {
        byte[] result = (byte[])fixture.Bytes.Clone();
        foreach (int address in fixture.PositionOffsets)
            MdlDocument.WriteVector(result, address, 2, MdlDocument.ReadVector(fixture.Bytes, address, 2, false) + offset, false);
        return result;
    }
}
