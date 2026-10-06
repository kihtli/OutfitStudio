using System.Text.Json.Nodes;
using OutfitStudio.Core.Mods;
using OutfitStudio.Core.Planning;

namespace OutfitStudio.Tests;

public sealed class AutomaticPlannerTests
{
    [Fact]
    public void MainAndExtraUnionStylesAndKeepModelOwnersDespiteIdenticalPaths()
    {
        using var f = new Fixture();
        using var source = f.Mod("source", "Body", RegionGroup("Chest", "top", "Small"), RegionGroup("Legs", "dwn", "Medium"));
        using var main = f.Mod("main", "Body (MAIN - Belly)", RegionGroup("Chest", "top", "Small", "Large"), RegionGroup("Legs", "dwn", "Medium"));
        using var extra = f.Mod("extra", "Body (EXTRA - Belly)", RegionGroup("Chest", "top", "Small", "Pushup"));
        using var outfit = f.Mod("outfit", "Outfit", RegionGroup("Chest size", "top", "Small"), RegionGroup("Legs size", "dwn", "Medium"));
        var plan = AutoConversionPlanner.Plan(source, new[] { main, extra }, outfit);
        Assert.True(plan.CanConvert, string.Join(";", plan.Issues));
        var tops = plan.Groups.Single(g => g.Region == "top");
        Assert.Equal(4, tops.Options.Count);
        Assert.Equal(4, tops.Options.Select(o => o.Name).Distinct().Count());
        Assert.Equal(0, tops.DestinationDefaultOptionIndex);
        Assert.Equal(new[] { 0, 0, 1, 1 }, tops.Options.Select(o => o.Models.Single().TargetModIndex));
        Assert.Equal(tops.Options[0].Models[0].TargetModelPath, tops.Options[2].Models[0].TargetModelPath);
        Assert.All(tops.Options, option => Assert.Equal(0, Assert.Single(option.Models[0].AdditionalBodyReferences).TargetModIndex));
        var legs = Assert.Single(plan.Groups.Single(g => g.Region == "dwn").Options);
        Assert.Equal(new[] { 0, 0, 1, 1 }, legs.Models.Select(m => Assert.Single(m.AdditionalBodyReferences).TargetModIndex));
        Assert.Equal(new[] { 0, 1, 2, 3 }, legs.Models.Select(m => Assert.Single(m.RequiredOptions).OptionIndex));
    }

    [Fact]
    public void ExplicitNeighboringStyleSelectionsOverridePackageDefaults()
    {
        using var f = new Fixture();
        using var source = f.Mod("source", "Source", RegionGroup("Chest", "top", "Small"), RegionGroup("Legs", "dwn", "Medium"));
        using var first = f.Mod("first", "First body", RegionGroup("Chest", "top", "Large"), RegionGroup("Legs", "dwn", "Large"));
        using var second = f.Mod("second", "Second body", RegionGroup("Chest", "top", "Large"), RegionGroup("Legs", "dwn", "Large"));
        using var outfit = f.Mod("outfit", "Outfit", RegionGroup("Chest size", "top", "Small"), RegionGroup("Legs size", "dwn", "Medium"));
        var plan = AutoConversionPlanner.Plan(source, new[] { first, second }, outfit);
        Assert.True(plan.CanConvert, string.Join(";", plan.Issues));
        foreach (var group in plan.Groups)
            foreach (var option in group.Options)
            {
                Assert.Equal(new[] { 0, 1 }, option.Models.Select(m => Assert.Single(m.AdditionalBodyReferences).TargetModIndex));
                foreach (var model in option.Models)
                {
                    var required = Assert.Single(model.RequiredOptions);
                    Assert.NotEqual(group.Region, plan.Groups[required.GroupIndex].Region);
                    var neighbor = plan.Groups[required.GroupIndex].Options[required.OptionIndex].Models[0];
                    Assert.Equal(neighbor.TargetModIndex, model.AdditionalBodyReferences[0].TargetModIndex);
                    Assert.Equal(neighbor.TargetModelPath, model.AdditionalBodyReferences[0].TargetModelPath);
                }
            }
    }

    [Fact]
    public void NeighboringSizesCoordinateBothRegionsAndRetainDisabledOptionFallbacks()
    {
        using var f = new Fixture();
        using var source = f.Mod("source", "Source", RegionGroup("Chest", "top", "Small"), RegionGroup("Legs", "dwn", "Medium"));
        using var target = f.Mod("target", "Destination", RegionGroup("Chest", "top", "Small", "Large"), RegionGroup("Legs", "dwn", "Medium", "Large"));
        var material = new JsonObject { ["Name"] = "Material", ["Type"] = "Single", ["Options"] = new JsonArray(new JsonObject { ["Name"] = "Silk" }) };
        using var outfit = f.Mod("outfit", "Outfit", material, RegionGroup("Chest size", "top", "None", "Small"),
            RegionGroup("Legs size", "dwn", "None", "Medium"));
        var plan = AutoConversionPlanner.Plan(source, target, outfit);
        Assert.True(plan.CanConvert, string.Join(";", plan.Issues));
        Assert.Equal(12, plan.Groups.SelectMany(g => g.Options).Sum(o => o.Models.Count));
        for (int groupIndex = 0; groupIndex < 2; groupIndex++)
            foreach (var option in plan.Groups[groupIndex].Options)
            {
                Assert.Equal(3, option.Models.Count);
                var enabled = option.Models.Where(m => !Assert.Single(m.RequiredOptions).IsOriginalOption).ToArray();
                Assert.Equal(new[] { 0, 1 }, enabled.Select(m => m.RequiredOptions[0].OptionIndex));
                foreach (var model in enabled)
                {
                    var required = Assert.Single(model.RequiredOptions);
                    Assert.Equal(1 - groupIndex, required.GroupIndex);
                    var neighbor = plan.Groups[required.GroupIndex].Options[required.OptionIndex].Models[0];
                    Assert.Equal(neighbor.SourceModelPath, model.AdditionalBodyReferences[0].SourceModelPath);
                    Assert.Equal(neighbor.TargetModelPath, model.AdditionalBodyReferences[0].TargetModelPath);
                }
                var disabled = Assert.Single(option.Models, m => m.RequiredOptions[0].IsOriginalOption);
                Assert.Equal(0, disabled.RequiredOptions[0].OptionIndex);
                Assert.Equal(enabled[0].AdditionalBodyReferences, disabled.AdditionalBodyReferences);
            }
        Assert.Contains(plan.Warnings, warning => warning.Contains("12 coordinated model variants"));
    }

    [Fact]
    public void MultipleGamePathsAndDefaultModelsHaveStableSelectorCoordinates()
    {
        using var f = new Fixture();
        using var source = f.Mod("source", "Source", RegionGroup("Chest", "top", "Small"), RegionGroup("Legs", "dwn", "Medium"));
        using var target = f.Mod("target", "Destination", RegionGroup("Chest", "top", "Small", "Large"), RegionGroup("Legs", "dwn", "Medium", "Large"));
        using var outfit = f.DefaultMod("outfit", new JsonObject
        {
            ["chara/equipment/e0000/model/c0201e0000_top.mdl"] = "top.mdl",
            ["chara/equipment/e0001/model/c0201e0001_top.mdl"] = "other-top.mdl",
            ["chara/equipment/e0000/model/c0201e0000_dwn.mdl"] = "legs.mdl",
        });
        var plan = AutoConversionPlanner.Plan(source, target, outfit);
        Assert.True(plan.CanConvert, string.Join(";", plan.Issues));
        Assert.All(plan.Groups, group => Assert.Equal(-1, group.SourceGroupIndex));
        var tops = plan.Groups.Single(g => g.Region == "top");
        Assert.All(tops.Options, option =>
        {
            Assert.Equal(4, option.Models.Count);
            Assert.Equal(2, option.Models.Select(m => m.OutfitModelPath).Distinct().Count());
        });
        foreach (var model in plan.Groups.SelectMany(g => g.Options).SelectMany(o => o.Models))
        {
            var required = Assert.Single(model.RequiredOptions);
            Assert.False(required.IsOriginalOption);
            var neighbor = plan.Groups[required.GroupIndex].Options[required.OptionIndex].Models[0];
            Assert.Equal(neighbor.TargetModelPath, Assert.Single(model.AdditionalBodyReferences).TargetModelPath);
        }
    }

    [Fact]
    public void MissingNeighborSelectorKeepsCompanionDefaultWithoutCreatingInventedChoices()
    {
        using var f = new Fixture();
        using var source = f.Mod("source", "Source", RegionGroup("Chest", "top", "Small"), RegionGroup("Legs", "dwn", "Medium"));
        using var target = f.Mod("target", "Destination", RegionGroup("Chest", "top", "Small", "Large"), RegionGroup("Legs", "dwn", "Medium", "Large"));
        using var outfit = f.Mod("outfit", "Outfit", RegionGroup("Chest size", "top", "Small"));
        var plan = AutoConversionPlanner.Plan(source, target, outfit);
        Assert.True(plan.CanConvert, string.Join(";", plan.Issues));
        foreach (var option in Assert.Single(plan.Groups).Options)
        {
            var model = Assert.Single(option.Models);
            Assert.Empty(model.RequiredOptions);
            Assert.Equal("dwn-model-0.mdl", Assert.Single(model.AdditionalBodyReferences).TargetModelPath);
        }
    }

    [Fact]
    public void DisabledNeighborDoesNotRemoveGarmentWhenItsFamilyHasNoFallbackRegion()
    {
        using var f = new Fixture();
        using var source = f.Mod("source", "Source", RegionGroup("Chest", "top", "Small"), RegionGroup("Legs", "dwn", "Medium"));
        using var main = f.Mod("main", "Body (MAIN - Belly)", RegionGroup("Chest", "top", "Small"));
        using var extra = f.Mod("extra", "Body (EXTRA - Belly)", RegionGroup("Chest", "top", "Pushup"));
        using var legs = f.Mod("legs", "Leg body", RegionGroup("Legs", "dwn", "Medium"));
        using var outfit = f.Mod("outfit", "Outfit", RegionGroup("Chest size", "top", "Small"), RegionGroup("Legs size", "dwn", "None", "Medium"));
        var plan = AutoConversionPlanner.Plan(source, new[] { main, extra, legs }, outfit);
        Assert.True(plan.CanConvert, string.Join(";", plan.Issues));
        var extraOption = plan.Groups.Single(g => g.Region == "top").Options.Single(o => o.Models[0].TargetModIndex == 1);
        Assert.Equal(2, extraOption.Models.Count);
        var enabled = extraOption.Models.Single(m => !Assert.Single(m.RequiredOptions).IsOriginalOption);
        Assert.Equal(2, Assert.Single(enabled.AdditionalBodyReferences).TargetModIndex);
        var disabled = extraOption.Models.Single(m => Assert.Single(m.RequiredOptions).IsOriginalOption);
        Assert.Empty(disabled.AdditionalBodyReferences);
    }

    [Fact]
    public void ExcessiveSizeProductIsReportedBeforeAllocatingAllVariants()
    {
        using var f = new Fixture();
        using var source = f.Mod("source", "Source", RegionGroup("Chest", "top", "Small"), RegionGroup("Legs", "dwn", "Medium"));
        var sizes = Enumerable.Range(0, 65).Select(i => $"Size {i}").ToArray();
        using var target = f.Mod("target", "Destination", RegionGroup("Chest", "top", sizes), RegionGroup("Legs", "dwn", sizes));
        using var outfit = f.Mod("outfit", "Outfit", RegionGroup("Chest size", "top", "Small"), RegionGroup("Legs size", "dwn", "Medium"));
        var plan = AutoConversionPlanner.Plan(source, target, outfit);
        Assert.False(plan.CanConvert);
        Assert.Contains(plan.Issues, issue => issue.Contains("8,450 coordinated model variants"));
        Assert.True(plan.Groups.SelectMany(g => g.Options).Sum(o => o.Models.Count) < 8450);
    }

    [Fact]
    public void ExtraBellyFindsMatchingMainEvenAlongsidePlainMainAndInReverseOrder()
    {
        using var f = new Fixture();
        using var source = f.Mod("source", "Source", RegionGroup("Chest", "top", "Small"), RegionGroup("Legs", "dwn", "Medium"));
        using var plain = f.Mod("plain", "[HS] Neolithe (MAIN - Default)", RegionGroup("Chest", "top", "Small"), RegionGroup("Legs", "dwn", "Medium"));
        using var extra = f.Mod("extra", "[HS] Neolithe (EXTRA - Neobelly)", RegionGroup("Chest", "top", "Almond"));
        using var belly = f.Mod("belly", "[HS] Neolithe (MAIN - Neobelly)", RegionGroup("Chest", "top", "Small"), RegionGroup("Legs", "dwn", "Medium"));
        using var outfit = f.Mod("outfit", "Outfit", RegionGroup("Chest size", "top", "Small"));
        var plan = AutoConversionPlanner.Plan(source, new[] { plain, extra, belly }, outfit);
        Assert.True(plan.CanConvert, string.Join(";", plan.Issues));
        var addon = plan.Groups.Single().Options.SelectMany(o => o.Models).Single(m => m.TargetModIndex == 1);
        Assert.Equal(2, Assert.Single(addon.AdditionalBodyReferences).TargetModIndex);
        Assert.Equal(0, plan.Groups.Single().DestinationDefaultOptionIndex);
    }

    [Fact]
    public void ExtraDoesNotBorrowDifferentStyleMainOrInventMissingCompanion()
    {
        using var f = new Fixture();
        using var source = f.Mod("source", "Source", RegionGroup("Chest", "top", "Small"));
        using var plain = f.Mod("plain", "Body (MAIN - Default)", RegionGroup("Chest", "top", "Small"));
        using var extra = f.Mod("extra", "Body (EXTRA - Belly)", RegionGroup("Chest", "top", "Pushup"));
        using var outfit = f.Mod("outfit", "Outfit", RegionGroup("Chest size", "top", "Small"));
        foreach (var targets in new[] { new[] { extra }, new[] { plain, extra } })
        {
            var plan = AutoConversionPlanner.Plan(source, targets, outfit);
            Assert.False(plan.CanConvert);
            Assert.Contains(plan.Issues, issue => issue.Contains("matching MAIN"));
        }
    }

    [Fact]
    public void DuplicateMainCompanionsAreAmbiguousEvenWhenNamesMatch()
    {
        using var f = new Fixture();
        using var source = f.Mod("source", "Source", RegionGroup("Chest", "top", "Small"));
        using var first = f.Mod("main1", "Body (MAIN - Belly)", RegionGroup("Chest", "top", "Small"));
        using var second = f.Mod("main2", "Body (MAIN - Belly)", RegionGroup("Chest", "top", "Small"));
        using var extra = f.Mod("extra", "Body (EXTRA - Belly)", RegionGroup("Chest", "top", "Pushup"));
        using var outfit = f.Mod("outfit", "Outfit", RegionGroup("Chest size", "top", "Small"));
        var plan = AutoConversionPlanner.Plan(source, new[] { first, extra, second }, outfit);
        Assert.False(plan.CanConvert);
        Assert.Contains(plan.Issues, issue => issue.Contains("several matching MAIN"));
        Assert.Equal(3, plan.Groups.Single().Options.Select(o => o.Name).Distinct().Count());
    }

    [Fact]
    public void PartialDestinationDoesNotGuessBetweenUnrelatedNeighborBodies()
    {
        using var f = new Fixture();
        using var source = f.Mod("source", "Source", RegionGroup("Chest", "top", "Small"), RegionGroup("Legs", "dwn", "Medium"));
        using var top = f.Mod("top", "Partial chest", RegionGroup("Chest", "top", "Large"));
        using var legs1 = f.Mod("legs1", "Legs one", RegionGroup("Legs", "dwn", "Medium"));
        using var legs2 = f.Mod("legs2", "Legs two", RegionGroup("Legs", "dwn", "Large"));
        using var outfit = f.Mod("outfit", "Outfit", RegionGroup("Chest size", "top", "Small"));
        var plan = AutoConversionPlanner.Plan(source, new[] { top, legs1, legs2 }, outfit);
        Assert.False(plan.CanConvert);
        Assert.Contains(plan.Issues, issue => issue.Contains("several selected bodies"));
        var paired = AutoConversionPlanner.Plan(source, new[] { top, legs1 }, outfit);
        Assert.True(paired.CanConvert, string.Join(";", paired.Issues));
        Assert.Equal(1, Assert.Single(paired.Groups.Single().Options[0].Models[0].AdditionalBodyReferences).TargetModIndex);
    }

    private static JsonObject RegionGroup(string name, string slot, params string[] options)
    {
        var group = Group(name, slot, 0, options);
        foreach (var option in group["Options"]!.AsArray().OfType<JsonObject>())
            foreach (var key in option["Files"]!.AsObject().Select(p => p.Key).ToArray())
                option["Files"]![key] = slot + "-" + option["Files"]![key]!.GetValue<string>();
        return group;
    }

    [Fact]
    public void YabAliasesResolveSmallButtAndGenDefaultWithoutReadingGeometry()
    {
        using var fixture = new Fixture();
        using var source = fixture.Mod("source", "YAB+", Group("Legs - Smallclothes", "dwn", 4,
            "None", "Watermelon Crushers - A", "Watermelon Crushers - B", "Small - Watermelon Crushers - A", "Skull Crushers - A"));
        using var target = fixture.Mod("target", "Destination", Group("Leg sizes", "dwn", 1, "Small", "Medium", "Large"));
        using var outfit = fixture.Mod("outfit", "Garment", Group("Buttocks size", "dwn", 1, "W.C", "W.C - Small Butt"));
        var plan = AutoConversionPlanner.Plan(source, target, outfit);
        Assert.True(plan.CanConvert, string.Join(";", plan.Issues));
        var group = Assert.Single(plan.Groups);
        Assert.Equal("W.C - Small Butt", group.TemplateOptionName);
        Assert.Equal(new[] { "Small", "Medium", "Large" }, group.Options.Select(o => o.Name));
        Assert.All(group.Options, o => Assert.Equal("model-3.mdl", Assert.Single(o.Models).SourceModelPath));
        Assert.Equal(1, group.DestinationDefaultOptionIndex);
    }

    [Fact]
    public void DisabledSourceDefaultDoesNotInventGenitalVariant()
    {
        using var fixture = new Fixture();
        using var source = fixture.Mod("source", "YAB+", Group("Legs", "dwn", 0, "None", "Watermelon Crushers - A", "Watermelon Crushers - B"));
        using var target = fixture.Mod("target", "Destination", Group("Legs", "dwn", 0, "Small", "Large"));
        using var outfit = fixture.Mod("outfit", "Outfit", Group("Buttocks size", "dwn", 0, "W.C"));
        var plan = AutoConversionPlanner.Plan(source, target, outfit);
        Assert.False(plan.CanConvert);
        Assert.Contains(plan.Issues, issue => issue.Contains("matches several source body references"));
    }

    [Fact]
    public void BuffOrderingMatchesAndMediumTemplateIsPreferredWhenDefaultIsNone()
    {
        using var fixture = new Fixture();
        using var source = fixture.Mod("source", "YAB+", Group("Chest", "top", 2, "Small", "Medium", "Buff - Medium"));
        using var target = fixture.Mod("target", "Destination", Group("Chest", "top", 0, "Puffy", "EXQB"));
        using var outfit = fixture.Mod("outfit", "Outfit", Group("Chest size", "top", 0, "None", "Small", "Medium", "Medium - Buff"));
        var plan = AutoConversionPlanner.Plan(source, target, outfit);
        Assert.True(plan.CanConvert, string.Join(";", plan.Issues));
        Assert.Equal("Medium", Assert.Single(plan.Groups).TemplateOptionName);
    }

    [Fact]
    public void UnknownStyleModelOptionsBlockInsteadOfBeingDiscarded()
    {
        using var fixture = new Fixture();
        using var source = fixture.Mod("source", "Body", Group("Chest", "top", 0, "Default"));
        using var target = fixture.Mod("target", "Destination", Group("Chest", "top", 0, "Small", "Large"));
        using var outfit = fixture.Mod("outfit", "Outfit", Group("Color", "top", 0, "Red", "Blue"));
        var plan = AutoConversionPlanner.Plan(source, target, outfit);
        Assert.False(plan.CanConvert);
        Assert.Contains(plan.Issues, issue => issue.Contains("'Color' / 'Red'"));
    }

    [Fact]
    public void CompetingBodyGroupsAreReportedInsteadOfChoosingFirst()
    {
        using var fixture = new Fixture();
        var a = Group("Chest one", "top", 0, "Small"); var b = Group("Chest two", "top", 0, "Large");
        using var source = fixture.Mod("source", "Body", a, b);
        using var target = fixture.Mod("target", "Destination", Group("Chest", "top", 0, "Small"));
        using var outfit = fixture.Mod("outfit", "Outfit", Group("Chest size", "top", 0, "Small"));
        var plan = AutoConversionPlanner.Plan(source, target, outfit);
        Assert.False(plan.CanConvert);
        Assert.Contains(plan.Issues, issue => issue.Contains("competing Chest option groups"));
    }

    [Fact]
    public void NonModelGroupSnapshotsAreNeverMutatedByPlanning()
    {
        using var fixture = new Fixture();
        using var source = fixture.Mod("source", "Body", Group("Chest", "top", 0, "Small"));
        using var target = fixture.Mod("target", "Destination", Group("Chest", "top", 0, "Large"));
        var colors = new JsonObject { ["Name"] = "Material", ["Type"] = "Single", ["Options"] = new JsonArray(new JsonObject { ["Name"] = "Silk" }) };
        using var outfit = fixture.Mod("outfit", "Outfit", Group("Chest size", "top", 0, "Small"), colors);
        var before = outfit.GetGroupSnapshots().Select(g => g.ToJsonString()).ToArray();
        Assert.True(AutoConversionPlanner.Plan(source, target, outfit).CanConvert);
        Assert.Equal(before, outfit.GetGroupSnapshots().Select(g => g.ToJsonString()));
    }

    [Fact]
    public void PlainSizeUsesPlainReferenceEvenWhenYiggleIsThePackageDefault()
    {
        using var fixture = new Fixture();
        using var source = fixture.Mod("source", "YAB+", Group("Chest", "top", 1, "Small", "Yiggle - Small"));
        using var target = fixture.Mod("target", "Destination", Group("Chest", "top", 0, "Large"));
        using var outfit = fixture.Mod("outfit", "Outfit", Group("Chest size", "top", 0, "Small"));
        var plan = AutoConversionPlanner.Plan(source, target, outfit);
        Assert.True(plan.CanConvert, string.Join(";", plan.Issues));
        Assert.Equal("model-0.mdl", Assert.Single(Assert.Single(Assert.Single(plan.Groups).Options).Models).SourceModelPath);
    }

    [Fact]
    public void NestedModelMappingsBlockRatherThanBeingOmitted()
    {
        using var fixture = new Fixture();
        using var source = fixture.Mod("source", "Body", Group("Chest", "top", 0, "Small"));
        using var target = fixture.Mod("target", "Destination", Group("Chest", "top", 0, "Large"));
        var group = Group("Chest size", "top", 0, "Small");
        group["Options"]![0]!["Conditional"] = new JsonObject { ["Files"] = new JsonObject { ["chara/equipment/e0000/model/c0201e0000_top.mdl"] = "model-0.mdl" } };
        using var outfit = fixture.Mod("outfit", "Outfit", group);
        var plan = AutoConversionPlanner.Plan(source, target, outfit);
        Assert.False(plan.CanConvert);
        Assert.Contains(plan.Issues, issue => issue.Contains("conditional or nested"));
    }

    [Fact]
    public void PreferredTemplateUsesValidatedAlternativeAndPreservesCandidateOrder()
    {
        using var fixture = new Fixture();
        using var source = fixture.Mod("source", "Body", Group("Chest", "top", 1, "Small", "Medium", "Large"));
        using var target = fixture.Mod("target", "Destination", Group("Chest", "top", 0, "Puffy"));
        using var outfit = fixture.Mod("outfit", "Outfit", Group("Chest size", "top", 1, "Small", "Medium", "Large"));
        var original = AutoConversionPlanner.Plan(source, target, outfit);
        var replanned = AutoConversionPlanner.Plan(source, target, outfit, new Dictionary<int, int> { [0] = 2 });
        Assert.True(replanned.CanConvert, string.Join(";", replanned.Issues));
        var group = Assert.Single(replanned.Groups);
        Assert.Equal("Large", group.TemplateOptionName);
        Assert.Equal(Assert.Single(original.Groups).ValidTemplateOptionIndices, group.ValidTemplateOptionIndices);
        Assert.Equal("model-2.mdl", Assert.Single(Assert.Single(group.Options).Models).OutfitModelPath);
        Assert.False(AutoConversionPlanner.Plan(source, target, outfit, new Dictionary<int, int> { [0] = 99 }).CanConvert);
    }

    [Fact]
    public void SourceStylePayloadDifferencesAreNotDiscarded()
    {
        using var fixture = new Fixture();
        using var source = fixture.Mod("source", "Body", Group("Chest", "top", 0, "Small", "Medium"));
        using var target = fixture.Mod("target", "Destination", Group("Chest", "top", 0, "Large"));
        var sizes = Group("Chest size", "top", 0, "Small", "Medium");
        sizes["Options"]![0]!["Files"]!["chara/diffuse.tex"] = "red.tex";
        sizes["Options"]![1]!["Files"]!["chara/diffuse.tex"] = "blue.tex";
        using var outfit = fixture.Mod("outfit", "Outfit", sizes);
        var plan = AutoConversionPlanner.Plan(source, target, outfit);
        Assert.False(plan.CanConvert);
        Assert.Contains(plan.Issues, issue => issue.Contains("style differences"));
    }

    [Fact]
    public void DottedYabLegAbbreviationsRemainValidWithBodyLabelNormalization()
    {
        using var fixture = new Fixture();
        using var source = fixture.Mod("source", "YAB+", Group("Legs", "dwn", 0, "Skull Crushers - A", "Small - Skull Crushers - A"));
        using var target = fixture.Mod("target", "Destination", Group("Legs", "dwn", 0, "Medium"));
        using var outfit = fixture.Mod("outfit", "Outfit", Group("Leg size", "dwn", 0, "S.C", "S.C - Small Butt"));
        var plan = AutoConversionPlanner.Plan(source, target, outfit);
        Assert.True(plan.CanConvert, string.Join("; ", plan.Issues));
    }

    [Fact]
    public void KnownYabSmallButtShapeIsARecognizedSizeDifference()
    {
        using var fixture = new Fixture();
        using var source = fixture.Mod("source", "YAB+", Group("Legs", "dwn", 0, "Watermelon Crushers - A", "Small - Watermelon Crushers - A"));
        using var target = fixture.Mod("target", "Destination", Group("Legs", "dwn", 0, "Medium"));
        var sizes = Group("Buttocks size", "dwn", 0, "W.C", "W.C - Small Butt");
        sizes["Options"]![1]!["Manipulations"] = JsonNode.Parse("""[{"Type":"Shp","Manipulation":{"Slot":"Body","Id":6019,"Shape":"shpx_smolbutt","Entry":true}}]""");
        using var outfit = fixture.Mod("outfit", "Outfit", sizes);
        Assert.True(AutoConversionPlanner.Plan(source, target, outfit).CanConvert);
        sizes["Options"]![1]!["Manipulations"]![0]!["Manipulation"]!["Shape"] = "different_style";
        using var changed = fixture.Mod("changed", "Outfit", sizes);
        Assert.False(AutoConversionPlanner.Plan(source, target, changed).CanConvert);
    }

    [Fact]
    public void MultipleDefaultBodyReferencesForOneRegionAreAmbiguous()
    {
        using var fixture = new Fixture();
        using var source = fixture.DefaultMod("source", new JsonObject
        {
            ["chara/equipment/e0000/model/c0201e0000_top.mdl"] = "first.mdl",
            ["chara/equipment/e0279/model/c0201e0279_top.mdl"] = "second.mdl",
        });
        using var target = fixture.Mod("target", "Destination", Group("Chest", "top", 0, "Large"));
        using var outfit = fixture.Mod("outfit", "Outfit", Group("Chest size", "top", 0, "Small"));
        var plan = AutoConversionPlanner.Plan(source, target, outfit);
        Assert.False(plan.CanConvert);
        Assert.Contains(plan.Issues, issue => issue.Contains("several distinct default Chest model references"));
    }

    private static JsonObject Group(string name, string slot, int defaultIndex, params string[] names)
    {
        var options = new JsonArray();
        for (int i = 0; i < names.Length; i++) options.Add(new JsonObject { ["Name"] = names[i], ["Files"] = names[i] == "None" ? new JsonObject()
            : new JsonObject { [$"chara/equipment/e0000/model/c0201e0000_{slot}.mdl"] = $"model-{i}.mdl" } });
        return new() { ["Name"] = name, ["Type"] = "Single", ["DefaultSettings"] = defaultIndex, ["Options"] = options };
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "OutfitStudio-plan-" + Guid.NewGuid().ToString("N"));
        public PenumbraMod Mod(string directory, string name, params JsonObject[] groups)
        {
            string path = Path.Combine(root, directory); Directory.CreateDirectory(path);
            foreach (var group in groups)
                foreach (var option in group["Options"]!.AsArray().OfType<JsonObject>())
                    if (option["Files"] is JsonObject files)
                        foreach (var file in files) File.WriteAllBytes(Path.Combine(path, file.Value!.GetValue<string>()), [1, 2, 3]); // Deliberately not an MDL: planning only reads metadata.
            File.WriteAllText(Path.Combine(path, "meta.json"), new JsonObject { ["FileVersion"] = 4, ["Name"] = name,
                ["Groups"] = new JsonArray(groups.Select(g => (JsonNode)g.DeepClone()).ToArray()) }.ToJsonString());
            return PenumbraMod.Open(path);
        }
        public PenumbraMod DefaultMod(string directory, JsonObject files)
        {
            string path = Path.Combine(root, directory); Directory.CreateDirectory(path);
            foreach (var file in files) File.WriteAllBytes(Path.Combine(path, file.Value!.GetValue<string>()), [1, 2, 3]);
            File.WriteAllText(Path.Combine(path, "meta.json"), new JsonObject { ["FileVersion"] = 4, ["Name"] = directory,
                ["DefaultData"] = new JsonObject { ["Files"] = files.DeepClone() }, ["Groups"] = new JsonArray() }.ToJsonString());
            return PenumbraMod.Open(path);
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
