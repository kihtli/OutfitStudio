using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OutfitStudio.Core.Geometry;
using OutfitStudio.Core.Models;
using OutfitStudio.Core.Mods;
using OutfitStudio.Core.Protocol;

namespace OutfitStudio.Core.Planning;

/// <summary>Plans destination-size options from package metadata without loading model geometry.</summary>
public static class AutoConversionPlanner
{
    // Conditional variants grow with the product of neighboring size choices. Reject
    // an impractical plan before allocating that product or starting model conversion.
    private const int MaximumModelVariants = 4096;
    private sealed record RegionKey(string Race, string Slot);
    private sealed record Features(string Shape, string? Genital, string? Coverage, bool Yiggle, bool Ivcs);
    private sealed record BodyOption(int Index, string Name, ModModelFile Model, Features Features)
    {
        public int TargetModIndex { get; init; }
    }
    private sealed record BodyGroup(int Index, string Name, RegionKey Key, IReadOnlyList<BodyOption> Options, BodyOption Default, int Preference, bool HasModelDefault);
    private sealed record OutfitModel(ModModelFile Model, RegionKey Key)
    {
        public bool IsAccessory { get; init; }
    }
    private sealed record MatchedOption(int Index, string Name, IReadOnlyList<(OutfitModel Outfit, BodyOption Body)> Models)
    {
        public string Style { get; init; } = "";
    }
    private sealed record Draft(AutomaticGroupPlan Plan, RegionKey Key, MatchedOption Template);
    private sealed record SupplementalChoice(BodyReferencePair? Reference, AutomaticOptionSelection? Selection);

    public static AutomaticConversionPlan Plan(PenumbraMod source, PenumbraMod target, PenumbraMod outfit,
        IReadOnlyDictionary<int, int>? preferredTemplateIndices = null,
        IReadOnlyDictionary<string, string>? accessoryRegions = null)
        => Plan(source, new[] { target }, outfit, preferredTemplateIndices, accessoryRegions);

    public static AutomaticConversionPlan Plan(PenumbraMod source, IReadOnlyList<PenumbraMod> targetMods, PenumbraMod outfit,
        IReadOnlyDictionary<int, int>? preferredTemplateIndices = null,
        IReadOnlyDictionary<string, string>? accessoryRegions = null)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(targetMods); ArgumentNullException.ThrowIfNull(outfit);
        if (targetMods.Count == 0 || targetMods.Any(m => m is null)) throw new ArgumentException("Choose at least one destination body mod.", nameof(targetMods));
        var issues = new List<string>(); var warnings = new List<string>();
        var sources = DiscoverBodies(source, "Source", issues);
        var targetParts = targetMods.Select((mod, index) => DiscoverBodies(mod, $"Destination '{mod.Name}'", issues)
            .ToDictionary(p => p.Key, p => p.Value with
            {
                Options = p.Value.Options.Select(o => o with { TargetModIndex = index }).ToArray(),
                Default = p.Value.Default with { TargetModIndex = index },
            })).ToArray();
        var companions = ResolveCompanions(targetMods, issues);
        var targets = MergeDestinations(targetMods, targetParts);
        for (int i = 0; i < targetMods.Count; i++)
            if (targetParts[i].Count == 0)
                warnings.Add($"Destination '{targetMods[i].Name}' provides no recognized body size models; it contributes no fitting choices. Textures and materials are not body shapes.");
        if (targetMods.Count > 1)
            warnings.Add("Checked destination mods contribute separate labelled size choices. The first selected mod supplying each region sets its initial size; this selection does not change Penumbra's enabled body mods.");
        var inventory = outfit.GetModelFiles().ToDictionary(m => PathKey(m.RelativePath), StringComparer.Ordinal);
        var groups = outfit.GetGroupSnapshots();
        if (preferredTemplateIndices is not null && preferredTemplateIndices.Keys.Any(i => i < 0 || i >= groups.Count))
            issues.Add("A preferred source template names an outfit group that does not exist.");
        var drafts = new List<Draft>();
        var preservedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool sourceYab = IsYab(source.Name);
        for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex]; string name = Text(group, "Name");
            if (!HasModels(group)) continue;
            var fixedOptions = Objects(group, "Options");
            var fixedMappings = fixedOptions.SelectMany(ModelGamePaths).ToArray();
            if (Text(group, "Type") == "Single" && fixedOptions.Count == 1 && !ModelGamePaths(group).Any()
                && fixedMappings.Length > 0 && fixedMappings.All(p => TryAccessoryRace(p.Key, out var race)
                    && !sources.Keys.Any(k => k.Race == race))
                && !HasModelSwapsOrNestedMappings(fixedOptions[0]))
            {
                foreach (var mapping in fixedMappings) preservedModels.Add(PathKey(mapping.Value));
                warnings.Add($"Fixed accessory support group '{name}' is retained unchanged for races outside the selected source body.");
                continue;
            }
            if (Text(group, "Type") != "Single")
            {
                issues.Add($"Outfit group '{name}' contains models in a {Text(group, "Type")} group. Automatic sizing requires a single-choice size group.");
                continue;
            }
            if (ModelGamePaths(group).Any())
            {
                issues.Add($"Outfit group '{name}' has shared model mappings outside its options. Automatic sizing cannot separate those dependencies.");
                continue;
            }
            var options = Objects(group, "Options");
            var matches = new List<MatchedOption>();
            var groupIssues = new List<string>();
            var referencePayloads = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            bool accessoryGroup = options.SelectMany(ModelGamePaths).Any(p => TryAccessoryRace(p.Key, out _));
            int otherFamilies = 0;
            RegionKey? groupKey = null;
            var gamePathsByStyle = new Dictionary<string, string[]>(StringComparer.Ordinal);
            for (int optionIndex = 0; optionIndex < options.Count; optionIndex++)
            {
                var option = options[optionIndex]; string optionName = Text(option, "Name");
                if (!HasModels(option)) continue;
                var label = accessoryGroup ? BodyFitLabels.ParseOutfitLabel(optionName, source.Name) : null;
                if (label is { MatchesSource: false }) { otherFamilies++; continue; }
                string sourceSize = label?.SourceSize ?? optionName;
                string style = label?.Style ?? "";
                if (option["Manipulations"] is not null and not JsonArray)
                    groupIssues.Add($"Outfit option '{name}' / '{optionName}' has an unsupported metadata manipulation encoding.");
                var payload = NonModelPayload(option, sourceYab);
                if (referencePayloads.TryGetValue(style, out var referencePayload) && !JsonNode.DeepEquals(referencePayload, payload))
                    groupIssues.Add($"Outfit group '{name}' changes textures, file swaps or non-size metadata between its model options. Automatic sizing cannot discard those style differences; separate them into their own option group first.");
                referencePayloads.TryAdd(style, payload);
                var direct = DirectModels(option, inventory, $"'{name}' / '{optionName}'", groupIssues, accessoryRegions);
                if (direct.Count == 0) continue;
                var keys = direct.Select(m => m.Key).Distinct().ToArray();
                if (keys.Length != 1)
                {
                    groupIssues.Add($"Outfit option '{name}' / '{optionName}' changes several races or body regions together. Its size choices need an explicit mapping.");
                    continue;
                }
                if (groupKey is not null && groupKey != keys[0])
                {
                    groupIssues.Add($"Outfit group '{name}' mixes different races or body regions between options.");
                    continue;
                }
                groupKey = keys[0];
                var paths = ModelGamePaths(option).Select(p => p.Key.ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray();
                if (gamePathsByStyle.TryGetValue(style, out var gamePaths) && !gamePaths.SequenceEqual(paths))
                    groupIssues.Add($"Outfit group '{name}' changes which equipment files are present between sizes. Automatic sizing cannot discard those differences.");
                gamePathsByStyle.TryAdd(style, paths);
                if (!sources.TryGetValue(keys[0], out var sourceGroup))
                {
                    groupIssues.Add($"The source body has no {RegionName(keys[0].Slot)} reference for race {keys[0].Race}, required by '{name}'.");
                    continue;
                }
                var selected = MatchSource(sourceSize, sourceGroup, sourceYab);
                if (selected.Length != 1)
                {
                    string reason = selected.Length == 0 ? "does not match a source body size" : $"matches several source body references ({string.Join(", ", selected.Select(x => x.Name))})";
                    groupIssues.Add($"Outfit option '{name}' / '{optionName}' {reason}. Rename it to an unambiguous source size or use advanced mapping.");
                    continue;
                }
                matches.Add(new(optionIndex, optionName, direct.Select(m => (m, selected[0])).ToArray()) { Style = style });
                var requested = ParseFeatures(sourceSize, sourceYab);
                if ((requested.Genital is null && selected[0].Features.Genital is not null)
                    || (requested.Coverage is null && selected[0].Features.Coverage is not null))
                    warnings.Add($"Source outfit size '{optionName}' uses body reference '{selected[0].Name}'; unspecified anatomy or coverage follows the source package's enabled default.");
            }
            if (otherFamilies > 0)
                warnings.Add($"'{name}' omits {otherFamilies} fits labelled for other source body families; only '{source.Name}' fits are used.");
            if (groupIssues.Count > 0) { issues.AddRange(groupIssues); continue; }
            if (matches.Count == 0 || groupKey is null)
            {
                issues.Add(otherFamilies > 0
                    ? $"Outfit group '{name}' has no matching fit for source body '{source.Name}'. Choose one of the body families included in the outfit."
                    : $"Outfit group '{name}' contains model data that cannot be read as direct size options.");
                continue;
            }
            if (!targets.TryGetValue(groupKey, out var targetGroup))
            {
                issues.Add($"The destination body has no {RegionName(groupKey.Slot)} sizes for race {groupKey.Race}, required by '{name}'.");
                continue;
            }
            int defaultIndex = DefaultIndex(group);
            var canonicalTemplate = matches.FirstOrDefault(m => m.Index == defaultIndex)
                ?? matches.OrderBy(m => TemplateRank(m.Models[0].Body.Name, sourceYab)).ThenBy(m => m.Index).First();
            var template = canonicalTemplate;
            if (preferredTemplateIndices is not null && preferredTemplateIndices.TryGetValue(groupIndex, out int preferred))
            {
                var candidate = matches.FirstOrDefault(m => m.Index == preferred);
                if (candidate is null)
                {
                    issues.Add($"Preferred source template {preferred} is not a validated size option in outfit group '{name}'.");
                    continue;
                }
                template = candidate;
            }
            var styleTemplates = matches.GroupBy(m => m.Style).Select(cohort => cohort.Key == template.Style ? template
                : cohort.OrderBy(m => m.Models[0].Body.Features == template.Models[0].Body.Features ? 0 : 1)
                    .ThenBy(m => TemplateRank(m.Models[0].Body.Name, sourceYab)).ThenBy(m => m.Index).First()).ToArray();
            var destinationOptions = targetGroup.Options.SelectMany(destination => styleTemplates.Select(styleTemplate =>
                new AutomaticOptionPlan(string.IsNullOrEmpty(styleTemplate.Style) ? destination.Name : $"{destination.Name} / {styleTemplate.Style}",
                    styleTemplate.Models.Select(m => new AutomaticModelPlan(m.Outfit.Model.RelativePath, m.Body.Model.RelativePath,
                        destination.Model.RelativePath, Array.Empty<BodyReferencePair>())
                        { TargetModIndex = destination.TargetModIndex, LocalizeReference = m.Outfit.IsAccessory }).ToArray())
                { TemplateOptionIndex = styleTemplate.Index })).ToArray();
            int destinationDefault = targetGroup.Options.ToList().FindIndex(o => o == targetGroup.Default);
            if (!targetGroup.HasModelDefault)
                warnings.Add($"Destination group '{targetGroup.Name}' has no enabled model default. The generated group initially selects '{targetGroup.Default.Name}'.");
            var planned = new AutomaticGroupPlan(groupIndex, name, template.Index, template.Name, groupKey.Slot,
                targetGroup.Name, Math.Max(0, destinationDefault) * styleTemplates.Length + Array.FindIndex(styleTemplates, m => m.Style == canonicalTemplate.Style), destinationOptions)
            {
                ValidTemplateOptionIndices = new[] { canonicalTemplate.Index }.Concat(matches.OrderBy(m => TemplateRank(m.Models[0].Body.Name, sourceYab))
                    .ThenBy(m => m.Index).Select(m => m.Index)).Distinct().ToArray(),
            };
            drafts.Add(new(planned, groupKey, template));
            warnings.Add($"'{name}' uses {string.Join(", ", styleTemplates.Select(m => $"'{m.Name}'"))} as conversion templates and creates {destinationOptions.Length} destination fits. Original non-size groups are retained.");
        }

        var defaultData = outfit.GetDefaultDataSnapshot();
        if (HasModels(defaultData))
            PlanDefaultModels(defaultData);

        if (drafts.GroupBy(d => d.Key).Any(g => g.Count() > 1))
            issues.Add("Several outfit model groups control the same body region and race. Automatic sizing cannot choose a consistent neighboring-region reference for them.");
        if (preferredTemplateIndices is not null)
            foreach (int groupIndex in preferredTemplateIndices.Keys)
                if (!drafts.Any(d => d.Plan.SourceGroupIndex == groupIndex))
                    issues.Add($"Preferred source template group {groupIndex} is not an automatically convertible outfit size group.");

        var finalGroups = new List<AutomaticGroupPlan>();
        long variantCount = 0;
        foreach (var draft in drafts)
        {
            IReadOnlyList<AutomaticModelPlan> Variants(AutomaticModelPlan model)
            {
                var dimensions = new List<IReadOnlyList<SupplementalChoice>>();
                // Accessory inference deliberately accepts only geometry localized
                // to one body region. Unrelated hand/foot/leg defaults do not fit it.
                foreach (var otherKey in targets.Keys.Where(k => k.Race == draft.Key.Race && k.Slot != draft.Key.Slot
                    && !model.LocalizeReference))
                {
                    if (!sources.TryGetValue(otherKey, out var otherSource)) continue;
                    int neighborIndex = drafts.FindIndex(d => d.Key == otherKey);
                    var neighbor = neighborIndex >= 0 ? drafts[neighborIndex] : null;
                    // An explicit neighboring selector controls every garment that uses
                    // that body region, including a skirt stored inside a top model.
                    var choices = new List<SupplementalChoice>();
                    if (neighbor is not null)
                    {
                        for (int optionIndex = 0; optionIndex < neighbor.Plan.Options.Count; optionIndex++)
                        {
                            var selected = neighbor.Plan.Options[optionIndex].Models[0];
                            choices.Add(new(new(selected.SourceModelPath, selected.TargetModelPath)
                            {
                                TargetModIndex = selected.TargetModIndex,
                            }, new(neighborIndex, optionIndex)));
                        }
                    }
                    var retained = neighbor?.Plan.SourceGroupIndex is >= 0
                        ? Objects(groups[neighbor.Plan.SourceGroupIndex], "Options")
                            .Select((option, index) => (Option: option, Index: index)).Where(o => !HasModels(o.Option)).ToArray()
                        : [];
                    if (neighbor is null || retained.Length > 0)
                    {
                        // Missing/disabled neighboring outfit parts still use the body's
                        // package default. EXTRA fallbacks stay with their matching MAIN.
                        BodyGroup? otherTarget = null;
                        int owner = model.TargetModIndex;
                        if (targetParts[owner].TryGetValue(otherKey, out var own)) otherTarget = own;
                        else if (companions.TryGetValue(owner, out int companion)) targetParts[companion].TryGetValue(otherKey, out otherTarget);
                        else if (!IsExtra(targetMods[owner].Name))
                        {
                            var candidates = targetParts.Where(p => p.ContainsKey(otherKey)).Select(p => p[otherKey]).ToArray();
                            if (candidates.Length == 1) otherTarget = candidates[0];
                            else if (candidates.Length > 1)
                                issues.Add($"Destination '{targetMods[owner].Name}' has no {RegionName(otherKey.Slot)} reference and several selected bodies could supply it. Select only its matching companion body with this partial destination.");
                        }
                        if (otherTarget is not null)
                        {
                            if (neighbor is null && !otherSource.HasModelDefault)
                                warnings.Add($"No unambiguous source default is available for neighboring {RegionName(otherKey.Slot)} geometry; that supplemental body region was omitted.");
                            else
                            {
                                string sourcePath = neighbor?.Template.Models.First().Body.Model.RelativePath ?? otherSource.Default.Model.RelativePath;
                                var reference = new BodyReferencePair(sourcePath, otherTarget.Default.Model.RelativePath)
                                {
                                    TargetModIndex = otherTarget.Default.TargetModIndex,
                                };
                                if (neighbor is null) choices.Add(new(reference, null));
                                else foreach (var option in retained)
                                    choices.Add(new(reference, new(neighborIndex, option.Index) { IsOriginalOption = true }));
                            }
                        }
                        else if (neighbor is not null)
                            foreach (var option in retained)
                                choices.Add(new(null, new(neighborIndex, option.Index) { IsOriginalOption = true }));
                    }
                    if (choices.Count > 0) dimensions.Add(choices);
                }
                long count = 1;
                foreach (var dimension in dimensions)
                    count = count > long.MaxValue / dimension.Count
                        ? long.MaxValue : count * dimension.Count;
                variantCount = count > long.MaxValue - variantCount ? long.MaxValue : variantCount + count;
                if (variantCount > MaximumModelVariants)
                    return [model]; // The complete plan is rejected below; do not allocate the product.

                IReadOnlyList<AutomaticModelPlan> variants = [model];
                foreach (var dimension in dimensions)
                    variants = variants.SelectMany(current => dimension.Select(choice => current with
                    {
                        AdditionalBodyReferences = choice.Reference is null ? current.AdditionalBodyReferences
                            : current.AdditionalBodyReferences.Append(choice.Reference).ToArray(),
                        RequiredOptions = choice.Selection is null ? current.RequiredOptions
                            : current.RequiredOptions.Append(choice.Selection).ToArray(),
                    })).ToArray();
                return variants;
            }
            var newOptions = draft.Plan.Options.Select(option => option with
            {
                Models = option.Models.SelectMany(Variants).ToArray(),
            }).ToArray();
            finalGroups.Add(draft.Plan with { Options = newOptions });
        }
        if (variantCount > MaximumModelVariants)
            issues.Add($"The selected sizes require {variantCount:N0} coordinated model variants, exceeding the {MaximumModelVariants:N0} variant limit. Choose fewer destination body styles or convert the outfit parts separately.");
        if (finalGroups.Count == 0 && issues.Count == 0) issues.Add("The outfit does not contain recognizable model size options to convert.");
        warnings.Add("Destination option names identify the body reference used for fitting. Unsupported YAB/IVCS weight influences are adapted to that body; body textures, underwear, genital meshes and physics are not copied into the outfit.");
        if (finalGroups.SelectMany(g => g.Options).SelectMany(o => o.Models).Any(m => m.RequiredOptions.Count > 0))
            warnings.Add($"Sizes stay independently selectable in Penumbra. {variantCount:N0} coordinated model variants keep garments fitted to the selected neighboring sizes, including skirts inside chest models. Disabled or absent neighboring outfit parts use the body package's default fit.");
        else if (finalGroups.SelectMany(g => g.Options).SelectMany(o => o.Models).Any(m => !m.LocalizeReference))
            warnings.Add("Neighboring body regions without outfit size selectors use the destination body's package-default fit.");
        return new(finalGroups, issues.Distinct().ToArray(), warnings.Distinct().ToArray()) { PreservedModelPaths = preservedModels.ToArray() };

        void PlanDefaultModels(JsonObject data)
        {
            var localIssues = new List<string>();
            var models = DirectModels(data, inventory, "outfit default data", localIssues, accessoryRegions);
            issues.AddRange(localIssues);
            foreach (var region in models.GroupBy(m => m.Key))
            {
                if (drafts.Any(d => d.Key == region.Key))
                {
                    issues.Add($"The outfit has both default and option-controlled {RegionName(region.Key.Slot)} models. Automatic sizing needs that shared model dependency resolved first.");
                    continue;
                }
                if (!sources.TryGetValue(region.Key, out var sg) || !targets.TryGetValue(region.Key, out var tg))
                {
                    issues.Add($"Default outfit models have no matching source and destination {RegionName(region.Key.Slot)} references for race {region.Key.Race}.");
                    continue;
                }
                // A default-only outfit has no size label. A unique reference is safe;
                // otherwise package defaults do not establish what size the outfit was authored for.
                var unique = sg.Options.GroupBy(o => PathKey(o.Model.RelativePath)).Select(g => g.First()).ToArray();
                if (unique.Length != 1)
                {
                    issues.Add($"The outfit's default {RegionName(region.Key.Slot)} models have no source size label, while the source body has several sizes. Use a size-labelled outfit or advanced mapping.");
                    continue;
                }
                var template = new MatchedOption(-1, "Default", region.Select(m => (m, unique[0])).ToArray());
                string name = $"{RegionName(region.Key.Slot)} size";
                var options = tg.Options.Select(o => new AutomaticOptionPlan(o.Name, region.Select(m =>
                    new AutomaticModelPlan(m.Model.RelativePath, unique[0].Model.RelativePath, o.Model.RelativePath, Array.Empty<BodyReferencePair>())
                    { TargetModIndex = o.TargetModIndex, LocalizeReference = m.IsAccessory }).ToArray())).ToArray();
                drafts.Add(new(new(-1, name, -1, "Default", region.Key.Slot, tg.Name,
                    Math.Max(0, tg.Options.ToList().FindIndex(o => o == tg.Default)), options), region.Key, template));
            }
        }
    }

    private sealed record ComponentName(string Family, string Role, string Style);

    private static ComponentName? Component(string name)
    {
        // MAIN/EXTRA is an explicit package naming convention. Keep the style
        // token: sharing an author/body name alone does not imply a matching waist.
        var match = Regex.Match(name, @"^(?<stem>.+?)\s*\(\s*(?<role>MAIN|EXTRA)\s*[-–—]\s*(?<style>[^()]+?)\s*\)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? new(Normalized(match.Groups["stem"].Value),
            match.Groups["role"].Value.ToUpperInvariant(), Normalized(match.Groups["style"].Value)) : null;
    }

    private static bool IsExtra(string name) => Component(name)?.Role == "EXTRA";

    private static Dictionary<int, int> ResolveCompanions(IReadOnlyList<PenumbraMod> mods, List<string> issues)
    {
        var result = new Dictionary<int, int>();
        var names = mods.Select(m => Component(m.Name)).ToArray();
        for (int i = 0; i < mods.Count; i++)
        {
            if (names[i] is not { Role: "EXTRA" } addon) continue;
            var matches = Enumerable.Range(0, mods.Count).Where(j => names[j] is { Role: "MAIN" } main
                && main.Family == addon.Family && main.Style == addon.Style).ToArray();
            if (matches.Length == 1) result[i] = matches[0];
            else issues.Add(matches.Length == 0
                ? $"Destination '{mods[i].Name}' needs its matching MAIN - {addon.Style} body. Check both MAIN and EXTRA for the same style."
                : $"Destination '{mods[i].Name}' has several matching MAIN body mods selected. Keep one MAIN body for this style.");
        }
        return result;
    }

    private static Dictionary<RegionKey, BodyGroup> MergeDestinations(IReadOnlyList<PenumbraMod> mods,
        IReadOnlyList<Dictionary<RegionKey, BodyGroup>> parts)
    {
        if (mods.Count == 1) return parts[0];
        var duplicateNames = mods.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string Label(int owner) => mods[owner].Name + (duplicateNames.Contains(mods[owner].Name) ? $" ({owner + 1})" : "");
        BodyOption Named(BodyOption option) => option with { Name = $"{Label(option.TargetModIndex)} / {option.Name}" };
        var result = new Dictionary<RegionKey, BodyGroup>();
        foreach (var region in parts.SelectMany(p => p).GroupBy(p => p.Key))
        {
            var first = region.First().Value;
            // Union styles within one Single-choice group. Separate groups would
            // compete for the same equipment path and conceal selected styles.
            result[region.Key] = first with
            {
                Name = $"{RegionName(region.Key.Slot)} size",
                Options = region.SelectMany(p => p.Value.Options).Select(Named).ToArray(),
                Default = Named(first.Default),
            };
        }
        return result;
    }

    private static Dictionary<RegionKey, BodyGroup> DiscoverBodies(PenumbraMod mod, string role, List<string> issues)
    {
        var models = mod.GetModelFiles().ToDictionary(m => PathKey(m.RelativePath), StringComparer.Ordinal);
        var candidates = new List<BodyGroup>(); var groups = mod.GetGroupSnapshots();
        for (int gi = 0; gi < groups.Count; gi++)
        {
            var group = groups[gi]; if (Text(group, "Type") != "Single") continue;
            string name = Text(group, "Name");
            if (Normalized(name).Contains("connector", StringComparison.Ordinal)) continue;
            var options = Objects(group, "Options");
            var perRegion = new Dictionary<RegionKey, List<BodyOption>>(); int preference = 0;
            for (int oi = 0; oi < options.Count; oi++)
                foreach (var mapping in ModelGamePaths(options[oi]))
                {
                    if (!IsBodyReferencePath(mapping.Key) || !TryRegion(mapping.Key, out var region)) continue;
                    if (!models.TryGetValue(PathKey(mapping.Value), out var model)) continue;
                    if (!perRegion.TryGetValue(region, out var list)) perRegion[region] = list = [];
                    list.Add(new(oi, Text(options[oi], "Name"), model, ParseFeatures(Text(options[oi], "Name"), IsYab(mod.Name))));
                    preference = Math.Max(preference, mapping.Key.Contains("/e0000/", StringComparison.OrdinalIgnoreCase) ? 100
                        : mapping.Key.Contains("/e0279/", StringComparison.OrdinalIgnoreCase) ? 50 : 10);
                }
            foreach (var (key, raw) in perRegion)
            {
                var list = raw.DistinctBy(o => (o.Name, PathKey(o.Model.RelativePath))).ToArray();
                if (list.GroupBy(o => o.Name).Any(g => g.Count() > 1))
                {
                    issues.Add($"{role} body group '{name}' has an option mapping several different {RegionName(key.Slot)} models for race {key.Race}.");
                    continue;
                }
                var chosen = list.FirstOrDefault(o => o.Index == DefaultIndex(group)) ?? list.OrderBy(o => TemplateRank(o.Name, IsYab(mod.Name))).ThenBy(o => o.Index).First();
                candidates.Add(new(gi, name, key, list, chosen, preference, list.Any(o => o.Index == DefaultIndex(group))));
            }
        }
        // Default-only body mods are supported when there is exactly one model per region/race.
        var defaultReferences = new List<(RegionKey Key, ModModelFile Model)>();
        foreach (var mapping in ModelGamePaths(mod.GetDefaultDataSnapshot()))
            if (IsBodyReferencePath(mapping.Key) && TryRegion(mapping.Key, out var key) && models.TryGetValue(PathKey(mapping.Value), out var model))
                defaultReferences.Add((key, model));
        foreach (var references in defaultReferences.GroupBy(r => r.Key))
            if (!candidates.Any(c => c.Key == references.Key))
            {
                var distinct = references.Select(r => r.Model).DistinctBy(m => PathKey(m.RelativePath)).ToArray();
                if (distinct.Length != 1)
                {
                    issues.Add($"{role} body has several distinct default {RegionName(references.Key.Slot)} model references for race {references.Key.Race}. Automatic sizing cannot choose one safely.");
                    continue;
                }
                var model = distinct[0];
                var option = new BodyOption(-1, "Default", model, ParseFeatures("Default", false));
                candidates.Add(new(-1, "Default", references.Key, [option], option, 0, true));
            }
        var result = new Dictionary<RegionKey, BodyGroup>();
        foreach (var grouped in candidates.GroupBy(c => c.Key))
        {
            int priority = grouped.Max(g => g.Preference);
            var best = grouped.Where(g => g.Preference == priority).ToArray();
            if (best.Length > 1)
            {
                string Fingerprint(BodyGroup g) => string.Join("|", g.Options.Select(o => o.Name + "=" + PathKey(o.Model.RelativePath)).Order(StringComparer.Ordinal));
                if (best.Select(Fingerprint).Distinct().Count() > 1)
                {
                    issues.Add($"{role} body has several competing {RegionName(grouped.Key.Slot)} option groups for race {grouped.Key.Race}: {string.Join(", ", best.Select(g => g.Name))}. Automatic sizing cannot pick one safely.");
                    continue;
                }
            }
            result[grouped.Key] = best.OrderBy(g => g.Index).First();
        }
        return result;
    }

    private static BodyOption[] MatchSource(string outfitOption, BodyGroup source, bool yab)
    {
        var requested = ParseFeatures(outfitOption, yab);
        var matches = source.Options.Where(o => o.Features.Shape == requested.Shape).ToArray();
        var baseline = source.HasModelDefault ? source.Default.Features : new Features("", null, null, false, false);
        matches = matches.Where(o => o.Features.Yiggle == requested.Yiggle
            && o.Features.Ivcs == requested.Ivcs
            && (requested.Genital is null ? baseline.Genital is null || o.Features.Genital == baseline.Genital : o.Features.Genital == requested.Genital)
            && (requested.Coverage is null ? baseline.Coverage is null || o.Features.Coverage == baseline.Coverage : o.Features.Coverage == requested.Coverage)).ToArray();
        return matches.DistinctBy(o => PathKey(o.Model.RelativePath)).ToArray();
    }

    private static Features ParseFeatures(string name, bool yab)
    {
        string value = Normalized(BodyFitLabels.NormalizeBodyOptionLabel(name));
        if (yab)
        {
            value = Regex.Replace(value, @"\b(?:w c|wc|watermelon crushers)\b", "watermeloncrushers");
            value = Regex.Replace(value, @"\b(?:s c|sc|skull crushers)\b", "skullcrushers");
            value = Regex.Replace(value, @"\bsmall butt\b", "small");
            value = Regex.Replace(value, @"\b(?:yab|yet another body)\b", "");
        }
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        string? genital = null;
        int gen = tokens.IndexOf("gen");
        if (gen >= 0 && gen + 1 < tokens.Count) { genital = tokens[gen + 1]; tokens.RemoveRange(gen, 2); }
        else if (yab && tokens.Count > 0 && tokens[^1] is "a" or "b" or "c") { genital = tokens[^1]; tokens.RemoveAt(tokens.Count - 1); }
        string? coverage = tokens.FirstOrDefault(t => t is "sfw" or "nsfw");
        bool yiggle = tokens.Contains("yiggle"), ivcs = tokens.Contains("ivcs");
        tokens.RemoveAll(t => t is "sfw" or "nsfw" or "yiggle" or "ivcs" or "size");
        for (int i = 0; i < tokens.Count; i++) tokens[i] = tokens[i] switch { "s" => "small", "m" => "medium", "l" => "large", "xs" => "extrasmall", "xl" => "extralarge", _ => tokens[i] };
        return new(string.Join(" ", tokens.Order(StringComparer.Ordinal)), genital, coverage, yiggle, ivcs);
    }

    private static int TemplateRank(string name, bool yab)
    {
        var f = ParseFeatures(name, yab);
        int modifiers = f.Yiggle || f.Ivcs || f.Shape.Contains("buff", StringComparison.Ordinal) ? 100 : 0;
        if (f.Shape == "medium") return modifiers;
        if (f.Shape.Contains("small", StringComparison.Ordinal) && (f.Shape.Contains("crushers", StringComparison.Ordinal) || f.Shape.Contains("yanilla", StringComparison.Ordinal))) return modifiers + 20;
        return modifiers + 10;
    }

    private static JsonObject NonModelPayload(JsonObject option, bool yab)
    {
        var files = new JsonObject();
        if (option["Files"] is JsonObject originalFiles)
            foreach (var (path, file) in originalFiles.Where(p => !p.Key.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)))
                files[path] = file?.DeepClone();
        var manipulations = new JsonArray();
        if (option["Manipulations"] is JsonArray originalManipulations)
            foreach (var manipulation in originalManipulations)
            {
                // YAB's known Small Butt option switches this size-specific shape on
                // the outfit. Keep the selected template's complete payload when writing;
                // exclude this known size delta only from the style-equivalence check.
                bool smallButtShape = yab && manipulation is JsonObject item && Text(item, "Type") == "Shp"
                    && item["Manipulation"] is JsonObject content && Text(content, "Shape") == "shpx_smolbutt";
                if (!smallButtShape) manipulations.Add(manipulation?.DeepClone());
            }
        return new() { ["Files"] = files, ["FileSwaps"] = option["FileSwaps"]?.DeepClone() ?? new JsonObject(), ["Manipulations"] = manipulations };
    }

    private static List<OutfitModel> DirectModels(JsonObject container, Dictionary<string, ModModelFile> inventory, string context, List<string> issues,
        IReadOnlyDictionary<string, string>? accessoryRegions)
    {
        var list = new List<OutfitModel>();
        if (container.Any(p => p.Key is not ("Files" or "FileSwaps" or "Manipulations") && HasModels(p.Value)))
            issues.Add($"{context} contains conditional or nested model mappings. Automatic sizing requires direct model options.");
        foreach (var (gamePath, relative) in ModelGamePaths(container))
        {
            if (!TryRegion(gamePath, out var key))
            {
                if (!TryAccessoryRace(gamePath, out var race))
                { issues.Add($"Model '{gamePath}' in {context} is not a supported equipment body region or accessory slot."); continue; }
                if (accessoryRegions is null || !accessoryRegions.TryGetValue(AccessoryKey(race, relative), out var slot)
                    || slot is not ("top" or "dwn" or "glv" or "sho"))
                { issues.Add($"Accessory '{relative}' in {context} cannot be assigned to one source body region from its geometry. Use advanced body references for ambiguous accessories."); continue; }
                key = new(race, slot);
            }
            if (!inventory.TryGetValue(PathKey(relative), out var model)) { issues.Add($"Model '{relative}' in {context} is missing from the included model inventory."); continue; }
            list.Add(new(model, key) { IsAccessory = TryAccessoryRace(gamePath, out _) });
        }
        if (container["FileSwaps"] is JsonObject swaps && swaps.Any(p => p.Key.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)))
            issues.Add($"{context} uses model file swaps; automatic sizing requires included models.");
        return list.DistinctBy(m => (PathKey(m.Model.RelativePath), m.Key)).ToList();
    }

    // A ring slot can hold a chest ornament. Equip slots identify the race, while
    // the actual geometry establishes which source body surface should fit it.
    internal static IReadOnlyDictionary<string, string> ResolveAccessoryRegions(PenumbraMod source, PenumbraMod outfit, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var bodyGroups = DiscoverBodies(source, "Source", []);
        var inventory = outfit.GetModelFiles().ToDictionary(m => PathKey(m.RelativePath), StringComparer.Ordinal);
        var references = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
        var options = outfit.GetGroupSnapshots().SelectMany(g => Objects(g, "Options"))
            .Where(o => BodyFitLabels.ParseOutfitLabel(Text(o, "Name"), source.Name) is not { MatchesSource: false })
            .Append(outfit.GetDefaultDataSnapshot());
        var inspected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in options.SelectMany(ModelGamePaths))
        {
            ct.ThrowIfCancellationRequested();
            if (!TryAccessoryRace(mapping.Key, out var race)) continue;
            string key = AccessoryKey(race, mapping.Value);
            if (!inspected.Add(key) || !inventory.TryGetValue(PathKey(mapping.Value), out var model)) continue;
            if (!references.TryGetValue(race, out var bodies))
            {
                bodies = bodyGroups.Where(p => p.Key.Race == race).ToDictionary(p => p.Key.Slot,
                    p => Read(p.Value.Default.Model.FullPath), StringComparer.Ordinal);
                references.Add(race, bodies);
            }
            if (bodies.Count == 0) continue;
            try
            {
                var region = AccessoryRegionResolver.Resolve(Read(model.FullPath), bodies, ct);
                if (region is not null) result.Add(key, region);
            }
            catch (Exception e) when (e is ModelConversionException or InvalidDataException)
            {
                // Leave this model unresolved: the planner reports its precise
                // option rather than silently assigning an unreadable accessory.
            }
        }
        return result;

        static byte[] Read(string path)
        {
            if (new FileInfo(path).Length > 256L * 1024 * 1024) throw new InvalidDataException("Model exceeds 256 MiB.");
            return File.ReadAllBytes(path);
        }
    }

    internal static string AccessoryKey(string race, string relative) => race + ":" + PathKey(relative);
    private static bool TryAccessoryRace(string path, out string race)
    {
        var match = Regex.Match(path.Replace('\\', '/'), @"(?:^|/)c(?<race>\d{4})a\d{4}_(?:ear|nek|wrs|ril|rir)\.mdl$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        race = match.Groups["race"].Value;
        return match.Success;
    }

    private static bool HasModelSwapsOrNestedMappings(JsonObject option)
        => option["FileSwaps"] is JsonObject swaps && swaps.Any(p => p.Key.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            || option.Any(p => p.Key is not ("Files" or "FileSwaps" or "Manipulations") && HasModels(p.Value));

    private static bool TryRegion(string gamePath, out RegionKey key)
    {
        var match = Regex.Match(gamePath.Replace('\\', '/'), @"(?:^|/)c(?<race>\d{4})(?:e\d{4}|b\d{4})_(?<slot>top|dwn|glv|sho)\.mdl$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        key = new(match.Groups["race"].Value, match.Groups["slot"].Value.ToLowerInvariant()); return match.Success;
    }
    private static bool IsBodyReferencePath(string path) => Regex.IsMatch(path.Replace('\\', '/'),
        @"(?:^|/)c\d{4}(?:e\d{4}|b0001)_(?:top|dwn|glv|sho)\.mdl$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static bool IsYab(string name) => name.Contains("yet another body", StringComparison.OrdinalIgnoreCase) || Normalized(name).Split(' ').Contains("yab");
    private static string PathKey(string path) => path.Replace('\\', '/').ToLowerInvariant();
    private static string RegionName(string slot) => slot switch { "top" => "Chest", "dwn" => "Legs", "glv" => "Hands", "sho" => "Feet", _ => slot };
    private static string Normalized(string name)
    {
        var builder = new StringBuilder();
        foreach (char c in name.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }
    private static string Text(JsonObject obj, string name) => obj[name]?.GetValue<string>() ?? "";
    private static int DefaultIndex(JsonObject obj) => obj["DefaultSettings"] is JsonValue scalar && scalar.TryGetValue<ulong>(out var n) && n <= int.MaxValue ? (int)n : 0;
    private static IReadOnlyList<JsonObject> Objects(JsonObject obj, string name) => obj[name] is JsonArray array ? array.OfType<JsonObject>().ToArray() : [];
    private static IEnumerable<KeyValuePair<string, string>> ModelGamePaths(JsonObject obj) => obj["Files"] is JsonObject files
        ? files.Where(p => p.Key.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)).Select(p => KeyValuePair.Create(p.Key.Replace('\\', '/'), p.Value!.GetValue<string>())) : [];
    private static bool HasModels(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Any(p => ((p.Key is "Files" or "FileSwaps") && p.Value is JsonObject files && files.Any(f => f.Key.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))) || (p.Key != "Manipulations" && HasModels(p.Value))),
        JsonArray array => array.Any(HasModels), _ => false,
    };
}
