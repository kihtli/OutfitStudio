using System.Text.Json;
using System.Text.Json.Nodes;

namespace OutfitStudio.Core.Mods;

/// <summary>
/// A read-only installed Penumbra mod, or an owned temporary extraction of a PMP ZIP.
/// Format reference: https://github.com/xivdev/Penumbra/tree/testing/schemas
/// </summary>
public sealed partial class PenumbraMod : IDisposable
{
    private readonly string? temporaryDirectory;
    private readonly ModReadLimits limits;
    private readonly JsonObject metadata;
    private readonly JsonObject defaultData;
    private readonly List<JsonObject> groupData;
    private readonly Dictionary<string, string> fileInventory;
    private readonly Dictionary<string, string> originalDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> groupDocuments = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    public string RootPath { get; }
    public string Name { get; }
    public int FileVersion { get; }
    public IReadOnlyList<ModGroupInfo> Groups { get; }

    /// <summary>An independent snapshot; modifying it never changes the loaded source mod.</summary>
    public JsonObject GetDefaultDataSnapshot()
    {
        ThrowIfDisposed();
        return (JsonObject)defaultData.DeepClone();
    }

    /// <summary>Independent group snapshots in Penumbra's original group order.</summary>
    public IReadOnlyList<JsonObject> GetGroupSnapshots()
    {
        ThrowIfDisposed();
        return groupData.Select(group => (JsonObject)group.DeepClone()).ToArray();
    }

    private PenumbraMod(string rootPath, string? temporaryDirectory, ModReadLimits limits)
    {
        RootPath = rootPath;
        this.temporaryDirectory = temporaryDirectory;
        this.limits = limits;
        fileInventory = SafeModFiles.Enumerate(rootPath, limits).ToDictionary(p => p, p => p, StringComparer.OrdinalIgnoreCase);
        metadata = ReadDocument("meta.json");
        FileVersion = Integer(metadata, "FileVersion", -1);
        if (FileVersion is not (3 or 4))
            throw new NotSupportedException($"Penumbra metadata version {FileVersion} is not supported. Import and re-save older mods in Penumbra first.");
        Name = Text(metadata, "Name");
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidDataException("The mod metadata has no name.");

        if (FileVersion == 4)
        {
            defaultData = OptionalObject(metadata, "DefaultData");
            groupData = ObjectArray(metadata, "Groups");
        }
        else
        {
            defaultData = fileInventory.ContainsKey("default_mod.json") ? ReadDocument("default_mod.json") : new JsonObject();
            groupData = [];
            foreach (var path in fileInventory.Values.Where(p => !p.Contains('/')
                         && p.StartsWith("group_", StringComparison.OrdinalIgnoreCase)
                         && p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
            {
                groupDocuments.Add(path);
                groupData.Add(ReadDocument(path));
            }
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        Groups = groupData.Select(group =>
        {
            var name = Text(group, "Name");
            if (!names.Add(name))
                throw new InvalidDataException($"Duplicate option group name: {name}");
            return new ModGroupInfo(name, Text(group, "Type"), Integer(group, "Priority"), Unsigned(group, "DefaultSettings"),
                ObjectArray(group, "Options").Select(option => new ModOptionInfo(Text(option, "Name"), Integer(option, "Priority"))).ToArray());
        }).ToArray();

        // Validate every redirection before any output is created, including unselected options.
        foreach (var container in AllContainers())
            foreach (var (_, path) in StringMap(container, "Files"))
                _ = ResolveFile(path);
    }

    public static PenumbraMod Open(string directoryOrPmp, ModReadLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryOrPmp);
        limits ??= new ModReadLimits();
        if (limits.MaxFiles <= 0 || limits.MaxFileBytes <= 0 || limits.MaxTotalBytes <= 0 || limits.MaxMetadataBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
        var input = SafeModFiles.ResolveAnchor(directoryOrPmp);
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(input))
            return new PenumbraMod(input, null, limits);
        if (!File.Exists(input))
            throw new FileNotFoundException("The Penumbra mod directory or PMP does not exist.", input);
        if (!Path.GetExtension(input).Equals(".pmp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a Penumbra mod directory or a .pmp ZIP archive.");

        var temporary = SafeModFiles.ResolveAnchor(Path.Combine(Path.GetTempPath(), "OutfitStudio-import-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(temporary);
        try
        {
            SafeModFiles.Extract(input, temporary, limits, cancellationToken);
            return new PenumbraMod(temporary, temporary, limits);
        }
        catch
        {
            Directory.Delete(temporary, true);
            throw;
        }
    }

    public IReadOnlyList<ModModelFile> GetModelFiles(ModConversionScope scope = ModConversionScope.AllOptions,
        IReadOnlyDictionary<string, List<string>>? selections = null)
    {
        ThrowIfDisposed();
        var containers = scope switch
        {
            ModConversionScope.AllOptions => AllContainers(),
            ModConversionScope.SelectedOptions => [ResolveSelected(selections)],
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in containers)
        {
            var directFiles = StringMap(container, "Files").ToArray();
            var directPaths = directFiles.Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (gamePath, _) in StringMap(container, "FileSwaps"))
                if (gamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) && !directPaths.Contains(gamePath))
                    throw new NotSupportedException($"Model file swap '{gamePath}' requires game data. Replace it with an included model before converting.");
            foreach (var (gamePath, path) in directFiles)
            {
                if (!gamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                    continue;
                var actual = ResolveFile(path);
                if (!result.TryGetValue(actual, out var gamePaths))
                    result[actual] = gamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                gamePaths.Add(gamePath);
            }
        }
        return result.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => new ModModelFile(p.Key, SafeModFiles.Contained(RootPath, p.Key), p.Value.Order(StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArray();
    }

    /// <summary>
    /// Copies into a private staging directory and publishes a new sibling directory only after
    /// every conversion succeeds. The callback reads model.FullPath and writes destinationPath.
    /// SelectedOptions flattens the resolved configuration so unconverted options cannot be selected.
    /// </summary>
    public async Task<string> CloneAndConvertAsync(string outputParent, string outputName,
        Func<ModModelFile, string, CancellationToken, Task> convert,
        ModConversionScope scope = ModConversionScope.AllOptions,
        IReadOnlyDictionary<string, List<string>>? selections = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);
        ArgumentNullException.ThrowIfNull(convert);
        outputParent = SafeModFiles.ResolveAnchor(outputParent);
        if (SafeModFiles.IsInside(RootPath, outputParent))
            throw new InvalidOperationException("The output directory must be outside the input mod directory.");
        var models = GetModelFiles(scope, selections);
        if (models.Count == 0)
            throw new InvalidDataException("This configuration has no included model files to convert.");
        var resolved = scope == ModConversionScope.SelectedOptions ? ResolveSelected(selections) : null;
        var files = SafeModFiles.Enumerate(RootPath, limits);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(outputParent);
        var suffix = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(outputParent, ".outfitstudio-" + suffix);
        var slug = new string(outputName.Where(char.IsAsciiLetterOrDigit).Take(48).ToArray());
        var destination = Path.Combine(outputParent, (slug.Length == 0 ? "ConvertedOutfit" : slug) + "-" + suffix[..12]);
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var relative in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A derived mod must not advertise the original package-manager identity or
                // retain recovery metadata that could restore the original model mappings.
                if (!relative.Contains('/') && (relative.Equals("heliosphere.json", StringComparison.OrdinalIgnoreCase)
                    || relative.Equals("meta.json.bak", StringComparison.OrdinalIgnoreCase)
                    || relative.Equals("default_mod.json.bak", StringComparison.OrdinalIgnoreCase)
                    || (relative.StartsWith("group_", StringComparison.OrdinalIgnoreCase)
                        && relative.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase))))
                    continue;
                if (resolved is not null && groupDocuments.Contains(relative))
                    continue;
                if (originalDocuments.TryGetValue(relative, out var originalJson))
                {
                    await File.WriteAllTextAsync(SafeModFiles.Contained(stage, relative), originalJson, cancellationToken);
                    continue;
                }
                var source = SafeModFiles.Contained(RootPath, relative);
                await SafeModFiles.CopyAsync(source, SafeModFiles.Contained(stage, relative), new FileInfo(source).Length, cancellationToken, RootPath, outputParent);
            }
            var outputMeta = (JsonObject)metadata.DeepClone();
            outputMeta["Name"] = outputName.Trim();
            if (FileVersion == 4)
            {
                outputMeta["Identifier"] = Guid.NewGuid().ToString();
                outputMeta["LastWrite"] = DateTimeOffset.UtcNow.ToString("O");
                if (resolved is not null)
                {
                    outputMeta["DefaultData"] = resolved;
                    outputMeta["Groups"] = new JsonArray();
                }
            }
            else if (resolved is not null)
            {
                resolved["Version"] = 0;
                var defaultPath = fileInventory.TryGetValue("default_mod.json", out var existingDefault) ? existingDefault : "default_mod.json";
                await WriteJsonAsync(SafeModFiles.Contained(stage, defaultPath), resolved, cancellationToken);
            }
            await WriteJsonAsync(SafeModFiles.Contained(stage, fileInventory["meta.json"]), outputMeta, cancellationToken);
            foreach (var model in models)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SafeModFiles.NoLinks(model.FullPath, RootPath);
                var target = SafeModFiles.Contained(stage, model.RelativePath);
                SafeModFiles.NoLinks(target, outputParent);
                await convert(model, target, cancellationToken);
                SafeModFiles.NoLinks(target, outputParent);
                if (!File.Exists(target) || new FileInfo(target).Length == 0)
                    throw new InvalidDataException($"Conversion did not produce a model: {model.RelativePath}");
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Re-read the completed package before publication, including link and metadata checks.
            using (var validation = Open(stage, limits, cancellationToken))
                _ = validation.GetModelFiles(ModConversionScope.AllOptions);
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

    private JsonObject ResolveSelected(IReadOnlyDictionary<string, List<string>>? selections)
    {
        if (selections is not null)
            foreach (var name in selections.Keys)
                if (!Groups.Any(group => group.Name == name))
                    throw new InvalidDataException($"Selection names an unknown group: {name}");
        var containers = new List<JsonObject>();
        // Penumbra gives later groups the tie-break; earlier multi options win equal priorities.
        // https://github.com/xivdev/Penumbra/blob/testing/Penumbra/Mods/Mod.cs
        foreach (var group in groupData.AsEnumerable().Reverse().OrderByDescending(g => Integer(g, "Priority")))
        {
            RejectConditional(group);
            var type = Text(group, "Type");
            var name = Text(group, "Name");
            if (type is not ("Single" or "Multi"))
                throw new NotSupportedException($"Selected-options conversion does not support '{type}' group '{name}'. Use all options.");
            var options = ObjectArray(group, "Options");
            foreach (var option in options)
                RejectConditional(option);
            if (options.Select(o => Text(o, "Name")).Distinct(StringComparer.Ordinal).Count() != options.Count)
                throw new NotSupportedException($"Selected-options conversion cannot resolve duplicate option names in '{name}'.");
            var chosen = new HashSet<string>(StringComparer.Ordinal);
            if (selections is not null && selections.TryGetValue(name, out var explicitChoice))
            {
                foreach (var choice in explicitChoice)
                    if (!chosen.Add(choice) || !options.Any(o => Text(o, "Name") == choice))
                        throw new InvalidDataException($"Invalid or repeated option '{choice}' in group '{name}'.");
                if (type == "Single" && chosen.Count != Math.Min(1, options.Count))
                    throw new InvalidDataException($"Single group '{name}' requires exactly one selected option.");
            }
            else
            {
                var defaults = Unsigned(group, "DefaultSettings");
                if (type == "Single" && options.Count > 0)
                    chosen.Add(Text(options[(int)Math.Min(defaults, (ulong)options.Count - 1)], "Name"));
                else
                    for (var i = 0; i < options.Count; i++)
                    {
                        if (i >= 64)
                            throw new NotSupportedException($"Multi group '{name}' has more than 64 options.");
                        if ((defaults & (1UL << i)) != 0)
                            chosen.Add(Text(options[i], "Name"));
                    }
            }
            containers.AddRange(options.Where(option => chosen.Contains(Text(option, "Name")))
                .OrderByDescending(option => type == "Multi" ? Integer(option, "Priority") : 0));
        }
        containers.Add(defaultData);
        return SelectedModData.Flatten(containers);
    }

    private static void RejectConditional(JsonObject node)
    {
        if (node["Condition"] is not null || (node["ParentSetting"] is JsonValue parent
            && parent.TryGetValue<string>(out var parentValue) && !string.IsNullOrEmpty(parentValue) && parentValue != Guid.Empty.ToString()))
            throw new NotSupportedException("Selected-options conversion does not support conditional or parented groups/options. Use all options.");
    }

    private IEnumerable<JsonObject> AllContainers()
    {
        foreach (var container in WalkContainers(defaultData))
            yield return container;
        foreach (var group in groupData)
            foreach (var container in WalkContainers(group))
                yield return container;
    }

    private static IEnumerable<JsonObject> WalkContainers(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey("Files") || obj.ContainsKey("FileSwaps"))
                yield return obj;
            foreach (var property in obj)
            {
                if (property.Key is "Files" or "FileSwaps" or "Manipulations")
                    continue;
                foreach (var nested in WalkContainers(property.Value))
                    yield return nested;
            }
        }
        else if (node is JsonArray array)
            foreach (var item in array)
                foreach (var nested in WalkContainers(item))
                    yield return nested;
    }

    private string ResolveFile(string relative)
    {
        relative = SafeModFiles.Relative(relative);
        if (!fileInventory.TryGetValue(relative, out var actual))
            throw new FileNotFoundException($"Mod redirection points at a missing file: {relative}");
        SafeModFiles.NoLinks(SafeModFiles.Contained(RootPath, actual), RootPath);
        return actual;
    }

    private JsonObject ReadDocument(string relative)
    {
        var path = SafeModFiles.Contained(RootPath, ResolveFile(relative));
        if (new FileInfo(path).Length > limits.MaxMetadataBytes)
            throw new InvalidDataException($"Metadata file exceeds the size limit: {relative}");
        try
        {
            var json = File.ReadAllText(path);
            var parsed = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 128 }) as JsonObject
                ?? throw new InvalidDataException($"Metadata is not an object: {relative}");
            originalDocuments[relative] = json;
            return parsed;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid JSON in {relative}: {ex.Message}", ex);
        }
    }

    internal static IEnumerable<KeyValuePair<string, string>> StringMap(JsonObject container, string key)
    {
        if (container[key] is null)
            yield break;
        if (container[key] is not JsonObject obj)
            throw new InvalidDataException($"{key} must be a JSON object.");
        foreach (var (name, value) in obj)
        {
            SafeModFiles.Relative(name);
            if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var path) || string.IsNullOrWhiteSpace(path))
                throw new InvalidDataException($"Invalid {key} redirection for {name}.");
            yield return KeyValuePair.Create(name, SafeModFiles.Relative(path));
        }
    }

    private static JsonObject OptionalObject(JsonObject obj, string key)
        => obj[key] switch { null => new JsonObject(), JsonObject value => value, _ => throw new InvalidDataException($"{key} must be an object.") };

    private static List<JsonObject> ObjectArray(JsonObject obj, string key)
        => obj[key] switch
        {
            null => [],
            JsonArray array => array.Select(node => node as JsonObject ?? throw new InvalidDataException($"{key} contains a non-object.")).ToList(),
            _ => throw new InvalidDataException($"{key} must be an array."),
        };

    internal static string Text(JsonObject obj, string key)
        => obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text
            : throw new InvalidDataException($"{key} must be a string.");

    private static int Integer(JsonObject obj, string key, int fallback = 0)
        => obj[key] is null ? fallback : obj[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number
            : throw new InvalidDataException($"{key} must be an integer.");

    private static ulong Unsigned(JsonObject obj, string key)
        => obj[key] is null ? 0 : obj[key] is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            && ulong.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number) ? number
            : throw new InvalidDataException($"{key} must be an unsigned integer.");

    private static Task WriteJsonAsync(string path, JsonObject node, CancellationToken token)
        => File.WriteAllTextAsync(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), token);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (temporaryDirectory is not null && Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, true);
    }
}
