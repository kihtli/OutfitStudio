using System.Numerics;
using OutfitStudio.Core.Geometry;

namespace OutfitStudio.Tests;

public sealed class GarmentContactTests
{
    [Fact]
    public void NearerUndersideDoesNotPullAnAlreadyClearConnectedRibbonIntoSkin()
    {
        var model = Ribbon();
        var output = MoveBody(model, 0.0003f);
        var before = (byte[])output.Clone();

        var result = GarmentClearance.Refine(model, output, new HashSet<string> { "/body.mtrl" }, 0.0025f, default);

        Assert.Equal(3, result.Samples);
        Assert.Equal(0, result.UnresolvedSamples);
        // The inward-facing underside is only 0.7 mm from the skin, but the
        // exterior already clears it by 2.7 mm. Moving the connected ribbon to
        // satisfy the underside's reversed normal would spoil this valid fit.
        var cloth = model.Meshes[1];
        for (int vertex = 0; vertex < cloth.VertexCount; vertex++)
            Assert.Equal(Position(cloth, before, vertex), Position(cloth, output, vertex));
        AssertExteriorClearance(model, output);
    }

    [Theory]
    [InlineData(0.0015f, false)]
    [InlineData(0.0015f, true)]
    [InlineData(0.005f, false)]
    [InlineData(0.005f, true)]
    public void ConnectedRibbonFitsItsExteriorEvenWhenTargetSkinNormalsPointInward(float bodyHeight, bool reverseTargetNormals)
    {
        var model = Ribbon();
        var output = MoveBody(model, bodyHeight, reverseTargetNormals);
        var bodyBefore = output.AsSpan(0, model.Meshes[0].VertexCount * 40).ToArray();

        var result = GarmentClearance.Refine(model, output, new HashSet<string> { "/body.mtrl" }, 0.0025f, default);

        Assert.Equal(3, result.Samples);
        Assert.Equal(0, result.UnresolvedSamples);
        AssertExteriorClearance(model, output);
        Assert.Equal(bodyBefore, output.AsSpan(0, bodyBefore.Length).ToArray());
    }

    [Theory]
    [InlineData(0.1f)]
    [InlineData(2f)]
    public void ContactOrientationUsesTheSourceNormalDirectionRatherThanItsMagnitude(float normalLength)
    {
        var unit = Ribbon();
        var scaled = Ribbon(normalLength);
        var unitOutput = MoveBody(unit, 0.0015f);
        var scaledOutput = MoveBody(scaled, 0.0015f);

        var unitResult = GarmentClearance.Refine(unit, unitOutput, new HashSet<string> { "/body.mtrl" }, 0.0025f, default);
        var scaledResult = GarmentClearance.Refine(scaled, scaledOutput, new HashSet<string> { "/body.mtrl" }, 0.0025f, default);

        Assert.Equal(unitResult.Samples, scaledResult.Samples);
        Assert.Equal(3, scaledResult.Samples);
        Assert.Equal(0, scaledResult.UnresolvedSamples);
        for (int vertex = 0; vertex < unit.Meshes[1].VertexCount; vertex++)
            Assert.True(Vector3.Distance(Position(unit.Meshes[1], unitOutput, vertex),
                Position(scaled.Meshes[1], scaledOutput, vertex)) < 1e-6f);
        AssertExteriorClearance(scaled, scaledOutput);
    }

    [Theory]
    [InlineData(100f)]
    [InlineData(140f)]
    public void OutwardContactDirectionFollowsTheGarmentThroughALargeRotation(float degrees)
    {
        var model = Ribbon(extent: 0.1f);
        var output = (byte[])model.Data.Clone();
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees * MathF.PI / 180);
        var rotatedOutward = Vector3.Transform(Vector3.UnitZ, rotation);
        Assert.True(Vector3.Dot(rotatedOutward, Vector3.UnitZ) < 0);
        foreach (var mesh in model.Meshes)
            for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
            {
                var position = mesh.Positions[vertex];
                if (mesh.Index == 0) position.Z += 0.005f;
                MdlDocument.WriteVector(output, mesh.Address(mesh.Position, vertex), 2,
                    Vector3.Transform(position, rotation), false);
                if (mesh.Index == 0)
                    MdlDocument.WriteVector(output, mesh.StreamOffsets[0] + vertex * 40 + 12, 2, -rotatedOutward, true);
            }

        var result = GarmentClearance.Refine(model, output, new HashSet<string> { "/body.mtrl" }, 0.0025f, default);

        Assert.Equal(3, result.Samples);
        Assert.Equal(0, result.UnresolvedSamples);
        // Inspect coverage in the independently known rotated frame. The current
        // exterior faces away from the original world-space skin direction, and
        // deliberately reversed target normals cannot supply the contact guide.
        AssertExteriorClearance(model, output, Quaternion.Inverse(rotation));
    }

    private static void AssertExteriorClearance(MdlDocument model, byte[] output, Quaternion? inspectionRotation = null)
    {
        var cloth = model.Meshes[1];
        Vector3 Inspect(MdlDocument.Mesh mesh, int vertex)
        {
            var position = Position(mesh, output, vertex);
            return inspectionRotation is { } rotation ? Vector3.Transform(position, rotation) : position;
        }
        foreach (int vertex in model.Meshes[0].Indices.Distinct())
        {
            var body = Inspect(model.Meshes[0], vertex);
            var heights = new List<float>();
            // Intersect a vertical ray with the two known exterior faces. This
            // audit deliberately does not use the production nearest-face search
            // or count the inward-facing underside as the visible cover.
            for (int face = 0; face < 2; face++)
            {
                var a = Inspect(cloth, cloth.Indices[face * 3]);
                var b = Inspect(cloth, cloth.Indices[face * 3 + 1]);
                var c = Inspect(cloth, cloth.Indices[face * 3 + 2]);
                float determinant = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                Assert.True(MathF.Abs(determinant) > 1e-9f);
                float u = ((b.Y - c.Y) * (body.X - c.X) + (c.X - b.X) * (body.Y - c.Y)) / determinant;
                float v = ((c.Y - a.Y) * (body.X - c.X) + (a.X - c.X) * (body.Y - c.Y)) / determinant;
                float w = 1 - u - v;
                if (MathF.Min(u, MathF.Min(v, w)) >= -1e-6f) heights.Add(a.Z * u + b.Z * v + c.Z * w);
            }
            Assert.NotEmpty(heights);
            Assert.True(heights.Max() - body.Z >= 0.00247f,
                $"Ribbon exterior remains too close to skin: {heights.Max() - body.Z:G9}.");
        }
    }

    private static byte[] MoveBody(MdlDocument model, float height, bool reverseNormals = false)
    {
        var output = (byte[])model.Data.Clone();
        var body = model.Meshes[0];
        for (int vertex = 0; vertex < body.VertexCount; vertex++)
        {
            MdlDocument.WriteVector(output, body.Address(body.Position, vertex), 2,
                body.Positions[vertex] + new Vector3(0, 0, height), false);
            if (reverseNormals) MdlDocument.WriteVector(output, body.StreamOffsets[0] + vertex * 40 + 12, 2, -Vector3.UnitZ, true);
        }
        return output;
    }

    private static Vector3 Position(MdlDocument.Mesh mesh, byte[] data, int vertex)
        => MdlDocument.ReadVector(data, mesh.Address(mesh.Position, vertex), 2, false);

    private static MdlDocument Ribbon(float sourceNormalLength = 1, float extent = 0.01f)
    {
        var body = Mesh(0, 0,
            [new(-0.0013f, -0.0007f, 0), new(0.0011f, -0.0003f, 0), new(-0.0006f, 0.0014f, 0)], [0, 1, 2]);
        // A thin closed strip: exterior at 3 mm, underside at 1 mm, connected
        // around its edges. Contact support can legitimately reach both sides.
        var cloth = Mesh(1, body.VertexCount * 40,
            [new(-extent, -extent, 0.003f), new(extent, -extent, 0.003f), new(extent, extent, 0.003f), new(-extent, extent, 0.003f),
             new(-extent, -extent, 0.001f), new(extent, -extent, 0.001f), new(extent, extent, 0.001f), new(-extent, extent, 0.001f)],
            [0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6, 0, 4, 5, 0, 5, 1, 1, 5, 6, 1, 6, 2, 2, 6, 7, 2, 7, 3, 3, 7, 4, 3, 4, 0]);
        var data = new byte[(body.VertexCount + cloth.VertexCount) * 40];
        foreach (var mesh in new[] { body, cloth })
            for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
            {
                int address = mesh.StreamOffsets[0] + vertex * 40;
                MdlDocument.WriteVector(data, address, 2, mesh.Positions[vertex], false);
                var normal = mesh == body ? Vector3.UnitZ * sourceNormalLength : vertex < 4 ? Vector3.UnitZ : -Vector3.UnitZ;
                MdlDocument.WriteVector(data, address + 12, 2, normal, true);
                MdlDocument.WriteVector(data, address + 24, 8, Vector3.UnitX, true);
            }
        return new() { Data = data, Version = 0x01000005, LodCount = 1, ModelHeaderOffset = 0, BoundsOffset = 0,
            BoneCount = 0, ShapeCount = 0, Meshes = [body, cloth], Materials = ["/body.mtrl", "/dress.mtrl"] };
    }

    private static MdlDocument.Mesh Mesh(int index, int offset, Vector3[] positions, ushort[] indices) => new()
    {
        Index = index, Lod = 0, Material = index, VertexCount = positions.Length, StartIndex = 0,
        StreamOffsets = [offset, 0, 0], Strides = [40, 0, 0],
        Elements = [new(0, 0, 2, 0, 0), new(0, 12, 2, 3, 0), new(0, 24, 8, 6, 0)],
        Positions = positions, Indices = indices, Uvs = positions.Select(p => new Vector2(p.X, p.Y)).ToArray(),
    };
}
