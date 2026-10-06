using System.Numerics;
using System.Text;
using OutfitStudio.Core.Geometry;
using OutfitStudio.Core.Models;

namespace OutfitStudio.Tests;

public sealed class SkinningParserTests
{
    [Theory]
    [InlineData(false, 8)]
    [InlineData(true, 17)]
    [InlineData(true, 3)]
    [InlineData(true, 14)]
    public void ReadsAndWritesNamedWeightsWithoutChangingTheModelLayout(bool v6, byte encoding)
    {
        var fixture = SkinningFixture.Create(v6: v6, weightType: encoding, meshes: 2);
        var original = (byte[])fixture.Bytes.Clone(); var document = MdlDocument.Parse(original);
        var mesh = document.Meshes[0];
        Assert.Equal("iv_thigh", Assert.Single(document.GetBoneWeights(mesh, 0)).Bone);
        var output = (byte[])original.Clone();
        document.WriteBoneWeights(output, mesh, 0, [new("j_asi_a_l", 1), new("j_kosi", 2)]);
        var reparsed = MdlDocument.Parse(output);
        var weights = reparsed.GetBoneWeights(reparsed.Meshes[0], 0).ToDictionary(i => i.Bone, i => i.Weight);
        Assert.InRange(weights["j_asi_a_l"], 1f / 3 - .001f, 1f / 3 + .001f);
        Assert.InRange(weights["j_kosi"], 2f / 3 - .001f, 2f / 3 + .001f);
        Assert.Equal("iv_thigh", Assert.Single(reparsed.GetBoneWeights(reparsed.Meshes[1], 0)).Bone);
        var weight = mesh.Elements.Single(e => e.Usage == 1); var index = mesh.Elements.Single(e => e.Usage == 2);
        for (int i = 0; i < output.Length; i++)
            if (!(i >= mesh.Address(weight, 0) && i < mesh.Address(weight, 0) + weight.Size)
                && !(i >= mesh.Address(index, 0) && i < mesh.Address(index, 0) + index.Size))
                Assert.Equal(original[i], output[i]);
    }

    [Fact]
    public void PackedWeightRoundingIsDeterministicAndSumsTo255()
    {
        var fixture = SkinningFixture.Create(weightType: 17); var document = MdlDocument.Parse(fixture.Bytes); var mesh = document.Meshes[0];
        var output = (byte[])fixture.Bytes.Clone(); var second = (byte[])fixture.Bytes.Clone();
        document.WriteBoneWeights(output, mesh, 0, [new("j_kosi", .1f), new("j_asi_a_l", .1f), new("iv_thigh", .1f), new("j_kosi", .1f)]);
        document.WriteBoneWeights(second, mesh, 0, [new("iv_thigh", .1f), new("j_asi_a_l", .1f), new("j_kosi", .2f)]);
        Assert.Equal(output, second);
        int address = mesh.Address(mesh.Elements.Single(e => e.Usage == 1), 0);
        Assert.Equal(255, output.Skip(address).Take(8).Sum(v => (int)v));
    }

    [Theory]
    [InlineData((byte)8, 4)]
    [InlineData((byte)17, 8)]
    public void CanonicalizationDropsOnlyEncodedZerosBeforeCheckingSlotsOrReservingNames(byte encoding, int slots)
    {
        var names = Enumerable.Range(0, slots).Select(i => $"j_bone_{i}").ToArray();
        var fixture = SkinningFixture.Create(weightType: encoding, bones: names, palette: Enumerable.Range(0, slots).Select(i => (ushort)i).ToArray());
        var document = MdlDocument.Parse(fixture.Bytes); var mesh = document.Meshes[0];
        var input = names.Select(name => new MdlDocument.BoneInfluence(name, 1f / slots))
            .Concat([new("numerical_residue_1", 2e-8f), new("numerical_residue_2", 1e-7f)]).ToArray();
        var canonical = document.CanonicalizeBoneWeights(mesh, input);
        Assert.Equal(slots, canonical.Length);
        Assert.DoesNotContain(canonical, i => i.Bone.StartsWith("numerical_residue"));
        var output = (byte[])fixture.Bytes.Clone();
        document.EnsureBoneNames(output, canonical.Select(i => i.Bone));
        document.EnsureBonePalette(output, 0, canonical.Select(i => i.Bone));
        document.WriteBoneWeights(output, mesh, 0, canonical, canonicalized: true);
        Assert.Equal(canonical, document.GetBoneWeights(mesh, 0, output));
        var direct = (byte[])fixture.Bytes.Clone();
        document.WriteBoneWeights(direct, mesh, 0, input);
        Assert.Equal(direct, output);
    }

    [Fact]
    public void CanonicalizationPreservesAuthoredPackedSkirtMass()
    {
        var fixture = SkinningFixture.Create(bones: ["j_sk_b_a_l", "j_kosi", "unused"]);
        var document = MdlDocument.Parse(fixture.Bytes); var mesh = document.Meshes[0];
        var canonical = document.CanonicalizeBoneWeights(mesh, [new("j_sk_b_a_l", 192f / 255), new("j_kosi", 63f / 255), new("unused_residue", 2e-8f)]);
        Assert.Equal(192f / 255, canonical.Single(i => i.Bone == "j_sk_b_a_l").Weight);
        Assert.Equal(63f / 255, canonical.Single(i => i.Bone == "j_kosi").Weight);
        Assert.Equal(2, canonical.Length);
    }

    [Theory]
    [InlineData((byte)3, 1e-8f, true)]
    [InlineData((byte)14, 1e-8f, false)]
    [InlineData((byte)14, 1e-4f, true)]
    public void FloatAndHalfSlotRequirementsUseRepresentablePositivesWithoutAnEpsilon(byte encoding, float residual, bool excess)
    {
        string[] names = ["a", "b", "c", "d", "extra"];
        var fixture = SkinningFixture.Create(weightType: encoding, bones: names, palette: [0, 1, 2, 3, 4]);
        var document = MdlDocument.Parse(fixture.Bytes); var mesh = document.Meshes[0];
        var input = names.Take(4).Select(n => new MdlDocument.BoneInfluence(n, .25f)).Append(new("extra", residual)).ToArray();
        if (excess) Assert.Throws<ModelConversionException>(() => document.CanonicalizeBoneWeights(mesh, input));
        else Assert.Equal(4, document.CanonicalizeBoneWeights(mesh, input).Length);
    }

    [Theory]
    [InlineData((byte)3)]
    [InlineData((byte)14)]
    public void PreparedFloatAndHalfWeightsAreWrittenWithoutSecondNormalization(byte encoding)
    {
        var fixture = SkinningFixture.Create(weightType: encoding);
        var document = MdlDocument.Parse(fixture.Bytes); var mesh = document.Meshes[0];
        var canonical = document.CanonicalizeBoneWeights(mesh, [new("iv_thigh", .1f), new("j_asi_a_l", .2f), new("j_kosi", .7f)]);
        var output = (byte[])fixture.Bytes.Clone();
        document.WriteBoneWeights(output, mesh, 0, canonical, canonicalized: true);
        Assert.Equal(canonical, document.GetBoneWeights(mesh, 0, output));
    }

    [Fact]
    public void MissingPaletteBoneFailsBeforeMutatingAnyBytes()
    {
        var fixture = SkinningFixture.Create(bones: ["iv_thigh", "j_asi_a_l", "j_kosi", "j_sebo_a"]);
        var document = MdlDocument.Parse(fixture.Bytes); var output = (byte[])fixture.Bytes.Clone();
        var error = Assert.Throws<ModelConversionException>(() => document.WriteBoneWeights(output, document.Meshes[0], 0, [new("j_sebo_a", 1)]));
        Assert.Contains("existing palette", error.Message);
        Assert.Equal(fixture.Bytes, output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReusesOnlyFinalUnusedPaletteSlotsAndReadsTheEffectiveOutputPalette(bool v6)
    {
        var fixture = SkinningFixture.Create(v6: v6, bones: ["iv_thigh", "j_asi_a_l", "j_kosi", "j_sebo_a"], meshes: 2);
        var document = MdlDocument.Parse(fixture.Bytes); var output = (byte[])fixture.Bytes.Clone();
        document.EnsureBonePalette(output, 0, ["j_asi_a_l", "j_kosi", "j_sebo_a"]);
        for (int m = 0; m < 2; m++)
            for (int v = 0; v < 3; v++)
                document.WriteBoneWeights(output, document.Meshes[m], v, [new(m == 0 ? "j_sebo_a" : "j_asi_a_l", 1)]);
        Assert.Equal("j_sebo_a", Assert.Single(document.GetBoneWeights(document.Meshes[0], 0, output)).Bone);
        Assert.Equal("j_asi_a_l", Assert.Single(document.GetBoneWeights(document.Meshes[1], 0, output)).Bone);
        Assert.Equal("iv_thigh", Assert.Single(document.GetBoneWeights(document.Meshes[0], 0)).Bone);
        Assert.Equal(new[] { 0, 1, 2 }, document.BonePalettes[0].BoneIndices);
        var reparsed = MdlDocument.Parse(output);
        Assert.Equal(new[] { 3, 1, 2 }, reparsed.BonePalettes[0].BoneIndices);
        int entry = document.BonePalettes[0].EntryOffset;
        var blendRanges = document.Meshes.SelectMany(mesh => Enumerable.Range(0, mesh.VertexCount)
            .SelectMany(vertex => mesh.Elements.Where(e => e.Usage is 1 or 2).Select(e => (Start: mesh.Address(e, vertex), e.Size)))).ToArray();
        for (int i = 0; i < output.Length; i++)
            if (!(i >= entry && i < entry + 2) && !blendRanges.Any(r => i >= r.Start && i < r.Start + r.Size))
                Assert.Equal(fixture.Bytes[i], output[i]);
    }

    [Fact]
    public void PalettePreparationRejectsUnavailableCapacityAndGlobalNamesBeforeWriting()
    {
        var fixture = SkinningFixture.Create(bones: ["iv_thigh", "j_asi_a_l", "j_kosi", "j_sebo_a"]);
        var document = MdlDocument.Parse(fixture.Bytes); var output = (byte[])fixture.Bytes.Clone();
        var capacity = Assert.Throws<ModelConversionException>(() => document.EnsureBonePalette(output, 0, document.Bones));
        Assert.Contains("no safely reusable slot", capacity.Message);
        Assert.Equal(fixture.Bytes, output);
        var missing = Assert.Throws<ModelConversionException>(() => document.EnsureBonePalette(output, 0, ["j_sebo_a", "unknown_destination_bone"]));
        Assert.Contains("global bone names", missing.Message);
        Assert.Equal(fixture.Bytes, output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReusesIsolatedUnusedGlobalNamesWithBestFitAndRetainsOriginalNames(bool v6)
    {
        var fixture = SkinningFixture.Create(v6: v6, bones: ["iv_aaaaa", "ya_bbbbbbbb", "j_kosi"]);
        var document = MdlDocument.Parse(fixture.Bytes); var output = (byte[])fixture.Bytes.Clone();
        document.EnsureBoneNames(output, ["j_middle", "j_longname", "j_kosi"]);
        document.EnsureBonePalette(output, 0, ["j_longname", "j_middle", "j_kosi"]);
        document.WriteBoneWeights(output, document.Meshes[0], 0, [new("j_longname", .5f), new("j_middle", .5f)]);
        var effective = document.GetBoneWeights(document.Meshes[0], 0, output);
        Assert.Equal(new[] { "j_longname", "j_middle" }, effective.Select(i => i.Bone).Order().ToArray());
        Assert.Equal("iv_aaaaa", Assert.Single(document.GetBoneWeights(document.Meshes[0], 0)).Bone);
        Assert.Equal(new[] { "iv_aaaaa", "ya_bbbbbbbb", "j_kosi" }, document.Bones);
        Assert.Equal(new[] { "j_middle", "j_longname", "j_kosi" }, MdlDocument.Parse(output).Bones);
        var mesh = document.Meshes[0];
        var blendRanges = mesh.Elements.Where(e => e.Usage is 1 or 2).Select(e => (Start: mesh.Address(e, 0), e.Size)).ToArray();
        for (int i = 0; i < output.Length; i++)
            if (!document.BoneNamesStorage.Take(2).Any(s => i >= s.Offset && i < s.Offset + s.Capacity)
                && !(i >= document.StringCountOffset && i < document.StringCountOffset + 2)
                && !blendRanges.Any(r => i >= r.Start && i < r.Start + r.Size)) Assert.Equal(fixture.Bytes[i], output[i]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShorterBoneNamesKeepLaterMaterialAndShapeNamesVisibleToSequentialReaders(bool v6)
    {
        var fixture = SkinningFixture.Create(v6: v6, bones: ["iv_very_long_source", "j_kosi", "later_bone"], trailingReferences: true);
        var document = MdlDocument.Parse(fixture.Bytes); var output = (byte[])fixture.Bytes.Clone();
        var before = SequentialStrings(fixture.Bytes, document.StringCountOffset);
        document.EnsureBoneNames(output, ["j_mune_l", "j_kosi", "later_bone"]);
        var after = SequentialStrings(output, document.StringCountOffset);
        int removed = Encoding.UTF8.GetByteCount("iv_very_long_source") - Encoding.UTF8.GetByteCount("j_mune_l");
        Assert.Equal(before.Count + removed, after.Count);
        foreach (string late in new[] { "later_bone", "/late_material.mtrl", "shape_late" })
        {
            int originalOffset = before.Single(p => p.Value == late).Key;
            Assert.Equal(late, after[originalOffset]);
        }
        Assert.Equal(document.Materials, MdlDocument.Parse(output).Materials);
        Assert.Equal(document.ShapeCount, MdlDocument.Parse(output).ShapeCount);
        int nameOffset = document.BoneNamesStorage[0].Offset;
        int capacity = document.BoneNamesStorage[0].Capacity;
        for (int i = 0; i < output.Length; i++)
            if (!(i >= nameOffset && i < nameOffset + capacity)
                && !(i >= document.StringCountOffset && i < document.StringCountOffset + 2)) Assert.Equal(fixture.Bytes[i], output[i]);
    }

    [Fact]
    public void RepeatedNameReplacementAdjustsCountByActualNullDeltaWithoutAccumulatingPadding()
    {
        var fixture = SkinningFixture.Create(bones: ["iv_very_long_source", "j_kosi", "later_bone"], trailingReferences: true);
        var document = MdlDocument.Parse(fixture.Bytes); var output = (byte[])fixture.Bytes.Clone();
        int initial = BitConverter.ToUInt16(output, document.StringCountOffset);
        document.EnsureBoneNames(output, ["iv_short", "j_kosi", "later_bone"]);
        document.EnsureBoneNames(output, ["iv_medium_name", "j_kosi", "later_bone"]);
        Assert.Equal(initial + "iv_very_long_source".Length - "iv_medium_name".Length, BitConverter.ToUInt16(output, document.StringCountOffset));
        Assert.Contains("shape_late", SequentialStrings(output, document.StringCountOffset).Values);
        MdlDocument.Parse(output);
    }

    [Fact]
    public void StringCountOverflowFailsBeforeChangingAnyBytes()
    {
        var fixture = SkinningFixture.Create(bones: ["iv_very_long_source", "j_kosi", "later_bone"]);
        var document = MdlDocument.Parse(fixture.Bytes); var output = (byte[])fixture.Bytes.Clone();
        BitConverter.GetBytes(ushort.MaxValue).CopyTo(output, document.StringCountOffset);
        var originalOutput = (byte[])output.Clone();
        Assert.Contains("string-count capacity", Assert.Throws<ModelConversionException>(() => document.EnsureBoneNames(output, ["j_mune_l", "j_kosi", "later_bone"])).Message);
        Assert.Equal(originalOutput, output);
    }

    [Fact]
    public void NameOutsideTheDeclaredSequenceCannotBeReused()
    {
        var fixture = SkinningFixture.Create();
        int countOffset = 68 + 136;
        BitConverter.GetBytes((ushort)1).CopyTo(fixture.Bytes, countOffset); // only material is declared
        var document = MdlDocument.Parse(fixture.Bytes); var output = (byte[])fixture.Bytes.Clone();
        Assert.False(document.BoneNamesStorage[0].Exclusive);
        Assert.Throws<ModelConversionException>(() => document.EnsureBoneNames(output, ["j_new", "j_kosi"]));
        Assert.Equal(fixture.Bytes, output);
    }

    private static Dictionary<int, string> SequentialStrings(byte[] bytes, int countOffset)
    {
        int count = BitConverter.ToUInt16(bytes, countOffset);
        int cursor = countOffset + 8;
        var result = new Dictionary<int, string>();
        for (int i = 0; i < count; i++)
        {
            int end = Array.IndexOf(bytes, (byte)0, cursor);
            Assert.True(end >= cursor);
            result.Add(cursor, Encoding.UTF8.GetString(bytes, cursor, end - cursor));
            cursor = end + 1;
        }
        return result;
    }

    [Fact]
    public void GlobalNameReuseProtectsFinalConsumersAndRejectsLongerNamesBeforeWriting()
    {
        var fixture = SkinningFixture.Create(); var document = MdlDocument.Parse(fixture.Bytes);
        var output = (byte[])fixture.Bytes.Clone();
        Assert.Contains("no safely reusable", Assert.Throws<ModelConversionException>(() =>
            document.EnsureBoneNames(output, ["iv_thigh", "j_kosi", "j_new"])).Message);
        Assert.Equal(fixture.Bytes, output);
        Assert.Contains("no safely reusable", Assert.Throws<ModelConversionException>(() =>
            document.EnsureBoneNames(output, ["j_destination_name_far_too_long"])).Message);
        Assert.Equal(fixture.Bytes, output);
        Assert.Throws<ModelConversionException>(() => document.EnsureBoneNames(output, ["j_null\0hidden"]));
        Assert.Equal(fixture.Bytes, output);
    }

    [Theory]
    [InlineData("bone")]
    [InlineData("interior")]
    [InlineData("material")]
    public void GlobalNameReuseRejectsAliasedOrOverlappingReferencedStrings(string alias)
    {
        var fixture = SkinningFixture.Create(bones: ["iv_thigh", "j_kosi", "unused"]);
        int bonesStart = fixture.BoneTableOffset - 3 * 4;
        int source = BitConverter.ToInt32(fixture.Bytes, bonesStart);
        int field = alias == "material" ? bonesStart - 4 : bonesStart + 8;
        BitConverter.GetBytes(source + (alias == "interior" ? 3 : 0)).CopyTo(fixture.Bytes, field);
        var document = MdlDocument.Parse(fixture.Bytes); var output = (byte[])fixture.Bytes.Clone();
        Assert.False(document.BoneNamesStorage[0].Exclusive);
        Assert.Throws<ModelConversionException>(() => document.EnsureBoneNames(output, ["j_kosi", "j_new"]));
        Assert.Equal(fixture.Bytes, output);
    }

    [Fact]
    public void ExcessInfluencesAndInvalidWeightsFailBeforeWriting()
    {
        var fixture = SkinningFixture.Create(bones: ["a", "b", "c", "d", "e"], palette: [0, 1, 2, 3, 4]);
        var document = MdlDocument.Parse(fixture.Bytes); var output = (byte[])fixture.Bytes.Clone();
        Assert.Throws<ModelConversionException>(() => document.WriteBoneWeights(output, document.Meshes[0], 0, document.Bones.Select(b => new MdlDocument.BoneInfluence(b, 1))));
        Assert.Throws<ModelConversionException>(() => document.WriteBoneWeights(output, document.Meshes[0], 0, [new("a", float.NaN)]));
        Assert.Throws<ModelConversionException>(() => document.WriteBoneWeights(output, document.Meshes[0], 0, [new("a", 0)]));
        Assert.Equal(fixture.Bytes, output);
    }

    [Fact]
    public void RejectsCorruptV6TableAndGlobalBoneReferences()
    {
        var invalidOffset = SkinningFixture.Create(v6: true);
        BitConverter.GetBytes((ushort)0).CopyTo(invalidOffset.Bytes, invalidOffset.BoneTableOffset);
        Assert.Contains("table section", Assert.Throws<ModelConversionException>(() => MdlDocument.Parse(invalidOffset.Bytes)).Message);
        var invalidGlobal = SkinningFixture.Create(v6: true);
        BitConverter.GetBytes((ushort)999).CopyTo(invalidGlobal.Bytes, invalidGlobal.BoneTableOffset + 4);
        Assert.Contains("global bone", Assert.Throws<ModelConversionException>(() => MdlDocument.Parse(invalidGlobal.Bytes)).Message);
    }

    [Fact]
    public void RejectsDifferentV6PalettesAliasingTheSameStorage()
    {
        var fixture = SkinningFixture.Create(v6: true);
        BitConverter.GetBytes((ushort)2).CopyTo(fixture.Bytes, fixture.HeaderOffset + 14); // two table headers
        BitConverter.GetBytes((ushort)2).CopyTo(fixture.Bytes, fixture.HeaderOffset + 44); // same total section length
        BitConverter.GetBytes((ushort)2).CopyTo(fixture.Bytes, fixture.BoneTableOffset);
        BitConverter.GetBytes((ushort)1).CopyTo(fixture.Bytes, fixture.BoneTableOffset + 2);
        BitConverter.GetBytes((ushort)1).CopyTo(fixture.Bytes, fixture.BoneTableOffset + 4);
        BitConverter.GetBytes((ushort)1).CopyTo(fixture.Bytes, fixture.BoneTableOffset + 6);
        Assert.Contains("Overlapping bone palette storage", Assert.Throws<ModelConversionException>(() => MdlDocument.Parse(fixture.Bytes)).Message);
    }

    [Fact]
    public void RejectsInvalidWeightedLocalIndexButAllowsUnusedIndexBytes()
    {
        var fixture = SkinningFixture.Create(); var document = MdlDocument.Parse(fixture.Bytes); var mesh = document.Meshes[0];
        int indices = mesh.Address(mesh.Elements.Single(e => e.Usage == 2), 0);
        fixture.Bytes[indices + 1] = 255;
        MdlDocument.Parse(fixture.Bytes);
        fixture.Bytes[indices] = 255;
        Assert.Contains("outside its palette", Assert.Throws<ModelConversionException>(() => MdlDocument.Parse(fixture.Bytes)).Message);
    }

    [Fact]
    public void RejectsNonFiniteFloatWeightsAndMismatchedSlotCounts()
    {
        var fixture = SkinningFixture.Create(weightType: 3); var document = MdlDocument.Parse(fixture.Bytes); var mesh = document.Meshes[0];
        BitConverter.GetBytes(float.NaN).CopyTo(fixture.Bytes, mesh.Address(mesh.Elements.Single(e => e.Usage == 1), 0));
        Assert.Contains("invalid bone weight", Assert.Throws<ModelConversionException>(() => MdlDocument.Parse(fixture.Bytes)).Message);
        var mismatch = SkinningFixture.Create(weightType: 17);
        mismatch.Bytes[68 + 4 * 8 + 2] = 8;
        Assert.Contains("encodings", Assert.Throws<ModelConversionException>(() => MdlDocument.Parse(mismatch.Bytes)).Message);
    }

    [Fact]
    public void ExistingGeometryFixtureWithWeightsButNoIndicesRemainsUnskinned()
    {
        var document = MdlDocument.Parse(GeometryTests.Fixture.Create().Bytes);
        Assert.False(document.Meshes[0].HasSkinning);
        Assert.Empty(document.GetBoneWeights(document.Meshes[0], 0));
    }
}

// Independently encoded synthetic v5/v6 model with a shared palette. No game assets.
internal sealed record SkinningFixture(byte[] Bytes, int HeaderOffset, int BoneTableOffset)
{
    internal static SkinningFixture Create(bool v6 = false, byte weightType = 8, int meshes = 1,
        string[]? bones = null, ushort[]? palette = null, string material = "/body.mtrl", Vector3 offset = default, bool trailingReferences = false)
    {
        bones ??= ["iv_thigh", "j_asi_a_l", "j_kosi"]; palette ??= [0, 1, 2];
        int slots = weightType == 17 ? 8 : 4;
        int weightSize = weightType switch { 3 => 16, 14 or 17 => 8, _ => 4 };
        int stride = 32 + weightSize + slots;
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(new byte[68]);
        for (int m = 0; m < meshes; m++)
        {
            long start = stream.Position;
            foreach (var e in new[] { (0, 2, 0), (12, 2, 3), (24, 1, 4), (32, (int)weightType, 1), (32 + weightSize, slots == 8 ? 17 : 8, 2) })
            { w.Write((byte)0); w.Write((byte)e.Item1); w.Write((byte)e.Item2); w.Write((byte)e.Item3); w.Write(0u); }
            w.Write((byte)255); w.Write(new byte[136 - (int)(stream.Position - start)]);
        }
        var names = new[] { material }.Concat(bones).Concat(trailingReferences ? ["/late_material.mtrl", "shape_late"] : Array.Empty<string>()).ToArray(); var offsets = new int[names.Length];
        byte[] strings = Encoding.UTF8.GetBytes(string.Join('\0', names) + '\0');
        for (int i = 1; i < names.Length; i++) offsets[i] = offsets[i - 1] + Encoding.UTF8.GetByteCount(names[i - 1]) + 1;
        w.Write((ushort)names.Length); w.Write((ushort)0); w.Write(strings.Length); w.Write(strings);
        int header = (int)stream.Position; w.Write(new byte[56]);
        int lods = (int)stream.Position; w.Write(new byte[180]);
        int meshTable = (int)stream.Position; w.Write(new byte[meshes * 36]);
        w.Write(offsets[0]);
        if (trailingReferences) w.Write(offsets[bones.Length + 1]);
        for (int b = 0; b < bones.Length; b++) w.Write(offsets[b + 1]);
        int boneTable = (int)stream.Position;
        if (v6)
        {
            w.Write((ushort)1); w.Write((ushort)palette.Length);
            foreach (ushort b in palette) w.Write(b);
            if (palette.Length % 2 != 0) w.Write((ushort)0);
        }
        else
        {
            foreach (ushort b in palette) w.Write(b);
            w.Write(new byte[(64 - palette.Length) * 2]); w.Write((uint)palette.Length);
        }
        if (trailingReferences) { w.Write(offsets[bones.Length + 2]); w.Write(new byte[12]); }
        w.Write(0u); w.Write((byte)0);
        for (int i = 0; i < 4 + bones.Length; i++)
        { w.Write(-2f); w.Write(-2f); w.Write(-2f); w.Write(0f); w.Write(2f); w.Write(2f); w.Write(2f); w.Write(0f); }
        int dataStart = (int)stream.Position;
        for (int m = 0; m < meshes; m++)
            for (int i = 0; i < 3; i++)
            {
                var p = (i == 0 ? Vector3.Zero : i == 1 ? Vector3.UnitX : Vector3.UnitY) + offset;
                w.Write(p.X); w.Write(p.Y); w.Write(p.Z);
                w.Write(0f); w.Write(0f); w.Write(1f); w.Write(i == 1 ? 1f : 0f); w.Write(i == 2 ? 1f : 0f);
                for (int j = 0; j < slots; j++)
                    if (weightType == 3) w.Write(j == 0 ? 1f : 0f);
                    else if (weightType == 14) w.Write(BitConverter.HalfToUInt16Bits((Half)(j == 0 ? 1 : 0)));
                    else w.Write((byte)(j == 0 ? 255 : 0));
                w.Write(new byte[slots]);
            }
        int indexStart = (int)stream.Position;
        for (int m = 0; m < meshes; m++) { w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)2); }
        stream.Position = 0; w.Write(v6 ? 0x01000006u : 0x01000005u); w.Write(meshes * 136); w.Write(dataStart - 68 - meshes * 136);
        w.Write((ushort)meshes); w.Write((ushort)(trailingReferences ? 2 : 1)); w.Write(dataStart); w.Write(0ul); w.Write(indexStart); w.Write(0ul);
        w.Write(meshes * 3 * stride); w.Write(0ul); w.Write(meshes * 6); w.Write(0ul); w.Write((byte)1);
        stream.Position = header; w.Write(2f); w.Write((ushort)meshes); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)(trailingReferences ? 2 : 1)); w.Write((ushort)bones.Length); w.Write((ushort)1);
        if (trailingReferences) { stream.Position = header + 16; w.Write((ushort)1); }
        stream.Position = header + 22; w.Write((byte)1);
        if (v6) { stream.Position = header + 44; w.Write((ushort)(palette.Length + palette.Length % 2)); }
        stream.Position = lods; w.Write((ushort)0); w.Write((ushort)meshes);
        stream.Position = lods + 44; w.Write(meshes * 3 * stride); w.Write(meshes * 6); w.Write(dataStart); w.Write(indexStart);
        for (int m = 0; m < meshes; m++)
        {
            stream.Position = meshTable + m * 36; w.Write((ushort)3); w.Write((ushort)0); w.Write(3); w.Write(new byte[8]); w.Write(m * 3);
            w.Write(m * 3 * stride); w.Write(0ul); w.Write((byte)stride); w.Write((byte)0); w.Write((byte)0); w.Write((byte)1);
        }
        return new(stream.ToArray(), header, boneTable);
    }
}
