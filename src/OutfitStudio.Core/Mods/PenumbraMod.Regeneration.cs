using System.Text.Json.Nodes;

namespace OutfitStudio.Core.Mods;

public sealed partial class PenumbraMod
{
    /// <summary>Checks a proposed package without creating directories or running model generation.</summary>
    public void ValidateRegenerationPlan(ModRegenerationPlan plan)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(plan);
        _ = ReviewRegeneration(plan);
    }

    /// <summary>
    /// Publishes regenerated destination-size groups only after all new models succeed.
    /// The callback reads SourceRelativePath beneath RootPath and writes the supplied staging path.
    /// Original files are copied; generated model paths must be new and never overwrite them.
    /// </summary>
    public async Task<string> CloneWithGeneratedOptionsAsync(string outputParent, string outputName,
        ModRegenerationPlan plan, Func<ModGeneratedModel, string, CancellationToken, Task> generate,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(generate);
        outputParent = SafeModFiles.ResolveAnchor(outputParent);
        if (SafeModFiles.IsInside(RootPath, outputParent))
            throw new InvalidOperationException("The output directory must be outside the input mod directory.");

        // Own snapshots prevent the caller from changing the plan while file generation awaits.
        var reviewed = ReviewRegeneration(plan);
        var sourceFiles = SafeModFiles.Enumerate(RootPath, limits);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(outputParent);
        var suffix = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(outputParent, ".outfitstudio-" + suffix);
        var slug = new string(outputName.Where(char.IsAsciiLetterOrDigit).Take(48).ToArray());
        var destination = Path.Combine(outputParent, (slug.Length == 0 ? "ConvertedOutfit" : slug) + "-" + suffix[..12]);
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var relative in sourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SkipGeneratedCloneFile(relative) || groupDocuments.Contains(relative))
                    continue;
                var target = SafeModFiles.Contained(stage, relative);
                if (originalDocuments.TryGetValue(relative, out var originalJson))
                {
                    await File.WriteAllTextAsync(target, originalJson, cancellationToken);
                    continue;
                }
                var source = SafeModFiles.Contained(RootPath, relative);
                await SafeModFiles.CopyAsync(source, target, new FileInfo(source).Length, cancellationToken, RootPath, outputParent);
            }

            await WriteRegeneratedMetadata(stage, outputName.Trim(), reviewed, cancellationToken);
            foreach (var model in reviewed.Models)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SafeModFiles.NoLinks(SafeModFiles.Contained(RootPath, model.SourceRelativePath), RootPath);
                var target = SafeModFiles.Contained(stage, model.OutputRelativePath);
                SafeModFiles.NoLinks(target, outputParent);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // Recheck after copying: a generated filename may not replace even an unused asset.
                if (File.Exists(target) || Directory.Exists(target))
                    throw new InvalidDataException($"Generated model would overwrite an existing path: {model.OutputRelativePath}");
                await generate(model, target, cancellationToken);
                SafeModFiles.NoLinks(target, outputParent);
                if (!File.Exists(target) || new FileInfo(target).Length == 0)
                    throw new InvalidDataException($"Generation did not produce model '{model.Id}'.");
                if (new FileInfo(target).Length > limits.MaxFileBytes)
                    throw new InvalidDataException($"Generated model exceeds the size limit: {model.OutputRelativePath}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            using (var validation = Open(stage, limits, cancellationToken))
            {
                var actual = validation.GetModelFiles().Select(model => model.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!actual.SetEquals(reviewed.Models.Select(model => model.OutputRelativePath).Concat(reviewed.PreservedModelPaths)))
                    throw new InvalidDataException("The finished mod does not reference exactly the generated and explicitly preserved models.");
            }
            SafeModFiles.NoLinks(stage, outputParent);
            Directory.Move(stage, destination);
            return destination;
        }
        catch
        {
            if (Directory.Exists(stage))
                Directory.Delete(stage, true);
            throw;
        }
    }

    private sealed record ReviewedRegeneration(JsonObject Default, List<JsonObject> Groups,
        List<ModGeneratedModel> Models, HashSet<string> PreservedModelPaths, HashSet<int> ReplacedIndices);

    private ReviewedRegeneration ReviewRegeneration(ModRegenerationPlan plan)
    {
        if (plan.Models is null || plan.GroupReplacements is null || plan.PreservedModelPaths is null || plan.Models.Count == 0)
            throw new InvalidDataException("Regeneration requires metadata changes and at least one generated model.");
        if ((long)plan.Models.Count + plan.PreservedModelPaths.Count > limits.MaxFiles)
            throw new InvalidDataException("The regeneration plan exceeds the file limit.");
        var originals = GetModelFiles().ToDictionary(model => model.RelativePath, StringComparer.OrdinalIgnoreCase);
        var preserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in plan.PreservedModelPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidDataException("Preserved model paths must be nonempty.");
            var relative = SafeModFiles.Relative(path);
            if (!originals.TryGetValue(relative, out var original))
                throw new InvalidDataException($"Preserved model is not a referenced original outfit model: {relative}");
            if (!preserved.Add(original.RelativePath))
                throw new InvalidDataException($"Duplicate preserved model: {relative}");
        }
        var models = new List<ModGeneratedModel>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var generated = new Dictionary<string, ModGeneratedModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in plan.Models)
        {
            if (job is null || string.IsNullOrWhiteSpace(job.Id) || !ids.Add(job.Id))
                throw new InvalidDataException("Generated model IDs must be nonempty and unique.");
            var source = SafeModFiles.Relative(job.SourceRelativePath);
            var output = SafeModFiles.Relative(job.OutputRelativePath);
            if (!originals.TryGetValue(source, out var actualSource))
                throw new InvalidDataException($"Generated model source is not a referenced outfit model: {source}");
            if (!output.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Generated model output must have an .mdl extension: {output}");
            if (fileInventory.ContainsKey(output) || Directory.Exists(SafeModFiles.Contained(RootPath, output)))
                throw new InvalidDataException($"Generated model would overwrite an original mod asset: {output}");
            for (var slash = output.IndexOf('/'); slash >= 0; slash = output.IndexOf('/', slash + 1))
                if (fileInventory.ContainsKey(output[..slash]))
                    throw new InvalidDataException($"Generated model parent is an original file: {output}");
            var normalized = job with { SourceRelativePath = actualSource.RelativePath, OutputRelativePath = output };
            if (!generated.TryAdd(output, normalized))
                throw new InvalidDataException($"Duplicate generated model output: {output}");
            models.Add(normalized);
        }
        foreach (var path in generated.Keys)
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                if (generated.ContainsKey(path[..slash]))
                    throw new InvalidDataException($"A generated model is also used as a directory: {path}");

        var groups = groupData.Select(group => (JsonObject)group.DeepClone()).ToList();
        var replaced = new HashSet<int>();
        var generatedGroups = new List<JsonObject>();
        foreach (var replacement in plan.GroupReplacements)
        {
            if (replacement is null || replacement.Group is null)
                throw new InvalidDataException("Generated group cannot be null.");
            var group = (JsonObject)replacement.Group.DeepClone();
            ValidateGeneratedGroupShape(group);
            generatedGroups.Add(group);
            if (replacement.SourceGroupName is null)
            {
                groups.Add(group);
                continue;
            }
            var index = groupData.FindIndex(original => Text(original, "Name") == replacement.SourceGroupName);
            if (index < 0 || !replaced.Add(index))
                throw new InvalidDataException($"Unknown or repeated source group replacement: {replacement.SourceGroupName}");
            foreach (var originalOption in ObjectArray(groupData[index], "Options"))
            {
                if (WalkContainers(originalOption).Any(container => StringMap(container, "Files")
                        .Any(file => file.Key.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))))
                    continue;
                if (!ObjectArray(group, "Options").Any(option => PreservesNonModelOption(originalOption, option)))
                    throw new InvalidDataException($"Generated group must preserve the non-model option '{Text(originalOption, "Name")}'.");
            }
            groups[index] = group;
        }
        if (groups.Select(group => Text(group, "Name")).Distinct(StringComparer.Ordinal).Count() != groups.Count)
            throw new InvalidDataException("Generated mod contains duplicate group names.");
        var newDefault = (JsonObject)(plan.DefaultData ?? defaultData).DeepClone();
        if (!JsonNode.DeepEquals(WithoutModelFiles(defaultData), WithoutModelFiles(newDefault)))
            throw new InvalidDataException("Regeneration must preserve the default non-model files, file swaps and metadata.");

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedPreserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in new[] { newDefault }.Concat(groups).SelectMany(WalkContainers))
        {
            foreach (var (gamePath, relative) in StringMap(container, "Files"))
            {
                if (gamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                {
                    if (preserved.Contains(relative))
                    {
                        if (!originals[relative].GamePaths.Contains(gamePath, StringComparer.OrdinalIgnoreCase))
                            throw new InvalidDataException($"Preserved model maps to a different game path than its original: {gamePath}");
                        usedPreserved.Add(relative);
                    }
                    else if (generated.TryGetValue(relative, out var job))
                    {
                        if (!originals[job.SourceRelativePath].GamePaths.Contains(gamePath, StringComparer.OrdinalIgnoreCase))
                            throw new InvalidDataException($"Generated model maps to a different game path than its source: {gamePath}");
                        used.Add(relative);
                    }
                    else
                        throw new InvalidDataException($"Output still references an ungenerated model: {relative}");
                }
                else
                {
                    if (generated.ContainsKey(relative))
                        throw new InvalidDataException($"A generated model is referenced as a non-model file: {gamePath}");
                    if (preserved.Contains(relative))
                        throw new InvalidDataException($"A preserved model is referenced as a non-model file: {gamePath}");
                    _ = ResolveFile(relative);
                }
            }
            foreach (var (gamePath, _) in StringMap(container, "FileSwaps"))
                if (gamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("Generated size groups cannot include unresolved model file swaps.");
        }
        if (!used.SetEquals(generated.Keys))
            throw new InvalidDataException("Every generated model must be referenced by an output option or the default data.");
        if (!usedPreserved.SetEquals(preserved))
            throw new InvalidDataException("Every explicitly preserved model must remain referenced by an output option or the default data.");
        ValidateGeneratedReferences(groupData, groups);
        ValidateGeneratedSelections(generatedGroups, generated.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), groups);
        return new(newDefault, groups, models, preserved, replaced);
    }

    // Conditional size data is deliberately a small, verifiable subset of Penumbra's
    // condition language: selected Single options, optionally joined with And. Source
    // conditions are still left intact and selected-options conversion stays conservative.
    private static bool PreservesNonModelOption(JsonObject original, JsonObject generated)
    {
        if (JsonNode.DeepEquals(original, generated)) return true;
        if (original.ContainsKey("Id") || generated["Id"] is not JsonValue idValue
            || !idValue.TryGetValue<string>(out var text) || !Guid.TryParse(text, out var id) || id == Guid.Empty)
            return false;
        var copy = (JsonObject)generated.DeepClone();
        copy.Remove("Id");
        return JsonNode.DeepEquals(original, copy);
    }

    private static void ValidateGeneratedGroupShape(JsonObject group)
    {
        if (string.IsNullOrWhiteSpace(Text(group, "Name")))
            throw new InvalidDataException("Generated group needs a name.");
        var type = Text(group, "Type");
        if (type is not ("Single" or "Multi"))
            throw new NotSupportedException("Generated size groups must use Single or Multi selection.");
        var options = ObjectArray(group, "Options");
        if (options.Count == 0 || options.Select(option => Text(option, "Name")).Distinct(StringComparer.Ordinal).Count() != options.Count)
            throw new InvalidDataException("Generated groups need nonempty, uniquely named options.");
        foreach (var item in new[] { group }.Concat(options))
        {
            if (item["ParentSetting"] is not null && Text(item, "ParentSetting") is not ("" or "00000000-0000-0000-0000-000000000000"))
                throw new NotSupportedException("Generated size groups cannot use parent-setting links.");
            if (item["Condition"] is not null) _ = ReadGeneratedCondition(item["Condition"]);
        }
        var setting = Unsigned(group, "DefaultSettings");
        if (type == "Single")
        {
            if (setting >= (ulong)options.Count)
                throw new InvalidDataException("Generated Single group default is outside the option list.");
        }
        else if (options.Count > 32 || (setting >> options.Count) != 0)
            throw new InvalidDataException("Generated Multi group has unsupported options or invalid default bits.");
    }

    private static Guid[] ReadGeneratedCondition(JsonNode? condition, int depth = 0)
    {
        if (condition is null) return [];
        if (depth > 4 || condition is not JsonObject obj)
            throw new InvalidDataException("Generated size condition is malformed or too deeply nested.");
        if (Text(obj, "Type") == "Setting" && obj.Count == 2 && obj["Setting"] is JsonValue value
            && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) && id != Guid.Empty)
            return [id];
        if (Text(obj, "Type") == "And" && obj.Count == 2 && obj["Conditions"] is JsonArray { Count: > 0 and <= 32 } children)
        {
            if (children.Any(child => child is null))
                throw new InvalidDataException("Generated size condition has an empty child.");
            return children.SelectMany(child => ReadGeneratedCondition(child, depth + 1)).Distinct().ToArray();
        }
        throw new NotSupportedException("Generated size conditions support only Setting and And with valid option IDs.");
    }

    private static void ValidateGeneratedSelections(IReadOnlyList<JsonObject> groups, HashSet<string> generatedPaths,
        IReadOnlyList<JsonObject> outputGroups)
    {
        static Guid? OptionId(JsonObject option)
            => option["Id"] is JsonValue value && value.TryGetValue<string>(out var text)
                && Guid.TryParse(text, out var id) && id != Guid.Empty ? id : null;
        static bool HasModel(JsonObject option, HashSet<string> paths)
            => WalkContainers(option).SelectMany(container => StringMap(container, "Files"))
                .Any(file => file.Key.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) && paths.Contains(file.Value));
        static bool HasCondition(JsonObject group)
            => group["Condition"] is not null || ObjectArray(group, "Options").Any(option => option["Condition"] is not null);
        var fixedGroups = groups.Where(HasCondition).ToArray();
        var fixedIds = fixedGroups.SelectMany(group => new[] { group }.Concat(ObjectArray(group, "Options")))
            .Select(OptionId).Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
        foreach (var group in outputGroups)
            if (group["ParentSetting"] is JsonValue parentValue && parentValue.TryGetValue<string>(out var parentText)
                && Guid.TryParse(parentText, out var parent) && fixedIds.Contains(parent))
                throw new NotSupportedException("Fixed size data cannot have child groups, including preserved outfit settings.");
        var selectorOptions = new Dictionary<Guid, int>();
        var defaults = new HashSet<Guid>();
        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            if (Text(group, "Type") != "Single" || HasCondition(group)) continue;
            var options = ObjectArray(group, "Options");
            foreach (var option in options)
                if (OptionId(option) is Guid id) selectorOptions.Add(id, index);
            if (OptionId(options[(int)Unsigned(group, "DefaultSettings")]) is Guid selected) defaults.Add(selected);
        }

        var referenced = new HashSet<Guid>();
        var activeDefaults = new HashSet<Guid>();
        var mappings = new Dictionary<string, List<Dictionary<int, Guid>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in fixedGroups)
        {
            var options = ObjectArray(group, "Options");
            // A one-option Single group is fixed data in Penumbra, not a visible
            // selector. Multi groups remain checkboxes even with Layout.Hide.
            if (Text(group, "Type") != "Single" || options.Count != 1
                || Unsigned(group, "DefaultSettings") != 0 || group["Condition"] is not null
                || group["ParentSetting"] is not null || options[0]["ParentSetting"] is not null
                || options[0]["Condition"] is null)
                throw new InvalidDataException("Conditional size data must use one fixed Single option, default zero, with its condition on the option and no parent links.");
            if (StringMap(group, "Files").Any() || StringMap(group, "FileSwaps").Any()
                || group["Manipulations"] is not null)
                throw new InvalidDataException("Fixed size data groups cannot contain shared files or metadata; place generated model mappings on their conditional option.");
            foreach (var option in options)
            {
                var ids = ReadGeneratedCondition(option["Condition"]);
                if (ids.Length == 0 || option["Files"] is not JsonObject { Count: > 0 } || !HasModel(option, generatedPaths))
                    throw new InvalidDataException("Conditional size data must select a generated model using visible size options.");
                var requirements = new Dictionary<int, Guid>();
                foreach (var id in ids)
                {
                    if (!selectorOptions.TryGetValue(id, out var selector))
                        throw new InvalidDataException("Generated size condition references an unknown or conditional selector option.");
                    if (requirements.TryGetValue(selector, out var previous) && previous != id)
                        throw new InvalidDataException("Generated size condition requires conflicting options of one selector.");
                    requirements[selector] = id;
                    referenced.Add(id);
                }
                foreach (var container in WalkContainers(option))
                {
                    if (!ReferenceEquals(container, option)
                        || StringMap(container, "Files").Any(file => !file.Key.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) || !generatedPaths.Contains(file.Value))
                        || StringMap(container, "FileSwaps").Any()
                        || (container["Manipulations"] is not null && container["Manipulations"] is not JsonArray { Count: 0 }))
                        throw new InvalidDataException("Fixed size data must contain only direct generated model mappings; keep other outfit settings on the visible selectors.");
                    foreach (var gamePath in StringMap(container, "Files").Select(file => file.Key))
                    {
                        if (!mappings.TryGetValue(gamePath, out var prior)) mappings[gamePath] = prior = [];
                        if (prior.Any(other => requirements.All(pair => !other.TryGetValue(pair.Key, out var selected) || selected == pair.Value)))
                            throw new InvalidDataException("Conditional size data contains simultaneously active mappings for the same game path.");
                        prior.Add(requirements);
                    }
                }
                if (ids.All(defaults.Contains)) activeDefaults.UnionWith(ids);
            }
        }
        foreach (var group in groups.Where(group => !HasCondition(group)))
        {
            var options = ObjectArray(group, "Options");
            var setting = Unsigned(group, "DefaultSettings");
            var selected = Text(group, "Type") == "Single" ? new[] { options[(int)setting] }
                : options.Where((_, index) => (setting & (1UL << index)) != 0);
            if (!selected.Any(option => HasModel(option, generatedPaths)
                    || (OptionId(option) is Guid id && referenced.Contains(id) && activeDefaults.Contains(id))))
                throw new InvalidDataException("Generated size group default must enable a generated destination model, not an empty option.");
        }
    }

    private static JsonObject WithoutModelFiles(JsonObject data)
    {
        var clone = (JsonObject)data.DeepClone();
        if (clone["Files"] is JsonObject files)
        {
            foreach (var key in files.Select(pair => pair.Key).Where(key => key.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)).ToArray())
                files.Remove(key);
            if (files.Count == 0)
                clone.Remove("Files");
        }
        return clone;
    }

    private static void ValidateGeneratedReferences(IEnumerable<JsonObject> originals, IEnumerable<JsonObject> output)
    {
        static IEnumerable<JsonObject> Settings(IEnumerable<JsonObject> groups)
            => groups.SelectMany(group => new[] { group }.Concat(ObjectArray(group, "Options")));
        static Guid? Id(JsonObject item)
            => item["Id"] is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) && id != Guid.Empty ? id : null;
        var oldIds = Settings(originals).Select(Id).Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
        var settings = Settings(output).ToArray();
        var newIds = new HashSet<Guid>();
        foreach (var item in settings)
            if (Id(item) is Guid id && !newIds.Add(id))
                throw new InvalidDataException("Generated groups or options contain duplicate IDs.");
        foreach (var item in settings)
        {
            if (item["ParentSetting"] is JsonValue parent && parent.TryGetValue<string>(out var parentText)
                && Guid.TryParse(parentText, out var parentId) && parentId != Guid.Empty && !newIds.Contains(parentId))
                throw new InvalidDataException("Regeneration would leave a dangling parent-setting reference.");
            foreach (var conditionId in ConditionGuids(item["Condition"]))
                if (oldIds.Contains(conditionId) && !newIds.Contains(conditionId))
                    throw new NotSupportedException("Regeneration would remove an option used by an existing condition.");
        }
    }

    private static IEnumerable<Guid> ConditionGuids(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id))
            yield return id;
        else if (node is JsonObject obj)
            foreach (var property in obj)
                foreach (var nested in ConditionGuids(property.Value))
                    yield return nested;
        else if (node is JsonArray array)
            foreach (var item in array)
                foreach (var nested in ConditionGuids(item))
                    yield return nested;
    }

    private async Task WriteRegeneratedMetadata(string stage, string name, ReviewedRegeneration plan, CancellationToken token)
    {
        var outputMeta = (JsonObject)metadata.DeepClone();
        outputMeta["Name"] = name;
        if (FileVersion == 4)
        {
            outputMeta["Identifier"] = Guid.NewGuid().ToString();
            outputMeta["LastWrite"] = DateTimeOffset.UtcNow.ToString("O");
            outputMeta["DefaultData"] = plan.Default.DeepClone();
            outputMeta["Groups"] = new JsonArray(plan.Groups.Select(group => group.DeepClone()).ToArray());
        }
        else
        {
            var defaultPath = fileInventory.TryGetValue("default_mod.json", out var existingDefault) ? existingDefault : "default_mod.json";
            await WriteJsonAsync(SafeModFiles.Contained(stage, defaultPath), plan.Default, token);
            var originalPaths = groupDocuments.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            for (var index = 0; index < plan.Groups.Count; index++)
            {
                var path = SafeModFiles.Contained(stage, $"group_{index + 1:D3}.json");
                if (index < originalPaths.Length && !plan.ReplacedIndices.Contains(index))
                    await File.WriteAllTextAsync(path, originalDocuments[originalPaths[index]], token);
                else
                    await WriteJsonAsync(path, plan.Groups[index], token);
            }
        }
        await WriteJsonAsync(SafeModFiles.Contained(stage, fileInventory["meta.json"]), outputMeta, token);
    }

    private static bool SkipGeneratedCloneFile(string relative)
        => !relative.Contains('/') && (relative.Equals("heliosphere.json", StringComparison.OrdinalIgnoreCase)
            || relative.Equals("meta.json.bak", StringComparison.OrdinalIgnoreCase)
            || relative.Equals("default_mod.json.bak", StringComparison.OrdinalIgnoreCase)
            || (relative.StartsWith("group_", StringComparison.OrdinalIgnoreCase) && relative.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase)));
}
