using System.Collections.Concurrent;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using OutfitStudio.Core.Protocol;
using OutfitStudio.Paths;

namespace OutfitStudio.Plugin;

internal sealed class StudioWindow : Window, IDisposable
{
    private static readonly Vector4 Accent = new(0.42f, 0.85f, 0.79f, 1);
    private static readonly Vector4 Muted = new(0.64f, 0.69f, 0.76f, 1);
    private static readonly Vector4 Warning = new(1f, 0.75f, 0.43f, 1);
    private readonly PenumbraBridge penumbra;
    private readonly WorkerClient worker;
    private readonly Configuration configuration;
    private readonly Action save;
    private readonly IPluginLog log;
    private readonly ConcurrentQueue<Action> updates = new();
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? job;
    private bool disposed;
    private bool busy;
    private bool refreshing;
    private bool refreshRequested;
    private string status = "Connect to Penumbra to choose your mods.";
    private string? error;
    private PenumbraSnapshot? snapshot;
    private string source = "";
    private string target = "";
    private readonly List<string> targets = [];
    private string outfit = "";
    private string sourceModel = "";
    private string targetModel = "";
    private string sourceFilter = "";
    private string targetFilter = "";
    private string outfitFilter = "";
    private Guid collection;
    private bool manualMode;
    private ConversionScope scope;
    private string outputName = "Converted Outfit";
    private float strength = 1f;
    private float clearance = 0.0025f;
    private float maxDistance = 0.25f;
    private bool enableOutput;
    private bool redraw;
    private int outputPriority = 1;
    private bool readingEnableSettings;
    private int enableGeneration;
    private WorkerResponse? analysis;
    private WorkerRequest? analyzedRequest;
    private Dictionary<string, List<string>>? discoveredOptions;
    private WorkerResponse? result;
    private WorkerProgress? progress;
    private string installStatus = "";
    private readonly Dictionary<string, ModelOverride> overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ModelOverride> additionalReferences = [];
    private sealed class ModelOverride { public string Source = ""; public string Target = ""; }

    public StudioWindow(PenumbraBridge penumbra, WorkerClient worker, Configuration configuration, Action save, IPluginLog log)
        : base("Outfit Studio###OutfitStudio")
    {
        this.penumbra = penumbra; this.worker = worker; this.configuration = configuration; this.save = save; this.log = log;
        Size = new Vector2(900, 790);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(650, 520), MaximumSize = new Vector2(float.MaxValue) };
    }

    public void RefreshIfNeeded() => refreshRequested = snapshot is null;

    public override void Draw()
    {
        while (updates.TryDequeue(out var update)) update();
        if (refreshRequested && !busy && !refreshing) { refreshRequested = false; Refresh(); }
        ImGui.TextColored(Accent, "OUTFIT STUDIO");
        ImGui.SameLine(); ImGui.TextColored(Muted, " /  experimental body conversion");
        Wrap("Choose a source body, an outfit and the destination bodies to include. Create one outfit with their body and size choices while keeping its styles.");
        ImGui.Spacing();
        ImGui.BeginDisabled(busy || refreshing);
        if (ImGui.Button(refreshing ? "Connecting..." : "Refresh Penumbra")) Refresh();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextColored(snapshot is null ? Warning : Accent,
            snapshot is null ? "Not connected" : $"{snapshot.Mods.Length} mods  /  {snapshot.Collections.Length} collections");
        if (snapshot is not null)
        {
            ImGui.Spacing();
            ImGui.BeginDisabled(busy || refreshing);
            DrawInputs();
            if (manualMode) DrawModels();
            else DrawDestinationSizes();
            DrawSettings();
            DrawAdvancedConversion();
            DrawActions();
            ImGui.EndDisabled();
        }
        if (busy)
        {
            ImGui.Spacing();
            if (ImGui.Button("Cancel job")) { job?.Cancel(); status = "Cancelling worker..."; }
            ImGui.SameLine(); ImGui.TextUnformatted(progress?.Stage ?? "Starting");
            if (progress is { } current)
            {
                var fraction = current.Total > 0 ? Math.Clamp((float)current.Completed / current.Total, 0, 1) : 0;
                ImGui.ProgressBar(fraction, new Vector2(-1, 0), current.Total > 0 ? $"{current.Completed} / {current.Total}" : current.Stage);
                Wrap(current.Message);
            }
        }
        ImGui.Separator();
        Wrap(status);
        if (error is not null) { ImGui.PushStyleColor(ImGuiCol.Text, Warning); Wrap(error); ImGui.PopStyleColor(); }
        DrawResult();
        DrawAdvanced();
    }

    private void DrawInputs()
    {
        Heading("1", "Choose installed mods");
        if (ModCombo("Source body", ref source, ref sourceFilter)) Invalidate(true);
        if (manualMode)
        {
            if (ModCombo("Destination body", ref target, ref targetFilter)) { Invalidate(true); SuggestOutputName(); }
            if (targets.Count > 1)
                Small($"Manual conversion uses one destination body. Your {targets.Count} automatic destinations are kept for when you return to automatic mode.");
        }
        else if (DestinationMods()) { Invalidate(true); SuggestOutputName(); }
        if (ModCombo("Outfit", ref outfit, ref outfitFilter))
        {
            SuggestOutputName();
            Invalidate(true);
            ResetEnable();
        }
        Small(manualMode
            ? "Source body: the body the outfit already fits. Destination body: the body you want it to fit."
            : "Source body: the body the outfit already fits. Select all destination bodies whose body parts and sizes you want in the new outfit.");
    }

    private void SuggestOutputName()
    {
        var outfitName = snapshot!.Mods.FirstOrDefault(m => m.Directory == outfit)?.Name;
        var primary = manualMode ? target : targets.FirstOrDefault();
        var targetName = !manualMode && targets.Count > 1 ? "Selected bodies"
            : snapshot.Mods.FirstOrDefault(m => m.Directory == primary)?.Name;
        if (outfitName is not null) outputName = $"{outfitName} - {targetName ?? "Converted"}";
    }

    private void DrawCollection()
    {
        var selectedCollection = snapshot!.Collections.FirstOrDefault(c => c.Id == collection);
        ImGui.SetNextItemWidth(-150 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Collection", selectedCollection?.Name ?? "Choose a collection"))
        {
            foreach (var item in snapshot.Collections)
                if (ImGui.Selectable($"{item.Name}##{item.Id}", item.Id == collection))
                {
                    collection = item.Id;
                    if (manualMode && scope == ConversionScope.CurrentSelection) Invalidate(true);
                    if (enableOutput) { readingEnableSettings = true; _ = ReadEnableSettingsAsync(collection, outfit, ++enableGeneration); }
                }
            ImGui.EndCombo();
        }
    }

    private void DrawScope()
    {
        var all = scope == ConversionScope.AllOptions;
        if (ImGui.RadioButton("All outfit options", all)) { scope = ConversionScope.AllOptions; Invalidate(true); }
        ImGui.SameLine();
        if (ImGui.RadioButton("Current collection selection", !all)) { scope = ConversionScope.CurrentSelection; Invalidate(true); }
        Small(all ? "Retains option groups. Every included outfit model must use the appropriate body references."
            : "Uses the outfit's saved options, including inherited settings, captured when you analyze. Temporary overrides are excluded.");
    }

    private void DrawDestinationSizes()
    {
        Heading("2", "Destination sizes");
        if (analysis?.AutomaticPlan is not { } plan)
        {
            Wrap("Click Find destination sizes to match the outfit's source sizes and prepare the destination options.");
            return;
        }
        ImGui.TextColored(plan.Ready ? Accent : Warning,
            plan.Ready ? "Your new outfit will offer these size options:" : "Automatic conversion needs attention");
        foreach (var group in plan.DestinationGroups)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted(group.Name);
            Wrap(string.Join("  ·  ", group.Options));
            if (group.SourceSize.Length > 0) Small($"Matched source fit: {group.SourceSize}");
        }
        if (plan.SourceSizeGroups.Length > 0)
            Small("Replaces source size choices in: " + string.Join(", ", plan.SourceSizeGroups));
        if (plan.PreservedGroups.Length > 0)
            Small("Keeps outfit options: " + string.Join(", ", plan.PreservedGroups));
        foreach (var reason in plan.BlockingReasons)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Warning);
            Wrap(reason);
            ImGui.PopStyleColor();
        }
        if (plan.Ready) Small($"{plan.ModelJobs} fitted models will be created. Choose their sizes in Penumbra after conversion.");
        var notes = plan.Notes.Concat(analysis.Warnings).Distinct(StringComparer.Ordinal).ToArray();
        if (notes.Length > 0 && ImGui.TreeNode($"Conversion details and fit checks ({notes.Length})###AutomaticFitChecks"))
        {
            foreach (var note in notes) Wrap("• " + note);
            ImGui.TreePop();
        }
        if (analyzedRequest is null) Small("Settings changed. Find destination sizes again before creating the outfit.");
    }

    private void DrawModels()
    {
        Heading("2", "Inspect models and choose body references");
        if (analysis is null)
        {
            Wrap("Analyze the three mods to list their model files. Choose the source and target meshes for the same body region and shape variant.");
            return;
        }
        ImGui.TextColored(Muted, $"{analysis.SourceModels.Length} source  /  {analysis.TargetModels.Length} target  /  {analysis.OutfitModelCount} outfit models");
        if (ModelCombo("Source model", analysis.SourceModels, ref sourceModel, "Choose a source body mesh")) Invalidate(false);
        if (ModelCombo("Target model", analysis.TargetModels, ref targetModel, "Choose a target body mesh")) Invalidate(false);
        Small("Uses matching vertex correspondence when available, or compatible UV surfaces for different topology. UV fitting is approximate; body references must represent the same region and pose.");
        DrawAdditionalReferences();
        DrawOverrides();
        if (analyzedRequest is not null && analysis.BodiesCompatible.HasValue)
        {
            ImGui.TextColored(analysis.BodiesCompatible.Value ? Accent : Warning,
                analysis.BodiesCompatible.Value ? "Body reference compatibility passed" : "Body reference compatibility failed");
            if (analysis.CompatibilityMessage is { } message) Wrap(message);
        }
        else Small("After choosing or changing references, analyze again to check compatibility.");
        if (analysis.Warnings.Length > 0 && ImGui.TreeNode($"Analysis notes ({analysis.Warnings.Length})"))
        {
            foreach (var warning in analysis.Warnings) Wrap("• " + warning);
            ImGui.TreePop();
        }
        if (analyzedRequest is { Scope: ConversionScope.CurrentSelection } captured && ImGui.TreeNode("Captured outfit options"))
        {
            if (captured.SelectedOptions.Count == 0) Wrap("Default files only; no option groups selected.");
            foreach (var group in captured.SelectedOptions) Wrap($"{group.Key}: {string.Join(", ", group.Value)}");
            ImGui.TreePop();
        }
    }

    private void DrawOverrides()
    {
        if (analysis!.OutfitModels.Length == 0) return;
        Small("A single reference pair applies to all models unless overridden below. Map tops, legs, hands and different sizes to their own references.");
        if (!ImGui.TreeNode($"Per outfit model body references ({analysis.OutfitModels.Length})")) return;
        foreach (var candidate in analysis.OutfitModels)
        {
            ImGui.PushID(candidate.RelativePath);
            if (!overrides.TryGetValue(candidate.RelativePath, out var selection))
                overrides[candidate.RelativePath] = selection = new ModelOverride();
            if (ImGui.TreeNode(candidate.RelativePath))
            {
                foreach (var gamePath in candidate.GamePaths) Small(gamePath);
                if (ModelCombo("Source override", analysis.SourceModels, ref selection.Source, "Use global source", true)) Invalidate(false);
                if (ModelCombo("Target override", analysis.TargetModels, ref selection.Target, "Use global target", true)) Invalidate(false);
                ImGui.TreePop();
            }
            ImGui.PopID();
        }
        ImGui.TreePop();
    }

    private void DrawAdditionalReferences()
    {
        if (!ImGui.TreeNode("Additional body regions")) return;
        Small("Add matching pairs for other regions beneath the outfit, such as legs beneath a skirt or bodysuit. Use one pair per body part; a model's primary reference takes precedence for its own part.");
        var remove = -1;
        for (var i = 0; i < additionalReferences.Count; i++)
        {
            var pair = additionalReferences[i];
            ImGui.PushID(i);
            ImGui.TextUnformatted($"Region {i + 1}"); ImGui.SameLine();
            if (ImGui.SmallButton("Remove")) remove = i;
            if (ModelCombo("Source region", analysis!.SourceModels, ref pair.Source, "Choose source region")) Invalidate(false);
            if (ModelCombo("Target region", analysis!.TargetModels, ref pair.Target, "Choose target region")) Invalidate(false);
            ImGui.PopID();
        }
        if (remove >= 0) { additionalReferences.RemoveAt(remove); Invalidate(false); }
        if (ImGui.Button("Add body region")) { additionalReferences.Add(new ModelOverride()); Invalidate(false); }
        ImGui.TreePop();
    }

    private void DrawSettings()
    {
        Heading("3", "Create your converted outfit");
        ImGui.SetNextItemWidth(-150 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Output mod name", ref outputName, 200);
        ImGui.BeginDisabled(outfit.Length == 0 || snapshot!.Collections.Length == 0);
        if (ImGui.Checkbox("Enable the new outfit when finished", ref enableOutput))
        {
            if (enableOutput)
            {
                if (collection == Guid.Empty) collection = snapshot!.Collections[0].Id;
                readingEnableSettings = true;
                _ = ReadEnableSettingsAsync(collection, outfit, ++enableGeneration);
            }
            else ResetEnable();
        }
        ImGui.EndDisabled();
        if (enableOutput)
        {
            ImGui.Indent();
            DrawCollection();
            ImGui.BeginDisabled(readingEnableSettings);
            ImGui.SetNextItemWidth(130 * ImGuiHelpers.GlobalScale);
            ImGui.InputInt("Output priority", ref outputPriority);
            ImGui.EndDisabled();
            Small(readingEnableSettings ? "Reading the original outfit's priority..." :
                "Priority starts one above the original outfit. Choose the new outfit's size in Penumbra after conversion.");
            ImGui.Checkbox("Redraw my character after enabling", ref redraw);
            ImGui.Unindent();
        }
        else redraw = false;
        Small("Creates a separate mod in Penumbra. Your original outfit stays unchanged. Check the fit in game after conversion.");
    }

    private void DrawAdvancedConversion()
    {
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Advanced conversion settings")) return;
        if (ImGui.Checkbox("Choose body meshes manually", ref manualMode))
        {
            if (manualMode)
            {
                // Multiple automatic destinations must never silently become a
                // single manual conversion. Require an explicit choice instead.
                target = targets.Count == 1 ? targets[0] : "";
            }
            else if (targets.Count == 0 && target.Length > 0) targets.Add(target);
            scope = ConversionScope.AllOptions;
            Invalidate(true);
            SuggestOutputName();
        }
        Small(manualMode ? "Manual mode preserves the outfit's existing size options and requires individual body references."
            : "Source sizes are matched automatically. The output combines the selected bodies' size choices and keeps the outfit's other styles.");
        if (manualMode)
        {
            DrawScope();
            if (scope == ConversionScope.CurrentSelection && !enableOutput) DrawCollection();
        }
        ImGui.SetNextItemWidth(240 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("Deformation strength", ref strength, 0, 1, "%.2f")) Invalidate(false);
        Tip("Blend from original garment positions (0) to the full body deformation (1).");
        ImGui.SetNextItemWidth(240 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputFloat("Clothing clearance (model units)", ref clearance, 0.001f, 0.01f, "%.4f")) Invalidate(false);
        Tip("Adds space between clothing and the body. Skin matching the reference materials stays aligned without this offset. Model units are not calibrated millimetres. Inspect the result for clipping.");
        ImGui.SetNextItemWidth(240 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputFloat("Maximum influence distance", ref maxDistance, 0.01f, 0.1f, "%.3f")) Invalidate(false);
        Tip("Vertices farther than this from the source body are left unchanged. Uses native model units.");
        if (!float.IsFinite(clearance) || clearance is < 0 or > 0.1f || !float.IsFinite(maxDistance) || maxDistance is <= 0 or > 2)
            Small("Clearance must be 0–0.1 and maximum influence distance must be greater than 0 and at most 2 model units.");
    }

    private void DrawActions()
    {
        ImGui.Spacing();
        if (manualMode && additionalReferences.Any(pair => pair.Source.Length == 0 || pair.Target.Length == 0))
            Small("Choose both models for every additional body region, or remove the incomplete region, before analyzing.");
        var hasInputs = source.Length > 0 && (manualMode ? target.Length > 0 : targets.Count > 0) && outfit.Length > 0 &&
            (!manualMode || scope == ConversionScope.AllOptions || collection != Guid.Empty) &&
            (!manualMode || additionalReferences.All(pair => pair.Source.Length > 0 && pair.Target.Length > 0));
        ImGui.BeginDisabled(!hasInputs);
        if (ImGui.Button(manualMode ? analysis is null ? "Analyze mods" : "Analyze references" : "Find destination sizes", new Vector2(190, 32) * ImGuiHelpers.GlobalScale)) Start(false);
        ImGui.EndDisabled();
        ImGui.SameLine();
        var mode = manualMode ? ConversionMode.ManualReferences : ConversionMode.DestinationSizes;
        var compatible = manualMode ? analysis?.BodiesCompatible == true : analysis?.AutomaticPlan?.Ready == true;
        var canConvert = hasInputs && !readingEnableSettings && analyzedRequest?.Mode == mode && compatible &&
            !string.IsNullOrWhiteSpace(outputName) && float.IsFinite(clearance) && clearance is >= 0 and <= 0.1f &&
            float.IsFinite(strength) && strength is >= 0 and <= 1 &&
            float.IsFinite(maxDistance) && maxDistance is > 0 and <= 2 && (!enableOutput || collection != Guid.Empty);
        ImGui.BeginDisabled(!canConvert);
        if (ImGui.Button(manualMode ? "Convert and add to Penumbra" : "Create outfit", new Vector2(245, 32) * ImGuiHelpers.GlobalScale)) Start(true);
        ImGui.EndDisabled();
    }

    private bool DestinationMods()
    {
        var changed = false;
        var display = targets.Count switch
        {
            0 => "Choose destination bodies",
            1 => snapshot!.Mods.FirstOrDefault(m => m.Directory == targets[0])?.Name ?? targets[0],
            _ => $"{targets.Count} bodies selected",
        };
        ImGui.SetNextItemWidth(-150 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Destination bodies", display, ImGuiComboFlags.HeightLarge))
        {
            ImGui.PushID("DestinationBodies");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##filter", "Search names or directories...", ref targetFilter, 200);
            var matches = snapshot!.Mods.Where(m => m.Name.Contains(targetFilter, StringComparison.OrdinalIgnoreCase)
                || m.Directory.Contains(targetFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) ImGui.TextUnformatted("No matching mods.");
            foreach (var item in matches)
            {
                var selected = targets.Contains(item.Directory);
                // Checkboxes leave the combo open, so several bodies can be
                // selected without reopening it. Filtering never changes the list.
                if (ImGui.Checkbox($"{item.Name}##{item.Directory}", ref selected))
                {
                    if (selected) targets.Add(item.Directory);
                    else targets.Remove(item.Directory);
                    changed = true;
                }
                Tip(item.Directory);
            }
            ImGui.PopID();
            ImGui.EndCombo();
        }
        if (targets.Count == 0) return changed;
        ImGui.PushID("SelectedDestinationBodies");
        ImGui.TextColored(Muted, "Selected destinations");
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear")) { targets.Clear(); changed = true; }
        string? remove = null;
        foreach (var directory in targets)
        {
            ImGui.PushID(directory);
            if (ImGui.SmallButton("Remove")) remove = directory;
            ImGui.SameLine();
            Wrap(snapshot!.Mods.FirstOrDefault(m => m.Directory == directory)?.Name ?? directory);
            Tip(directory);
            ImGui.PopID();
        }
        if (remove is not null) { targets.Remove(remove); changed = true; }
        ImGui.PopID();
        return changed;
    }

    private bool ModCombo(string label, ref string selected, ref string filter)
    {
        ImGui.SetNextItemWidth(-150 * ImGuiHelpers.GlobalScale);
        var selectedDirectory = selected;
        var display = snapshot!.Mods.FirstOrDefault(m => m.Directory == selectedDirectory)?.Name ?? "Choose a mod";
        var changed = false;
        if (!ImGui.BeginCombo(label, display)) return false;
        ImGui.PushID(label);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "Search names or directories...", ref filter, 200);
        var query = filter;
        foreach (var item in snapshot.Mods.Where(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     m.Directory.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            if (ImGui.Selectable($"{item.Name}##{item.Directory}", item.Directory == selected))
            { selected = item.Directory; changed = true; }
            Tip(item.Directory);
        }
        ImGui.PopID(); ImGui.EndCombo();
        return changed;
    }

    private static bool ModelCombo(string label, ModelCandidate[] candidates, ref string selected, string empty, bool allowDefault = false)
    {
        ImGui.SetNextItemWidth(-150 * ImGuiHelpers.GlobalScale);
        var changed = false;
        if (!ImGui.BeginCombo(label, selected.Length == 0 ? empty : selected)) return false;
        if (allowDefault && ImGui.Selectable(empty, selected.Length == 0)) { selected = ""; changed = true; }
        foreach (var candidate in candidates)
        {
            if (ImGui.Selectable(candidate.RelativePath, candidate.RelativePath == selected))
            { selected = candidate.RelativePath; changed = true; }
            Tip($"{candidate.VertexCount:N0} vertices / {candidate.TriangleCount:N0} triangles\n{string.Join("\n", candidate.GamePaths)}");
        }
        ImGui.EndCombo();
        return changed;
    }

    private void Invalidate(bool clearModels)
    {
        analyzedRequest = null;
        error = null;
        if (!clearModels) return;
        analysis = null; discoveredOptions = null; sourceModel = ""; targetModel = ""; overrides.Clear(); additionalReferences.Clear();
    }

    private void Refresh()
    {
        refreshing = true; error = null;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var fresh = await penumbra.ReadAsync();
            Queue(() =>
            {
                snapshot = fresh;
                if (!fresh.Mods.Any(m => m.Directory == source)) source = "";
                var destinationsChanged = targets.RemoveAll(directory => !fresh.Mods.Any(m => m.Directory == directory)) > 0;
                if (!fresh.Mods.Any(m => m.Directory == target))
                {
                    destinationsChanged |= target.Length > 0;
                    target = "";
                }
                if (!fresh.Mods.Any(m => m.Directory == outfit)) outfit = "";
                if (!fresh.Collections.Any(c => c.Id == collection)) collection = fresh.CurrentCollection ?? Guid.Empty;
                Invalidate(true);
                if (destinationsChanged) SuggestOutputName();
                ResetEnable();
                status = "Choose your source body, destination bodies and outfit, then find the destination sizes.";
            });
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not connect to Penumbra");
            Queue(() => { snapshot = null; error = $"Penumbra is unavailable or incompatible. Enable Penumbra, then refresh. {ex.Message}"; });
        }
        finally { Queue(() => refreshing = false); }
    }

    private void Start(bool convert)
    {
        if (snapshot is null || busy) return;
        try
        {
            string[] selectedTargets = manualMode ? target.Length > 0 ? [target] : [] : targets.ToArray();
            if (source.Length == 0 || outfit.Length == 0 || selectedTargets.Length == 0)
                throw new InvalidOperationException(manualMode
                    ? "Choose a source body, one destination body and an outfit before analyzing."
                    : "Choose a source body, at least one destination body and an outfit before finding sizes.");
            var mappings = overrides.Where(p => p.Value.Source.Length > 0 || p.Value.Target.Length > 0)
                .Select(p => new ModelConversionMapping(p.Key,
                    p.Value.Source.Length > 0 ? p.Value.Source : sourceModel,
                    p.Value.Target.Length > 0 ? p.Value.Target : targetModel)).ToList();
            var request = new WorkerRequest
            {
                Operation = convert ? WorkerOperation.Convert : WorkerOperation.Analyze,
                Mode = manualMode ? ConversionMode.ManualReferences : ConversionMode.DestinationSizes,
                SourceModPath = PenumbraBridge.InstalledPath(snapshot.Root, source),
                TargetModPath = PenumbraBridge.InstalledPath(snapshot.Root, selectedTargets[0]),
                TargetModPaths = manualMode ? [] : selectedTargets.Select(directory => PenumbraBridge.InstalledPath(snapshot.Root, directory)).ToList(),
                OutfitModPath = PenumbraBridge.InstalledPath(snapshot.Root, outfit),
                SourceModelPath = !manualMode || string.IsNullOrEmpty(sourceModel) ? null : sourceModel,
                TargetModelPath = !manualMode || string.IsNullOrEmpty(targetModel) ? null : targetModel,
                ModelMappings = manualMode ? mappings : [],
                AdditionalBodyReferences = manualMode ? additionalReferences.Select(pair => new BodyReferencePair(pair.Source, pair.Target)).ToList() : [],
                OutputRoot = snapshot.Root,
                OutputName = outputName.Trim(),
                Scope = manualMode ? scope : ConversionScope.AllOptions,
                SelectedOptions = convert ? analyzedRequest!.SelectedOptions : new(),
                Strength = strength,
                Clearance = clearance,
                MaxDistance = maxDistance,
            };
            // A failed or cancelled fresh analysis must never retain a previous successful validation.
            if (!convert) analyzedRequest = null;
            job = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            busy = true; progress = null; error = null; result = null; installStatus = "";
            status = convert ? "Creating your converted outfit..." : manualMode ? "Analyzing installed mods..." : "Matching source fits and destination sizes...";
            _ = RunAsync(request, outfit, collection, enableOutput, redraw, outputPriority,
                configuration.WorkerPath, configuration.DotnetPath, discoveredOptions, job);
        }
        catch (Exception ex) { error = ex.Message; }
    }

    private async Task RunAsync(WorkerRequest request, string outfitDirectory, Guid selectedCollection,
        bool shouldEnable, bool shouldRedraw, int priority, string workerPath, string dotnetPath,
        Dictionary<string, List<string>>? previousOptions, CancellationTokenSource currentJob)
    {
        var cancellation = currentJob.Token;
        var optionsChanged = false;
        try
        {
            // Resolve all Penumbra information on the framework before starting any file processing.
            var fresh = await penumbra.ReadAsync();
            if (!ModPath.PathsEqual(fresh.Root, request.OutputRoot))
                throw new InvalidOperationException("Penumbra's mod directory changed. Refresh the mod list and analyze again.");
            if (request.Scope == ConversionScope.CurrentSelection && request.Operation == WorkerOperation.Analyze)
            {
                var selected = await penumbra.ReadOptionsAsync(selectedCollection, outfitDirectory);
                optionsChanged = previousOptions is not null && !SameOptions(previousOptions, selected);
                request = request with { SelectedOptions = selected };
                if (optionsChanged)
                    request = request with { SourceModelPath = null, TargetModelPath = null, ModelMappings = [], AdditionalBodyReferences = [] };
            }
            cancellation.ThrowIfCancellationRequested();
            var preservedOptions = request.Operation == WorkerOperation.Convert && shouldEnable && request.Scope == ConversionScope.AllOptions
                ? (await penumbra.ReadSettingsAsync(selectedCollection, outfitDirectory))?.Options : null;
            var response = await worker.RunAsync(request, workerPath, dotnetPath,
                value => Queue(() => progress = value), cancellation);
            if (request.Operation == WorkerOperation.Analyze)
            {
                var captured = request;
                Queue(() =>
                {
                    if (optionsChanged)
                    {
                        sourceModel = ""; targetModel = ""; overrides.Clear(); additionalReferences.Clear();
                    }
                    analysis = response; analyzedRequest = captured;
                    discoveredOptions = captured.Scope == ConversionScope.CurrentSelection ? captured.SelectedOptions : null;
                    status = captured.Mode == ConversionMode.DestinationSizes
                        ? response.AutomaticPlan?.Ready == true ? "Destination sizes are ready. Create your converted outfit when ready."
                            : "Automatic conversion needs attention. See the destination size details above."
                        : optionsChanged ? "The outfit's selected options changed. Model files were refreshed; choose and analyze references for this selection."
                        : response.BodiesCompatible == true ? "References checked. Ready to convert."
                        : response.BodiesCompatible == false ? "These reference meshes cannot be converted together. Review compatibility details."
                        : "Model files found. Choose body references, then analyze again.";
                });
            }
            else
            {
                Queue(() => { result = response; status = "Conversion complete. Registering the new mod in Penumbra..."; });
                if (response.OutputDirectory is null) throw new InvalidDataException("Worker did not return a converted mod directory.");
                await penumbra.RegisterAsync(response.OutputDirectory, request.OutputRoot, cancellation);
                Queue(() => installStatus = "Registered in Penumbra.");
                if (shouldEnable)
                {
                    // Current-selection output is baked by the worker and has no option groups to set.
                    if (request.Mode == ConversionMode.DestinationSizes && preservedOptions is not null)
                    {
                        // Source size names cannot be applied to newly generated destination size groups.
                        var retainedGroups = response.PreservedOptionGroups.ToHashSet(StringComparer.Ordinal);
                        preservedOptions = preservedOptions.Where(group => retainedGroups.Contains(group.Key))
                            .ToDictionary(group => group.Key, group => group.Value, StringComparer.Ordinal);
                    }
                    await penumbra.EnableAsync(selectedCollection, response.OutputDirectory, request.OutputRoot,
                        priority, shouldRedraw, preservedOptions, cancellation);
                    Queue(() => installStatus = "Registered and enabled in the selected collection.");
                }
                Queue(() => status = request.Mode == ConversionMode.DestinationSizes
                    ? "Your converted outfit is ready. Choose its destination size in Penumbra, then check the fit in game."
                    : "Conversion complete. Inspect the fit in game, including movement and outfit options.");
            }
        }
        catch (OperationCanceledException)
        {
            Queue(() => status = result?.OutputDirectory is not null
                ? $"Job cancelled after conversion. Output was saved at the path below. {(installStatus.Length > 0 ? installStatus : "Penumbra registration was not completed; add the saved mod manually.")}"
                : "Job cancelled. No completed output was reported by the worker.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Outfit Studio job failed");
            Queue(() => { error = ex.Message; status = result is null ? "Job stopped." : "Conversion files were saved; Penumbra setup needs attention."; });
        }
        finally
        {
            if (disposed) currentJob.Dispose();
            else Queue(() => { if (ReferenceEquals(job, currentJob)) job = null; busy = false; currentJob.Dispose(); });
        }
    }

    private void DrawResult()
    {
        if (result is null) return;
        ImGui.Spacing();
        ImGui.TextColored(Accent, $"{result.ConvertedModelCount} models  /  {result.ConvertedVertexCount:N0} vertices converted");
        if (installStatus.Length > 0) Wrap(installStatus);
        if (result.OutputDirectory is { } directory)
        {
            Wrap(directory);
            if (ImGui.Button("Copy output path")) ImGui.SetClipboardText(directory);
        }
        if (result.Warnings.Length > 0 && ImGui.TreeNode($"Fit notes ({result.Warnings.Length})"))
        {
            foreach (var warning in result.Warnings) Wrap("• " + warning);
            ImGui.TreePop();
        }
    }

    private void ResetEnable()
    {
        enableOutput = false; redraw = false; readingEnableSettings = false; enableGeneration++;
    }

    private static bool SameOptions(Dictionary<string, List<string>> first, Dictionary<string, List<string>> second)
        => first.Count == second.Count && first.All(group => second.TryGetValue(group.Key, out var selected)
            && group.Value.ToHashSet(StringComparer.Ordinal).SetEquals(selected));

    private async Task ReadEnableSettingsAsync(Guid selectedCollection, string selectedOutfit, int generation)
    {
        try
        {
            var settings = await penumbra.ReadSettingsAsync(selectedCollection, selectedOutfit);
            Queue(() =>
            {
                if (generation != enableGeneration) return;
                var original = settings?.Priority ?? 0;
                outputPriority = original < int.MaxValue ? original + 1 : int.MaxValue;
                readingEnableSettings = false;
            });
        }
        catch (Exception ex)
        {
            Queue(() =>
            {
                if (generation != enableGeneration) return;
                ResetEnable(); error = $"Could not read the outfit's collection settings: {ex.Message}";
            });
        }
    }

    private void DrawAdvanced()
    {
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Advanced / worker location")) return;
        ImGui.BeginDisabled(busy);
        var path = configuration.WorkerPath;
        if (ImGui.InputText("Worker override", ref path, 2048)) { configuration.WorkerPath = path; save(); }
        Small("Leave blank to use the bundled worker: " + worker.BundledPath);
        var dotnet = configuration.DotnetPath;
        if (ImGui.InputText("dotnet host (DLL only)", ref dotnet, 2048)) { configuration.DotnetPath = dotnet; save(); }
        Small("An override can point to a worker .exe or .dll. The dotnet host is needed only for a framework-dependent DLL.");
        ImGui.EndDisabled();
    }

    private void Queue(Action action) { if (!disposed) updates.Enqueue(action); }
    private static void Heading(string number, string title)
    { ImGui.Spacing(); ImGui.Separator(); ImGui.TextColored(Accent, number); ImGui.SameLine(); ImGui.TextUnformatted(title); ImGui.Spacing(); }
    private static void Wrap(string text) { ImGui.PushTextWrapPos(0); ImGui.TextUnformatted(text); ImGui.PopTextWrapPos(); }
    private static void Small(string text) { ImGui.PushStyleColor(ImGuiCol.Text, Muted); Wrap(text); ImGui.PopStyleColor(); }
    private static void Tip(string text)
    {
        if (!ImGui.IsItemHovered()) return;
        ImGui.BeginTooltip(); ImGui.PushTextWrapPos(440 * ImGuiHelpers.GlobalScale); ImGui.TextUnformatted(text); ImGui.PopTextWrapPos(); ImGui.EndTooltip();
    }

    public void Dispose()
    {
        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
