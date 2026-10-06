using System.Text.Json;
using System.Text.Json.Nodes;
using OutfitStudio.Core.Protocol;
using OutfitStudio.Core.Services;

namespace OutfitStudio.Tests;

public sealed class MultiTargetServiceTests
{
    [Fact]
    public async Task IdenticalRelativePathsInDifferentTargetsUseTheirOwnGeometryAndProvenance()
    {
        using var scene = new Scene();
        // The full list is authoritative, including when an old single field is stale.
        var response = await ConversionService.ExecuteAsync(scene.Request with { TargetModPath = "ignored-old-selection" });
        Assert.True(response.Success);
        Assert.Equal(2, response.ConvertedModelCount);
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(response.OutputDirectory!, "outfitstudio-report.json")));
        var provenance = report.RootElement.GetProperty("targetMods").EnumerateArray().ToArray();
        Assert.Equal(2, provenance.Length);
        Assert.Equal(scene.Main, provenance[0].GetProperty("requestedPath").GetString());
        Assert.Equal(scene.Extra, provenance[1].GetProperty("requestedPath").GetString());
        var references = report.RootElement.GetProperty("referencePairs").EnumerateArray().ToArray();
        Assert.Equal(2, references.Length);
        Assert.All(references, reference => Assert.Equal("body.mdl", reference.GetProperty("targetModel").GetString()));
        Assert.Equal(2, references.Select(reference => reference.GetProperty("targetSha256").GetString()).Distinct().Count());
        var models = report.RootElement.GetProperty("models").EnumerateArray().ToArray();
        Assert.Equal(new[] { 0, 1 }, models.Select(model => model.GetProperty("targetModIndex").GetInt32()).Order().ToArray());
        foreach (var model in models)
        {
            int owner = model.GetProperty("targetModIndex").GetInt32();
            Assert.Equal(owner == 0 ? scene.Main : scene.Extra, model.GetProperty("targetModPath").GetString());
            Assert.Equal("body.mdl", model.GetProperty("targetModel").GetString());
            Assert.Empty(model.GetProperty("additionalBodyReferences").EnumerateArray());
            var bytes = File.ReadAllBytes(Path.Combine(response.OutputDirectory!, model.GetProperty("outputModel").GetString()!));
            foreach (int address in scene.OutfitFixture.PositionOffsets)
                Assert.Equal(owner == 0 ? 0.12f : 0.22f, BitConverter.ToSingle(bytes, address + 8), 5);
        }
        scene.AssertUnchanged();
    }

    [Fact]
    public async Task LegacySingleTargetRequestStillConvertsAndReturnsTheNewProtocol()
    {
        using var scene = new Scene();
        var response = await ConversionService.ExecuteAsync(scene.Request with { ProtocolVersion = 1, TargetModPaths = [] });
        Assert.True(response.Success);
        Assert.Equal(2, response.ProtocolVersion);
        Assert.Equal(1, response.ConvertedModelCount);
        Assert.Equal("Main fit", Assert.Single(Assert.Single(response.AutomaticPlan!.DestinationGroups).Options));
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(response.OutputDirectory!, "outfitstudio-report.json")));
        Assert.Equal("MAIN", report.RootElement.GetProperty("targetMod").GetString());
        Assert.Single(report.RootElement.GetProperty("targetMods").EnumerateArray());
        scene.AssertUnchanged();
    }

    [Fact]
    public async Task OutputInsideTheSecondaryDestinationIsRejectedWithoutWriting()
    {
        using var scene = new Scene();
        var output = Path.Combine(scene.Extra, "nested-output");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ConversionService.ExecuteAsync(scene.Request with { OutputRoot = output }));
        Assert.Contains("inside", error.Message);
        Assert.False(Directory.Exists(output));
        scene.AssertUnchanged();
    }

    [Fact]
    public async Task DuplicatePhysicalDestinationsAreRejectedBeforePublishing()
    {
        using var scene = new Scene();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ConversionService.ExecuteAsync(scene.Request with
        {
            TargetModPaths = [scene.Main, Path.Combine(scene.Main, ".")],
        }));
        Assert.Contains("more than once", error.Message);
        Assert.False(Directory.Exists(scene.Output));
        scene.AssertUnchanged();
    }

    [Fact]
    public async Task DestinationAliasesCannotBypassDuplicateOrOutputProtection()
    {
        if (OperatingSystem.IsWindows()) return;
        using var scene = new Scene();
        string alias = scene.At("extra-alias");
        Directory.CreateSymbolicLink(alias, scene.Extra);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ConversionService.ExecuteAsync(scene.Request with
        {
            TargetModPaths = [scene.Extra, alias],
        }));
        Assert.Contains("more than once", error.Message);
        var nested = Path.Combine(scene.Extra, "nested-output");
        await Assert.ThrowsAsync<InvalidDataException>(() => ConversionService.ExecuteAsync(scene.Request with
        {
            TargetModPaths = [scene.Main, alias], OutputRoot = nested,
        }));
        Assert.False(Directory.Exists(nested));
        scene.AssertUnchanged();
    }

    private sealed class Scene : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "OutfitStudio-multiple-targets-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, byte[]> originals = [];
        public string Main => At("main");
        public string Extra => At("extra");
        public string Output => At("output");
        public GeometryTests.Fixture OutfitFixture { get; } = GeometryTests.Fixture.Create(offset: new(0, 0, 0.02f));
        public WorkerRequest Request => new()
        {
            Mode = ConversionMode.DestinationSizes, Operation = WorkerOperation.Convert,
            SourceModPath = At("source"), OutfitModPath = At("outfit"), TargetModPath = Main,
            TargetModPaths = [Main, Extra], OutputRoot = Output, OutputName = "Multiple targets", Clearance = 0,
        };

        public Scene()
        {
            WriteMod(At("source"), "Source", "Medium", "body.mdl", GeometryTests.Fixture.Create().Bytes, true);
            WriteMod(Main, "MAIN", "Main fit", "body.mdl", GeometryTests.Fixture.Create(offset: new(0, 0, 0.1f)).Bytes, true);
            WriteMod(Extra, "EXTRA", "Extra fit", "body.mdl", GeometryTests.Fixture.Create(offset: new(0, 0, 0.2f)).Bytes, true);
            WriteMod(At("outfit"), "Outfit", "Medium", "outfit.mdl", OutfitFixture.Bytes, false);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) originals[file] = File.ReadAllBytes(file);
        }

        private static void WriteMod(string path, string name, string optionName, string file, byte[] model, bool body)
        {
            Directory.CreateDirectory(path);
            var gamePath = body ? "chara/c0101e0001_top.mdl" : "chara/c0101e0003_top.mdl";
            File.WriteAllText(Path.Combine(path, "meta.json"), new JsonObject
            {
                ["FileVersion"] = 4, ["Name"] = name,
                ["Groups"] = new JsonArray(new JsonObject
                {
                    ["Name"] = "Chest - Smallclothes", ["Type"] = "Single", ["DefaultSettings"] = 0,
                    ["Options"] = new JsonArray(new JsonObject { ["Name"] = optionName,
                        ["Files"] = new JsonObject { [gamePath] = file } }),
                }),
            }.ToJsonString());
            File.WriteAllBytes(Path.Combine(path, file), model);
        }

        public string At(string name) => Path.Combine(root, name);
        public void AssertUnchanged() { foreach (var (path, bytes) in originals) Assert.Equal(bytes, File.ReadAllBytes(path)); }
        public void Dispose() => Directory.Delete(root, true);
    }
}
