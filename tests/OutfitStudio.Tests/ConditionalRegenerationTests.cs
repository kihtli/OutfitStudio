using System.Text.Json.Nodes;
using OutfitStudio.Core.Mods;

namespace OutfitStudio.Tests;

public sealed class ConditionalRegenerationTests
{
    [Fact]
    public async Task LinkedSizesPublishWithIndependentSelectorsAndFixedModelData()
    {
        using var fixture = new Fixture();
        var plan = fixture.Plan();
        var output = await fixture.Mod.CloneWithGeneratedOptionsAsync(fixture.Output, "Linked sizes", plan,
            (model, destination, token) => File.WriteAllTextAsync(destination, model.Id, token));
        using var result = PenumbraMod.Open(output);
        Assert.Equal(6, result.GetModelFiles().Count);
        var snapshot = result.GetGroupSnapshots();
        // Native Single groups with one fixed option provide data without a choice
        // control. Only the two independent selectors have more than one option.
        Assert.Equal(new[] { "Chest", "Legs" }, result.Groups.Where(group => group.Options.Count > 1).Select(group => group.Name));
        Assert.Equal(4, snapshot.Skip(2).Count());
        Assert.All(snapshot.Skip(2), group =>
        {
            Assert.Equal("Single", group["Type"]!.GetValue<string>());
            Assert.Single(group["Options"]!.AsArray());
            Assert.Equal(0UL, result.Groups.Single(value => value.Name == group["Name"]!.GetValue<string>()).DefaultSettings);
            Assert.Null(group["Condition"]);
            Assert.Null(group["ParentSetting"]);
        });
        Assert.All(snapshot[0]["Options"]!.AsArray(), option => Assert.Null(option!["Files"]));
        foreach (var chest in new[] { 0, 1 })
        foreach (var legs in new[] { 0, 1 })
        {
            var ids = new HashSet<string> { Fixture.Id(chest + 1), Fixture.Id(legs + 3) };
            var enabled = snapshot.Skip(2).SelectMany(group => group["Options"]!.AsArray())
                .Where(option => option!["Condition"]!["Conditions"]!.AsArray()
                    .All(condition => ids.Contains(condition!["Setting"]!.GetValue<string>()))).ToArray();
            Assert.Single(enabled);
            Assert.Equal($"generated/top-{chest}-{legs}.mdl", enabled[0]!["Files"]!["chara/top.mdl"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData("unknown-id")]
    [InlineData("group-id")]
    [InlineData("fixed-option-id")]
    [InlineData("contradiction")]
    [InlineData("overlap")]
    [InlineData("invalid-default")]
    [InlineData("legacy-checkboxes")]
    [InlineData("visible-extra-choice")]
    [InlineData("group-condition")]
    [InlineData("missing-default")]
    [InlineData("side-effect")]
    [InlineData("shared-data")]
    [InlineData("nested-data")]
    public void InvalidConditionalDataFailsBeforeCreatingOutput(string invalid)
    {
        using var fixture = new Fixture();
        var plan = fixture.Plan();
        var chest = plan.GroupReplacements[0].Group;
        var hidden = plan.GroupReplacements[2].Group;
        var option = hidden["Options"]![0]!;
        switch (invalid)
        {
            case "unknown-id": option["Condition"]!["Conditions"]![0]!["Setting"] = Fixture.Id(99); break;
            case "group-id": option["Condition"]!["Conditions"]![0]!["Setting"] = Fixture.Id(10); break;
            case "fixed-option-id": option["Condition"]!["Conditions"]![0]!["Setting"] = Fixture.Id(30); break;
            case "contradiction": option["Condition"]!["Conditions"]![1]!["Setting"] = Fixture.Id(2); break;
            case "overlap": plan.GroupReplacements[3].Group["Options"]![0]!["Condition"] = option["Condition"]!.DeepClone(); break;
            case "invalid-default": hidden["DefaultSettings"] = 1; break;
            case "legacy-checkboxes": hidden["Type"] = "Multi"; hidden["DefaultSettings"] = 1; hidden["Layout"] = new JsonArray("Hide"); break;
            case "visible-extra-choice": hidden["Options"]!.AsArray().Add(new JsonObject { ["Name"] = "Off" }); break;
            case "group-condition": hidden["Condition"] = option["Condition"]!.DeepClone(); option.AsObject().Remove("Condition"); break;
            case "missing-default":
                chest["Options"]!.AsArray().Insert(0, new JsonObject { ["Name"] = "Unmapped", ["Id"] = Fixture.Id(99) });
                break;
            case "side-effect": option["Files"]!["chara/skin.tex"] = "skin.tex"; break;
            case "shared-data": hidden["Files"] = new JsonObject { ["chara/top.mdl"] = "generated/top-0-0.mdl" }; break;
            case "nested-data": option["Nested"] = new JsonObject { ["Files"] = option["Files"]!.DeepClone() }; break;
        }
        Assert.Throws<InvalidDataException>(() => fixture.Mod.ValidateRegenerationPlan(plan));
        Assert.False(Directory.Exists(fixture.Output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FixedDataCannotHaveParentLinksOrAttachedChildren(bool child)
    {
        using var fixture = new Fixture();
        var plan = fixture.Plan();
        var data = plan.GroupReplacements[2].Group;
        if (child) plan.GroupReplacements[0].Group["ParentSetting"] = data["Id"]!.DeepClone();
        else data["ParentSetting"] = Fixture.Id(10);
        Assert.Throws<NotSupportedException>(() => fixture.Mod.ValidateRegenerationPlan(plan));
    }

    [Fact]
    public void PreservedSourceChildCannotMakeFixedDataVisible()
    {
        using var fixture = new Fixture(preservedChild: true);
        Assert.Throws<NotSupportedException>(() => fixture.Mod.ValidateRegenerationPlan(fixture.Plan()));
        Assert.False(Directory.Exists(fixture.Output));
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void UnconditionalMultiKeepsNativeOptionCapacity(int count, bool valid)
    {
        using var fixture = new Fixture();
        var plan = fixture.Plan();
        var group = plan.GroupReplacements[1].Group;
        group["Type"] = "Multi";
        var options = group["Options"]!.AsArray();
        for (var index = options.Count; index < count; index++)
            options.Add(new JsonObject { ["Name"] = $"Extra {index}" });
        group["DefaultSettings"] = 1UL;
        // This test isolates ordinary Multi capacity; conditional data cannot
        // reference a Multi selector, so replace it with direct chest mappings.
        var chest = plan.GroupReplacements[0].Group;
        chest["Options"]![0]!["Files"] = new JsonObject { ["chara/top.mdl"] = "generated/top-0-0.mdl" };
        chest["Options"]![1]!["Files"] = new JsonObject { ["chara/top.mdl"] = "generated/top-1-0.mdl" };
        var direct = plan with
        {
            GroupReplacements = plan.GroupReplacements.Take(2).ToArray(),
            Models = plan.Models.Where(model => model.Id.StartsWith("legs-") || model.Id is "top-0-0" or "top-1-0").ToArray(),
        };
        if (valid) fixture.Mod.ValidateRegenerationPlan(direct);
        else Assert.Throws<InvalidDataException>(() => fixture.Mod.ValidateRegenerationPlan(direct));
    }

    [Theory]
    [InlineData("True")]
    [InlineData("Not")]
    [InlineData("Or")]
    public void UnsupportedGeneratedConditionLanguageIsRejected(string type)
    {
        using var fixture = new Fixture();
        var plan = fixture.Plan();
        plan.GroupReplacements[2].Group["Options"]![0]!["Condition"] = new JsonObject { ["Type"] = type };
        Assert.Throws<NotSupportedException>(() => fixture.Mod.ValidateRegenerationPlan(plan));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "OutfitStudio-linked-tests-" + Guid.NewGuid().ToString("N"));
        public PenumbraMod Mod { get; }
        public string Output => Path.Combine(root, "output");
        public Fixture(bool preservedChild = false)
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "meta.json"), """
                {"FileVersion":4,"Name":"Source","Groups":[
                 {"Name":"Chest","Type":"Single","Options":[{"Name":"Source","Files":{"chara/top.mdl":"top.mdl"}}]},
                 {"Name":"Legs","Type":"Single","Options":[{"Name":"Source","Files":{"chara/legs.mdl":"legs.mdl"}}]}]}
                """);
            if (preservedChild)
            {
                var path = Path.Combine(source, "meta.json");
                var metadata = JsonNode.Parse(File.ReadAllText(path))!;
                metadata["Groups"]![0]!["Id"] = Id(20);
                metadata["Groups"]!.AsArray().Add(new JsonObject
                {
                    ["Name"] = "Style", ["Type"] = "Single", ["ParentSetting"] = Id(20),
                    ["Options"] = new JsonArray(new JsonObject { ["Name"] = "Matte" }, new JsonObject { ["Name"] = "Glossy" }),
                });
                File.WriteAllText(path, metadata.ToJsonString());
            }
            foreach (var file in new[] { "top.mdl", "legs.mdl", "skin.tex" }) File.WriteAllText(Path.Combine(source, file), "source");
            Mod = PenumbraMod.Open(source);
        }
        public static string Id(int n) => $"00000000-0000-0000-0000-{n:D12}";
        public ModRegenerationPlan Plan()
        {
            var chest = new JsonObject { ["Name"] = "Chest", ["Type"] = "Single", ["Id"] = Id(10), ["DefaultSettings"] = 0,
                ["Options"] = new JsonArray(new JsonObject { ["Name"] = "Small", ["Id"] = Id(1) }, new JsonObject { ["Name"] = "Large", ["Id"] = Id(2) }) };
            var legs = new JsonObject { ["Name"] = "Legs", ["Type"] = "Single", ["DefaultSettings"] = 0 };
            var legOptions = new JsonArray();
            var jobs = new List<ModGeneratedModel>();
            var replacements = new List<ModGroupReplacement> { new("Chest", chest), new("Legs", legs) };
            for (var leg = 0; leg < 2; leg++)
            {
                var path = $"generated/legs-{leg}.mdl";
                legOptions.Add(new JsonObject { ["Name"] = leg == 0 ? "Small" : "Large", ["Id"] = Id(leg + 3),
                    ["Files"] = new JsonObject { ["chara/legs.mdl"] = path } });
                jobs.Add(new($"legs-{leg}", "legs.mdl", path));
            }
            legs["Options"] = legOptions;
            for (var top = 0; top < 2; top++)
            for (var leg = 0; leg < 2; leg++)
            {
                var path = $"generated/top-{top}-{leg}.mdl";
                var option = new JsonObject { ["Name"] = $"Top {top} legs {leg}", ["Id"] = Id(30 + top * 2 + leg),
                    ["Condition"] = new JsonObject { ["Type"] = "And", ["Conditions"] = new JsonArray(
                        new JsonObject { ["Type"] = "Setting", ["Setting"] = Id(top + 1) },
                        new JsonObject { ["Type"] = "Setting", ["Setting"] = Id(leg + 3) }) },
                    ["Files"] = new JsonObject { ["chara/top.mdl"] = path } };
                replacements.Add(new(null, new JsonObject { ["Name"] = $"Fitted chest {top}-{leg}", ["Id"] = Id(20 + top * 2 + leg),
                    ["Type"] = "Single", ["DefaultSettings"] = 0, ["Options"] = new JsonArray(option) }));
                jobs.Add(new($"top-{top}-{leg}", "top.mdl", path));
            }
            return new(replacements, jobs);
        }
        public void Dispose() { Mod.Dispose(); Directory.Delete(root, true); }
    }
}
