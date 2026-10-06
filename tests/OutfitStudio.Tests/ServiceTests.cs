using System.Text.Json.Nodes;
using OutfitStudio.Core.Mods;
using OutfitStudio.Core.Protocol;
using OutfitStudio.Core.Services;

namespace OutfitStudio.Tests;

public sealed class ServiceTests
{
    [Fact]
    public async Task AnalysisListsModelsWithoutCreatingOutputOrChangingInputs()
    {
        using var scene = new Scene();
        var result = await ConversionService.ExecuteAsync(scene.Request with
        {
            Operation = WorkerOperation.Analyze,
            SourceModelPath = null,
            TargetModelPath = null,
        });
        Assert.True(result.Success);
        Assert.Null(result.BodiesCompatible);
        Assert.Single(result.SourceModels);
        Assert.Equal(2, result.TargetModels.Length);
        Assert.Equal(2, result.OutfitModels.Length);
        Assert.False(Directory.Exists(scene.Output));
        scene.AssertInputsUnchanged();
    }

    [Fact]
    public async Task PerModelReferencesProduceIndependentGeometryAndKeepAllOptions()
    {
        using var scene = new Scene();
        var result = await ConversionService.ExecuteAsync(scene.Request with
        {
            ModelMappings = [new("outfit-b.mdl", "body.mdl", "target-b.mdl")],
        });
        Assert.True(result.Success);
        Assert.Equal(2, result.ConvertedModelCount);
        Assert.Equal(6, result.ConvertedVertexCount);
        Assert.NotNull(result.OutputDirectory);
        using var mod = PenumbraMod.Open(result.OutputDirectory);
        Assert.Single(mod.Groups);
        Assert.Equal(2, mod.GetModelFiles().Count);
        var a = File.ReadAllBytes(Path.Combine(result.OutputDirectory, "outfit-a.mdl"));
        var b = File.ReadAllBytes(Path.Combine(result.OutputDirectory, "outfit-b.mdl"));
        foreach (var position in scene.OutfitFixture.PositionOffsets)
        {
            Assert.Equal(0.12f, BitConverter.ToSingle(a, position + 8), 5);
            Assert.Equal(0.22f, BitConverter.ToSingle(b, position + 8), 5);
        }
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory, "outfitstudio-report.json")));
        scene.AssertInputsUnchanged();
    }

    [Fact]
    public async Task CurrentSelectionOutputCannotActivateUnconvertedOptions()
    {
        using var scene = new Scene();
        var result = await ConversionService.ExecuteAsync(scene.Request with
        {
            Scope = ConversionScope.CurrentSelection,
            SelectedOptions = new() { ["Fit"] = ["B"] },
        });
        Assert.Equal(1, result.ConvertedModelCount);
        using var mod = PenumbraMod.Open(result.OutputDirectory!);
        Assert.Empty(mod.Groups);
        Assert.Equal("outfit-b.mdl", Assert.Single(mod.GetModelFiles()).RelativePath);
        Assert.Equal(scene.OutfitFixture.Bytes, File.ReadAllBytes(Path.Combine(result.OutputDirectory!, "outfit-a.mdl")));
        scene.AssertInputsUnchanged();
    }

    [Fact]
    public async Task CancellingDuringConversionRemovesTheWholeStage()
    {
        using var scene = new Scene();
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(p =>
        {
            if (p.Stage == "Convert")
                cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConversionService.ExecuteAsync(scene.Request, progress, cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scene.Output));
        scene.AssertInputsUnchanged();
    }

    [Fact]
    public async Task OutputInsideAnAliasedBodyModIsRejected()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var scene = new Scene();
        var alias = scene.At("source-alias");
        Directory.CreateSymbolicLink(alias, scene.Source);
        await Assert.ThrowsAsync<InvalidDataException>(() => ConversionService.ExecuteAsync(scene.Request with
        {
            SourceModPath = alias,
            OutputRoot = Path.Combine(scene.Source, "nested"),
        }));
        Assert.False(Directory.Exists(Path.Combine(scene.Source, "nested")));
        scene.AssertInputsUnchanged();
    }

    [Fact]
    public async Task UnsupportedOutfitPreventsPublishingAnyPartialConversion()
    {
        using var scene = new Scene();
        File.WriteAllBytes(Path.Combine(scene.Outfit, "outfit-b.mdl"), [0, 1, 2]);
        var analysis = await ConversionService.ExecuteAsync(scene.Request with { Operation = WorkerOperation.Analyze });
        Assert.False(analysis.BodiesCompatible);
        Assert.Contains(analysis.Warnings, warning => warning.Contains("outfit-b.mdl", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => ConversionService.ExecuteAsync(scene.Request));
        Assert.False(Directory.Exists(scene.Output));
    }

    private sealed class ImmediateProgress(Action<WorkerProgress> report) : IProgress<WorkerProgress>
    {
        public void Report(WorkerProgress value) => report(value);
    }

    private sealed class Scene : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "OutfitStudio-service-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, byte[]> original = [];
        public string Source => At("source");
        public string Target => At("target");
        public string Outfit => At("outfit");
        public string Output => At("output");
        public GeometryTests.Fixture OutfitFixture { get; } = GeometryTests.Fixture.Create(offset: new(0, 0, 0.02f));
        public WorkerRequest Request => new()
        {
            Operation = WorkerOperation.Convert,
            SourceModPath = Source, TargetModPath = Target, OutfitModPath = Outfit,
            SourceModelPath = "body.mdl", TargetModelPath = "target-a.mdl",
            OutputRoot = Output, OutputName = "Converted fixture", Clearance = 0,
        };

        public Scene()
        {
            Directory.CreateDirectory(root);
            WriteMod(Source, "Source", new() { ["chara/c0101e0001_top.mdl"] = "body.mdl" });
            WriteMod(Target, "Target", new()
            {
                ["chara/c0101e0001_top.mdl"] = "target-a.mdl",
                ["chara/c0101e0002_top.mdl"] = "target-b.mdl",
            });
            Directory.CreateDirectory(Outfit);
            File.WriteAllText(Path.Combine(Outfit, "meta.json"), """
                {"FileVersion":4,"Name":"Outfit","Author":"Fixture creator","Groups":[{"Name":"Fit","Type":"Single",
                 "Options":[{"Name":"A","Files":{"chara/c0101e0003_top.mdl":"outfit-a.mdl"}},
                            {"Name":"B","Files":{"chara/c0101e0003_top.mdl":"outfit-b.mdl"}}]}]}
                """);
            File.WriteAllBytes(Path.Combine(Source, "body.mdl"), GeometryTests.Fixture.Create().Bytes);
            File.WriteAllBytes(Path.Combine(Target, "target-a.mdl"), GeometryTests.Fixture.Create(offset: new(0, 0, 0.1f)).Bytes);
            File.WriteAllBytes(Path.Combine(Target, "target-b.mdl"), GeometryTests.Fixture.Create(offset: new(0, 0, 0.2f)).Bytes);
            File.WriteAllBytes(Path.Combine(Outfit, "outfit-a.mdl"), OutfitFixture.Bytes);
            File.WriteAllBytes(Path.Combine(Outfit, "outfit-b.mdl"), OutfitFixture.Bytes);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                original[file] = File.ReadAllBytes(file);
        }

        private static void WriteMod(string path, string name, JsonObject files)
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "meta.json"), new JsonObject
            {
                ["FileVersion"] = 4, ["Name"] = name, ["DefaultData"] = new JsonObject { ["Files"] = files },
            }.ToJsonString());
        }

        public string At(string name) => Path.Combine(root, name);
        public void AssertInputsUnchanged()
        {
            foreach (var (path, bytes) in original)
                Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        public void Dispose() => Directory.Delete(root, true);
    }
}
