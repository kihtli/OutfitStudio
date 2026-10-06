using System.Numerics;
using OutfitStudio.Core.Geometry;
using OutfitStudio.Core.Models;

namespace OutfitStudio.Tests;

public sealed class BoneWeightTransferTests
{
    private static readonly Vector3[] Triangle = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];

    [Fact]
    public void AffectedSkinUsesDestinationWeightsOnWholeMeshAndShapeVertices()
    {
        var target = Document(Triangle, ["j_asi_a_l", "j_kosi"],
            [[new("j_asi_a_l", 1)], [new("j_kosi", 1)], [new("j_kosi", 1)]]);
        Vector3[] positions = [new(.25f, .25f, 0), new(.5f, .25f, 0), new(.25f, .5f, 0), new(.4f, .4f, .01f)];
        var outfit = Document(positions, ["j_asi_a_l", "j_kosi", "ya_daitai_phys_l"],
            [[new("ya_daitai_phys_l", 1)], [new("j_asi_a_l", 1)], [new("j_asi_a_l", 1)], [new("ya_daitai_phys_l", 1)]], shape: true);
        var output = (byte[])outfit.Data.Clone();
        Assert.Equal(4, Apply(target, outfit, output));
        var weights = Read(outfit, output, 0);
        Assert.InRange(weights["j_asi_a_l"], .498f, .502f);
        Assert.InRange(weights["j_kosi"], .498f, .502f);
        for (int i = 0; i < 4; i++)
        {
            Assert.DoesNotContain(Read(outfit, output, i).Keys, BoneWeightTransfer.IsBodyExtension);
            Assert.Equal(255, output.AsSpan(64 + i * 20 + 12, 4).ToArray().Sum(b => b));
            Assert.Equal(outfit.Data.AsSpan(64 + i * 20, 12).ToArray(), output.AsSpan(64 + i * 20, 12).ToArray());
        }
        Assert.True(Read(outfit, output, 1)["j_kosi"] > .7f); // original vanilla-only seam vertex also follows target
    }

    [Fact]
    public void GarmentKeepsAuthoredSkirtMotionWhileMissingBodyPhysicsMassIsReplaced()
    {
        var target = Uniform(Triangle, "j_kosi");
        var outfit = Document(Triangle, ["j_kosi", "j_sk_b_b_l", "iv_shiri_l"],
            Enumerable.Range(0, 3).Select(_ => new MdlDocument.BoneInfluence[] { new("j_sk_b_b_l", 192f / 255), new("iv_shiri_l", 63f / 255) }).ToArray(), "/skirt.mtrl");
        var output = (byte[])outfit.Data.Clone();
        Assert.Equal(3, Apply(target, outfit, output));
        for (int i = 0; i < 3; i++)
        {
            var weights = Read(outfit, output, i);
            Assert.Equal(192f / 255, weights["j_sk_b_b_l"]);
            Assert.Equal(63f / 255, weights["j_kosi"]);
            Assert.DoesNotContain("iv_shiri_l", weights.Keys);
        }
    }

    [Fact]
    public void GarmentBodyMassFollowsDestinationWithoutAttenuatingBreastMotionTwice()
    {
        var target = Document(Triangle, ["j_mune_l", "j_sebo_b"],
            Enumerable.Range(0, 3).Select(_ => new MdlDocument.BoneInfluence[] { new("j_mune_l", .3f), new("j_sebo_b", .7f) }).ToArray());
        var outfit = Document(Triangle, ["iv_c_mune_l", "j_sebo_b", "j_mune_l"],
            [[new("iv_c_mune_l", .3f), new("j_sebo_b", .7f)],
                [new("j_sebo_b", 1)], // a vanilla-only seam in the same affected garment must remain consistent
                [new("iv_c_mune_l", .3f), new("j_sebo_b", .7f)]], "/dress.mtrl");
        var output = (byte[])outfit.Data.Clone();
        Assert.Equal(3, Apply(target, outfit, output, ["iv_c_mune_l", "j_sebo_b"]));
        var expected = target.GetBoneWeights(target.Meshes[0], 0).ToDictionary(w => w.Bone, w => w.Weight);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(expected, Read(outfit, output, i));
            Assert.InRange(Read(outfit, output, i)["j_mune_l"], .298f, .302f);
        }
    }

    [Fact]
    public void GarmentKeepsUnknownClothAndSkirtBonesWhileReplacingSourceOnlyBodyJoint()
    {
        var target = Uniform(Triangle, "j_mune_l");
        var outfit = Document(Triangle, ["iv_c_mune_l", "j_sebo_b", "cape_hinge", "j_sk_b_b_l", "j_mune_l"],
            Enumerable.Range(0, 3).Select(_ => new MdlDocument.BoneInfluence[] {
                new("iv_c_mune_l", 63f / 255), new("j_sebo_b", 64f / 255),
                new("cape_hinge", 64f / 255), new("j_sk_b_b_l", 64f / 255) }).ToArray(), "/dress.mtrl");
        var output = (byte[])outfit.Data.Clone();
        // A skirt chain stays authored even if an unusual body package happens to contain it.
        Assert.Equal(3, Apply(target, outfit, output, ["iv_c_mune_l", "j_sebo_b", "j_sk_b_b_l"]));
        for (int i = 0; i < 3; i++)
            Assert.Equal(new Dictionary<string, float> {
                ["j_mune_l"] = 127f / 255, ["cape_hinge"] = 64f / 255, ["j_sk_b_b_l"] = 64f / 255
            }, Read(outfit, output, i));
    }

    [Fact]
    public void CompatibleFamilyPreservesOriginalBodyDistributionEvenWithSourceInventory()
    {
        var target = Uniform(Triangle, "ya_fukubu_phys");
        var outfit = Document(Triangle, ["iv_c_mune_l", "j_sebo_b", "j_sk_b_b_l"],
            Enumerable.Range(0, 3).Select(_ => new MdlDocument.BoneInfluence[] {
                new("iv_c_mune_l", .3f), new("j_sebo_b", .4f), new("j_sk_b_b_l", .3f) }).ToArray(), "/dress.mtrl");
        var output = (byte[])outfit.Data.Clone();
        Assert.Equal(0, Apply(target, outfit, output, ["iv_c_mune_l", "j_sebo_b"]));
        Assert.Equal(outfit.Data, output);
    }

    [Fact]
    public void OnePackedBodyUnitCanBeReassignedWithoutChangingSevenAuthoredSkirtInfluences()
    {
        var target = Document(Triangle, ["j_kosi", "j_asi_a_r"],
            Enumerable.Range(0, 3).Select(_ => new MdlDocument.BoneInfluence[] { new("j_kosi", .11322779f), new("j_asi_a_r", .8867722f) }).ToArray());
        var skirts = Enumerable.Range(0, 7).Select(i => $"j_sk_chain_{i}").ToArray();
        int[] units = [1, 39, 3, 143, 58, 1, 1];
        var sourceWeights = skirts.Select((name, i) => new MdlDocument.BoneInfluence(name, units[i] / 255f))
            .Append(new("iv_shiri_l", 9f / 255)).ToArray();
        var outfit = Document(Triangle, [.. skirts, "iv_shiri_l", "j_kosi", "j_asi_a_r"],
            Enumerable.Range(0, 3).Select(_ => sourceWeights).ToArray(), "/dress.mtrl", weightType: 17);
        var output = (byte[])outfit.Data.Clone(); var warnings = new List<string>();
        Assert.Equal(3, Apply(target, outfit, output, warnings: warnings));
        for (int v = 0; v < 3; v++)
        {
            var after = Read(outfit, output, v);
            Assert.Equal(8, after.Count);
            for (int i = 0; i < skirts.Length; i++) Assert.Equal(units[i] / 255f, after[skirts[i]]);
            Assert.Equal(9f / 255, after["j_asi_a_r"]);
            Assert.DoesNotContain("j_kosi", after.Keys);
        }
        Assert.Contains(warnings, warning => warning.Contains("3 garment vertices") && warning.Contains("1/255"));
    }

    [Fact]
    public void SignificantBodyMassCannotBeDroppedToFitSlotsAndGarmentWeightsAreNeverDiscarded()
    {
        var target = Document(Triangle, ["j_kosi", "j_asi_a_r"],
            Enumerable.Range(0, 3).Select(_ => new MdlDocument.BoneInfluence[] { new("j_kosi", .5f), new("j_asi_a_r", .5f) }).ToArray());
        var skirts = Enumerable.Range(0, 7).Select(i => $"j_sk_chain_{i}").ToArray();
        int[] units = [1, 39, 3, 143, 58, 1, 1];
        var sourceWeights = skirts.Select((name, i) => new MdlDocument.BoneInfluence(name, units[i] / 255f))
            .Append(new("iv_shiri_l", 9f / 255)).ToArray();
        var outfit = Document(Triangle, [.. skirts, "iv_shiri_l", "j_kosi", "j_asi_a_r"],
            Enumerable.Range(0, 3).Select(_ => sourceWeights).ToArray(), "/dress.mtrl", weightType: 17);
        var output = (byte[])outfit.Data.Clone();
        var error = Assert.Throws<ModelConversionException>(() => Apply(target, outfit, output));
        Assert.Contains("9 destination bone influences", error.Message);
        Assert.Contains("Vertex 0", error.Message);
        Assert.Equal(outfit.Data, output);
    }

    [Theory]
    [InlineData((byte)3)]
    [InlineData((byte)14)]
    public void FloatAndHalfGarmentsDoNotUsePackedWeightTolerance(byte weightType)
    {
        var target = Document(Triangle, ["j_kosi", "j_asi_a_r"],
            Enumerable.Range(0, 3).Select(_ => new MdlDocument.BoneInfluence[] { new("j_kosi", .1f), new("j_asi_a_r", .9f) }).ToArray());
        var outfit = Document(Triangle, ["cape_a", "cape_b", "cape_c", "iv_shiri_l", "j_kosi", "j_asi_a_r"],
            Enumerable.Range(0, 3).Select(_ => new MdlDocument.BoneInfluence[] {
                new("cape_a", .3f), new("cape_b", .3f), new("cape_c", .399f), new("iv_shiri_l", .001f) }).ToArray(),
            "/dress.mtrl", weightType: weightType);
        var output = (byte[])outfit.Data.Clone();
        Assert.Throws<ModelConversionException>(() => Apply(target, outfit, output));
        Assert.Equal(outfit.Data, output);
    }

    [Fact]
    public void CompatibleCustomRigAndOrdinaryGameGarmentBonesRemainByteIdentical()
    {
        var target = Uniform(Triangle, "iv_shiri_l");
        var outfit = Document(Triangle, ["j_sk_b_b_l", "iv_shiri_l"],
            Enumerable.Range(0, 3).Select(_ => new MdlDocument.BoneInfluence[] { new("j_sk_b_b_l", .6f), new("iv_shiri_l", .4f) }).ToArray(), "/skirt.mtrl");
        var output = (byte[])outfit.Data.Clone();
        Assert.Equal(0, Apply(target, outfit, output));
        Assert.Equal(outfit.Data, output);
        var vanillaTarget = Uniform(Triangle, "j_kosi");
        var ordinarySkirt = Uniform(Triangle, "j_sk_b_b_l", "/skirt.mtrl");
        output = (byte[])ordinarySkirt.Data.Clone();
        Assert.Equal(0, Apply(vanillaTarget, ordinarySkirt, output));
        Assert.Equal(ordinarySkirt.Data, output);
    }

    [Fact]
    public void CombinedBodyReferencesSampleClosestDestinationRegion()
    {
        var left = Uniform(Triangle, "j_asi_a_l");
        var right = Uniform(Triangle.Select(p => p + new Vector3(3, 0, 0)).ToArray(), "j_asi_a_r");
        var outfit = Document(Triangle.Select(p => p + new Vector3(3, 0, .01f)).ToArray(),
            ["j_asi_a_l", "j_asi_a_r", "ya_daitai_phys_r"],
            Enumerable.Range(0, 3).Select(_ => new MdlDocument.BoneInfluence[] { new("ya_daitai_phys_r", 1) }).ToArray());
        var output = (byte[])outfit.Data.Clone();
        var transfer = BoneWeightTransfer.Combine([BoneWeightTransfer.Create(left, left.Meshes), BoneWeightTransfer.Create(right, right.Meshes)]);
        Assert.Equal(3, transfer.Apply(outfit, output, new HashSet<string> { "/body.mtrl" }, .25f,
            new Dictionary<int, bool[]> { [0] = [true, true, true] }, default));
        Assert.Equal(new Dictionary<string, float> { ["j_asi_a_r"] = 1 }, Read(outfit, output, 0));
    }

    [Fact]
    public void UnboundMissingPhysicsWeightsAreRejectedInsteadOfWrittenUnsafely()
    {
        var target = Uniform(Triangle, "j_kosi");
        var outfit = Uniform(Triangle, "ya_shiri_phys_l");
        var output = (byte[])outfit.Data.Clone();
        var transfer = BoneWeightTransfer.Create(target, target.Meshes);
        var error = Assert.Throws<ModelConversionException>(() => transfer.Apply(outfit, output, new HashSet<string> { "/body.mtrl" }, .25f,
            new Dictionary<int, bool[]> { [0] = [false, false, false] }, default));
        Assert.Contains("outside the body binding distance", error.Message);
        Assert.Equal(outfit.Data, output);
    }

    [Fact]
    public void MissingDestinationBoneInOutfitPaletteFailsClearly()
    {
        var target = Uniform(Triangle, "j_kosi");
        var outfit = Uniform(Triangle, "ya_shiri_phys_l");
        var output = (byte[])outfit.Data.Clone();
        var error = Assert.Throws<ModelConversionException>(() => Apply(target, outfit, output));
        Assert.Contains("bone", error.Message);
        Assert.Equal(outfit.Data, output);
    }

    [Fact]
    public void PaletteReuseKeepsUnchangedSharedMeshAndItsBoneIndexIntact()
    {
        var target = Uniform(Triangle, "j_kosi");
        var outfit = Document(Triangle.Concat(Triangle).ToArray(), ["iv_shiri_l", "j_sk_b_b_l", "j_kosi"],
            Enumerable.Range(0, 6).Select(i => new MdlDocument.BoneInfluence[] { new(i < 3 ? "iv_shiri_l" : "j_sk_b_b_l", 1) }).ToArray(),
            splitCloth: true, palette: [0, 1]);
        var output = (byte[])outfit.Data.Clone();
        Assert.Equal(3, Apply(target, outfit, output));
        Assert.Equal(2, BitConverter.ToUInt16(output, 0)); // freed custom slot now names an existing global pelvis bone
        Assert.Equal(1, BitConverter.ToUInt16(output, 2)); // unchanged shared mesh keeps its slot
        Assert.Equal(outfit.Data.AsSpan(124, 60).ToArray(), output.AsSpan(124, 60).ToArray());
        Assert.Equal("j_kosi", outfit.GetBoneWeights(outfit.Meshes[0], 0, output).Single().Bone);
        Assert.Equal("j_sk_b_b_l", outfit.GetBoneWeights(outfit.Meshes[1], 0, output).Single().Bone);
    }

    [Fact]
    public void SharedMeshRequiredBoneCannotBeReusedToHidePaletteCapacityFailure()
    {
        var target = Document(Triangle, ["j_kosi", "j_asi_a_l"],
            Enumerable.Range(0, 3).Select(_ => new MdlDocument.BoneInfluence[] { new("j_kosi", .5f), new("j_asi_a_l", .5f) }).ToArray());
        var outfit = Document(Triangle.Concat(Triangle).ToArray(), ["iv_shiri_l", "j_sk_b_b_l", "j_kosi", "j_asi_a_l"],
            Enumerable.Range(0, 6).Select(i => new MdlDocument.BoneInfluence[] { new(i < 3 ? "iv_shiri_l" : "j_sk_b_b_l", 1) }).ToArray(),
            splitCloth: true, palette: [0, 1]);
        var output = (byte[])outfit.Data.Clone();
        Assert.Throws<ModelConversionException>(() => Apply(target, outfit, output));
        Assert.Equal(outfit.Data, output);
    }

    [Fact]
    public void PaletteOnlyRebindingCountsChangedVerticesAndExpandsBoundsWithoutMovingGeometry()
    {
        var body = SkinningFixture.Create(bones: ["j_kosi"], palette: [0]);
        var outfit = SkinningFixture.Create(bones: ["iv_shiri_l", "j_kosi"], palette: [0]);
        var result = MdlConverter.Convert(body.Bytes, body.Bytes, outfit.Bytes, new() { Clearance = 0 });
        Assert.Equal(3, result.ConvertedVertices);
        Assert.Equal(0, result.UnchangedVertices);
        var before = MdlDocument.Parse(outfit.Bytes); var after = MdlDocument.Parse(result.ModelData);
        Assert.Equal(before.Meshes[0].Positions, after.Meshes[0].Positions);
        Assert.All(Enumerable.Range(0, 3), i => Assert.Equal("j_kosi", after.GetBoneWeights(after.Meshes[0], i).Single().Bone));
        Assert.True(MdlDocument.ReadFloat(result.ModelData, after.BoundsOffset + 128) < MdlDocument.ReadFloat(outfit.Bytes, before.BoundsOffset + 128));
        Assert.Equal(outfit.Bytes, MdlConverter.Convert(body.Bytes, body.Bytes, outfit.Bytes, new() { Strength = 0 }).ModelData);
    }

    [Fact]
    public void GlobalNameOnlyRebindingCountsChangedVerticesWithoutMovingGeometry()
    {
        var body = SkinningFixture.Create(bones: ["j_mune_l"], palette: [0]);
        var outfit = SkinningFixture.Create(bones: ["iv_c_mune_l"], palette: [0]);
        var result = MdlConverter.Convert(body.Bytes, body.Bytes, outfit.Bytes, new() { Clearance = 0 });
        var before = MdlDocument.Parse(outfit.Bytes); var after = MdlDocument.Parse(result.ModelData);
        Assert.Equal(3, result.ConvertedVertices);
        Assert.Equal(before.Meshes[0].Positions, after.Meshes[0].Positions);
        Assert.Equal("iv_c_mune_l", before.Bones.Single());
        Assert.Equal("j_mune_l", after.Bones.Single());
        Assert.All(Enumerable.Range(0, 3), v => Assert.Equal("j_mune_l", after.GetBoneWeights(after.Meshes[0], v).Single().Bone));
        Assert.Equal(outfit.Bytes.Length, result.ModelData.Length);
    }

    [Fact]
    public void GlobalReuseIncludesUnchangedMeshRequirementsAndKeepsOriginalNameCache()
    {
        var targetFixture = SkinningFixture.Create(meshes: 2, bones: ["iv_shiri_l", "j_mune_l"], palette: [0, 1]);
        var sourceFixture = SkinningFixture.Create(meshes: 2, bones: ["iv_shiri_l", "ya_c_mune_phys_l"], palette: [0, 1]);
        void Configure(SkinningFixture fixture, string secondBone)
        {
            var document = MdlDocument.Parse(fixture.Bytes);
            var mesh = document.Meshes[1];
            for (int vertex = 0; vertex < 3; vertex++)
            {
                document.WriteBoneWeights(fixture.Bytes, mesh, vertex, [new(secondBone, 1)]);
                MdlDocument.WriteVector(fixture.Bytes, mesh.Address(mesh.Position, vertex), mesh.Position.Type, mesh.Positions[vertex] + new Vector3(3, 0, 0), false);
            }
        }
        Configure(targetFixture, "j_mune_l"); Configure(sourceFixture, "ya_c_mune_phys_l");
        var target = MdlDocument.Parse(targetFixture.Bytes); var source = MdlDocument.Parse(sourceFixture.Bytes);
        var output = (byte[])source.Data.Clone();
        Assert.Equal(3, Apply(target, source, output));
        var after = MdlDocument.Parse(output);
        Assert.Equal(new[] { "iv_shiri_l", "j_mune_l" }, after.Bones);
        Assert.Equal(new[] { "iv_shiri_l", "ya_c_mune_phys_l" }, source.Bones);
        Assert.Equal("iv_shiri_l", after.GetBoneWeights(after.Meshes[0], 0).Single().Bone);
        Assert.Equal("j_mune_l", after.GetBoneWeights(after.Meshes[1], 0).Single().Bone);
        var unchanged = source.Meshes[0];
        Assert.Equal(source.Data.AsSpan(unchanged.StreamOffsets[0], unchanged.VertexCount * unchanged.Strides[0]).ToArray(),
            output.AsSpan(unchanged.StreamOffsets[0], unchanged.VertexCount * unchanged.Strides[0]).ToArray());
    }

    [Fact]
    public void YabReferenceFamilyPreservesOtherYabAndIvcsBonesAbsentFromReferenceMesh()
    {
        var target = Uniform(Triangle, "ya_fukubu_phys");
        var outfit = Document(Triangle, ["ya_daitai_phys_l", "iv_shiri_l"],
            Enumerable.Range(0, 3).Select(_ => new MdlDocument.BoneInfluence[] { new("ya_daitai_phys_l", .6f), new("iv_shiri_l", .4f) }).ToArray());
        var output = (byte[])outfit.Data.Clone();
        Assert.Equal(0, Apply(target, outfit, output));
        Assert.Equal(outfit.Data, output);
    }

    [Fact]
    public void IvcsOnlyReferenceKeepsIvcsGarmentWeightAndReplacesUnsupportedYabWeight()
    {
        var target = Uniform(Triangle, "iv_fukubu_phys");
        var outfit = Document(Triangle, ["ya_daitai_phys_l", "iv_shiri_l", "iv_fukubu_phys"],
            Enumerable.Range(0, 3).Select(_ => new MdlDocument.BoneInfluence[] { new("ya_daitai_phys_l", 63f / 255), new("iv_shiri_l", 192f / 255) }).ToArray(), "/skirt.mtrl");
        var output = (byte[])outfit.Data.Clone();
        Assert.Equal(3, Apply(target, outfit, output));
        var weights = Read(outfit, output, 0);
        Assert.Equal(192f / 255, weights["iv_shiri_l"]);
        Assert.Equal(63f / 255, weights["iv_fukubu_phys"]);
        Assert.DoesNotContain("ya_daitai_phys_l", weights.Keys);
    }

    [Fact]
    public void CombinedReferencesUseSkeletonFamiliesAcrossAllBodyRegions()
    {
        var leg = Uniform(Triangle, "j_kosi");
        var chest = Uniform(Triangle.Select(p => p + new Vector3(3, 0, 0)).ToArray(), "ya_fukubu_phys");
        var outfit = Uniform(Triangle, "iv_shiri_l");
        var output = (byte[])outfit.Data.Clone();
        var transfer = BoneWeightTransfer.Combine([BoneWeightTransfer.Create(leg, leg.Meshes), BoneWeightTransfer.Create(chest, chest.Meshes)]);
        Assert.Equal(0, transfer.Apply(outfit, output, new HashSet<string> { "/body.mtrl" }, .25f,
            new Dictionary<int, bool[]> { [0] = [true, true, true] }, default));
        Assert.Equal(outfit.Data, output);
    }

    [Fact]
    public void SubByteBoundaryInfluenceDoesNotRequireANameOrPaletteSlot()
    {
        var target = Document(Triangle, ["j_kosi", "j_oya_a_l"],
            [[new("j_kosi", 1)], [new("j_oya_a_l", 1)], [new("j_kosi", 1)]]);
        Vector3[] positions = [Vector3.Zero, new(0.0000001f, 0, 0), new(0, 0.0000001f, 0)];
        var outfit = Document(positions, ["ya_shiri_phys_l", "j_kosi"],
            Enumerable.Range(0, 3).Select(_ => new MdlDocument.BoneInfluence[] { new("ya_shiri_phys_l", 1) }).ToArray());
        var output = (byte[])outfit.Data.Clone();
        Assert.Equal(3, Apply(target, outfit, output));
        Assert.All(Enumerable.Range(0, 3), vertex => Assert.Equal(new Dictionary<string, float> { ["j_kosi"] = 1 }, Read(outfit, output, vertex)));
        Assert.Equal(new[] { "ya_shiri_phys_l", "j_kosi" }, outfit.Bones);
    }

    private static int Apply(MdlDocument target, MdlDocument outfit, byte[] output, IEnumerable<string>? sourceBodyBones = null, ICollection<string>? warnings = null) =>
        BoneWeightTransfer.Create(target, target.Meshes, sourceBodyBones: sourceBodyBones).Apply(outfit, output, new HashSet<string> { "/body.mtrl" }, .25f,
            outfit.Meshes.ToDictionary(m => m.Index, m => Enumerable.Repeat(true, m.VertexCount).ToArray()), default, warnings);

    private static Dictionary<string, float> Read(MdlDocument model, byte[] data, int vertex) =>
        model.GetBoneWeights(model.Meshes[0], vertex, data).ToDictionary(w => w.Bone, w => w.Weight);

    private static MdlDocument Uniform(Vector3[] positions, string bone, string material = "/body.mtrl") =>
        Document(positions, [bone], Enumerable.Range(0, positions.Length).Select(_ => new MdlDocument.BoneInfluence[] { new(bone, 1) }).ToArray(), material);

    private static MdlDocument Document(Vector3[] positions, string[] bones, MdlDocument.BoneInfluence[][] weights,
        string material = "/body.mtrl", bool shape = false, bool splitCloth = false, ushort[]? palette = null, byte weightType = 8)
    {
        palette ??= Enumerable.Range(0, bones.Length).Select(i => (ushort)i).ToArray();
        int slots = weightType == 17 ? 8 : 4;
        int weightBytes = weightType == 3 ? slots * 4 : weightType == 14 ? slots * 2 : slots;
        int stride = 12 + weightBytes + slots;
        var bytes = new byte[64 + positions.Length * stride];
        for (int i = 0; i < palette.Length; i++) BitConverter.GetBytes(palette[i]).CopyTo(bytes, i * 2);
        MdlDocument.Mesh Mesh(int index, int first, int count) => new() { Index = index, Lod = 0, VertexCount = count, Material = index,
            StartIndex = 0, StreamOffsets = [64 + first * stride, 0, 0], Strides = [(byte)stride, 0, 0], BonePaletteIndex = 0,
            Elements = [new(0, 0, 2, 0, 0), new(0, 12, weightType, 1, 0), new(0, (byte)(12 + weightBytes), (byte)(slots == 8 ? 17 : 5), 2, 0)],
            Indices = [0, 1, 2], Positions = positions.Skip(first).Take(count).ToArray(), Uvs = null };
        var meshes = splitCloth ? new[] { Mesh(0, 0, 3), Mesh(1, 3, 3) } : new[] { Mesh(0, 0, positions.Length) };
        var document = new MdlDocument { Data = bytes, Version = 0x01000005, LodCount = 1,
            ModelHeaderOffset = 0, BoundsOffset = 0, BoneCount = bones.Length, ShapeCount = shape ? 1 : 0,
            Meshes = meshes, Materials = splitCloth ? [material, "/skirt.mtrl"] : [material], Bones = bones,
            BonePalettes = [new(0, 0, palette.Select(i => (int)i).ToArray(), true)],
            ShapeBindings = shape ? [new(0, 0, positions.Length - 1)] : [] };
        foreach (var mesh in meshes)
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                MdlDocument.WriteVector(bytes, mesh.Address(mesh.Position, i), 2, mesh.Positions[i], false);
                document.WriteBoneWeights(bytes, mesh, i, weights[(mesh.StreamOffsets[0] - 64) / stride + i]);
            }
        return document;
    }
}
