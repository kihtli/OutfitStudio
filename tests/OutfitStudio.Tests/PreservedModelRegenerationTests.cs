using System.Text.Json.Nodes;
using OutfitStudio.Core.Mods;

namespace OutfitStudio.Tests;

public sealed class PreservedModelRegenerationTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ExplicitPreservationRetainsFixedRaceModelsAlongsideGeneratedSizes(int version)
    {
        using var fixture = new Fixture(version);
        var preserved = new List<string> { "FIXED\\other-race.mdl" };
        var plan = fixture.Plan() with { PreservedModelPaths = preserved };
        fixture.Mod.ValidateRegenerationPlan(plan);
        var fixedGroup = fixture.Mod.GetGroupSnapshots()[1];
        var calls = 0;
        var output = await fixture.Mod.CloneWithGeneratedOptionsAsync(fixture.Output, "Destination accessory", plan,
            async (model, destination, token) =>
            {
                ++calls;
                Assert.Equal("accessory.mdl", model.SourceRelativePath);
                // Validation must own its snapshot while generation awaits.
                preserved.Clear();
                await File.WriteAllTextAsync(destination, "generated accessory", token);
            });
        Assert.Equal(1, calls);
        using var result = PenumbraMod.Open(output);
        Assert.Equal(new[] { "fixed/other-race.mdl", "generated/accessory.mdl" }, result.GetModelFiles().Select(model => model.RelativePath));
        Assert.Equal(fixture.FixedBytes, File.ReadAllBytes(Path.Combine(output, "fixed", "other-race.mdl")));
        Assert.True(JsonNode.DeepEquals(fixedGroup, result.GetGroupSnapshots()[1]));
        Assert.Equal("materials/accessory.tex", result.GetGroupSnapshots()[0]["Options"]![0]!["Files"]!["chara/accessory.tex"]!.GetValue<string>());
        if (version == 3)
            Assert.Equal(fixture.FixedGroupDocument, File.ReadAllText(Path.Combine(output, "group_002.json")));
    }

    [Fact]
    public async Task ExplicitPreservationAlsoKeepsOriginalDefaultMappings()
    {
        using var fixture = new Fixture(defaultModel: true);
        var output = await fixture.Mod.CloneWithGeneratedOptionsAsync(fixture.Output, "Destination accessory", fixture.Plan(), Generate);
        using var result = PenumbraMod.Open(output);
        Assert.True(JsonNode.DeepEquals(fixture.Mod.GetDefaultDataSnapshot(), result.GetDefaultDataSnapshot()));
        Assert.Equal(fixture.FixedBytes, File.ReadAllBytes(Path.Combine(output, "fixed", "other-race.mdl")));
        Assert.Equal(2, result.GetModelFiles().Count);
    }

    [Fact]
    public async Task UndeclaredOriginalModelStillFailsBeforeCreatingOutput()
    {
        using var fixture = new Fixture();
        var plan = fixture.Plan() with { PreservedModelPaths = [] };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Mod.CloneWithGeneratedOptionsAsync(fixture.Output, "Invalid", plan, Generate));
        Assert.False(Directory.Exists(fixture.Output));
    }

    [Theory]
    [InlineData("missing.mdl")]
    [InlineData("unused.mdl")]
    [InlineData("materials/accessory.tex")]
    [InlineData("generated/accessory.mdl")]
    [InlineData("../outside.mdl")]
    [InlineData("")]
    [InlineData(null)]
    public void PreservationRequiresAReferencedOriginalModel(string? path)
    {
        using var fixture = new Fixture();
        var plan = fixture.Plan() with { PreservedModelPaths = ["fixed/other-race.mdl", path!] };
        Assert.Throws<InvalidDataException>(() => fixture.Mod.ValidateRegenerationPlan(plan));
        Assert.False(Directory.Exists(fixture.Output));
    }

    [Fact]
    public void DuplicatePreservationIsRejectedAfterPathNormalization()
    {
        using var fixture = new Fixture();
        var plan = fixture.Plan() with { PreservedModelPaths = ["fixed/other-race.mdl", "FIXED\\OTHER-RACE.MDL"] };
        Assert.Throws<InvalidDataException>(() => fixture.Mod.ValidateRegenerationPlan(plan));
    }

    [Fact]
    public void EveryDeclaredPreservedModelMustRemainReferenced()
    {
        using var fixture = new Fixture();
        var plan = fixture.Plan() with { PreservedModelPaths = ["fixed/other-race.mdl", "accessory.mdl"] };
        Assert.Throws<InvalidDataException>(() => fixture.Mod.ValidateRegenerationPlan(plan));
    }

    [Theory]
    [InlineData("chara/another-race.mdl")]
    [InlineData("chara/accessory.mdl")]
    [InlineData("chara/other-race.tex")]
    public void PreservedModelsCannotAcquireDifferentGamePathMappings(string gamePath)
    {
        using var fixture = new Fixture(defaultModel: true);
        var originalDefault = fixture.Mod.GetDefaultDataSnapshot();
        var files = originalDefault["Files"]!.AsObject();
        files[gamePath] = "fixed/other-race.mdl";
        var plan = fixture.Plan() with { DefaultData = originalDefault };
        Assert.Throws<InvalidDataException>(() => fixture.Mod.ValidateRegenerationPlan(plan));
    }

    private static Task Generate(ModGeneratedModel model, string destination, CancellationToken token)
        => File.WriteAllTextAsync(destination, model.Id, token);

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "OutfitStudio-preserved-tests-" + Guid.NewGuid().ToString("N"));
        public PenumbraMod Mod { get; }
        public string Output => Path.Combine(root, "output");
        public byte[] FixedBytes { get; } = [0, 1, 15, 31, 127, 128, 254, 255];
        public string FixedGroupDocument { get; }

        public Fixture(int version = 4, bool defaultModel = false)
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(Path.Combine(source, "fixed"));
            Directory.CreateDirectory(Path.Combine(source, "materials"));
            var groups = JsonNode.Parse("""
                [
                 {"Name":"Source fit","Type":"Single","Options":[{"Name":"Source chest",
                   "Files":{"chara/accessory.mdl":"accessory.mdl","chara/accessory.tex":"materials/accessory.tex"}}]},
                 {"Name":"Base Files","Type":"Single","Priority":5,"Options":[{"Name":"Fixed",
                   "Files":{"chara/other-race.mdl":"fixed/other-race.mdl","chara/fixed.tex":"materials/accessory.tex"}}]}
                ]
                """)!.AsArray();
            FixedGroupDocument = " \n" + groups[1]!.ToJsonString() + "\n\n";
            var data = new JsonObject();
            if (defaultModel)
            {
                data["Files"] = new JsonObject { ["chara/other-race.mdl"] = "fixed/other-race.mdl" };
                groups.RemoveAt(1);
            }
            var metadata = new JsonObject { ["Name"] = "Accessory fixture", ["FileVersion"] = version };
            if (version == 4)
            {
                metadata["Groups"] = groups;
                metadata["DefaultData"] = data;
            }
            else
            {
                File.WriteAllText(Path.Combine(source, "default_mod.json"), data.ToJsonString());
                File.WriteAllText(Path.Combine(source, "group_001_fit.json"), groups[0]!.ToJsonString());
                File.WriteAllText(Path.Combine(source, "group_009_fixed.json"), FixedGroupDocument);
            }
            File.WriteAllText(Path.Combine(source, "meta.json"), metadata.ToJsonString());
            File.WriteAllBytes(Path.Combine(source, "fixed", "other-race.mdl"), FixedBytes);
            foreach (var path in new[] { "accessory.mdl", "unused.mdl", "materials/accessory.tex" })
                File.WriteAllText(Path.Combine(source, path), "synthetic fixture");
            Mod = PenumbraMod.Open(source);
        }

        public ModRegenerationPlan Plan()
        {
            var group = Mod.GetGroupSnapshots()[0];
            group["Name"] = "Destination chest";
            group["Options"]![0]!["Name"] = "Target fit";
            group["Options"]![0]!["Files"]!["chara/accessory.mdl"] = "generated/accessory.mdl";
            return new([new("Source fit", group)], [new("accessory", "accessory.mdl", "generated/accessory.mdl")])
            {
                PreservedModelPaths = ["fixed/other-race.mdl"],
            };
        }

        public void Dispose()
        {
            Mod.Dispose();
            Directory.Delete(root, true);
        }
    }
}
