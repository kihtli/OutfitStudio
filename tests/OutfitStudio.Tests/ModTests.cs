using System.IO.Compression;
using System.Text.Json.Nodes;
using OutfitStudio.Core.Mods;

namespace OutfitStudio.Tests;

public sealed class ModTests
{
    [Fact]
    public async Task AllOptionsPreservesMetadataAndConvertsSharedFileOnce()
    {
        using var temp = new Scratch();
        var input = temp.Mod("source", """
            {"FileVersion":4,"Name":"Creator outfit","Author":"Original Author","Website":"https://example.com",
             "Identifier":"00000000-0000-0000-0000-000000000001","CustomField":{"untouched":true},
             "Groups":[{"Name":"Fit","Type":"Single","Options":[
                {"Name":"One","Files":{"chara/a.mdl":"shared.mdl","chara/a.tex":"skin.tex"}},
                {"Name":"Two","Files":{"chara/b.mdl":"shared.mdl"}}]}]}
            """, "shared.mdl", "skin.tex");
        using var mod = PenumbraMod.Open(input);
        File.WriteAllText(System.IO.Path.Combine(input, "heliosphere.json"), "{\"Id\":\"original-download\"}");
        File.WriteAllText(System.IO.Path.Combine(input, "meta.json.bak"), "{\"Name\":\"old metadata\"}");
        var calls = 0;
        var output = await mod.CloneAndConvertAsync(temp.Path("output"), "My Conversion", async (model, path, ct) =>
        {
            calls++;
            Assert.Equal(2, model.GamePaths.Count);
            await File.WriteAllTextAsync(path, "converted", ct);
        });
        Assert.Equal(1, calls);
        Assert.False(File.Exists(System.IO.Path.Combine(output, "heliosphere.json")));
        Assert.False(File.Exists(System.IO.Path.Combine(output, "meta.json.bak")));
        Assert.Equal("original", File.ReadAllText(System.IO.Path.Combine(input, "shared.mdl")));
        Assert.Equal("converted", File.ReadAllText(System.IO.Path.Combine(output, "shared.mdl")));
        Assert.Equal("original", File.ReadAllText(System.IO.Path.Combine(output, "skin.tex")));
        var before = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(input, "meta.json")))!;
        var after = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(output, "meta.json")))!;
        Assert.Equal("My Conversion", after["Name"]!.GetValue<string>());
        Assert.Equal("Original Author", after["Author"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(before["CustomField"], after["CustomField"]));
        Assert.True(JsonNode.DeepEquals(before["Groups"], after["Groups"]));
        Assert.NotEqual(before["Identifier"]!.GetValue<string>(), after["Identifier"]!.GetValue<string>());
    }

    [Fact]
    public async Task SelectedConfigurationResolvesPrioritiesAndFlattensGroups()
    {
        using var temp = new Scratch();
        var input = temp.Mod("source", """
            {"FileVersion":4,"Name":"Priority test","DefaultData":{"Files":{"chara/a.mdl":"default.mdl"}},
             "Groups":[
              {"Name":"First","Type":"Single","Priority":1,"Options":[{"Name":"A","Files":{"chara/a.mdl":"first.mdl"}}]},
              {"Name":"Last","Type":"Multi","Priority":1,"DefaultSettings":3,"Options":[
                {"Name":"First choice","Priority":2,"Files":{"chara/a.mdl":"winner.mdl"}},
                {"Name":"Second choice","Priority":2,"Files":{"chara/a.mdl":"loser.mdl"}}]}]}
            """, "default.mdl", "first.mdl", "winner.mdl", "loser.mdl");
        using var mod = PenumbraMod.Open(input);
        var models = mod.GetModelFiles(ModConversionScope.SelectedOptions);
        Assert.Equal("winner.mdl", Assert.Single(models).RelativePath);
        var output = await mod.CloneAndConvertAsync(temp.Path("output"), "Converted", ConvertMarker,
            ModConversionScope.SelectedOptions);
        using var clone = PenumbraMod.Open(output);
        Assert.Empty(clone.Groups);
        Assert.Equal("winner.mdl", Assert.Single(clone.GetModelFiles()).RelativePath);
        Assert.Equal("original", File.ReadAllText(System.IO.Path.Combine(output, "loser.mdl")));
    }

    [Fact]
    public void ExplicitSelectionsOverrideDefaultsAndRejectStaleNames()
    {
        using var temp = new Scratch();
        var input = temp.Mod("source", """
            {"FileVersion":4,"Name":"Choices","Groups":[{"Name":"Fit","Type":"Single","Options":[
              {"Name":"Small","Files":{"chara/a.mdl":"small.mdl"}},
              {"Name":"Large","Files":{"chara/a.mdl":"large.mdl"}}]}]}
            """, "small.mdl", "large.mdl");
        using var mod = PenumbraMod.Open(input);
        Assert.Equal("large.mdl", Assert.Single(mod.GetModelFiles(ModConversionScope.SelectedOptions,
            new Dictionary<string, List<string>> { ["Fit"] = ["Large"] })).RelativePath);
        Assert.Throws<InvalidDataException>(() => mod.GetModelFiles(ModConversionScope.SelectedOptions,
            new Dictionary<string, List<string>> { ["Fit"] = ["Deleted"] }));
        Assert.Throws<InvalidDataException>(() => mod.GetModelFiles(ModConversionScope.SelectedOptions,
            new Dictionary<string, List<string>> { ["Missing"] = ["Large"] }));
    }

    [Fact]
    public async Task SelectedMetadataKeepsWinningEntryForEachIdentifier()
    {
        using var temp = new Scratch();
        var input = temp.Mod("source", """
            {"FileVersion":4,"Name":"Metadata","DefaultData":{"Files":{"chara/a.mdl":"a.mdl"},
             "Manipulations":[{"Type":"Eqp","Manipulation":{"SetId":1,"Slot":"Body","Entry":100}}]},
             "Groups":[{"Name":"Fit","Type":"Single","Options":[{"Name":"On","Manipulations":[
               {"Type":"Eqp","Manipulation":{"Entry":200,"Slot":"Body","SetId":1}},
               {"Type":"Eqp","Manipulation":{"SetId":2,"Slot":"Body","Entry":300}}]}]}]}
            """, "a.mdl");
        using var mod = PenumbraMod.Open(input);
        var output = await mod.CloneAndConvertAsync(temp.Path("output"), "Flattened", ConvertMarker,
            ModConversionScope.SelectedOptions);
        var metadata = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(output, "meta.json")))!;
        var manipulations = metadata["DefaultData"]!["Manipulations"]!.AsArray();
        Assert.Equal(2, manipulations.Count);
        Assert.Equal(200, manipulations[0]!["Manipulation"]!["Entry"]!.GetValue<int>());
    }

    [Fact]
    public void FullModeFindsCombiningContainersWhileSelectedRejectsThem()
    {
        using var temp = new Scratch();
        var input = temp.Mod("source", """
            {"FileVersion":4,"Name":"Combined","Groups":[{"Name":"Parts","Type":"Combining",
             "Options":[{"Name":"Sleeves"}],"Containers":[{"Files":{"chara/a.mdl":"a.mdl"}},{}]}]}
            """, "a.mdl");
        using var mod = PenumbraMod.Open(input);
        Assert.Single(mod.GetModelFiles());
        Assert.Throws<NotSupportedException>(() => mod.GetModelFiles(ModConversionScope.SelectedOptions));
    }

    [Fact]
    public void SelectedModeRejectsConditions()
    {
        using var temp = new Scratch();
        var input = temp.Mod("source", """
            {"FileVersion":4,"Name":"Conditional","Groups":[{"Name":"Fit","Type":"Single","Condition":{"Type":"True"},
              "Options":[{"Name":"A","Files":{"chara/a.mdl":"a.mdl"}}]}]}
            """, "a.mdl");
        using var mod = PenumbraMod.Open(input);
        Assert.Throws<NotSupportedException>(() => mod.GetModelFiles(ModConversionScope.SelectedOptions));
    }

    [Fact]
    public async Task LegacyV3RetainsAllOptionsAndFlattensSelectedOutput()
    {
        using var temp = new Scratch();
        var input = temp.Mod("source", """{"FileVersion":3,"Name":"Legacy","Author":"Creator"}""", "a.mdl", "b.mdl");
        File.WriteAllText(System.IO.Path.Combine(input, "default_mod.json"), """{"Files":{"chara/a.mdl":"a.mdl"}}""");
        File.WriteAllText(System.IO.Path.Combine(input, "group_001_fit.json"), """
            {"Name":"Fit","Type":"Single","Options":[{"Name":"A","Files":{"chara/a.mdl":"b.mdl"}}]}
            """);
        using var mod = PenumbraMod.Open(input);
        Assert.Equal(2, mod.GetModelFiles().Count);
        var all = await mod.CloneAndConvertAsync(temp.Path("output"), "All", ConvertMarker);
        Assert.True(File.Exists(System.IO.Path.Combine(all, "group_001_fit.json")));
        var selected = await mod.CloneAndConvertAsync(temp.Path("output"), "Selected", ConvertMarker,
            ModConversionScope.SelectedOptions);
        Assert.False(File.Exists(System.IO.Path.Combine(selected, "group_001_fit.json")));
        using var clone = PenumbraMod.Open(selected);
        Assert.Equal(3, clone.FileVersion);
        Assert.Equal("b.mdl", Assert.Single(clone.GetModelFiles()).RelativePath);
    }

    [Theory]
    [InlineData("../escape.mdl")]
    [InlineData("C:\\escape.mdl")]
    [InlineData("/tmp/escape.mdl")]
    [InlineData("folder/../../escape.mdl")]
    [InlineData("CON.mdl")]
    [InlineData("model.mdl:stream")]
    public void ArchiveRejectsUnsafePaths(string unsafePath)
    {
        using var temp = new Scratch();
        var archivePath = temp.Path("unsafe.pmp");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry(unsafePath).Open());
            writer.Write("untrusted");
        }
        Assert.Throws<InvalidDataException>(() => PenumbraMod.Open(archivePath));
        Assert.False(File.Exists(temp.Path("escape.mdl")));
    }

    [Fact]
    public void ArchiveExtractionHasAnOwnedLifetimeAndEnforcesLimits()
    {
        using var temp = new Scratch();
        var source = temp.Mod("source", """{"FileVersion":4,"Name":"Zip","DefaultData":{"Files":{"chara/a.mdl":"a.mdl"}}}""", "a.mdl");
        var archive = temp.Path("input.pmp");
        ZipFile.CreateFromDirectory(source, archive);
        string extraction;
        using (var mod = PenumbraMod.Open(archive))
        {
            extraction = mod.RootPath;
            Assert.Single(mod.GetModelFiles());
            Assert.True(Directory.Exists(extraction));
        }
        Assert.False(Directory.Exists(extraction));
        Assert.Throws<InvalidDataException>(() => PenumbraMod.Open(archive, new ModReadLimits(MaxTotalBytes: 4)));
    }

    [Fact]
    public void ArchiveRejectsDuplicateWindowsPathsAndSymlinkEntries()
    {
        using var temp = new Scratch();
        var duplicate = temp.Path("duplicate.pmp");
        using (var zip = ZipFile.Open(duplicate, ZipArchiveMode.Create))
        {
            zip.CreateEntry("file.mdl");
            zip.CreateEntry("FILE.mdl");
        }
        Assert.Throws<InvalidDataException>(() => PenumbraMod.Open(duplicate));
        var link = temp.Path("link.pmp");
        using (var zip = ZipFile.Open(link, ZipArchiveMode.Create))
            zip.CreateEntry("link.mdl").ExternalAttributes = unchecked((int)0xA1FF0000);
        Assert.Throws<InvalidDataException>(() => PenumbraMod.Open(link));
    }

    [Fact]
    public void InstalledModRejectsSymlinkedUnreferencedFiles()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating links requires privileges on some Windows runners.
        using var temp = new Scratch();
        var source = temp.Mod("source", """{"FileVersion":4,"Name":"Links"}""");
        File.WriteAllText(temp.Path("outside"), "private");
        File.CreateSymbolicLink(System.IO.Path.Combine(source, "unreferenced"), temp.Path("outside"));
        Assert.Throws<InvalidDataException>(() => PenumbraMod.Open(source));
    }

    [Fact]
    public async Task UserChosenDirectoryLinksAreResolvedBeforeContainmentChecks()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var temp = new Scratch();
        var source = temp.Mod("source", """{"FileVersion":4,"Name":"Alias","DefaultData":{"Files":{"chara/a.mdl":"a.mdl"}}}""", "a.mdl");
        Directory.CreateSymbolicLink(temp.Path("alias"), source);
        using var mod = PenumbraMod.Open(temp.Path("alias"));
        Assert.Equal(source, mod.RootPath);
        Assert.Single(mod.GetModelFiles());
        await Assert.ThrowsAsync<InvalidOperationException>(() => mod.CloneAndConvertAsync(temp.Path("alias/nested"), "Nested", ConvertMarker));
    }

    [Fact]
    public async Task FailureAndCancellationLeaveNoOutputAndNeverChangeInputs()
    {
        using var temp = new Scratch();
        var source = temp.Mod("source", """{"FileVersion":4,"Name":"Safe","DefaultData":{"Files":{"chara/a.mdl":"a.mdl"}}}""", "a.mdl");
        using var mod = PenumbraMod.Open(source);
        var output = temp.Path("output");
        await Assert.ThrowsAsync<InvalidOperationException>(() => mod.CloneAndConvertAsync(output, "Failure", (_, _, _) =>
            throw new InvalidOperationException("test conversion failure")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mod.CloneAndConvertAsync(output, "Cancelled", async (_, path, ct) =>
        {
            await File.WriteAllTextAsync(path, "incomplete", ct);
            cancellation.Cancel();
        }, cancellationToken: cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        Assert.Equal("original", File.ReadAllText(System.IO.Path.Combine(source, "a.mdl")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mod.CloneAndConvertAsync(source, "Nested", ConvertMarker));
    }

    [Fact]
    public void ModelSwapsFailRatherThanPretendingTheirGeometryWasConverted()
    {
        using var temp = new Scratch();
        var source = temp.Mod("source", """
            {"FileVersion":4,"Name":"Swap","DefaultData":{"FileSwaps":{"chara/a.mdl":"chara/b.mdl"}}}
            """);
        using var mod = PenumbraMod.Open(source);
        Assert.Throws<NotSupportedException>(() => mod.GetModelFiles());
    }

    [Fact]
    public void ActiveModelSwapSuppressesDefaultFileButSameContainerFileWins()
    {
        using var temp = new Scratch();
        var source = temp.Mod("source", """
            {"FileVersion":4,"Name":"Swap priority","DefaultData":{"Files":{"chara/a.mdl":"a.mdl"}},
             "Groups":[{"Type":"Single","Name":"Fit","Options":[{"Name":"Swap",
               "FileSwaps":{"chara/a.mdl":"chara/b.mdl"}}]}]}
            """, "a.mdl");
        using var mod = PenumbraMod.Open(source);
        Assert.Throws<NotSupportedException>(() => mod.GetModelFiles(ModConversionScope.SelectedOptions));
        var sameContainer = temp.Mod("same", """
            {"FileVersion":4,"Name":"Same","DefaultData":{"Files":{"chara/a.mdl":"a.mdl"},
             "FileSwaps":{"chara/a.mdl":"chara/b.mdl"}}}
            """, "a.mdl");
        using var same = PenumbraMod.Open(sameContainer);
        Assert.Single(same.GetModelFiles());
        Assert.Single(same.GetModelFiles(ModConversionScope.SelectedOptions));
    }

    [Fact]
    public async Task LegacyCloneUsesMetadataSnapshotFromInitialAnalysis()
    {
        using var temp = new Scratch();
        var source = temp.Mod("source", """{"FileVersion":3,"Name":"Snapshot"}""", "a.mdl", "b.mdl");
        var dataPath = System.IO.Path.Combine(source, "default_mod.json");
        File.WriteAllText(dataPath, """{"Files":{"chara/a.mdl":"a.mdl"}}""");
        using var mod = PenumbraMod.Open(source);
        File.WriteAllText(dataPath, """{"Files":{"chara/a.mdl":"b.mdl"}}""");
        var output = await mod.CloneAndConvertAsync(temp.Path("output"), "Snapshot clone", ConvertMarker);
        using var clone = PenumbraMod.Open(output);
        Assert.Equal("a.mdl", Assert.Single(clone.GetModelFiles()).RelativePath);
        Assert.Equal("converted", File.ReadAllText(System.IO.Path.Combine(output, "a.mdl")));
    }

    private static Task ConvertMarker(ModModelFile _, string output, CancellationToken token)
        => File.WriteAllTextAsync(output, "converted", token);

    [Fact]
    public async Task GeneratedDestinationOptionsReplaceSourceSizesAndPreserveOtherSettings()
    {
        using var temp = new Scratch();
        var source = CreateRegenerationSource(temp);
        using var mod = PenumbraMod.Open(source);
        var snapshot = mod.GetGroupSnapshots();
        var unaffected = snapshot[1].DeepClone();
        var replacement = BuildDestinationGroup(snapshot[0]);
        var plan = new ModRegenerationPlan([new("Source sizes", replacement)],
            [new("small-target", "small.mdl", "generated/small.mdl"), new("large-target", "small.mdl", "generated/large.mdl")]);
        var calls = new List<string>();
        var output = await mod.CloneWithGeneratedOptionsAsync(temp.Path("output"), "Destination outfit", plan, async (job, path, token) =>
        {
            calls.Add(job.Id);
            Assert.Equal("original", await File.ReadAllTextAsync(System.IO.Path.Combine(mod.RootPath, job.SourceRelativePath), token));
            Assert.False(File.Exists(path));
            await File.WriteAllTextAsync(path, job.Id, token);
        });
        Assert.Equal(2, calls.Count);
        using var clone = PenumbraMod.Open(output);
        Assert.Equal("Destination sizes", clone.Groups[0].Name);
        Assert.Equal(["None", "Target small", "Target large"], clone.Groups[0].Options.Select(option => option.Name));
        Assert.Equal(1UL, clone.Groups[0].DefaultSettings);
        Assert.Equal(2, clone.GetModelFiles().Count);
        Assert.True(JsonNode.DeepEquals(unaffected, clone.GetGroupSnapshots()[1]));
        var generated = clone.GetGroupSnapshots()[0]["Options"]!.AsArray()[1]!;
        Assert.True(JsonNode.DeepEquals(snapshot[0]["Options"]![1]!["Manipulations"], generated["Manipulations"]));
        Assert.Equal("skin.tex", generated["Files"]!["chara/skin.tex"]!.GetValue<string>());
        Assert.Equal("original", File.ReadAllText(System.IO.Path.Combine(output, "skin.tex")));
        Assert.Equal("original", File.ReadAllText(System.IO.Path.Combine(source, "small.mdl")));
        Assert.Equal("original", File.ReadAllText(System.IO.Path.Combine(source, "large.mdl")));
        Assert.Equal("Creator", JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(output, "meta.json")))!["Author"]!.GetValue<string>());
        // Editing snapshots has no effect on the source object's metadata or future plans.
        snapshot[0]["Name"] = "mutated caller copy";
        Assert.Equal("Source sizes", mod.GetGroupSnapshots()[0]["Name"]!.GetValue<string>());
    }

    [Fact]
    public async Task GeneratedMultiGroupUsesValidDestinationDefaultsAndRetainsNone()
    {
        using var temp = new Scratch();
        using var mod = PenumbraMod.Open(CreateRegenerationSource(temp));
        var group = BuildDestinationGroup(mod.GetGroupSnapshots()[0]);
        group["Type"] = "Multi";
        group["DefaultSettings"] = 6;
        var output = await mod.CloneWithGeneratedOptionsAsync(temp.Path("output"), "Multi destinations",
            new([new("Source sizes", group)], [new("a", "small.mdl", "generated/small.mdl"), new("b", "small.mdl", "generated/large.mdl")]),
            GenerateMarker);
        using var clone = PenumbraMod.Open(output);
        Assert.Equal("Multi", clone.Groups[0].Type);
        Assert.Equal(6UL, clone.Groups[0].DefaultSettings);
        Assert.Equal(3, clone.Groups[0].Options.Count);
    }

    [Fact]
    public async Task DefaultModelsCanBecomeIndependentDestinationGroupsWithoutLosingMetadata()
    {
        using var temp = new Scratch();
        var source = temp.Mod("source", """
            {"FileVersion":4,"Name":"Default outfit","DefaultData":{"Files":{
             "chara/top.mdl":"top.mdl","chara/legs.mdl":"legs.mdl","chara/skin.tex":"skin.tex"},
             "Manipulations":[{"Type":"Eqp","Manipulation":{"SetId":10,"Slot":"Body","Entry":100}}]}}
            """, "top.mdl", "legs.mdl", "skin.tex");
        using var mod = PenumbraMod.Open(source);
        var data = mod.GetDefaultDataSnapshot();
        data["Files"]!.AsObject().Remove("chara/top.mdl");
        data["Files"]!.AsObject().Remove("chara/legs.mdl");
        var top = JsonNode.Parse("""{"Type":"Single","Name":"Target chest","Options":[{"Name":"Small","Files":{"chara/top.mdl":"generated/top.mdl"}}]}""")!.AsObject();
        var legs = JsonNode.Parse("""{"Type":"Single","Name":"Target legs","Options":[{"Name":"Medium","Files":{"chara/legs.mdl":"generated/legs.mdl"}}]}""")!.AsObject();
        var output = await mod.CloneWithGeneratedOptionsAsync(temp.Path("output"), "Split regions",
            new([new(null, top), new(null, legs)], [new("top", "top.mdl", "generated/top.mdl"), new("legs", "legs.mdl", "generated/legs.mdl")], data), GenerateMarker);
        using var clone = PenumbraMod.Open(output);
        Assert.Equal(2, clone.Groups.Count);
        Assert.Equal(2, clone.GetModelFiles().Count);
        Assert.Equal("skin.tex", clone.GetDefaultDataSnapshot()["Files"]!["chara/skin.tex"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(mod.GetDefaultDataSnapshot()["Manipulations"], clone.GetDefaultDataSnapshot()["Manipulations"]));
        Assert.Equal(3, mod.GetDefaultDataSnapshot()["Files"]!.AsObject().Count);
    }

    [Fact]
    public async Task LegacyRegenerationKeepsUnchangedGroupDocumentBytesAndGroupOrder()
    {
        using var temp = new Scratch();
        var source = CreateRegenerationSource(temp);
        var metadataPath = System.IO.Path.Combine(source, "meta.json");
        var metadata = JsonNode.Parse(File.ReadAllText(metadataPath))!.AsObject();
        var sourceGroups = metadata["Groups"]!.AsArray();
        File.WriteAllText(System.IO.Path.Combine(source, "group_005_sizes.json"), sourceGroups[0]!.ToJsonString());
        var unchanged = " \n" + sourceGroups[1]!.ToJsonString() + "\n\n";
        File.WriteAllText(System.IO.Path.Combine(source, "group_009_materials.json"), unchanged);
        metadata.Remove("Groups");
        metadata["FileVersion"] = 3;
        File.WriteAllText(metadataPath, metadata.ToJsonString());
        using var mod = PenumbraMod.Open(source);
        var output = await mod.CloneWithGeneratedOptionsAsync(temp.Path("output"), "Legacy destination",
            new([new("Source sizes", BuildDestinationGroup(mod.GetGroupSnapshots()[0]))],
                [new("a", "small.mdl", "generated/small.mdl"), new("b", "small.mdl", "generated/large.mdl")]), GenerateMarker);
        using var clone = PenumbraMod.Open(output);
        Assert.Equal(3, clone.FileVersion);
        Assert.Equal("Destination sizes", clone.Groups[0].Name);
        Assert.Equal("Dyes", clone.Groups[1].Name);
        Assert.Equal(unchanged, File.ReadAllText(System.IO.Path.Combine(output, "group_002.json")));
        Assert.False(File.Exists(System.IO.Path.Combine(output, "group_005_sizes.json")));
    }

    [Theory]
    [InlineData("overwrites-input")]
    [InlineData("traversal")]
    [InlineData("duplicate-output")]
    [InlineData("duplicate-id")]
    [InlineData("unreferenced-job")]
    [InlineData("unconverted-option")]
    [InlineData("missing-none")]
    [InlineData("none-default")]
    [InlineData("bad-default")]
    [InlineData("missing-source")]
    [InlineData("changed-default-texture")]
    public async Task InvalidRegenerationPlansFailBeforeCreatingOutput(string invalid)
    {
        using var temp = new Scratch();
        using var mod = PenumbraMod.Open(CreateRegenerationSource(temp));
        var group = BuildDestinationGroup(mod.GetGroupSnapshots()[0]);
        var models = new List<ModGeneratedModel> { new("a", "small.mdl", "generated/small.mdl"), new("b", "small.mdl", "generated/large.mdl") };
        JsonObject? data = null;
        switch (invalid)
        {
            case "overwrites-input": models[0] = models[0] with { OutputRelativePath = "small.mdl" }; break;
            case "traversal": models[0] = models[0] with { OutputRelativePath = "../outside.mdl" }; break;
            case "duplicate-output": models[1] = models[1] with { OutputRelativePath = models[0].OutputRelativePath }; break;
            case "duplicate-id": models[1] = models[1] with { Id = models[0].Id }; break;
            case "unreferenced-job": models.Add(new("unused", "small.mdl", "generated/unused.mdl")); break;
            case "unconverted-option": group["Options"]![2]!["Files"]!["chara/outfit.mdl"] = "large.mdl"; break;
            case "missing-none": group["Options"]!.AsArray().RemoveAt(0); group["DefaultSettings"] = 0; break;
            case "none-default": group["DefaultSettings"] = 0; break;
            case "bad-default": group["DefaultSettings"] = 99; break;
            case "missing-source": models[0] = models[0] with { SourceRelativePath = "skin.tex" }; break;
            case "changed-default-texture": data = new() { ["Files"] = new JsonObject { ["chara/skin.tex"] = "skin.tex" } }; break;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => mod.CloneWithGeneratedOptionsAsync(temp.Path("output"), "Invalid",
            new([new("Source sizes", group)], models, data), GenerateMarker));
        Assert.False(Directory.Exists(temp.Path("output")));
    }

    [Fact]
    public async Task GenerationFailureAndCancellationLeaveNoPublishedMod()
    {
        using var temp = new Scratch();
        var source = CreateRegenerationSource(temp);
        using var mod = PenumbraMod.Open(source);
        var plan = new ModRegenerationPlan([new("Source sizes", BuildDestinationGroup(mod.GetGroupSnapshots()[0]))],
            [new("a", "small.mdl", "generated/small.mdl"), new("b", "small.mdl", "generated/large.mdl")]);
        var output = temp.Path("output");
        await Assert.ThrowsAsync<InvalidOperationException>(() => mod.CloneWithGeneratedOptionsAsync(output, "Failure", plan, async (job, path, ct) =>
        {
            if (job.Id == "b") throw new InvalidOperationException("failed second variant");
            await GenerateMarker(job, path, ct);
        }));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mod.CloneWithGeneratedOptionsAsync(output, "Cancel", plan, async (job, path, ct) =>
        {
            await GenerateMarker(job, path, ct);
            cancellation.Cancel();
        }, cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        Assert.Equal("original", File.ReadAllText(System.IO.Path.Combine(source, "small.mdl")));
    }

    [Fact]
    public async Task RegenerationRejectsRemovingAnOptionReferencedByAnotherGroup()
    {
        using var temp = new Scratch();
        var source = CreateRegenerationSource(temp);
        var metadataPath = System.IO.Path.Combine(source, "meta.json");
        var metadata = JsonNode.Parse(File.ReadAllText(metadataPath))!;
        metadata["Groups"]![1]!["Condition"] = new JsonObject { ["Type"] = "Setting", ["Id"] = "00000000-0000-0000-0000-000000000003" };
        File.WriteAllText(metadataPath, metadata.ToJsonString());
        using var mod = PenumbraMod.Open(source);
        await Assert.ThrowsAsync<NotSupportedException>(() => mod.CloneWithGeneratedOptionsAsync(temp.Path("output"), "Dangling",
            new([new("Source sizes", BuildDestinationGroup(mod.GetGroupSnapshots()[0]))],
                [new("a", "small.mdl", "generated/small.mdl"), new("b", "small.mdl", "generated/large.mdl")]), GenerateMarker));
        Assert.False(Directory.Exists(temp.Path("output")));
    }

    private static string CreateRegenerationSource(Scratch temp) => temp.Mod("source", """
        {"FileVersion":4,"Name":"Size outfit","Author":"Creator","Groups":[
         {"Type":"Single","Name":"Source sizes","Id":"00000000-0000-0000-0000-000000000001","DefaultSettings":1,"Options":[
          {"Name":"None","Id":"00000000-0000-0000-0000-000000000002"},
          {"Name":"Source small","Id":"00000000-0000-0000-0000-000000000003","Files":{"chara/outfit.mdl":"small.mdl","chara/skin.tex":"skin.tex"},
           "Manipulations":[{"Type":"Shp","Manipulation":{"Shape":"shpx_small","Entry":true}}]},
          {"Name":"Source large","Id":"00000000-0000-0000-0000-000000000004","Files":{"chara/outfit.mdl":"large.mdl"}}]},
         {"Type":"Multi","Name":"Dyes","DefaultSettings":1,"Options":[
          {"Name":"Gold","Files":{"chara/gold.tex":"skin.tex"}},
          {"Name":"Silver","Files":{"chara/silver.tex":"skin.tex"}}]}]}
        """, "small.mdl", "large.mdl", "skin.tex");

    private static JsonObject BuildDestinationGroup(JsonObject source)
    {
        var group = (JsonObject)source.DeepClone();
        group["Name"] = "Destination sizes";
        var template = source["Options"]![1]!.AsObject();
        var options = new JsonArray(source["Options"]![0]!.DeepClone());
        foreach (var (name, path) in new[] { ("Target small", "generated/small.mdl"), ("Target large", "generated/large.mdl") })
        {
            var option = (JsonObject)template.DeepClone();
            option["Id"] = Guid.NewGuid().ToString();
            option["Name"] = name;
            option["Files"]!["chara/outfit.mdl"] = path;
            options.Add(option);
        }
        group["Options"] = options;
        group["DefaultSettings"] = 1;
        return group;
    }

    private static Task GenerateMarker(ModGeneratedModel job, string output, CancellationToken token)
        => File.WriteAllTextAsync(output, "generated " + job.Id, token);

    private sealed class Scratch : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OutfitStudio-test-" + Guid.NewGuid().ToString("N"));
        public Scratch() => Directory.CreateDirectory(root);
        public string Path(string relative) => System.IO.Path.Combine(root, relative);
        public string Mod(string relative, string metadata, params string[] files)
        {
            var directory = Path(relative);
            Directory.CreateDirectory(directory);
            File.WriteAllText(System.IO.Path.Combine(directory, "meta.json"), metadata);
            foreach (var file in files)
            {
                var path = System.IO.Path.Combine(directory, file);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "original");
            }
            return directory;
        }
        public void Dispose() => Directory.Delete(root, true);
    }
}
