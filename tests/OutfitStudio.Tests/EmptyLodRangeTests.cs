using System.Buffers.Binary;
using OutfitStudio.Core.Geometry;
using OutfitStudio.Core.Models;

namespace OutfitStudio.Tests;

public sealed class EmptyLodRangeTests
{
    [Fact]
    public void EmptyRangesIgnoreStaleStartIndicesInActiveAndUnusedLods()
    {
        var fixture = GeometryTests.Fixture.Create();
        int table = fixture.HeaderOffset + 56;
        for (int lod = 0; lod < 3; lod++)
            foreach (int range in new[] { 0, 12, 16, 24 })
                if (lod != 0 || range != 0)
                    WriteRange(fixture.Bytes, table + lod * 60 + range, ushort.MaxValue, 0);
        var original = (byte[])fixture.Bytes.Clone();

        var inspection = MdlConverter.Inspect(fixture.Bytes);
        Assert.Equal(1, inspection.LodCount);
        Assert.Equal(1, inspection.MeshCount);
        var result = MdlConverter.Convert(fixture.Bytes, fixture.Bytes, fixture.Bytes, new());
        Assert.Equal(0, result.ConvertedVertices);
        Assert.Equal(original, result.ModelData);
        Assert.Equal(original, fixture.Bytes);
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(0, 16, 1, 1)]
    [InlineData(0, 16, 65535, 1)]
    [InlineData(1, 0, 0, 1)]
    [InlineData(2, 16, 0, 1)]
    public void NonemptyRangesStillRequireValidMeshBoundsAndActiveLods(int lod, int range, ushort start, ushort count)
    {
        var fixture = GeometryTests.Fixture.Create();
        WriteRange(fixture.Bytes, fixture.HeaderOffset + 56 + lod * 60 + range, start, count);
        var error = Assert.Throws<ModelConversionException>(() => MdlConverter.Inspect(fixture.Bytes));
        Assert.Contains("Invalid LOD mesh range", error.Message);
    }

    private static void WriteRange(byte[] bytes, int address, ushort start, ushort count)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(address), start);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(address + 2), count);
    }
}
