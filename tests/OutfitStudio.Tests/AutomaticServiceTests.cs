using System.Text.Json.Nodes;
using OutfitStudio.Core.Mods;
using OutfitStudio.Core.Planning;
using OutfitStudio.Core.Protocol;
using OutfitStudio.Core.Services;

namespace OutfitStudio.Tests;

public sealed class AutomaticServiceTests
{
    [Fact]
    public async Task UsesAnotherMatchedSourceSizeWhenThePreferredOutfitModelIsUnsupported()
    {
        using var scene = new Scene();
        File.WriteAllBytes(Path.Combine(scene.Outfit, "Medium.mdl"), [0, 1, 2]);
        var response = await ConversionService.ExecuteAsync(scene.Request);
        Assert.True(response.AutomaticPlan!.Ready);
        Assert.Equal("Small", Assert.Single(response.AutomaticPlan.DestinationGroups).SourceSize);
        Assert.Equal(2, response.ConvertedModelCount);
        using var output = PenumbraMod.Open(response.OutputDirectory!);
        Assert.All(output.GetModelFiles(), m => Assert.StartsWith("outfitstudio/generated/", m.RelativePath));
    }

    [Fact]
    public async Task ThreeModsProduceDestinationSizesAndRetainMaterialChoicesWithoutManualReferences()
    {
        using var scene = new Scene();
        var analysis = await ConversionService.ExecuteAsync(scene.Request with { Operation = WorkerOperation.Analyze });
        Assert.True(analysis.Success);
        Assert.NotNull(analysis.AutomaticPlan);
        Assert.True(analysis.AutomaticPlan.Ready, string.Join("; ", analysis.AutomaticPlan.BlockingReasons));
        Assert.Equal(new[] { "Extra Small", "Extra Large" }, Assert.Single(analysis.AutomaticPlan.DestinationGroups).Options);
        Assert.Contains("Material", analysis.PreservedOptionGroups);
        Assert.DoesNotContain("Chest size", analysis.PreservedOptionGroups);
        Assert.False(Directory.Exists(scene.Output));

        var result = await ConversionService.ExecuteAsync(scene.Request);
        Assert.Equal(2, result.ConvertedModelCount);
        using var output = PenumbraMod.Open(result.OutputDirectory!);
        var fit = output.Groups.Single(g => g.Name != "Material");
        Assert.Equal(new[] { "None", "Extra Small", "Extra Large" }, fit.Options.Select(o => o.Name));
        Assert.Equal("Extra Large", fit.Options[(int)fit.DefaultSettings].Name);
        Assert.DoesNotContain(fit.Options, o => o.Name is "Small" or "Medium");
        var material = output.GetGroupSnapshots().Single(g => g["Name"]!.GetValue<string>() == "Material");
        Assert.True(JsonNode.DeepEquals(scene.Material, material));
        foreach (var (size, z) in new[] { ("Extra Small", .12f), ("Extra Large", .22f) })
        {
            var selected = output.GetModelFiles(ModConversionScope.SelectedOptions, new Dictionary<string, List<string>> { [fit.Name] = [size] });
            var file = Assert.Single(selected);
            Assert.StartsWith("outfitstudio/generated/", file.RelativePath);
            var bytes = File.ReadAllBytes(file.FullPath);
            foreach (var address in scene.Fixture.PositionOffsets)
                Assert.Equal(z, BitConverter.ToSingle(bytes, address + 8), 5);
        }
        Assert.Equal("texture", File.ReadAllText(Path.Combine(result.OutputDirectory!, "shiny.tex")));
        scene.AssertUnchanged();
    }

    [Fact]
    public async Task UnknownSourceSizeGivesReadableBlockAndNeverPublishes()
    {
        using var scene = new Scene();
        var path = Path.Combine(scene.Outfit, "meta.json");
        var meta = JsonNode.Parse(File.ReadAllText(path))!;
        meta["Groups"]![0]!["Options"]![1]!["Name"] = "Unidentified fit";
        File.WriteAllText(path, meta.ToJsonString());
        var result = await ConversionService.ExecuteAsync(scene.Request with { Operation = WorkerOperation.Analyze });
        Assert.False(result.AutomaticPlan!.Ready);
        Assert.NotEmpty(result.AutomaticPlan.BlockingReasons);
        await Assert.ThrowsAsync<InvalidDataException>(() => ConversionService.ExecuteAsync(scene.Request));
        Assert.False(Directory.Exists(scene.Output));
    }

    [Fact]
    public async Task NeighboringLegChoiceChangesTheDressWhileVisibleSelectorsRemainIndependent()
    {
        using var scene = new CoupledScene();
        var result = await ConversionService.ExecuteAsync(scene.Request);
        Assert.True(result.Success);
        using var output = PenumbraMod.Open(result.OutputDirectory!);
        var groups = output.GetGroupSnapshots();
        var visible = groups.Where(group => group["Type"]!.GetValue<string>() == "Single" && group["Options"]!.AsArray().Count > 1).ToArray();
        Assert.Equal(new[] { "Chest - Smallclothes", "Legs - Smallclothes" }, visible.Select(group => group["Name"]!.GetValue<string>()));
        Assert.All(visible, group => Assert.Equal(new[] { "None", "Small", "Large" },
            group["Options"]!.AsArray().Select(option => option!["Name"]!.GetValue<string>())));
        Assert.All(visible, group => Assert.All(group["Options"]!.AsArray().Skip(1), option =>
        {
            Assert.DoesNotContain(option!["Files"]!.AsObject(), pair => pair.Key.EndsWith(".mdl"));
            Assert.Equal("style.tex", option["Files"]!["chara/style.tex"]!.GetValue<string>());
        }));
        // This top model lies over the lower-body reference, like a long dress. Its
        // own chest selection stays fixed while the independent leg size changes.
        foreach (var (legs, z) in new[] { ("Small", .12f), ("Large", .22f), ("None", .22f) })
        {
            var active = ActiveFiles(groups, new Dictionary<string, string> { ["Chest - Smallclothes"] = "Small", ["Legs - Smallclothes"] = legs });
            var top = File.ReadAllBytes(Path.Combine(result.OutputDirectory!, active[CoupledScene.TopPath]));
            foreach (var address in scene.Dress.PositionOffsets) Assert.Equal(z, BitConverter.ToSingle(top, address + 8), 5);
            Assert.Equal(legs != "None", active.ContainsKey(CoupledScene.LegPath));
        }
        var noChest = ActiveFiles(groups, new Dictionary<string, string> { ["Chest - Smallclothes"] = "None", ["Legs - Smallclothes"] = "Large" });
        Assert.False(noChest.ContainsKey(CoupledScene.TopPath));
        Assert.True(noChest.ContainsKey(CoupledScene.LegPath));
        // None fallbacks reuse models for the body defaults rather than generating
        // identical physical conversions again under a different predicate.
        Assert.Equal(8, result.ConvertedModelCount);
        Assert.Equal(result.ConvertedModelCount, result.AutomaticPlan!.ModelJobs);
        scene.AssertUnchanged();
    }

    [Fact]
    public void ConditionalVariantsUseFixedDataGroupsAndReuseOnlyIdenticalReferences()
    {
        using var scene = new CoupledScene();
        using var outfit = PenumbraMod.Open(scene.Request.OutfitModPath);
        var neighborOptions = Enumerable.Range(0, 35).Select(index => new AutomaticOptionPlan($"Leg {index}",
            [new("legs.mdl", "source-legs.mdl", $"target-legs-{index}.mdl", [])])).ToArray();
        var variants = Enumerable.Range(0, 35).Select(index => new AutomaticModelPlan("dress.mdl", "source-chest.mdl", "target-chest.mdl",
            [new("source-legs.mdl", $"target-legs-{index}.mdl")]) { RequiredOptions = [new(1, index)] }).ToList();
        variants.Add(variants[0] with { RequiredOptions = [new(1, 0) { IsOriginalOption = true }] });
        var plan = new AutomaticConversionPlan([
            new(0, "Chest size", 1, "Small", "top", "Chest size", 0, [new("Chest", variants)]),
            new(1, "Legs size", 1, "Small", "dwn", "Legs size", 0, neighborOptions),
        ], [], []);
        var (package, jobs) = AutomaticConversionService.BuildPackage(outfit, plan);
        outfit.ValidateRegenerationPlan(package);
        Assert.Equal(70, jobs.Count);
        var hidden = package.GroupReplacements.Where(replacement => replacement.Group["Options"]!.AsArray().Any(option => option!["Condition"] is not null))
            .Select(replacement => replacement.Group).ToArray();
        Assert.Equal(36, hidden.Length);
        Assert.All(hidden, group =>
        {
            Assert.Equal("Single", group["Type"]!.GetValue<string>());
            Assert.Single(group["Options"]!.AsArray());
            Assert.Equal(0, group["DefaultSettings"]!.GetValue<int>());
            Assert.Null(group["Layout"]);
            Assert.Null(group["ParentSetting"]);
        });
        var groups = package.GroupReplacements.Select(replacement => replacement.Group).ToArray();
        for (int index = 0; index < 35; index++)
        {
            var files = ActiveFiles(groups, new Dictionary<string, string> { ["Chest size"] = "Chest", ["Legs size"] = $"Leg {index}" });
            var model = Assert.Single(jobs, job => job.File.OutputRelativePath == files[CoupledScene.TopPath]);
            Assert.Equal($"target-legs-{index}.mdl", Assert.Single(model.Mapping.AdditionalBodyReferences).TargetModelPath);
        }
        var none = ActiveFiles(groups, new Dictionary<string, string> { ["Chest size"] = "Chest", ["Legs size"] = "None" });
        var first = ActiveFiles(groups, new Dictionary<string, string> { ["Chest size"] = "Chest", ["Legs size"] = "Leg 0" });
        Assert.Equal(first[CoupledScene.TopPath], none[CoupledScene.TopPath]);
    }

    [Fact]
    public async Task CancellingAfterOneLinkedVariantRemovesAllPartialOutput()
    {
        using var scene = new CoupledScene();
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(value =>
        {
            if (value.Stage == "Create sizes" && value.Completed == 1) cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConversionService.ExecuteAsync(scene.Request, progress, cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scene.Request.OutputRoot));
        scene.AssertUnchanged();
    }

    private sealed class ImmediateProgress(Action<WorkerProgress> report) : IProgress<WorkerProgress>
    {
        public void Report(WorkerProgress value) => report(value);
    }

    // Evaluate the limited native condition grammar independently of generation.
    private static Dictionary<string, string> ActiveFiles(IReadOnlyList<JsonObject> groups, IReadOnlyDictionary<string, string> selected)
    {
        var activeIds = groups.Where(group => group["Type"]!.GetValue<string>() == "Single")
            .Select(group => selected.TryGetValue(group["Name"]!.GetValue<string>(), out var choice)
                ? group["Options"]!.AsArray().Single(option => option!["Name"]!.GetValue<string>() == choice)
                : group["Options"]![(int)group["DefaultSettings"]!.GetValue<int>()])
            .Select(option => option!["Id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        bool Matches(JsonNode? condition) => condition is null || condition["Type"]!.GetValue<string>() switch
        {
            "Setting" => activeIds.Contains(condition["Setting"]!.GetValue<string>()),
            "And" => condition["Conditions"]!.AsArray().All(Matches),
            _ => throw new InvalidDataException("Unexpected generated condition type."),
        };
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            if (!Matches(group["Condition"])) continue;
            int index = 0;
            foreach (var option in group["Options"]!.AsArray().OfType<JsonObject>())
            {
                bool enabled = group["Type"]!.GetValue<string>() == "Single" ? activeIds.Contains(option["Id"]!.GetValue<string>())
                    : (group["DefaultSettings"]!.GetValue<ulong>() & (1UL << index)) != 0;
                index++;
                if (!enabled || !Matches(option["Condition"]) || option["Files"] is not JsonObject mappings) continue;
                foreach (var mapping in mappings.Where(mapping => mapping.Key.EndsWith(".mdl")))
                {
                    var path = mapping.Value!.GetValue<string>();
                    if (files.TryGetValue(mapping.Key, out var previous)) Assert.Equal(previous, path);
                    files[mapping.Key] = path;
                }
            }
        }
        return files;
    }

    private sealed class CoupledScene : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "OutfitStudio-coupled-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, byte[]> originals = [];
        public const string TopPath = "chara/c0101e0003_top.mdl";
        public const string LegPath = "chara/c0101e0003_dwn.mdl";
        public GeometryTests.Fixture Dress { get; } = GeometryTests.Fixture.Create(offset: new(0, 0, .02f), material: "/dress.mtrl");
        public WorkerRequest Request => new() { Mode = ConversionMode.DestinationSizes, Operation = WorkerOperation.Convert,
            SourceModPath = Path.Combine(root, "source"), TargetModPath = Path.Combine(root, "target"), OutfitModPath = Path.Combine(root, "outfit"),
            OutputRoot = Path.Combine(root, "output"), OutputName = "Coupled fixture", Clearance = 0 };
        public CoupledScene()
        {
            foreach (var (directory, body, target) in new[] { ("source", true, false), ("target", true, true), ("outfit", false, false) })
            {
                var path = Path.Combine(root, directory); Directory.CreateDirectory(path);
                var groups = new JsonArray();
                foreach (var (region, slot) in new[] { ("Chest", "top"), ("Legs", "dwn") })
                {
                    var options = new JsonArray();
                    if (!body) options.Add(new JsonObject { ["Name"] = "None", ["Files"] = new JsonObject() });
                    foreach (var size in target ? new[] { "Small", "Large" } : new[] { "Small" })
                    {
                        var file = body ? $"{slot}-{size}.mdl" : slot == "top" ? "dress.mdl" : "legs.mdl";
                        var gamePath = body ? $"chara/c0101e0001_{slot}.mdl" : slot == "top" ? TopPath : LegPath;
                        var mappings = new JsonObject { [gamePath] = file };
                        if (!body) mappings["chara/style.tex"] = "style.tex";
                        options.Add(new JsonObject { ["Name"] = size, ["Files"] = mappings });
                        float z = target ? (size == "Small" ? .1f : .2f) : 0;
                        var fixture = body ? GeometryTests.Fixture.Create(offset: new(0, slot == "top" ? 3 : 0, z))
                            : slot == "top" ? Dress : GeometryTests.Fixture.Create();
                        File.WriteAllBytes(Path.Combine(path, file), fixture.Bytes);
                    }
                    groups.Add(new JsonObject { ["Name"] = body ? $"{region} - Smallclothes" : $"{region} size", ["Type"] = "Single",
                        ["DefaultSettings"] = body && !target ? 0 : 1, ["Options"] = options });
                }
                File.WriteAllText(Path.Combine(path, "style.tex"), "style");
                File.WriteAllText(Path.Combine(path, "meta.json"), new JsonObject { ["FileVersion"] = 4, ["Name"] = directory, ["Groups"] = groups }.ToJsonString());
            }
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) originals[file] = File.ReadAllBytes(file);
        }
        public void AssertUnchanged() { foreach (var (path, bytes) in originals) Assert.Equal(bytes, File.ReadAllBytes(path)); }
        public void Dispose() => Directory.Delete(root, true);
    }

    private sealed class Scene : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "OutfitStudio-auto-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, byte[]> originals = [];
        public string Outfit => Path.Combine(root, "outfit");
        public string Output => Path.Combine(root, "output");
        public GeometryTests.Fixture Fixture { get; } = GeometryTests.Fixture.Create(offset: new(0, 0, .02f));
        public JsonObject Material { get; } = JsonNode.Parse("""{"Name":"Material","Type":"Single","DefaultSettings":0,"Options":[{"Name":"Shiny","Files":{"chara/material.tex":"shiny.tex"}},{"Name":"Matte","Files":{"chara/material.tex":"matte.tex"}}]}""")!.AsObject();
        public WorkerRequest Request => new() { Mode = ConversionMode.DestinationSizes, Operation = WorkerOperation.Convert,
            SourceModPath = Path.Combine(root, "source"), TargetModPath = Path.Combine(root, "target"), OutfitModPath = Outfit,
            OutputRoot = Output, OutputName = "Automatic fixture", Clearance = 0 };
        public Scene()
        {
            WriteBody("source", new[] { "Small", "Medium" }, new[] { 0f, 0f });
            WriteBody("target", new[] { "Extra Small", "Extra Large" }, new[] { .1f, .2f });
            Directory.CreateDirectory(Outfit);
            var options = new JsonArray(new JsonObject { ["Name"] = "None", ["Files"] = new JsonObject() });
            foreach (var size in new[] { "Small", "Medium" })
            {
                options.Add(new JsonObject { ["Name"] = size, ["Files"] = new JsonObject { ["chara/c0101e0003_top.mdl"] = size + ".mdl" } });
                File.WriteAllBytes(Path.Combine(Outfit, size + ".mdl"), Fixture.Bytes);
            }
            var meta = new JsonObject { ["FileVersion"] = 4, ["Name"] = "Outfit", ["Groups"] = new JsonArray(
                new JsonObject { ["Name"] = "Chest size", ["Type"] = "Single", ["DefaultSettings"] = 2, ["Options"] = options }, Material.DeepClone()) };
            File.WriteAllText(Path.Combine(Outfit, "meta.json"), meta.ToJsonString());
            File.WriteAllText(Path.Combine(Outfit, "shiny.tex"), "texture");
            File.WriteAllText(Path.Combine(Outfit, "matte.tex"), "matte texture");
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) originals[file] = File.ReadAllBytes(file);
        }
        private void WriteBody(string directory, string[] names, float[] offsets)
        {
            var path = Path.Combine(root, directory); Directory.CreateDirectory(path);
            var options = new JsonArray();
            for (int i = 0; i < names.Length; i++)
            {
                var file = $"body{i}.mdl";
                options.Add(new JsonObject { ["Name"] = names[i], ["Files"] = new JsonObject { ["chara/c0101e0001_top.mdl"] = file } });
                File.WriteAllBytes(Path.Combine(path, file), GeometryTests.Fixture.Create(offset: new(0, 0, offsets[i])).Bytes);
            }
            File.WriteAllText(Path.Combine(path, "meta.json"), new JsonObject { ["FileVersion"] = 4, ["Name"] = directory,
                ["Groups"] = new JsonArray(new JsonObject { ["Name"] = "Chest - Smallclothes", ["Type"] = "Single", ["DefaultSettings"] = 1, ["Options"] = options }) }.ToJsonString());
        }
        public void AssertUnchanged() { foreach (var (path, bytes) in originals) Assert.Equal(bytes, File.ReadAllBytes(path)); }
        public void Dispose() => Directory.Delete(root, true);
    }
}
