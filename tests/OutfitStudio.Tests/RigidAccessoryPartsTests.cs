using System.Numerics;
using OutfitStudio.Core.Geometry;

namespace OutfitStudio.Tests;

public sealed class RigidAccessoryPartsTests
{
    private static readonly HashSet<string> Skin = ["/skin.mtrl"];
    private static readonly Vector3[] Triangle = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];

    [Fact]
    public void RigidPartRestoresAllInternalDistancesAfterNonuniformFitting()
    {
        var model = Document(new Spec(Triangle));
        var output = Deform(model, (_, position) => new(position.X * 2 + 3, position.Y * .5f - 2, .4f));
        var result = RigidAccessoryParts.Apply(model, output, Skin);
        Assert.Equal(1, result.Assemblies);
        Assert.Equal(1, result.Components);
        Assert.True(result.AdjustedVertices > 0);
        AssertDistances(model.Meshes[0].Positions, Positions(model.Meshes[0], output));
        AssertUnrelatedBytes(model, output);
    }

    [Fact]
    public void DisconnectedUniformlyWeightedPiecesRemainOneCoherentAssembly()
    {
        var first = Triangle;
        var second = Triangle.Select(position => position + new Vector3(3, 0, 0)).ToArray();
        var model = Document(new Spec(first, Weights: Uniform(3, "left")), new(second, Weights: Uniform(3, "left")));
        var output = Deform(model, (mesh, position) => position + new Vector3(mesh.Index == 0 ? 0 : 2, 0, .3f));
        var result = RigidAccessoryParts.Apply(model, output, Skin);
        Assert.Equal(2, result.Components);
        Assert.Equal(1, result.Assemblies);
        AssertDistances(first.Concat(second).ToArray(), model.Meshes.SelectMany(mesh => Positions(mesh, output)).ToArray());
        AssertUnrelatedBytes(model, output);
    }

    [Fact]
    public void VaryingSkinWeightsKeepDisconnectedLinksIndependent()
    {
        var second = Triangle.Select(position => position + new Vector3(3, 0, 0)).ToArray();
        var weights = new MdlDocument.BoneInfluence[][]
        {
            [new("left", .75f), new("right", .25f)],
            [new("left", .5f), new("right", .5f)],
            [new("left", .25f), new("right", .75f)],
        };
        var model = Document(new Spec(Triangle, Weights: weights), new(second, Weights: weights));
        var output = Deform(model, (mesh, position) => position + new Vector3(mesh.Index * 2, 0, .3f));
        byte[] fitted = (byte[])output.Clone();
        var result = RigidAccessoryParts.Apply(model, output, Skin);
        Assert.Equal(2, result.Assemblies);
        foreach (var mesh in model.Meshes)
        {
            AssertDistances(mesh.Positions, Positions(mesh, output));
            for (int i = 0; i < mesh.VertexCount; i++)
                Assert.True(Vector3.Distance(Read(mesh, fitted, i), Read(mesh, output, i)) < 1e-5f);
        }
    }

    [Fact]
    public void DifferentExactUniformWeightsDoNotMergeAssemblies()
    {
        var second = Triangle.Select(position => position + new Vector3(3, 0, 0)).ToArray();
        var model = Document(new Spec(Triangle, Weights: Uniform(3, "left")), new(second, Weights: Uniform(3, "right")));
        var output = Deform(model, (mesh, position) => position + new Vector3(mesh.Index * 2, 0, 0));
        Assert.Equal(2, RigidAccessoryParts.Apply(model, output, Skin).Assemblies);
    }

    [Fact]
    public void UnskinnedDisconnectedPiecesAreNotMerged()
    {
        var model = Document(new Spec(Triangle), new(Triangle.Select(position => position + new Vector3(3, 0, 0)).ToArray()));
        var output = Deform(model, (mesh, position) => position + new Vector3(mesh.Index * 2, 0, 0));
        Assert.Equal(2, RigidAccessoryParts.Apply(model, output, Skin).Assemblies);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(.0000005f)]
    public void UvAndMaterialMeshSplitsShareOneRigidFrame(float rounding)
    {
        var second = new Vector3[] { new(rounding, 0, 0), -Vector3.UnitX, -Vector3.UnitY };
        var model = Document(new Spec(Triangle, Material: "/metal-a.mtrl"), new(second, Material: "/metal-b.mtrl"));
        var output = Deform(model, (mesh, position) => position + new Vector3(mesh.Index, 0, .3f));
        var result = RigidAccessoryParts.Apply(model, output, Skin);
        Assert.Equal(1, result.Assemblies);
        Assert.Equal(1, result.Components);
        AssertDistances(Triangle.Concat(second).ToArray(), model.Meshes.SelectMany(mesh => Positions(mesh, output)).ToArray());
    }

    [Fact]
    public void ShapeAndUnusedVerticesFollowTheirPartWithoutInfluencingFit()
    {
        Vector3[] positions = [.. Triangle, new(0, 0, 4), new(.1f, .1f, 5)];
        var model = Document(new Spec(positions, ShapeReplacement: 3));
        var output = Deform(model, (_, position) => position.Z > 1 ? new Vector3(100, 100, 100) : position + new Vector3(2, 3, 4));
        RigidAccessoryParts.Apply(model, output, Skin);
        for (int i = 0; i < positions.Length; i++)
            Assert.True(Vector3.Distance(Read(model.Meshes[0], output, i), positions[i] + new Vector3(2, 3, 4)) < 1e-5f);
        AssertUnrelatedBytes(model, output);
    }

    [Fact]
    public void CoincidentLodsNeverShareAssembliesEvenWithSameWeights()
    {
        var model = Document(new Spec(Triangle, Weights: Uniform(3, "left")), new(Triangle, Lod: 1, Weights: Uniform(3, "left")));
        var output = Deform(model, (mesh, position) => position + new Vector3(mesh.Lod * 4, 0, .3f));
        var result = RigidAccessoryParts.Apply(model, output, Skin);
        Assert.Equal(2, result.Assemblies);
        foreach (var mesh in model.Meshes)
            for (int i = 0; i < mesh.VertexCount; i++)
                Assert.True(Vector3.Distance(Read(mesh, output, i), mesh.Positions[i] + new Vector3(mesh.Lod * 4, 0, .3f)) < 1e-5f);
    }

    [Fact]
    public void EmbeddedSkinRetainsTheEarlierDeformationByteForByte()
    {
        var model = Document(new Spec(Triangle, Material: "/skin.mtrl"), new(Triangle, Material: "/metal.mtrl"));
        var output = Deform(model, (_, position) => new(position.X * 2, position.Y * .5f, .3f));
        byte[] before = (byte[])output.Clone();
        var result = RigidAccessoryParts.Apply(model, output, Skin);
        Assert.Equal(1, result.Assemblies);
        var skin = model.Meshes[0];
        Assert.Equal(before.AsSpan(skin.StreamOffsets[0], skin.VertexCount * skin.Strides[0]).ToArray(),
            output.AsSpan(skin.StreamOffsets[0], skin.VertexCount * skin.Strides[0]).ToArray());
        AssertDistances(model.Meshes[1].Positions, Positions(model.Meshes[1], output));
    }

    [Fact]
    public void NormalsAndTangentsAreRotatedFromAuthoredValues()
    {
        var model = Document(new Spec(Triangle));
        var transform = Matrix4x4.CreateRotationX(.7f) * Matrix4x4.CreateTranslation(2, 3, 4);
        var output = Deform(model, (_, position) => Vector3.Transform(position, transform));
        foreach (var mesh in model.Meshes)
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                MdlDocument.WriteVector(output, mesh.StreamOffsets[0] + i * mesh.Strides[0] + 12, 2, Vector3.UnitX, true);
                MdlDocument.WriteVector(output, mesh.StreamOffsets[0] + i * mesh.Strides[0] + 24, 8, Vector3.UnitZ, true);
            }
        RigidAccessoryParts.Apply(model, output, Skin);
        foreach (var mesh in model.Meshes)
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                int address = mesh.StreamOffsets[0] + i * mesh.Strides[0];
                Assert.True(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitZ, transform), MdlDocument.ReadVector(output, address + 12, 2, true)) < 1e-5f);
                var authoredTangent = MdlDocument.ReadVector(model.Data, address + 24, 8, true);
                var expected = Vector3.Normalize(Vector3.TransformNormal(authoredTangent, transform));
                Assert.True(Vector3.Distance(expected, MdlDocument.ReadVector(output, address + 24, 8, true)) < .015f);
            }
    }

    [Fact]
    public void CancellationIsObservedBeforeMutatingOutput()
    {
        var model = Document(new Spec(Triangle));
        var output = (byte[])model.Data.Clone();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => RigidAccessoryParts.Apply(model, output, Skin, cancellation.Token));
        Assert.Equal(model.Data, output);
    }

    [Fact]
    public void LinkPlacementUsesAnchorWeightsInsteadOfDistortedFittedCenter()
    {
        var model = AnchoredLink(false);
        var output = Deform(model, (mesh, position) => position + (mesh.Index == 0 ? new Vector3(0, 0, 2)
            : mesh.Index == 1 ? new Vector3(0, 0, 4) : new Vector3(40, 40, 40)));
        var result = RigidAccessoryParts.Apply(model, output, Skin);
        Assert.Equal(1, result.BlendedParts);
        foreach (var mesh in model.Meshes)
        {
            float translation = mesh.Index == 0 ? 2 : mesh.Index == 1 ? 4 : 3;
            for (int i = 0; i < mesh.VertexCount; i++)
                Assert.True(Vector3.Distance(Read(mesh, output, i), mesh.Positions[i] + new Vector3(0, 0, translation)) < 1e-5f);
            AssertDistances(mesh.Positions, Positions(mesh, output));
        }
        AssertUnrelatedBytes(model, output);
    }

    [Fact]
    public void SharedBoneWeightChangesDoNotDistortAnchorInterpolation()
    {
        var model = AnchoredLink(true);
        var output = Deform(model, (mesh, position) => position + new Vector3(0, 0, mesh.Index == 0 ? 2 : mesh.Index == 1 ? 4 : 40));
        Assert.Equal(1, RigidAccessoryParts.Apply(model, output, Skin).BlendedParts);
        var link = model.Meshes[2];
        for (int i = 0; i < link.VertexCount; i++)
            Assert.True(Vector3.Distance(Read(link, output, i), link.Positions[i] + new Vector3(0, 0, 3)) < 1e-5f);
    }

    [Fact]
    public void AnchorRotationInterpolationKeepsLinksRigid()
    {
        const float angle = .4f;
        var model = AnchoredLink(false);
        var output = Deform(model, (mesh, position) => mesh.Index < 2
            ? Vector3.Transform(position, Matrix4x4.CreateRotationZ(mesh.Index == 0 ? angle : -angle))
            : position + new Vector3(20, 20, 20));
        Assert.Equal(1, RigidAccessoryParts.Apply(model, output, Skin).BlendedParts);
        var link = model.Meshes[2];
        var center = link.Positions.Aggregate(Vector3.Zero, (sum, point) => sum + point) / link.VertexCount;
        var translation = center * (MathF.Cos(angle) - 1);
        for (int i = 0; i < link.VertexCount; i++)
            Assert.True(Vector3.Distance(Read(link, output, i), link.Positions[i] + translation) < 1e-5f);
        AssertDistances(link.Positions, Positions(link, output));
    }

    [Fact]
    public void UnsupportedMixtureOfKnownAnchorChannelsKeepsIndependentFit()
    {
        MdlDocument.BoneInfluence[][] uniform(params MdlDocument.BoneInfluence[] weights)
            => Enumerable.Range(0, 3).Select(_ => weights).ToArray();
        var linkWeights = new MdlDocument.BoneInfluence[][]
        {
            [new("left", .1f), new("right", .4f), new("other", .5f)],
            [new("left", .1f), new("right", .5f), new("other", .4f)],
            [new("left", .1f), new("right", .45f), new("other", .45f)],
        };
        var model = Document(new Spec(Triangle.Select(p => p - new Vector3(3, 0, 0)).ToArray(),
                Weights: uniform(new("left", .8f), new("right", .2f))),
            new(Triangle.Select(p => p + new Vector3(3, 0, 0)).ToArray(), Weights: uniform(new("left", .2f), new("other", .8f))),
            new(Triangle, Weights: linkWeights));
        var output = Deform(model, (mesh, point) => point + new Vector3(0, 0, mesh.Index == 2 ? 20 : mesh.Index));
        Assert.Equal(0, RigidAccessoryParts.Apply(model, output, Skin).BlendedParts);
        var link = model.Meshes[2];
        for (int i = 0; i < link.VertexCount; i++)
            Assert.True(Vector3.Distance(Read(link, output, i), link.Positions[i] + new Vector3(0, 0, 20)) < 1e-5f);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnknownBonesOrDifferentLodsLeaveIndependentRigidFit(bool unknownBone)
    {
        var model = AnchoredLink(false, unknownBone: unknownBone, linkLod: unknownBone ? 0 : 1);
        var output = Deform(model, (mesh, position) => position + new Vector3(0, 0, mesh.Index == 0 ? 2 : mesh.Index == 1 ? 4 : 40));
        Assert.Equal(0, RigidAccessoryParts.Apply(model, output, Skin).BlendedParts);
        var link = model.Meshes[2];
        for (int i = 0; i < link.VertexCount; i++)
            Assert.True(Vector3.Distance(Read(link, output, i), link.Positions[i] + new Vector3(0, 0, 40)) < 1e-5f);
    }

    private static MdlDocument AnchoredLink(bool sharedBone, bool unknownBone = false, int linkLod = 0)
    {
        var left = Triangle.Select(point => point + new Vector3(-3, 0, 0)).ToArray();
        var right = Triangle.Select(point => point + new Vector3(3, 0, 0)).ToArray();
        MdlDocument.BoneInfluence[][] anchor(string bone) => Enumerable.Range(0, 3).Select(_ => sharedBone
            ? new MdlDocument.BoneInfluence[] { new(bone, .8f), new("other", .2f) } : [new(bone, 1)]).ToArray();
        var weights = Enumerable.Range(0, 3).Select(i => sharedBone
            ? new MdlDocument.BoneInfluence[] { new("left", .3f - i * .1f), new("right", .1f + i * .1f), new("other", .6f) }
            : unknownBone ? [new("left", .4f - i * .1f), new("right", .4f + i * .1f), new("other", .2f)]
            : [new("left", .6f - i * .1f), new("right", .4f + i * .1f)]).ToArray();
        return Document(new Spec(left, Weights: anchor("left")), new(right, Weights: anchor("right")), new(Triangle, Lod: linkLod, Weights: weights));
    }

    private sealed record Spec(Vector3[] Positions, int Lod = 0, string Material = "/metal.mtrl",
        MdlDocument.BoneInfluence[][]? Weights = null, int ShapeReplacement = -1);

    private static MdlDocument Document(params Spec[] specifications)
    {
        const int stride = 56;
        byte[] bytes = Enumerable.Repeat((byte)0xA5, 64 + specifications.Sum(spec => spec.Positions.Length) * stride).ToArray();
        string[] bones = ["left", "right", "other"];
        for (int i = 0; i < bones.Length; i++) BitConverter.GetBytes((ushort)i).CopyTo(bytes, i * 2);
        int offset = 64;
        var meshes = new List<MdlDocument.Mesh>();
        for (int i = 0; i < specifications.Length; i++)
        {
            var spec = specifications[i];
            var elements = new List<MdlDocument.Element> { new(0, 0, 2, 0, 0), new(0, 12, 2, 3, 0), new(0, 24, 8, 6, 0), new(0, 28, 1, 4, 0) };
            if (spec.Weights is not null) { elements.Add(new(0, 36, 3, 1, 0)); elements.Add(new(0, 52, 8, 2, 0)); }
            meshes.Add(new() { Index = i, Lod = spec.Lod, VertexCount = spec.Positions.Length, Material = i, StartIndex = 0,
                StreamOffsets = [offset, 0, 0], Strides = [stride, 0, 0], Elements = elements.ToArray(),
                Indices = [0, 1, 2], Positions = spec.Positions, Uvs = null, BonePaletteIndex = 0 });
            offset += spec.Positions.Length * stride;
        }
        var model = new MdlDocument { Data = bytes, Version = 0x01000005, LodCount = specifications.Max(spec => spec.Lod) + 1,
            ModelHeaderOffset = 0, BoundsOffset = 0, BoneCount = bones.Length, ShapeCount = specifications.Count(spec => spec.ShapeReplacement >= 0),
            Meshes = meshes.ToArray(), Materials = specifications.Select(spec => spec.Material).ToArray(), Bones = bones,
            BonePalettes = [new(0, 0, [0, 1, 2], true)],
            ShapeBindings = specifications.Select((spec, index) => (spec, index)).Where(pair => pair.spec.ShapeReplacement >= 0)
                .Select(pair => new MdlDocument.ShapeBinding(pair.index, 0, pair.spec.ShapeReplacement)).ToArray() };
        foreach (var mesh in meshes)
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                int address = mesh.StreamOffsets[0] + i * stride;
                MdlDocument.WriteVector(bytes, address, 2, mesh.Positions[i], false);
                MdlDocument.WriteVector(bytes, address + 12, 2, Vector3.UnitZ, true);
                MdlDocument.WriteVector(bytes, address + 24, 8, Vector3.UnitX, true);
                MdlDocument.WriteFloat(bytes, address + 28, i * .1f); MdlDocument.WriteFloat(bytes, address + 32, i * .2f);
                if (specifications[mesh.Index].Weights is { } weights) model.WriteBoneWeights(bytes, mesh, i, weights[i]);
            }
        return model;
    }

    private static MdlDocument.BoneInfluence[][] Uniform(int count, string bone)
        => Enumerable.Range(0, count).Select(_ => new MdlDocument.BoneInfluence[] { new(bone, 1) }).ToArray();
    private static byte[] Deform(MdlDocument model, Func<MdlDocument.Mesh, Vector3, Vector3> transform)
    {
        byte[] output = (byte[])model.Data.Clone();
        foreach (var mesh in model.Meshes)
            for (int i = 0; i < mesh.VertexCount; i++)
                MdlDocument.WriteVector(output, mesh.Address(mesh.Position, i), mesh.Position.Type, transform(mesh, mesh.Positions[i]), false);
        return output;
    }
    private static Vector3 Read(MdlDocument.Mesh mesh, byte[] bytes, int vertex)
        => MdlDocument.ReadVector(bytes, mesh.Address(mesh.Position, vertex), mesh.Position.Type, false);
    private static Vector3[] Positions(MdlDocument.Mesh mesh, byte[] bytes)
        => Enumerable.Range(0, mesh.VertexCount).Select(vertex => Read(mesh, bytes, vertex)).ToArray();
    private static void AssertDistances(Vector3[] source, Vector3[] target)
    {
        Assert.Equal(source.Length, target.Length);
        for (int i = 0; i < source.Length; i++)
            for (int j = i + 1; j < source.Length; j++)
                Assert.True(MathF.Abs(Vector3.Distance(source[i], source[j]) - Vector3.Distance(target[i], target[j])) < 1e-5f);
    }
    private static void AssertUnrelatedBytes(MdlDocument model, byte[] output)
    {
        for (int address = 0; address < output.Length; address++)
        {
            bool geometry = model.Meshes.Any(mesh => Enumerable.Range(0, mesh.VertexCount).Any(vertex =>
                address >= mesh.StreamOffsets[0] + vertex * mesh.Strides[0] && address < mesh.StreamOffsets[0] + vertex * mesh.Strides[0] + 27));
            if (!geometry) Assert.Equal(model.Data[address], output[address]);
        }
    }
}
