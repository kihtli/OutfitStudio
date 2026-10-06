using System.Security.Cryptography;
using System.Text.Json;
using OutfitStudio.Core.Geometry;
using OutfitStudio.Core.Models;
using OutfitStudio.Core.Mods;
using OutfitStudio.Core.Protocol;

namespace OutfitStudio.Core.Services;

public static class ConversionService
{
    public static async Task<WorkerResponse> ExecuteAsync(WorkerRequest request,
        IProgress<WorkerProgress>? progress = null, CancellationToken cancellation = default)
    {
        Validate(request);
        cancellation.ThrowIfCancellationRequested();
        var targetPaths = TargetPaths(request);
        progress?.Report(new("Read", 0, targetPaths.Count + 2, "Reading source body mod"));
        using var source = PenumbraMod.Open(request.SourceModPath, cancellationToken: cancellation);
        using var targets = new OpenedTargets(targetPaths, progress, cancellation);
        progress?.Report(new("Read", targetPaths.Count + 1, targetPaths.Count + 2, "Reading outfit mod"));
        using var outfit = PenumbraMod.Open(request.OutfitModPath, cancellationToken: cancellation);
        if (request.Mode == ConversionMode.DestinationSizes)
            return await AutomaticConversionService.ExecuteAsync(request, source, targets.Mods, outfit, progress, cancellation,
                requestedTargetPaths: targetPaths);
        return await ExecuteManualAsync(request, source, targets.Mods[0], outfit, progress, cancellation, targetPaths[0]);
    }

    private static async Task<WorkerResponse> ExecuteManualAsync(WorkerRequest request, PenumbraMod source,
        PenumbraMod target, PenumbraMod outfit, IProgress<WorkerProgress>? progress, CancellationToken cancellation,
        string requestedTargetPath)
    {
        var scope = request.Scope == ConversionScope.AllOptions ? ModConversionScope.AllOptions : ModConversionScope.SelectedOptions;
        var sourceFiles = source.GetModelFiles();
        var targetFiles = target.GetModelFiles();
        var outfitFiles = outfit.GetModelFiles(scope, request.SelectedOptions);
        if (sourceFiles.Count == 0 || targetFiles.Count == 0)
            throw new InvalidDataException("Source and target mods must both contain body .mdl files. Select the body mod rather than a textures or skeleton mod.");
        if (outfitFiles.Count == 0)
            throw new InvalidDataException("The selected outfit configuration has no .mdl files to convert.");

        var warnings = new List<string>
        {
            "Fitting uses body geometry in its stored bind pose. Unsupported YAB/IVCS weight influences are adapted to the destination body; existing game skirt bones, materials and textures are retained. Target physics and Customize+ scaling are not transferred.",
            "Clearance is a local surface offset, not a collision simulation. Check the result in movement and at equipment seams before using it.",
        };
        if (request.Scope == ConversionScope.AllOptions && outfitFiles.Count > 1)
            warnings.Add("Each outfit size needs its matching source body variant. Set per-model references for differing sizes and equipment parts; the default pair applies to every other model.");

        var settings = new ConversionSettings { Strength = request.Strength, Clearance = request.Clearance, MaximumDistance = request.MaxDistance };
        var sourceCandidates = Inspect(sourceFiles, "Source", warnings, cancellation);
        var targetCandidates = Inspect(targetFiles, "Target", warnings, cancellation);
        var outfitCandidates = Inspect(outfitFiles, "Outfit", warnings, cancellation);
        var baseResponse = new WorkerResponse
        {
            Success = true,
            SourceModels = sourceCandidates,
            TargetModels = targetCandidates,
            OutfitModels = outfitCandidates,
            OutfitModelCount = outfitFiles.Count,
        };
        bool hasDefault = !string.IsNullOrWhiteSpace(request.SourceModelPath) && !string.IsNullOrWhiteSpace(request.TargetModelPath);
        if (!hasDefault && request.ModelMappings.Count == 0)
        {
            if (request.Operation == WorkerOperation.Convert)
                throw new InvalidDataException("Choose explicit source and target body model references first.");
            return baseResponse with { Warnings = warnings.ToArray() };
        }

        var overrides = new Dictionary<string, ModelConversionMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in request.ModelMappings)
        {
            string key = Normalize(mapping.OutfitModelPath);
            if (!outfitFiles.Any(m => Normalize(m.RelativePath).Equals(key, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Unknown outfit model override: {mapping.OutfitModelPath}");
            if (!overrides.TryAdd(key, mapping))
                throw new InvalidDataException($"Duplicate outfit model override: {mapping.OutfitModelPath}");
        }

        var prepared = new Dictionary<(string Source, string Target), PreparedConversion>();
        var modelPairs = new Dictionary<string, (ModModelFile Source, ModModelFile Target)>();
        var reports = new List<object>();
        string? failure = null;
        foreach (var file in outfitFiles)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                overrides.TryGetValue(Normalize(file.RelativePath), out var mapping);
                var sourceFile = Find(sourceFiles, mapping?.SourceModelPath ?? request.SourceModelPath, "source");
                var targetFile = Find(targetFiles, mapping?.TargetModelPath ?? request.TargetModelPath, "target");
                ValidateRegion(sourceFile, targetFile, file);
                var key = (sourceFile.RelativePath, targetFile.RelativePath);
                modelPairs.Add(file.RelativePath, (sourceFile, targetFile));
                if (prepared.ContainsKey(key)) continue;
                progress?.Report(new("Analyze", prepared.Count, outfitFiles.Count, $"Mapping {sourceFile.RelativePath} → {targetFile.RelativePath}"));
                var sourceBytes = await ReadModel(sourceFile.FullPath, cancellation);
                var targetBytes = await ReadModel(targetFile.FullPath, cancellation);
                var pair = MdlConverter.Prepare(sourceBytes, targetBytes, settings, cancellation);
                prepared.Add(key, pair);
                warnings.AddRange(pair.Warnings.Select(w => $"{targetFile.RelativePath}: {w}"));
                reports.Add(new
                {
                    SourceModel = sourceFile.RelativePath, TargetModel = targetFile.RelativePath,
                    TargetModIndex = 0, TargetModName = target.Name, TargetModPath = requestedTargetPath,
                    SourceSha256 = Convert.ToHexString(SHA256.HashData(sourceBytes)),
                    TargetSha256 = Convert.ToHexString(SHA256.HashData(targetBytes)), pair.Method,
                });
            }
            catch (Exception e) when (e is ModelConversionException or InvalidDataException)
            {
                failure = $"{file.RelativePath}: {e.Message}";
                break;
            }
        }
        if (failure is not null)
        {
            if (request.Operation == WorkerOperation.Convert) throw new InvalidDataException(failure);
            return baseResponse with { BodiesCompatible = false, CompatibilityMessage = failure, Warnings = warnings.Distinct().ToArray() };
        }
        var supplemental = new List<(ModModelFile Source, ModModelFile Target)>();
        var supplementalRegions = new HashSet<string>();
        try
        {
            foreach (var reference in request.AdditionalBodyReferences)
            {
                cancellation.ThrowIfCancellationRequested();
                var sourceFile = Find(sourceFiles, reference.SourceModelPath, "additional source");
                var targetFile = Find(targetFiles, reference.TargetModelPath, "additional target");
                ValidateRegion(sourceFile, targetFile, sourceFile);
                var regions = Regions(sourceFile);
                if (regions.Count != 1 || !supplementalRegions.Add(regions.Single()))
                    throw new InvalidDataException("Choose only one additional reference pair for each body part (top, legs, hands or feet).");
                supplemental.Add((sourceFile, targetFile));
                var key = (sourceFile.RelativePath, targetFile.RelativePath);
                if (prepared.ContainsKey(key)) continue;
                progress?.Report(new("Analyze", prepared.Count, outfitFiles.Count + request.AdditionalBodyReferences.Count,
                    $"Mapping additional region {sourceFile.RelativePath}"));
                var sourceBytes = await ReadModel(sourceFile.FullPath, cancellation);
                var targetBytes = await ReadModel(targetFile.FullPath, cancellation);
                var pair = MdlConverter.Prepare(sourceBytes, targetBytes, settings, cancellation);
                prepared.Add(key, pair);
                warnings.AddRange(pair.Warnings.Select(w => $"{targetFile.RelativePath}: {w}"));
                reports.Add(new
                {
                    SourceModel = sourceFile.RelativePath, TargetModel = targetFile.RelativePath,
                    TargetModIndex = 0, TargetModName = target.Name, TargetModPath = requestedTargetPath,
                    SourceSha256 = Convert.ToHexString(SHA256.HashData(sourceBytes)),
                    TargetSha256 = Convert.ToHexString(SHA256.HashData(targetBytes)), pair.Method,
                });
            }
        }
        catch (Exception e) when (e is ModelConversionException or InvalidDataException)
        {
            if (request.Operation == WorkerOperation.Convert) throw;
            return baseResponse with { BodiesCompatible = false, CompatibilityMessage = e.Message, Warnings = warnings.Distinct().ToArray() };
        }
        var modelConverters = modelPairs.ToDictionary(p => p.Key, p =>
        {
            var primary = prepared[(p.Value.Source.RelativePath, p.Value.Target.RelativePath)];
            var regions = Regions(p.Value.Source);
            var extras = supplemental.Where(s => !Regions(s.Source).Overlaps(regions))
                .Select(s => prepared[(s.Source.RelativePath, s.Target.RelativePath)]);
            return MdlConverter.Combine(new[] { primary }.Concat(extras));
        });
        if (outfitCandidates.Length != outfitFiles.Count)
        {
            const string error = "Some outfit models have unsupported layouts. Check the analysis warnings; no conversion will be created.";
            if (request.Operation == WorkerOperation.Convert) throw new InvalidDataException(error);
            return baseResponse with { BodiesCompatible = false, CompatibilityMessage = error, Warnings = warnings.Distinct().ToArray() };
        }
        var compatibility = $"{prepared.Count} body reference pair(s) mapped using {string.Join(", ", prepared.Values.Select(p => p.Method).Distinct())}. Visual fit still needs inspection.";
        if (request.Operation == WorkerOperation.Analyze)
            return baseResponse with { BodiesCompatible = true, CompatibilityMessage = compatibility, Warnings = warnings.Distinct().ToArray() };

        ValidateOutputRoot(request.OutputRoot, source.RootPath, target.RootPath, outfit.RootPath);
        int models = 0, vertices = 0;
        var modelReports = new List<object>();
        var output = await outfit.CloneAndConvertAsync(request.OutputRoot, request.OutputName,
            async (file, destination, ct) =>
            {
                progress?.Report(new("Convert", models, outfitFiles.Count, file.RelativePath));
                var (sourceFile, targetFile) = modelPairs[file.RelativePath];
                var converter = modelConverters[file.RelativePath];
                var input = await ReadModel(file.FullPath, ct);
                var converted = converter.Convert(input, ct);
                // Reparse the entire output before it can enter a Penumbra mod.
                var before = MdlConverter.Inspect(input);
                var after = MdlConverter.Inspect(converted.ModelData);
                if (before.VertexCount != after.VertexCount || before.TriangleCount != after.TriangleCount ||
                    before.ShapeCount != after.ShapeCount || !before.Materials.SequenceEqual(after.Materials))
                    throw new InvalidDataException($"Conversion changed the structure of {file.RelativePath}.");
                if (converted.ConvertedVertices == 0)
                    warnings.Add($"{file.RelativePath}: no vertices moved with the selected reference pair and strength.");
                await File.WriteAllBytesAsync(destination, converted.ModelData, ct);
                models++;
                vertices += converted.ConvertedVertices;
                warnings.AddRange(converted.Warnings.Select(w => $"{file.RelativePath}: {w}"));
                modelReports.Add(new
                {
                    Model = file.RelativePath, SourceReference = sourceFile.RelativePath, TargetReference = targetFile.RelativePath,
                    TargetModIndex = 0, TargetModName = target.Name, TargetModPath = requestedTargetPath,
                    AdditionalReferences = supplemental.Where(s => !Regions(s.Source).Overlaps(Regions(sourceFile)))
                        .Select(s => new { SourceModel = s.Source.RelativePath, TargetModel = s.Target.RelativePath,
                            TargetModIndex = 0, TargetModName = target.Name, TargetModPath = requestedTargetPath }).ToArray(),
                    InputSha256 = Convert.ToHexString(SHA256.HashData(input)),
                    OutputSha256 = Convert.ToHexString(SHA256.HashData(converted.ModelData)),
                    converted.ConvertedVertices, converted.UnchangedVertices, converted.CorrespondenceMethod,
                });
            }, scope, request.SelectedOptions, cancellation);
        var report = new
        {
            FormatVersion = 1, CreatedUtc = DateTimeOffset.UtcNow,
            SourceMod = source.Name, TargetMod = target.Name, OutfitMod = outfit.Name,
            TargetMods = TargetProvenance([target], [requestedTargetPath]),
            request.Scope, request.Strength, request.Clearance, request.MaxDistance,
            ReferencePairs = reports, Models = modelReports, Warnings = warnings.Distinct().ToArray(),
        };
        // Committed output remains usable even when optional diagnostics cannot be written.
        try { await File.WriteAllTextAsync(Path.Combine(output, "outfitstudio-report.json"), JsonSerializer.Serialize(report, WorkerProtocol.Json)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add($"Could not save conversion report: {e.Message}"); }
        progress?.Report(new("Complete", models, outfitFiles.Count, output));
        return baseResponse with
        {
            OutputDirectory = output, ConvertedModelCount = models, ConvertedVertexCount = vertices,
            BodiesCompatible = true, CompatibilityMessage = compatibility, Warnings = warnings.Distinct().ToArray(),
        };
    }

    private static ModelCandidate[] Inspect(IReadOnlyList<ModModelFile> files, string label, List<string> warnings, CancellationToken ct)
    {
        var candidates = new List<ModelCandidate>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(file.FullPath).Length > 256L * 1024 * 1024) throw new InvalidDataException("Model exceeds 256 MiB.");
                var inspection = MdlConverter.Inspect(File.ReadAllBytes(file.FullPath));
                candidates.Add(new(file.RelativePath, file.GamePaths.ToArray(), inspection.VertexCount, inspection.TriangleCount));
            }
            catch (Exception e) when (e is ModelConversionException or InvalidDataException)
            { warnings.Add($"{label} model {file.RelativePath}: {e.Message}"); }
        }
        return candidates.ToArray();
    }

    private static ModModelFile Find(IReadOnlyList<ModModelFile> files, string? path, string kind)
        => files.FirstOrDefault(m => Normalize(m.RelativePath).Equals(Normalize(path ?? ""), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Choose a referenced {kind} body model; '{path}' is not in that mod.");

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static void ValidateRegion(ModModelFile source, ModModelFile target, ModModelFile outfit)
    {
        var a = Regions(source); var b = Regions(target); var c = Regions(outfit);
        if (a.Count > 0 && b.Count > 0 && !a.SetEquals(b))
            throw new InvalidDataException("Source and target references replace different body parts. Pair tops with tops and legs with legs.");
        if (a.Count > 0 && c.Count > 0 && !c.IsSubsetOf(a))
            throw new InvalidDataException("The outfit model replaces a different body part than the reference. Set a per-model source and target for this part.");
    }

    private static HashSet<string> Regions(ModModelFile m) => m.GamePaths.Select(p => Path.GetFileNameWithoutExtension(p).Split('_').Last())
        .Where(s => s is "top" or "dwn" or "glv" or "sho").ToHashSet();

    private static async Task<byte[]> ReadModel(string path, CancellationToken ct)
    {
        if (new FileInfo(path).Length > 256L * 1024 * 1024) throw new InvalidDataException("Model exceeds 256 MiB.");
        return await File.ReadAllBytesAsync(path, ct);
    }

    internal static void ValidateOutputRoot(string root, params string[] inputs)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new InvalidDataException("Choose an absolute output directory.");
        var full = Path.TrimEndingDirectorySeparator(SafeModFiles.ResolveAnchor(root));
        foreach (var input in inputs.Where(Directory.Exists))
        {
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
            if (SafeModFiles.IsInside(parent, full))
                throw new InvalidDataException("Output cannot be inside a source, target or outfit mod.");
        }
    }

    private static void Validate(WorkerRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.TargetModPaths is null || r.SelectedOptions is null || r.ModelMappings is null || r.AdditionalBodyReferences is null)
            throw new InvalidDataException("Destination mods, options and model mappings cannot be null.");
        if (r.ModelMappings.Any(mapping => mapping is null) || r.AdditionalBodyReferences.Any(reference => reference is null))
            throw new InvalidDataException("Model mappings and body references cannot contain null entries.");
        bool legacy = r.ProtocolVersion == 1 && r.TargetModPaths.Count == 0
            && r.AdditionalBodyReferences.All(reference => reference.TargetModIndex == 0);
        if (r.ProtocolVersion != WorkerProtocol.Version && !legacy)
            throw new InvalidDataException("Worker protocol version mismatch. Multiple destination mods require protocol version 2.");
        if (!Enum.IsDefined(r.Operation) || !Enum.IsDefined(r.Scope) || !Enum.IsDefined(r.Mode)) throw new InvalidDataException("Unknown worker operation or conversion mode.");
        if (string.IsNullOrWhiteSpace(r.SourceModPath) || string.IsNullOrWhiteSpace(r.OutfitModPath)
            || (r.TargetModPaths.Count == 0 && string.IsNullOrWhiteSpace(r.TargetModPath)))
            throw new InvalidDataException("Choose source body, target body and outfit mods.");
        if (r.TargetModPaths.Count > 32 || r.TargetModPaths.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Choose between 1 and 32 nonempty destination mod paths.");
        int targetCount = Math.Max(1, r.TargetModPaths.Count);
        if (r.Mode == ConversionMode.ManualReferences && targetCount != 1)
            throw new InvalidDataException("Manual references supports one destination mod. Use destination sizes for multiple destination mods.");
        if (r.AdditionalBodyReferences.Any(reference => reference.TargetModIndex < 0 || reference.TargetModIndex >= targetCount))
            throw new InvalidDataException("A body reference names a destination mod index outside the selected list.");
        if (!float.IsFinite(r.Strength) || r.Strength is < 0 or > 1 || !float.IsFinite(r.Clearance) || r.Clearance is < 0 or > 0.1f ||
            !float.IsFinite(r.MaxDistance) || r.MaxDistance is <= 0 or > 2)
            throw new InvalidDataException("Strength must be 0–1, clearance 0–0.1 and maximum distance greater than 0 and at most 2 model units.");
    }

    internal static IReadOnlyList<string> TargetPaths(WorkerRequest request)
        => request.TargetModPaths.Count == 0 ? [request.TargetModPath] : request.TargetModPaths.ToArray();

    internal sealed record DestinationProvenance(int Index, string Name, string RequestedPath, string RootPath);

    internal static DestinationProvenance[] TargetProvenance(IReadOnlyList<PenumbraMod> targets, IReadOnlyList<string> paths)
        => targets.Select((mod, index) => new DestinationProvenance(index, mod.Name, paths[index], mod.RootPath)).ToArray();

    private sealed class OpenedTargets : IDisposable
    {
        public List<PenumbraMod> Mods { get; } = [];

        public OpenedTargets(IReadOnlyList<string> paths, IProgress<WorkerProgress>? progress, CancellationToken ct)
        {
            // Resolve before extraction, so aliases of a directory or PMP cannot be
            // selected twice merely because their spelling or extraction roots differ.
            var roots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                if (!roots.Add(SafeModFiles.ResolveAnchor(path)))
                    throw new InvalidDataException($"The same destination mod was selected more than once: {path}");
            }
            try
            {
                foreach (var path in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new("Read", Mods.Count + 1, paths.Count + 2, $"Reading destination body mod {Mods.Count + 1} of {paths.Count}"));
                    Mods.Add(PenumbraMod.Open(path, cancellationToken: ct));
                }
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            foreach (var mod in Mods.AsEnumerable().Reverse()) mod.Dispose();
            Mods.Clear();
        }
    }
}
