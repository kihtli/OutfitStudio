using System.Numerics;
using OutfitStudio.Core.Geometry;
using OutfitStudio.Core.Models;

namespace OutfitStudio.Tests;

public sealed class UvTileAlignmentTests
{
    [Fact]
    public void ExistingUvCoverageKeepsZeroOffsetEvenWhenAnotherTileAlsoMatches()
    {
        var index = Atlas(Vector2.Zero, new(2, 0));
        Assert.Equal(Vector2.Zero, UvTileAlignment.Select([Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0, 1, 2], index, default));
        var source = GeometryTests.Fixture.Create();
        var target = GeometryTests.Fixture.Create(retessellated: true, offset: new(0, 0, 0.1f));
        var conversion = MdlConverter.Prepare(source.Bytes, target.Bytes, new());
        Assert.DoesNotContain(conversion.Warnings, warning => warning.Contains("tile offset"));
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(0, 1)]
    [InlineData(-3, 2)]
    [InlineData(2, -3)]
    public void UniformIntegerTranslationsProduceTheSameGeometryAndKeepStoredUvs(float u, float v)
    {
        var source = GeometryTests.Fixture.Create();
        var target = GeometryTests.Fixture.Create(retessellated: true, offset: new(0, 0, 0.1f));
        var outfit = GeometryTests.Fixture.Create(shape: true, offset: new(0, 0, 0.02f), material: "/dress.mtrl");
        var expected = MdlConverter.Convert(source.Bytes, target.Bytes, outfit.Bytes, new());
        var translated = (byte[])source.Bytes.Clone();
        OffsetUvs(translated, _ => new(u, v));
        var translatedBefore = (byte[])translated.Clone();
        var actual = MdlConverter.Convert(translated, target.Bytes, outfit.Bytes, new());
        Assert.Equal(expected.ModelData, actual.ModelData);
        Assert.Equal(translatedBefore, translated);
        Assert.Contains(actual.Warnings, warning => warning.Contains("uniform tile offset"));
    }

    [Fact]
    public void TwoTranslatedTilesAreAmbiguousWhenOriginalCoordinatesDoNotMatch()
    {
        var index = Atlas(Vector2.Zero, new(2, 0));
        Vector2[] source = [new(3, 0), new(4, 0), new(3, 1)];
        var error = Assert.Throws<ModelConversionException>(() => UvTileAlignment.Select(source, [0, 1, 2], index, default));
        Assert.Contains("multiple translated", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialTileWrappingAndNonintegerAlignmentRemainUnsupported(bool separateVertices)
    {
        var source = GeometryTests.Fixture.Create();
        var target = GeometryTests.Fixture.Create(retessellated: true);
        OffsetUvs(source.Bytes, vertex => separateVertices ? vertex == 1 ? Vector2.UnitX : Vector2.Zero : new(0.2f, 0));
        var error = Assert.Throws<ModelConversionException>(() => MdlConverter.Prepare(source.Bytes, target.Bytes, new()));
        Assert.Contains("no target UV correspondence within 0.015", error.Message);
    }

    [Fact]
    public void EveryRequiredSampleMustMatchBeforeAnOffsetIsAccepted()
    {
        Vector2[] source = [new(0, -1), new(1, -1), new(0, 0), new(3, 3)];
        var index = Atlas(Vector2.Zero);
        Assert.Equal(Vector2.UnitY, UvTileAlignment.Select(source, [0, 1, 2], index, default));
        Assert.Equal(Vector2.Zero, UvTileAlignment.Select(source, [0, 1, 2, 3], index, default));
    }

    private static TriangleIndex Atlas(params Vector2[] offsets)
        => new(offsets.Select(offset => new SurfaceTriangle(new(offset, 0), new(offset + Vector2.UnitX, 0),
            new(offset + Vector2.UnitY, 0), Vector3.Zero, Vector3.UnitX, Vector3.UnitY)));

    private static void OffsetUvs(byte[] bytes, Func<int, Vector2> offset)
    {
        var document = MdlDocument.Parse(bytes);
        foreach (var mesh in document.Meshes)
        {
            var element = mesh.Elements.Single(e => e.Usage == 4 && e.UsageIndex == 0);
            for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
            {
                var uv = mesh.Uvs![vertex] + offset(vertex);
                BitConverter.TryWriteBytes(bytes.AsSpan(mesh.Address(element, vertex)), uv.X);
                BitConverter.TryWriteBytes(bytes.AsSpan(mesh.Address(element, vertex) + 4), uv.Y);
            }
        }
    }
}
