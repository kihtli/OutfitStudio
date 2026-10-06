using System.Numerics;
using OutfitStudio.Core.Geometry;

namespace OutfitStudio.Tests;

public sealed class GarmentBindingTests
{
    [Fact]
    public void InfluenceHasFullNearFieldAndSmoothCompactSupport()
    {
        const float radius = .25f;
        Assert.Equal(1, GarmentBinding.Influence(0, radius));
        Assert.Equal(1, GarmentBinding.Influence(.0625f * .0625f, radius));
        Assert.Equal(.5f, GarmentBinding.Influence(.15625f * .15625f, radius), 6);
        Assert.Equal(0, GarmentBinding.Influence(radius * radius, radius));
        Assert.Equal(0, GarmentBinding.Influence(1, radius));
        float previous = 1;
        for (int i = 0; i <= 100; i++)
        {
            float distance = radius * i / 100;
            float next = GarmentBinding.Influence(distance * distance, radius);
            Assert.InRange(next, 0, previous);
            previous = next;
        }
        // Both ends join their constant regions with a vanishing slope.
        Assert.InRange(1 - GarmentBinding.Influence(.0626f * .0626f, radius), 0, .000001f);
        Assert.InRange(GarmentBinding.Influence(.2499f * .2499f, radius), 0, .000001f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConverterTapersDistantFabricButKeepsSkinCorrespondence(bool skin)
    {
        string material = skin ? "/body.mtrl" : "/dress.mtrl";
        var fixture = GeometryTests.Fixture.Create(material: material);
        Vector3[] positions = [new(.2f, .2f, .0625f), new(.3f, .2f, .15625f), new(.2f, .3f, .251f)];
        for (int i = 0; i < positions.Length; i++)
        {
            int address = fixture.PositionOffsets[i];
            BitConverter.TryWriteBytes(fixture.Bytes.AsSpan(address), positions[i].X);
            BitConverter.TryWriteBytes(fixture.Bytes.AsSpan(address + 4), positions[i].Y);
            BitConverter.TryWriteBytes(fixture.Bytes.AsSpan(address + 8), positions[i].Z);
        }
        var offset = new Vector3(.04f, 0, 0);
        var triangle = new SurfaceTriangle(Vector3.Zero, Vector3.UnitX, Vector3.UnitY,
            offset, Vector3.UnitX + offset, Vector3.UnitY + offset);
        var prepared = new PreparedConversion(new TriangleIndex([triangle]),
            new() { Clearance = 0, MaximumDistance = .25f }, "test", [], ["/body.mtrl"]);
        var result = MdlDocument.Parse(prepared.Convert(fixture.Bytes).ModelData).Meshes[0].Positions;
        Assert.True(Vector3.Distance(result[0], positions[0] + offset) < .000001f);
        Assert.True(Vector3.Distance(result[1], positions[1] + offset * (skin ? 1 : .5f)) < .000001f);
        Assert.Equal(positions[2], result[2]);
    }
}
