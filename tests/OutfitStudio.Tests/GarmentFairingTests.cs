using System.Numerics;
using OutfitStudio.Core.Geometry;

namespace OutfitStudio.Tests;

public sealed class GarmentFairingTests
{
    [Fact]
    public void FairingPreservesOriginalDetailUnderUniformTranslation()
    {
        var mesh = Grid(9, 0.01f, detail: true);
        var translation = new Vector3(0.01f, -0.02f, 0.03f);
        var before = mesh.Positions.Select(p => p + translation).ToArray();
        var output = (Vector3[])before.Clone();
        new GarmentFairing(mesh, Groups(mesh), Enumerable.Repeat(true, mesh.VertexCount).ToArray(), default)
            .Smooth(before, output, 0.01f, default);
        for (int i = 0; i < output.Length; i++) Assert.True(Vector3.Distance(before[i], output[i]) < 1e-6f);
    }

    [Fact]
    public void FairingRemovesAnIsolatedDeformationSpikeAndPreservesPinnedVertices()
    {
        var mesh = Grid(17, 0.0025f);
        var eligible = Enumerable.Range(0, mesh.VertexCount).Select(v => v % 17 is > 0 and < 16 && v / 17 is > 0 and < 16).ToArray();
        var before = (Vector3[])mesh.Positions.Clone();
        int center = 8 * 17 + 8;
        before[center] += new Vector3(0, 0, 0.02f);
        var output = (Vector3[])before.Clone();
        new GarmentFairing(mesh, Groups(mesh), eligible, default).Smooth(before, output, 0.01f, default);
        Assert.True(output[center].Z < 0.005f);
        Assert.True(output[center + 1].Z > 0);
        for (int i = 0; i < output.Length; i++)
        {
            Assert.True(MdlDocument.Finite(output[i]));
            if (!eligible[i]) Assert.Equal(before[i], output[i]);
        }
    }

    [Fact]
    public void ContactSupportDoesNotCrossIntoANearbyDisconnectedLayer()
    {
        var first = Grid(5, 0.005f);
        int count = first.VertexCount;
        var positions = first.Positions.Concat(first.Positions.Select(p => p + new Vector3(0, 0, 0.0001f))).ToArray();
        var indices = first.Indices.Concat(first.Indices.Select(v => (ushort)(v + count))).ToArray();
        var mesh = MakeMesh(0, 0, 0, positions, indices);
        var fairing = new GarmentFairing(mesh, Groups(mesh), Enumerable.Repeat(true, mesh.VertexCount).ToArray(), default);
        var patch = fairing.Patch(indices[0], indices[1], indices[2], new(1f / 3), 0.06f, default);
        Assert.NotEmpty(patch);
        Assert.All(patch, value => { Assert.InRange(value.Vertex, 0, count - 1); Assert.InRange(value.Weight, 0f, 1f); });
    }

    [Fact]
    public void FinalAuthoredFramesRetainInPlaneUvRotationAndTangentHandedness()
    {
        var clothGrid = Grid(9, 0.01f);
        var body = MakeMesh(0, 0, 0, [new(-0.0031f, 0.0017f, 0), new(0.0015f, -0.0028f, 0), new(0.0024f, 0.0036f, 0)], [0, 1, 2]);
        var cloth = MakeMesh(1, 1, body.VertexCount * 40,
            clothGrid.Positions.Select(p => p + new Vector3(0, 0, 0.001f)).ToArray(), clothGrid.Indices);
        var data = new byte[(body.VertexCount + cloth.VertexCount) * 40];
        foreach (var mesh in new[] { body, cloth })
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                int address = mesh.StreamOffsets[0] + i * 40;
                MdlDocument.WriteVector(data, address, 2, mesh.Positions[i], false);
                MdlDocument.WriteVector(data, address + 12, 2, Vector3.UnitZ, true);
                MdlDocument.WriteVector(data, address + 24, 8, Vector3.UnitX, true);
                data[address + 27] = 137;
            }
        var model = new MdlDocument { Data = data, Version = 0x01000005, LodCount = 1, ModelHeaderOffset = 0, BoundsOffset = 0,
            BoneCount = 0, ShapeCount = 0, Meshes = [body, cloth], Materials = ["/body.mtrl", "/dress.mtrl"] };
        var output = (byte[])data.Clone();
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        foreach (var mesh in model.Meshes)
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                int address = mesh.StreamOffsets[0] + i * 40;
                var position = Vector3.Transform(mesh.Positions[i], rotation);
                if (mesh == cloth) position.Z += 0.003f;
                MdlDocument.WriteVector(output, address, 2, position, false);
                // The body field's wrong cloth normal must not survive into the final frame.
                if (mesh == cloth) MdlDocument.WriteVector(output, address + 12, 2, Vector3.UnitX, true);
                MdlDocument.WriteVector(output, address + 24, 8, Vector3.UnitY, true);
            }
        var bound = new Dictionary<int, bool[]>
        {
            [0] = [true, true, true],
            [1] = Enumerable.Range(0, cloth.VertexCount).Select(v => v % 9 is > 0 and < 8 && v / 9 is > 0 and < 8).ToArray(),
        };
        GarmentClearance.Refine(model, output, new HashSet<string> { "/body.mtrl" }, 0.0025f, default, bound);
        int centerAddress = cloth.StreamOffsets[0] + 40 * (4 * 9 + 4);
        var normal = MdlDocument.ReadVector(output, centerAddress + 12, 2, true);
        var tangent = MdlDocument.ReadVector(output, centerAddress + 24, 8, true);
        Assert.True(normal.Z > 0.999f);
        Assert.True(tangent.Y > 0.99f && MathF.Abs(tangent.X) < 0.01f);
        Assert.True(MathF.Abs(Vector3.Dot(normal, tangent)) < 0.006f);
        Assert.Equal(137, output[centerAddress + 27]);
    }

    [Fact]
    public void CoveredTargetFeatureCanExceedTheDefaultContactBound()
    {
        var grid = Grid(17, 0.005f);
        var body = MakeMesh(0, 0, 0, [new(-0.0031f, 0.0017f, 0), new(0.0015f, -0.0028f, 0), new(0.0024f, 0.0036f, 0)], [0, 1, 2]);
        var cloth = MakeMesh(1, 1, body.VertexCount * 40,
            grid.Positions.Select(p => p + new Vector3(0, 0, 0.001f)).ToArray(), grid.Indices);
        var data = new byte[(body.VertexCount + cloth.VertexCount) * 40];
        foreach (var mesh in new[] { body, cloth })
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                int address = mesh.StreamOffsets[0] + i * 40;
                MdlDocument.WriteVector(data, address, 2, mesh.Positions[i], false);
                MdlDocument.WriteVector(data, address + 12, 2, Vector3.UnitZ, true);
                MdlDocument.WriteVector(data, address + 24, 8, Vector3.UnitX, true);
            }
        var model = new MdlDocument { Data = data, Version = 0x01000005, LodCount = 1, ModelHeaderOffset = 0, BoundsOffset = 0,
            BoneCount = 0, ShapeCount = 0, Meshes = [body, cloth], Materials = ["/body.mtrl", "/dress.mtrl"] };
        var output = (byte[])data.Clone();
        for (int i = 0; i < body.VertexCount; i++)
            MdlDocument.WriteVector(output, body.Address(body.Position, i), 2, body.Positions[i] + new Vector3(0, 0, 0.03f), false);
        var result = GarmentClearance.Refine(model, output, new HashSet<string> { "/body.mtrl" }, 0.0025f, default);
        Assert.Equal(3, result.Samples);
        Assert.Equal(0, result.UnresolvedSamples);
        Assert.InRange(result.MaximumAdjustment, 0.03f, 0.1f);
        var positions = Enumerable.Range(0, cloth.VertexCount)
            .Select(i => MdlDocument.ReadVector(output, cloth.Address(cloth.Position, i), 2, false)).ToArray();
        var triangles = Enumerable.Range(0, cloth.Indices.Length / 3).Select(face =>
        {
            var a = positions[cloth.Indices[face * 3]];
            var b = positions[cloth.Indices[face * 3 + 1]];
            var c = positions[cloth.Indices[face * 3 + 2]];
            return new SurfaceTriangle(a, b, c, a, b, c);
        }).ToArray();
        var surface = new TriangleIndex(triangles);
        foreach (var originalPoint in body.Positions)
        {
            var point = originalPoint + new Vector3(0, 0, 0.03f);
            var hit = surface.Nearest(point, 0.1f * 0.1f);
            Assert.NotNull(hit);
            var triangle = hit.Value.Triangle;
            var normal = Vector3.Normalize(Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A));
            Assert.True(Vector3.Dot(triangle.Sample(hit.Value.Barycentric) - point, normal) >= 0.00247f);
        }
        Assert.All(positions, value => Assert.True(MdlDocument.Finite(value)));
    }

    [Fact]
    public void LocalContactKeepsAnAlreadyFittedSideCloseWhileCoveringARaisedFeature()
    {
        var bodyGrid = Grid(21, 0.004f);
        var body = MakeMesh(0, 0, 0,
            bodyGrid.Positions.Select(p => p + new Vector3(0.0013f, 0.0017f, 0)).ToArray(), bodyGrid.Indices);
        var clothGrid = Grid(17, 0.005f);
        var cloth = MakeMesh(1, 1, body.VertexCount * 40,
            clothGrid.Positions.Select(p => p + new Vector3(0, 0, 0.001f)).ToArray(), clothGrid.Indices);
        var data = new byte[(body.VertexCount + cloth.VertexCount) * 40];
        foreach (var mesh in new[] { body, cloth })
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                int address = mesh.StreamOffsets[0] + i * 40;
                MdlDocument.WriteVector(data, address, 2, mesh.Positions[i], false);
                MdlDocument.WriteVector(data, address + 12, 2, Vector3.UnitZ, true);
                MdlDocument.WriteVector(data, address + 24, 8, Vector3.UnitX, true);
            }
        var model = new MdlDocument { Data = data, Version = 0x01000005, LodCount = 1, ModelHeaderOffset = 0, BoundsOffset = 0,
            BoneCount = 0, ShapeCount = 0, Meshes = [body, cloth], Materials = ["/body.mtrl", "/dress.mtrl"] };
        var output = (byte[])data.Clone();
        for (int i = 0; i < body.VertexCount; i++)
        {
            var position = body.Positions[i];
            position.Z = 0.022f * MathF.Exp(-(position.X * position.X + position.Y * position.Y) / (0.006f * 0.006f));
            MdlDocument.WriteVector(output, body.Address(body.Position, i), 2, position, false);
        }
        var result = GarmentClearance.Refine(model, output, new HashSet<string> { "/body.mtrl" }, 0.0025f, default);
        Assert.True(result.Samples > 100);
        Assert.Equal(0, result.UnresolvedSamples);
        // The bump needs more than 0.02 of movement, but repeated broad contact
        // pushes must not inflate the nearby flat, originally close-fitting side.
        Assert.InRange(result.MaximumAdjustment, 0.02f, 0.1f);
        var side = MdlDocument.ReadVector(output, cloth.Address(cloth.Position, 8 * 17 + 14), 2, false);
        Assert.InRange(side.Z, 0.00247f, 0.006f);
    }

    private static List<int>[] Groups(MdlDocument.Mesh mesh) => Enumerable.Range(0, mesh.VertexCount).Select(v => new List<int> { v }).ToArray();

    private static MdlDocument.Mesh Grid(int side, float spacing, bool detail = false)
    {
        var positions = Enumerable.Range(0, side * side).Select(i => new Vector3((i % side - side / 2) * spacing,
            (i / side - side / 2) * spacing, detail ? 0.002f * MathF.Cos(i % side * 2) : 0)).ToArray();
        var indices = new List<ushort>();
        for (int y = 0; y < side - 1; y++)
            for (int x = 0; x < side - 1; x++)
            {
                int a = y * side + x, b = a + 1, c = a + side, d = c + 1;
                indices.AddRange([(ushort)a, (ushort)b, (ushort)c, (ushort)b, (ushort)d, (ushort)c]);
            }
        return MakeMesh(0, 0, 0, positions, indices.ToArray());
    }

    private static MdlDocument.Mesh MakeMesh(int index, int material, int address, Vector3[] positions, ushort[] indices) => new()
    {
        Index = index, Lod = 0, VertexCount = positions.Length, Material = material, StartIndex = 0,
        StreamOffsets = [address, 0, 0], Strides = [40, 0, 0], Elements = [new(0, 0, 2, 0, 0), new(0, 12, 2, 3, 0), new(0, 24, 8, 6, 0)],
        Indices = indices, Positions = positions, Uvs = positions.Select(p => new Vector2(p.X, p.Y)).ToArray(),
    };
}
