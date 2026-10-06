using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using OutfitStudio.Core.Geometry;
using OutfitStudio.Core.Models;
using OutfitStudio.Core.Mods;
using OutfitStudio.Core.Planning;
using OutfitStudio.Core.Protocol;

namespace OutfitStudio.Core.Services;

internal static class AutomaticConversionService
{
    internal sealed record Job(ModGeneratedModel File, AutomaticModelPlan Mapping, string Label, int SourceGroupIndex);

    internal static Task<WorkerResponse> ExecuteAsync(WorkerRequest request, PenumbraMod source,
        PenumbraMod target, PenumbraMod outfit, IProgress<WorkerProgress>? progress, CancellationToken ct,
        Dictionary<int, int>? preferredTemplates = null, Dictionary<int, HashSet<int>>? triedTemplates = null)
        => ExecuteAsync(request, source, [target], outfit, progress, ct, preferredTemplates, triedTemplates);

    internal static async Task<WorkerResponse> ExecuteAsync(WorkerRequest request, PenumbraMod source,
        IReadOnlyList<PenumbraMod> targets, PenumbraMod outfit, IProgress<WorkerProgress>? progress, CancellationToken ct,
        Dictionary<int, int>? preferredTemplates = null, Dictionary<int, HashSet<int>>? triedTemplates = null,
        IReadOnlyList<string>? requestedTargetPaths = null)
    {
        if (targets.Count == 0) throw new InvalidDataException("Choose at least one destination body mod.");
        requestedTargetPaths ??= ConversionService.TargetPaths(request);
        if (requestedTargetPaths.Count != targets.Count)
            throw new InvalidDataException("The opened destination mods do not match the requested destination list.");
        var target = targets[0];
        if (request.Scope != ConversionScope.AllOptions)
            throw new InvalidDataException("Destination sizes uses all outfit options. Choose manual conversion to use a saved collection selection.");
        ct.ThrowIfCancellationRequested();
        progress?.Report(new("Sizes", 0, 1, "Matching outfit sizes to the source and destination bodies"));
        preferredTemplates ??= [];
        triedTemplates ??= [];
        var plan = AutoConversionPlanner.Plan(source, targets, outfit, preferredTemplates);
        var replaced = plan.Groups.Where(g => g.SourceGroupIndex >= 0).Select(g => g.SourceGroupName).ToHashSet(StringComparer.Ordinal);
        var summary = new AutomaticPlanSummary
        {
            Ready = plan.CanConvert,
            DestinationGroups = plan.Groups.Select(g => new DestinationSizeGroup(g.DestinationGroupName,
                g.TemplateOptionName, g.Options.Select(o => o.Name).ToArray())).ToArray(),
            SourceSizeGroups = replaced.ToArray(),
            PreservedGroups = outfit.Groups.Where(g => !replaced.Contains(g.Name)).Select(g => g.Name).ToArray(),
            ModelJobs = plan.Groups.Sum(g => g.Options.Sum(o => o.Models.Count)),
            Notes = plan.Warnings.ToArray(),
            BlockingReasons = plan.Issues.ToArray(),
        };
        var warnings = plan.Warnings.ToList();
        var baseResponse = new WorkerResponse
        {
            Success = true, AutomaticPlan = summary, OutfitModelCount = outfit.GetModelFiles().Count,
            BodiesCompatible = plan.CanConvert, PreservedOptionGroups = summary.PreservedGroups,
        };
        if (!plan.CanConvert) return Unavailable(plan.Issues);

        ModRegenerationPlan package;
        List<Job> jobs;
        try
        {
            (package, jobs) = BuildPackage(outfit, plan);
            outfit.ValidateRegenerationPlan(package);
            summary = summary with { ModelJobs = jobs.Count };
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
            return Unavailable([e.Message]);
        }
        var sourceFiles = source.GetModelFiles().ToDictionary(m => Normalize(m.RelativePath), StringComparer.OrdinalIgnoreCase);
        var targetFiles = targets.Select(mod => mod.GetModelFiles().ToDictionary(m => Normalize(m.RelativePath), StringComparer.OrdinalIgnoreCase)).ToArray();
        var outfitFiles = outfit.GetModelFiles().ToDictionary(m => Normalize(m.RelativePath), StringComparer.OrdinalIgnoreCase);
        var settings = new ConversionSettings { Strength = request.Strength, Clearance = request.Clearance, MaximumDistance = request.MaxDistance };
        var prepared = new Dictionary<(string Source, int TargetModIndex, string Target), PreparedConversion>();
        // Individual body references are reused, but the Cartesian size product must
        // not retain hundreds of combined triangle and bone-weight spatial indices.
        var checkedCombinations = new HashSet<string>(StringComparer.Ordinal);
        var references = new List<object>();
        var issues = new List<string>();
        var failedGroups = new HashSet<int>();
        int checkedJobs = 0;
        var inspected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new("Check sizes", checkedJobs++, jobs.Count, job.Label));
            try
            {
                if (inspected.Add(job.File.SourceRelativePath))
                    _ = MdlConverter.Inspect(await ReadModel(outfitFiles[Normalize(job.File.SourceRelativePath)].FullPath, ct));
                var pairs = ReferencePairs(job.Mapping);
                var key = JsonSerializer.Serialize(pairs, WorkerProtocol.Json);
                if (!checkedCombinations.Contains(key))
                {
                    var parts = new List<PreparedConversion>();
                    foreach (var pair in pairs)
                    {
                        if (pair.TargetModIndex < 0 || pair.TargetModIndex >= targets.Count)
                            throw new InvalidDataException("A planned body reference names an unknown destination mod.");
                        var pairKey = (Source: Normalize(pair.SourceModelPath), pair.TargetModIndex, Target: Normalize(pair.TargetModelPath));
                        if (!prepared.TryGetValue(pairKey, out var conversion))
                        {
                            var sourceBytes = await ReadModel(sourceFiles[pairKey.Source].FullPath, ct);
                            var targetBytes = await ReadModel(targetFiles[pair.TargetModIndex][pairKey.Target].FullPath, ct);
                            conversion = MdlConverter.Prepare(sourceBytes, targetBytes, settings, ct);
                            prepared.Add(pairKey, conversion);
                            warnings.AddRange(conversion.Warnings);
                            references.Add(new { SourceModel = pair.SourceModelPath, TargetModel = pair.TargetModelPath,
                                pair.TargetModIndex, TargetModName = targets[pair.TargetModIndex].Name, TargetModPath = requestedTargetPaths[pair.TargetModIndex],
                                SourceSha256 = Hash(sourceBytes), TargetSha256 = Hash(targetBytes), conversion.Method });
                        }
                        parts.Add(conversion);
                    }
                    _ = MdlConverter.Combine(parts);
                    checkedCombinations.Add(key);
                }
            }
            catch (Exception e) when (e is ModelConversionException or InvalidDataException or KeyNotFoundException)
            {
                issues.Add($"{job.Label}: {e.Message}");
                failedGroups.Add(job.SourceGroupIndex);
            }
        }
        if (issues.Count > 0)
        {
            // Source outfits often supply several correctly labelled fits. A different
            // valid source size can resolve a difficult surface mapping without weakening
            // correspondence checks or requiring the user to select mesh files.
            foreach (var group in plan.Groups.Where(g => failedGroups.Contains(g.SourceGroupIndex) && g.SourceGroupIndex >= 0))
            {
                if (!triedTemplates.TryGetValue(group.SourceGroupIndex, out var tried))
                    triedTemplates[group.SourceGroupIndex] = tried = [];
                tried.Add(group.TemplateOptionIndex);
                int next = group.ValidTemplateOptionIndices.FirstOrDefault(i => !tried.Contains(i), -1);
                if (next < 0) continue;
                tried.Add(next);
                preferredTemplates[group.SourceGroupIndex] = next;
                progress?.Report(new("Match source fit", 0, 0, $"Checking another labelled source fit for {group.DestinationGroupName}"));
                return await ExecuteAsync(request, source, targets, outfit, progress, ct, preferredTemplates, triedTemplates, requestedTargetPaths);
            }
            return Unavailable(issues);
        }
        summary = summary with { Ready = true };
        baseResponse = baseResponse with
        {
            AutomaticPlan = summary, BodiesCompatible = true,
            CompatibilityMessage = $"Ready to create {summary.ModelJobs} fitted models with destination size options.",
            Warnings = warnings.Distinct().ToArray(),
        };
        if (request.Operation == WorkerOperation.Analyze) return baseResponse;

        ConversionService.ValidateOutputRoot(request.OutputRoot, new[] { source.RootPath, outfit.RootPath }.Concat(targets.Select(mod => mod.RootPath)).ToArray());
        var jobMap = jobs.ToDictionary(j => j.File.Id, StringComparer.Ordinal);
        var modelReports = new List<object>();
        int models = 0, vertices = 0;
        string? lastCombinationKey = null;
        PreparedConversion? lastCombination = null;
        var output = await outfit.CloneWithGeneratedOptionsAsync(request.OutputRoot, request.OutputName, package,
            async (file, destination, cancellation) =>
            {
                var job = jobMap[file.Id];
                progress?.Report(new("Create sizes", models, jobs.Count, job.Label));
                var original = await ReadModel(outfitFiles[Normalize(file.SourceRelativePath)].FullPath, cancellation);
                var pairs = ReferencePairs(job.Mapping);
                var combinationKey = JsonSerializer.Serialize(pairs, WorkerProtocol.Json);
                if (lastCombinationKey != combinationKey)
                {
                    lastCombination = null;
                    lastCombination = MdlConverter.Combine(pairs.Select(pair => prepared[
                        (Normalize(pair.SourceModelPath), pair.TargetModIndex, Normalize(pair.TargetModelPath))]));
                    lastCombinationKey = combinationKey;
                }
                var converted = lastCombination!.Convert(original, cancellation);
                _ = MdlConverter.Inspect(converted.ModelData);
                await File.WriteAllBytesAsync(destination, converted.ModelData, cancellation);
                models++; vertices += converted.ConvertedVertices;
                warnings.AddRange(converted.Warnings.Select(w => $"{job.Label}: {w}"));
                modelReports.Add(new { SourceModel = file.SourceRelativePath, OutputModel = file.OutputRelativePath,
                    job.Mapping.SourceModelPath, TargetModel = job.Mapping.TargetModelPath, job.Mapping.TargetModIndex,
                    TargetModName = targets[job.Mapping.TargetModIndex].Name, TargetModPath = requestedTargetPaths[job.Mapping.TargetModIndex],
                    AdditionalBodyReferences = job.Mapping.AdditionalBodyReferences.Select(pair => new
                    {
                        SourceModel = pair.SourceModelPath, TargetModel = pair.TargetModelPath, pair.TargetModIndex,
                        TargetModName = targets[pair.TargetModIndex].Name, TargetModPath = requestedTargetPaths[pair.TargetModIndex],
                    }).ToArray(),
                    SourceSha256 = Hash(original), OutputSha256 = Hash(converted.ModelData), converted.ConvertedVertices,
                    converted.UnchangedVertices, converted.CorrespondenceMethod });
            }, ct);
        var report = new { FormatVersion = 3, CreatedUtc = DateTimeOffset.UtcNow, Mode = request.Mode,
            SourceMod = source.Name, TargetMod = target.Name, OutfitMod = outfit.Name, AutomaticPlan = summary,
            TargetMods = ConversionService.TargetProvenance(targets, requestedTargetPaths),
            request.Strength, request.Clearance, request.MaxDistance, ReferencePairs = references,
            Models = modelReports, Warnings = warnings.Distinct().ToArray() };
        try { await File.WriteAllTextAsync(Path.Combine(output, "outfitstudio-report.json"), JsonSerializer.Serialize(report, WorkerProtocol.Json)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add($"Could not save conversion report: {e.Message}"); }
        progress?.Report(new("Complete", models, jobs.Count, "Destination size options created"));
        return baseResponse with { OutputDirectory = output, ConvertedModelCount = models, ConvertedVertexCount = vertices,
            Warnings = warnings.Distinct().ToArray() };

        WorkerResponse Unavailable(IEnumerable<string> reasons)
        {
            var errors = reasons.Distinct().ToArray();
            if (request.Operation == WorkerOperation.Convert)
                throw new InvalidDataException(string.Join(Environment.NewLine, errors));
            return baseResponse with { BodiesCompatible = false, CompatibilityMessage = "Some sizes could not be matched automatically.",
                AutomaticPlan = summary with { Ready = false, BlockingReasons = errors }, Warnings = warnings.Distinct().ToArray() };
        }
    }

    internal static (ModRegenerationPlan Package, List<Job> Jobs) BuildPackage(PenumbraMod outfit, AutomaticConversionPlan plan)
    {
        var groups = outfit.GetGroupSnapshots();
        var defaults = outfit.GetDefaultDataSnapshot();
        var originalDefaults = (JsonObject)defaults.DeepClone();
        var jobs = new List<Job>();
        var jobsByReferences = new Dictionary<string, Job>(StringComparer.Ordinal);
        var replacements = new List<ModGroupReplacement>();
        var visibleGroups = new List<JsonObject>();
        var firstGeneratedOptions = new List<int>();
        var originalOptionIndices = new List<Dictionary<int, int>>();
        var conditionalOptions = new List<JsonObject>();

        // Give every visible choice its final identity before constructing conditions,
        // including retained None choices referenced by a neighboring size selector.
        foreach (var group in plan.Groups)
        {
            var original = group.SourceGroupIndex >= 0 ? groups[group.SourceGroupIndex] : new JsonObject();
            var originalOptions = original["Options"] as JsonArray ?? new JsonArray();
            var options = new JsonArray();
            var originalIndices = new Dictionary<int, int>();
            for (int originalIndex = 0; originalIndex < originalOptions.Count; originalIndex++)
            {
                if (originalOptions[originalIndex] is not JsonObject empty || ContainsModels(empty)) continue;
                var retained = (JsonObject)empty.DeepClone();
                EnsureId(retained);
                originalIndices.Add(originalIndex, options.Count);
                options.Add(retained);
            }
            firstGeneratedOptions.Add(options.Count);
            originalOptionIndices.Add(originalIndices);
            var template = group.TemplateOptionIndex >= 0 ? (JsonObject)originalOptions[group.TemplateOptionIndex]! : originalDefaults;
            foreach (var destination in group.Options)
            {
                var option = group.SourceGroupIndex >= 0 ? (JsonObject)template.DeepClone()
                    : new JsonObject { ["Files"] = template["Files"]?.DeepClone() ?? new JsonObject() };
                option["Name"] = destination.Name;
                option["Description"] = $"Fitted to {destination.Name} from {group.TemplateOptionName}.";
                option["Id"] = Guid.NewGuid().ToString();
                option.Remove("Identifier");
                options.Add(option);
            }
            var outputGroup = (JsonObject)original.DeepClone();
            outputGroup["Name"] = group.DestinationGroupName;
            outputGroup["Type"] = "Single";
            outputGroup["DefaultSettings"] = firstGeneratedOptions[^1] + Math.Clamp(group.DestinationDefaultOptionIndex, 0, group.Options.Count - 1);
            outputGroup["Options"] = options;
            EnsureId(outputGroup);
            visibleGroups.Add(outputGroup);
            replacements.Add(new(group.SourceGroupIndex >= 0 ? group.SourceGroupName : null, outputGroup));
        }

        for (int groupId = 0; groupId < plan.Groups.Count; groupId++)
        {
            var group = plan.Groups[groupId];
            bool conditional = group.Options.SelectMany(option => option.Models).Any(model => model.RequiredOptions.Count > 0);
            var options = (JsonArray)visibleGroups[groupId]["Options"]!;
            for (int optionId = 0; optionId < group.Options.Count; optionId++)
            {
                var destination = group.Options[optionId];
                var option = (JsonObject)options[firstGeneratedOptions[groupId] + optionId]!;
                var files = option["Files"] as JsonObject ?? new JsonObject();
                if (option["Files"] is null) option["Files"] = files;
                var originalFiles = (JsonObject)files.DeepClone();
                var included = destination.Models.Select(m => Normalize(m.OutfitModelPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (group.SourceGroupIndex < 0 || conditional)
                    foreach (var key in files.Where(p => p.Key.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)
                        && (conditional || !included.Contains(Normalize(p.Value!.GetValue<string>())))).Select(p => p.Key).ToArray()) files.Remove(key);

                for (int modelId = 0; modelId < destination.Models.Count; modelId++)
                {
                    var model = destination.Models[modelId];
                    var keys = originalFiles.Where(p => p.Key.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Normalize(p.Value!.GetValue<string>()), Normalize(model.OutfitModelPath), StringComparison.OrdinalIgnoreCase))
                        .Select(p => p.Key).ToArray();
                    if (keys.Length == 0) throw new InvalidDataException($"No outfit model is associated with {group.DestinationGroupName}: {destination.Name}.");
                    // None fallbacks and aliases can request exactly the same physical
                    // conversion under different conditions. Generate that model once.
                    var jobKey = JsonSerializer.Serialize(new { Outfit = Normalize(model.OutfitModelPath).ToLowerInvariant(),
                        References = ReferencePairs(model).Select(pair => new { Source = Normalize(pair.SourceModelPath).ToLowerInvariant(),
                            pair.TargetModIndex, Target = Normalize(pair.TargetModelPath).ToLowerInvariant() }) }, WorkerProtocol.Json);
                    if (!jobsByReferences.TryGetValue(jobKey, out var job))
                    {
                        var id = $"g{groupId:D3}-o{optionId:D3}-m{modelId:D3}";
                        var generated = new ModGeneratedModel(id, model.OutfitModelPath, $"outfitstudio/generated/{id}.mdl");
                        job = new(generated, model, $"{group.DestinationGroupName}: {destination.Name}", group.SourceGroupIndex);
                        jobs.Add(job);
                        jobsByReferences.Add(jobKey, job);
                    }
                    if (conditional)
                    {
                        var predicates = new JsonArray(SettingCondition(option));
                        foreach (var required in model.RequiredOptions)
                        {
                            if (required.GroupIndex < 0 || required.GroupIndex >= plan.Groups.Count)
                                throw new InvalidDataException("A fitting condition names an unknown destination group.");
                            var neighborOptions = (JsonArray)visibleGroups[required.GroupIndex]["Options"]!;
                            int selectedIndex;
                            if (required.IsOriginalOption)
                            {
                                if (!originalOptionIndices[required.GroupIndex].TryGetValue(required.OptionIndex, out selectedIndex))
                                    throw new InvalidDataException("A fitting condition names an original option that is not retained.");
                            }
                            else
                            {
                                if (required.OptionIndex < 0 || required.OptionIndex >= plan.Groups[required.GroupIndex].Options.Count)
                                    throw new InvalidDataException("A fitting condition names an unknown destination option.");
                                selectedIndex = firstGeneratedOptions[required.GroupIndex] + required.OptionIndex;
                            }
                            predicates.Add(SettingCondition((JsonObject)neighborOptions[selectedIndex]!));
                        }
                        var mappings = new JsonObject();
                        foreach (var key in keys) mappings[key] = job.File.OutputRelativePath;
                        conditionalOptions.Add(new JsonObject
                        {
                            ["Name"] = $"Fit {conditionalOptions.Count + 1:D5}", ["Id"] = Guid.NewGuid().ToString(),
                            ["Condition"] = predicates.Count == 1 ? predicates[0]!.DeepClone()
                                : new JsonObject { ["Type"] = "And", ["Conditions"] = predicates },
                            ["Files"] = mappings,
                        });
                    }
                    else
                        foreach (var key in keys) files[key] = job.File.OutputRelativePath;
                }
            }
            if (group.SourceGroupIndex < 0 && defaults["Files"] is JsonObject defaultFiles)
            {
                var moved = group.Options.SelectMany(o => o.Models).Select(m => Normalize(m.OutfitModelPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var key in defaultFiles.Where(p => p.Key.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)
                    && moved.Contains(Normalize(p.Value!.GetValue<string>()))).Select(p => p.Key).ToArray()) defaultFiles.Remove(key);
            }
        }
        var names = groups.Select(group => group["Name"]?.GetValue<string>() ?? string.Empty)
            .Concat(visibleGroups.Select(group => group["Name"]!.GetValue<string>())).ToHashSet(StringComparer.Ordinal);
        int dataGroupIndex = 1;
        // Penumbra omits a Single group with one fixed option from its settings UI,
        // while still evaluating that option's condition and applying its data.
        // Layout.Hide only hides failed conditions; it cannot hide active controls.
        foreach (var option in conditionalOptions)
        {
            string name;
            do name = $"Outfit Studio fit mappings {dataGroupIndex++:D3}"; while (!names.Add(name));
            replacements.Add(new(null, new JsonObject
            {
                ["Name"] = name, ["Id"] = Guid.NewGuid().ToString(), ["Type"] = "Single",
                ["DefaultSettings"] = 0,
                ["Options"] = new JsonArray(option),
            }));
        }
        return (new(replacements, jobs.Select(j => j.File).ToArray(), defaults), jobs);

        static void EnsureId(JsonObject setting)
        {
            if (setting["Id"] is JsonValue value && value.TryGetValue<string>(out var text)
                && Guid.TryParse(text, out var id) && id != Guid.Empty) return;
            setting["Id"] = Guid.NewGuid().ToString();
        }
        static JsonObject SettingCondition(JsonObject option) => new() { ["Type"] = "Setting", ["Setting"] = option["Id"]!.DeepClone() };
    }

    private static BodyReferencePair[] ReferencePairs(AutomaticModelPlan model)
        => new[] { new BodyReferencePair(Normalize(model.SourceModelPath), Normalize(model.TargetModelPath)) { TargetModIndex = model.TargetModIndex } }
            .Concat(model.AdditionalBodyReferences.Select(pair => pair with
                { SourceModelPath = Normalize(pair.SourceModelPath), TargetModelPath = Normalize(pair.TargetModelPath) })).Distinct().ToArray();

    private static bool ContainsModels(JsonObject option) => option["Files"] is JsonObject files
        && files.Any(f => f.Key.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase));
    private static string Normalize(string path) => path.Replace('\\', '/');
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static async Task<byte[]> ReadModel(string path, CancellationToken ct)
    {
        if (new FileInfo(path).Length > 256L * 1024 * 1024) throw new InvalidDataException("Model exceeds 256 MiB.");
        return await File.ReadAllBytesAsync(path, ct);
    }
}
