using System.Numerics;
using OutfitStudio.Core.Geometry;
using Fixture = OutfitStudio.Tests.GeometryTests.Fixture;

namespace OutfitStudio.Tests;

public sealed class AccessoryRegionResolverTests
{
    [Fact]
    public void ChestAccessoryChoosesChestFromGeometryRegardlessOfReferenceOrder()
    {
        var accessory = Fixture.Create(offset: new(0, 0, 0.01f), material: "/metal_clasp.mtrl");
        var top = Fixture.Create();
        var legs = Fixture.Create(offset: new(0, -2, 0));
        var hands = Fixture.Create(offset: new(2, 0, 0));
        var references = new Dictionary<string, byte[]> { ["dwn"] = legs.Bytes, ["glv"] = hands.Bytes, ["top"] = top.Bytes };
        Assert.Equal("top", AccessoryRegionResolver.Resolve(accessory.Bytes, references));
        Assert.Equal("top", AccessoryRegionResolver.Resolve(accessory.Bytes, references.Reverse().ToDictionary()));
    }

    [Fact]
    public void AccessoryAtLegsUsesLegReference()
    {
        var top = Fixture.Create();
        var legs = Fixture.Create(offset: new(0, -2, 0));
        var accessory = Fixture.Create(offset: new(0, -2, 0.01f), material: "/chain.mtrl");
        Assert.Equal("dwn", AccessoryRegionResolver.Resolve(accessory.Bytes,
            new Dictionary<string, byte[]> { ["top"] = top.Bytes, ["dwn"] = legs.Bytes }));
    }

    [Fact]
    public void UnusedShapeVerticesAndOtherLodsDoNotDistortInference()
    {
        var accessory = Fixture.Create(lods: 2, shape: true, farShape: true, material: "/metal.mtrl");
        for (int i = 4; i < accessory.PositionOffsets.Length; i++)
            MdlDocument.WriteVector(accessory.Bytes, accessory.PositionOffsets[i], 2, new(0, 0, 4), false);
        Assert.Equal("top", AccessoryRegionResolver.Resolve(accessory.Bytes,
            new Dictionary<string, byte[]> { ["top"] = Fixture.Create().Bytes, ["dwn"] = Fixture.Create(offset: new(0, 0, 4)).Bytes }));
    }

    [Fact]
    public void EmptyAndCollapsedReplacementStubsCannotChooseARegion()
    {
        var references = new Dictionary<string, byte[]> { ["top"] = Fixture.Create().Bytes };
        var empty = Fixture.Create();
        BitConverter.GetBytes(0).CopyTo(empty.Bytes, empty.HeaderOffset + 56 + 180 + 4);
        Assert.Null(AccessoryRegionResolver.Resolve(empty.Bytes, references));
        var collapsed = Fixture.Create();
        foreach (int address in collapsed.PositionOffsets)
            MdlDocument.WriteVector(collapsed.Bytes, address, 2, Vector3.Zero, false);
        Assert.Null(AccessoryRegionResolver.Resolve(collapsed.Bytes, references));
    }

    [Fact]
    public void MissingOrDegenerateBodyReferencesRemainUnresolved()
    {
        var accessory = Fixture.Create();
        Assert.Null(AccessoryRegionResolver.Resolve(accessory.Bytes, new Dictionary<string, byte[]>()));
        var collapsed = Fixture.Create();
        foreach (int address in collapsed.PositionOffsets)
            MdlDocument.WriteVector(collapsed.Bytes, address, 2, Vector3.Zero, false);
        Assert.Null(AccessoryRegionResolver.Resolve(accessory.Bytes, new Dictionary<string, byte[]> { ["top"] = collapsed.Bytes }));
    }

    [Fact]
    public void DistantGeometryIsNotAssignedToOnlyAvailableRegion()
    {
        Assert.Null(AccessoryRegionResolver.Resolve(Fixture.Create(offset: new(0, 0, 0.4f)).Bytes,
            new Dictionary<string, byte[]> { ["top"] = Fixture.Create().Bytes }));
    }

    [Fact]
    public void EqualOrNearlyEqualRegionsRemainAmbiguous()
    {
        var accessory = Fixture.Create(offset: new(0, 0, 0.01f));
        foreach (float secondOffset in new[] { 0f, 0.002f })
            Assert.Null(AccessoryRegionResolver.Resolve(accessory.Bytes, new Dictionary<string, byte[]>
            {
                ["top"] = Fixture.Create().Bytes,
                ["dwn"] = Fixture.Create(offset: new(0, 0, secondOffset)).Bytes,
            }));
    }

    [Fact]
    public void GeometrySplitBetweenDistinctRegionsRemainsUnresolved()
    {
        Assert.Null(AccessoryRegionResolver.Resolve(Fixture.Create(overlapping: true).Bytes,
            new Dictionary<string, byte[]>
            {
                ["top"] = Fixture.Create(offset: new(0, 0, -0.1f)).Bytes,
                ["dwn"] = Fixture.Create(offset: new(0, 0, 0.1f)).Bytes,
            }));
    }

    [Theory]
    [InlineData("/nails.mtrl")]
    [InlineData("/underwear.mtrl")]
    [InlineData("/piercing.mtrl")]
    [InlineData("/pubes.mtrl")]
    [InlineData("/undies.mtrl")]
    public void ReferenceAdornmentsAreExcluded(string material)
    {
        Assert.Equal("top", AccessoryRegionResolver.Resolve(Fixture.Create().Bytes,
            new Dictionary<string, byte[]>
            {
                ["glv"] = Fixture.Create(material: material).Bytes,
                ["top"] = Fixture.Create(offset: new(0, 0, 0.01f)).Bytes,
            }));
    }

    [Fact]
    public void AccessoryOwnMaterialNamesAreNotTreatedAsBodyExclusions()
    {
        Assert.Equal("top", AccessoryRegionResolver.Resolve(Fixture.Create(material: "/piercing.mtrl").Bytes,
            new Dictionary<string, byte[]> { ["top"] = Fixture.Create().Bytes }));
    }

    [Fact]
    public void TinyRemoteTriangleDoesNotOutweighMainAccessorySurface()
    {
        var accessory = Fixture.Create(overlapping: true);
        for (int i = 0; i < 3; i++)
            MdlDocument.WriteVector(accessory.Bytes, accessory.PositionOffsets[i], 2,
                new Vector3(i == 1 ? 1 : 0, i == 2 ? 1 : 0, 0), false);
        for (int i = 0; i < 3; i++)
            MdlDocument.WriteVector(accessory.Bytes, accessory.PositionOffsets[i + 3], 2,
                new Vector3(i == 1 ? 0.001f : 0, i == 2 ? 0.001f : 0, 1), false);
        Assert.Equal("top", AccessoryRegionResolver.Resolve(accessory.Bytes,
            new Dictionary<string, byte[]> { ["top"] = Fixture.Create().Bytes, ["dwn"] = Fixture.Create(offset: new(0, 0, 1)).Bytes }));
    }

    [Fact]
    public void CancellationIsObservedBeforeInspection()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => AccessoryRegionResolver.Resolve([], new Dictionary<string, byte[]>(), cancellation.Token));
    }
}
