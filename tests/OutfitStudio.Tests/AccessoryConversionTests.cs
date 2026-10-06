using System.Text.Json.Nodes;
using OutfitStudio.Core.Mods;
using OutfitStudio.Core.Planning;
using OutfitStudio.Core.Protocol;
using OutfitStudio.Core.Services;

namespace OutfitStudio.Tests;

public sealed class AccessoryConversionTests
{
    [Fact]
    public async Task MixedFamilyRingAccessoryCreatesBothStylesUsingNudeChestAndPreservesSupport()
    {
        using var scene = new Scene();
        var response = await ConversionService.ExecuteAsync(scene.Request);
        Assert.True(response.Success);
        Assert.True(response.AutomaticPlan!.Ready);
        Assert.Equal(4, response.ConvertedModelCount);
        Assert.Contains("Base files", response.PreservedOptionGroups);
        using var output = PenumbraMod.Open(response.OutputDirectory!);
        var groups = output.GetGroupSnapshots();
        Assert.True(JsonNode.DeepEquals(scene.Support, groups.Single(g => g["Name"]!.GetValue<string>() == "Base files")));
        Assert.Equal(scene.SupportBytes, File.ReadAllBytes(Path.Combine(output.RootPath, "stub.mdl")));
        var fits = groups.Single(g => g["Name"]!.GetValue<string>() == "Chest");
        var options = fits["Options"]!.AsArray();
        Assert.Equal(new[] { "None", "Small / Plain", "Small / Chain", "Large / Plain", "Large / Chain" },
            options.Select(o => o!["Name"]!.GetValue<string>()));
        Assert.Equal("Large / Plain", options[fits["DefaultSettings"]!.GetValue<int>()]!["Name"]!.GetValue<string>());
        foreach (var option in options.OfType<JsonObject>().Skip(1))
        {
            bool chain = option["Name"]!.GetValue<string>().EndsWith("Chain");
            var files = option["Files"]!.AsObject();
            Assert.Equal(files[Scene.Left]!.GetValue<string>(), files[Scene.Right]!.GetValue<string>());
            Assert.Equal(chain ? "chain.tex" : "plain.tex", files["chara/style.tex"]!.GetValue<string>());
            var model = File.ReadAllBytes(Path.Combine(output.RootPath, files[Scene.Left]!.GetValue<string>()));
            float z = option["Name"]!.GetValue<string>().StartsWith("Small") ? .1f : .2f;
            z += chain ? .04f : .02f;
            foreach (int address in scene.Accessory.PositionOffsets) Assert.Equal(z, BitConverter.ToSingle(model, address + 8), 5);
        }
        scene.AssertUnchanged();
    }

    [Fact]
    public async Task OtherFamilyWithSameSizesIsNotUsedAsSelectedSource()
    {
        using var scene = new Scene();
        scene.EditSource(meta => meta["Name"] = "YAB+");
        var response = await ConversionService.ExecuteAsync(scene.Request with { Operation = WorkerOperation.Analyze });
        Assert.False(response.AutomaticPlan!.Ready);
        Assert.Contains(response.AutomaticPlan.BlockingReasons, reason => reason.Contains("no matching fit for source body 'YAB+'"));
        Assert.False(Directory.Exists(scene.Request.OutputRoot));
    }

    [Fact]
    public void AccessoryEquipSlotAloneDoesNotChooseBodyRegion()
    {
        using var scene = new Scene();
        using var source = PenumbraMod.Open(scene.Request.SourceModPath);
        using var target = PenumbraMod.Open(scene.Request.TargetModPath);
        using var outfit = PenumbraMod.Open(scene.Request.OutfitModPath);
        var plan = AutoConversionPlanner.Plan(source, target, outfit);
        Assert.False(plan.CanConvert);
        Assert.Contains(plan.Issues, issue => issue.Contains("from its geometry"));
    }

    [Fact]
    public async Task UnexplainedPayloadDifferencesWithinOneStyleStillBlockConversion()
    {
        using var scene = new Scene();
        scene.EditOutfit(meta => meta["Groups"]![1]!["Options"]![4]!["Files"]!["chara/style.tex"] = "chain.tex");
        var response = await ConversionService.ExecuteAsync(scene.Request with { Operation = WorkerOperation.Analyze });
        Assert.False(response.AutomaticPlan!.Ready);
        Assert.Contains(response.AutomaticPlan.BlockingReasons, reason => reason.Contains("style differences"));
    }

    [Fact]
    public async Task ChestAccessoryDoesNotRequireUnrelatedLegReferenceConversion()
    {
        using var scene = new Scene();
        scene.AddUnrelatedLegs();
        var response = await ConversionService.ExecuteAsync(scene.Request);
        Assert.True(response.Success);
        using var source = PenumbraMod.Open(scene.Request.SourceModPath);
        using var target = PenumbraMod.Open(scene.Request.TargetModPath);
        using var outfit = PenumbraMod.Open(scene.Request.OutfitModPath);
        var regions = AutoConversionPlanner.ResolveAccessoryRegions(source, outfit, default);
        var plan = AutoConversionPlanner.Plan(source, target, outfit, accessoryRegions: regions);
        Assert.All(plan.Groups.SelectMany(g => g.Options).SelectMany(o => o.Models), m => Assert.Empty(m.AdditionalBodyReferences));
    }

    [Fact]
    public async Task MixedGarmentAndAccessoryOptionsExpandNeighborReferencesOnlyForTheGarment()
    {
        using var scene = new Scene();
        scene.AddUnrelatedLegs();
        const string garmentGamePath = "chara/equipment/e0001/model/c0201e0001_top.mdl";
        scene.EditOutfit(meta =>
        {
            foreach (var option in meta["Groups"]![1]!["Options"]!.AsArray().OfType<JsonObject>())
                if (option["Name"]!.GetValue<string>().StartsWith("Bibo+"))
                    option["Files"]![garmentGamePath] = "garment.mdl";
        });
        File.WriteAllBytes(Path.Combine(scene.Request.OutfitModPath, "garment.mdl"),
            GeometryTests.Fixture.Create(offset: new(0, 0, .03f), material: "/fabric.mtrl").Bytes);

        using var source = PenumbraMod.Open(scene.Request.SourceModPath);
        using var target = PenumbraMod.Open(scene.Request.TargetModPath);
        using var outfit = PenumbraMod.Open(scene.Request.OutfitModPath);
        var regions = AutoConversionPlanner.ResolveAccessoryRegions(source, outfit, default);
        var plan = AutoConversionPlanner.Plan(source, target, outfit, accessoryRegions: regions);
        Assert.True(plan.CanConvert, string.Join("; ", plan.Issues));
        foreach (var option in Assert.Single(plan.Groups).Options)
        {
            var accessory = Assert.Single(option.Models, model => model.LocalizeReference);
            Assert.Empty(accessory.AdditionalBodyReferences);
            var garment = Assert.Single(option.Models, model => !model.LocalizeReference);
            Assert.Equal("legs.mdl", Assert.Single(garment.AdditionalBodyReferences).SourceModelPath);
        }

        var response = await ConversionService.ExecuteAsync(scene.Request);
        Assert.True(response.Success);
        Assert.Equal(6, response.ConvertedModelCount);
        using var output = PenumbraMod.Open(response.OutputDirectory!);
        var fits = output.GetGroupSnapshots().Single(group => group["Name"]!.GetValue<string>() == "Chest");
        foreach (var option in fits["Options"]!.AsArray().OfType<JsonObject>().Skip(1))
        {
            var files = option["Files"]!.AsObject();
            Assert.NotEqual(files[garmentGamePath]!.GetValue<string>(), files[Scene.Left]!.GetValue<string>());
            Assert.Equal(files[Scene.Left]!.GetValue<string>(), files[Scene.Right]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task ExplicitStylesMayUseDifferentAccessorySlotsWithoutLosingMappings()
    {
        using var scene = new Scene();
        scene.EditOutfit(meta =>
        {
            foreach (int index in new[] { 3, 5 }) meta["Groups"]![1]!["Options"]![index]!["Files"]!.AsObject().Remove(Scene.Right);
        });
        var response = await ConversionService.ExecuteAsync(scene.Request);
        using var output = PenumbraMod.Open(response.OutputDirectory!);
        var fits = output.GetGroupSnapshots().Single(g => g["Name"]!.GetValue<string>() == "Chest");
        foreach (var option in fits["Options"]!.AsArray().OfType<JsonObject>().Skip(1))
            Assert.Equal(!option["Name"]!.GetValue<string>().EndsWith("Chain"), option["Files"]!.AsObject().ContainsKey(Scene.Right));
    }

    [Fact]
    public void RetryCanUseSourceTemplateFromAnotherStyleWithoutChangingDefaultStyle()
    {
        using var scene = new Scene();
        using var source = PenumbraMod.Open(scene.Request.SourceModPath);
        using var target = PenumbraMod.Open(scene.Request.TargetModPath);
        using var outfit = PenumbraMod.Open(scene.Request.OutfitModPath);
        var regions = AutoConversionPlanner.ResolveAccessoryRegions(source, outfit, default);
        var initial = AutoConversionPlanner.Plan(source, target, outfit, accessoryRegions: regions);
        Assert.Contains(3, initial.Groups.Single().ValidTemplateOptionIndices);
        var retry = AutoConversionPlanner.Plan(source, target, outfit, new Dictionary<int, int> { [1] = 3 }, regions);
        Assert.True(retry.CanConvert, string.Join("; ", retry.Issues));
        var group = Assert.Single(retry.Groups);
        Assert.Equal("Large / Plain", group.Options[group.DestinationDefaultOptionIndex].Name);
        Assert.All(group.Options.Where(o => o.Name.EndsWith("Chain")), o => Assert.Equal(3, o.TemplateOptionIndex));
        var (package, _) = AutomaticConversionService.BuildPackage(outfit, retry);
        outfit.ValidateRegenerationPlan(package);
    }

    [Fact]
    public void DefaultOnlyAccessoryWithOneSourceSizeAlsoUsesLocalizedReferences()
    {
        using var scene = new Scene();
        scene.EditSource(meta =>
        {
            var group = meta["Groups"]![0]!;
            group["Options"] = new JsonArray(group["Options"]![0]!.DeepClone());
            group["DefaultSettings"] = 0;
        });
        scene.EditOutfit(meta =>
        {
            var options = meta["Groups"]![1]!["Options"]!;
            meta["DefaultData"] = new JsonObject { ["Files"] = options[2]!["Files"]!.DeepClone() };
            meta["Groups"]!.AsArray().RemoveAt(1);
        });
        using var source = PenumbraMod.Open(scene.Request.SourceModPath);
        using var target = PenumbraMod.Open(scene.Request.TargetModPath);
        using var outfit = PenumbraMod.Open(scene.Request.OutfitModPath);
        var regions = AutoConversionPlanner.ResolveAccessoryRegions(source, outfit, default);
        var plan = AutoConversionPlanner.Plan(source, target, outfit, accessoryRegions: regions);
        Assert.True(plan.CanConvert, string.Join("; ", plan.Issues));
        Assert.All(plan.Groups.SelectMany(g => g.Options).SelectMany(o => o.Models), m => Assert.True(m.LocalizeReference));
    }

    private sealed class Scene : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "OutfitStudio-accessory-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, byte[]> originals = [];
        public const string Left = "chara/accessory/a0001/model/c0201a0001_ril.mdl";
        public const string Right = "chara/accessory/a0001/model/c0201a0001_rir.mdl";
        public GeometryTests.Fixture Accessory { get; } = GeometryTests.Fixture.Create(offset: new(0, 0, .02f), material: "/metal.mtrl");
        public byte[] SupportBytes { get; } = GeometryTests.Fixture.Create(offset: new(0, 8, 0)).Bytes;
        public JsonObject Support { get; } = Group("Base files", 0, Option("Required", new JsonObject
        { ["chara/accessory/a0001/model/c1101a0001_ril.mdl"] = "stub.mdl", ["chara/base.tex"] = "base.tex" }));
        public WorkerRequest Request => new() { Mode = ConversionMode.DestinationSizes, Operation = WorkerOperation.Convert,
            SourceModPath = Path.Combine(root, "source"), TargetModPath = Path.Combine(root, "target"), OutfitModPath = Path.Combine(root, "outfit"),
            OutputRoot = Path.Combine(root, "output"), OutputName = "Accessory fixture", Clearance = 0 };

        public Scene()
        {
            WriteBody("source", "[Author] Bibo+ (Base Install)", ["Nude - Small", "Nude - Medium", "SFW Bra - Medium"], [0, 0, .04f], 2);
            WriteBody("target", "YAB+", ["Small", "Large"], [.1f, .2f], 1);
            var options = new List<JsonObject> { Option("None", new()), AccessoryOption("Other M - Plain", "foreign.mdl", false) };
            foreach (var size in new[] { "S", "M" })
                foreach (bool chain in new[] { false, true })
                    options.Add(AccessoryOption($"Bibo+ {size} - {(chain ? "Chain" : "Plain")}", $"{size}-{chain}.mdl", chain));
            WriteMeta("outfit", "Accessory", Support, Group("Body selection", 1, options.ToArray()));
            File.WriteAllBytes(Path.Combine(Request.OutfitModPath, "stub.mdl"), SupportBytes);
            File.WriteAllBytes(Path.Combine(Request.OutfitModPath, "foreign.mdl"), [1, 2, 3]);
            foreach (var size in new[] { "S", "M" })
                foreach (bool chain in new[] { false, true })
                    File.WriteAllBytes(Path.Combine(Request.OutfitModPath, $"{size}-{chain}.mdl"), chain
                        ? GeometryTests.Fixture.Create(offset: new(0, 0, .04f), material: "/metal.mtrl").Bytes : Accessory.Bytes);
            foreach (var file in new[] { "base.tex", "plain.tex", "chain.tex" }) File.WriteAllText(Path.Combine(Request.OutfitModPath, file), file);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) originals[file] = File.ReadAllBytes(file);
        }

        public void AddUnrelatedLegs()
        {
            foreach (var (directory, z) in new[] { ("source", 0f), ("target", .8f) })
            {
                string path = Path.Combine(root, directory, "meta.json");
                var meta = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                meta["Groups"]!.AsArray().Add(Group("Legs", 0, Option("Medium", new JsonObject
                    { ["chara/c0201e0000_dwn.mdl"] = "legs.mdl" })));
                File.WriteAllText(path, meta.ToJsonString());
                File.WriteAllBytes(Path.Combine(root, directory, "legs.mdl"), GeometryTests.Fixture.Create(offset: new(0, -4, z)).Bytes);
            }
        }
        private void WriteBody(string directory, string name, string[] sizes, float[] offsets, int selected)
        {
            WriteMeta(directory, name, Group("Chest", selected, sizes.Select((size, index) =>
                Option(size, new JsonObject { ["chara/c0201e0000_top.mdl"] = $"body{index}.mdl" })).ToArray()));
            for (int i = 0; i < sizes.Length; i++) File.WriteAllBytes(Path.Combine(root, directory, $"body{i}.mdl"),
                GeometryTests.Fixture.Create(offset: new(0, 0, offsets[i])).Bytes);
        }
        private void WriteMeta(string directory, string name, params JsonObject[] groups)
        {
            string path = Path.Combine(root, directory); Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "meta.json"), new JsonObject { ["FileVersion"] = 4, ["Name"] = name,
                ["Groups"] = new JsonArray(groups.Select(g => g.DeepClone()).ToArray()) }.ToJsonString());
        }
        private static JsonObject AccessoryOption(string name, string file, bool chain) => Option(name, new JsonObject
            { [Left] = file, [Right] = file, ["chara/style.tex"] = chain ? "chain.tex" : "plain.tex" });
        private static JsonObject Option(string name, JsonObject files) => new() { ["Name"] = name, ["Files"] = files };
        private static JsonObject Group(string name, int selected, params JsonObject[] options) => new()
            { ["Name"] = name, ["Type"] = "Single", ["DefaultSettings"] = selected, ["Options"] = new JsonArray(options.Select(o => o.DeepClone()).ToArray()) };
        public void EditSource(Action<JsonObject> edit) => Edit(Request.SourceModPath, edit);
        public void EditOutfit(Action<JsonObject> edit) => Edit(Request.OutfitModPath, edit);
        private static void Edit(string directory, Action<JsonObject> edit)
        { string path = Path.Combine(directory, "meta.json"); var meta = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); edit(meta); File.WriteAllText(path, meta.ToJsonString()); }
        public void AssertUnchanged() { foreach (var (path, bytes) in originals) Assert.Equal(bytes, File.ReadAllBytes(path)); }
        public void Dispose() => Directory.Delete(root, true);
    }
}
