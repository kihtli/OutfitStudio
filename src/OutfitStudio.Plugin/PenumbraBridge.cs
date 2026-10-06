using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using OutfitStudio.Paths;

namespace OutfitStudio.Plugin;

internal sealed record InstalledMod(string Directory, string Name);
internal sealed record CollectionChoice(Guid Id, string Name);
internal sealed record PenumbraSnapshot(string Root, InstalledMod[] Mods, CollectionChoice[] Collections, Guid? CurrentCollection);
internal sealed record ModSettingsSnapshot(int Priority, Dictionary<string, List<string>> Options);

/// <summary>
/// Wire signatures verified against Ottermandias/Penumbra.Api commit
/// 45749c89e80ab9f7a7a0557b9c87770980048051 (IpcSubscribers and Enums).
/// Uses the stable V5 gates without loading Penumbra or its private dependencies.
/// Every IPC invocation runs through the Dalamud framework scheduler.
/// </summary>
internal sealed class PenumbraBridge(IDalamudPluginInterface pi, IFramework framework, IObjectTable objects)
{
    public Task<PenumbraSnapshot> ReadAsync() => framework.RunOnFrameworkThread(() =>
    {
        CheckVersion();
        var root = pi.GetIpcSubscriber<string>("Penumbra.GetModDirectory").InvokeFunc();
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("Set a mod directory in Penumbra before using Outfit Studio.");
        var mods = pi.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList").InvokeFunc()
            .Select(p => new InstalledMod(p.Key, p.Value)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var collections = pi.GetIpcSubscriber<Dictionary<Guid, string>>("Penumbra.GetCollections.V5").InvokeFunc()
            .Where(p => p.Key != Guid.Empty).Select(p => new CollectionChoice(p.Key, p.Value))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var current = pi.GetIpcSubscriber<byte, (Guid Id, string Name)?>("Penumbra.GetCollection").InvokeFunc(0xE2);
        return new PenumbraSnapshot(ModPath.ResolveAnchor(root), mods, collections, current?.Id);
    });

    public async Task<Dictionary<string, List<string>>> ReadOptionsAsync(Guid collection, string mod)
        => (await ReadSettingsAsync(collection, mod))?.Options
            ?? throw new InvalidOperationException("Set the outfit's options in this Penumbra collection first, or choose All options.");

    public Task<ModSettingsSnapshot?> ReadSettingsAsync(Guid collection, string mod) => framework.RunOnFrameworkThread(() =>
    {
        CheckVersion();
        var (code, value) = pi.GetIpcSubscriber<Guid, string, string, bool,
            (int, (bool, int, Dictionary<string, List<string>>, bool)?)>("Penumbra.GetCurrentModSettings.V5")
            .InvokeFunc(collection, mod, "", false);
        Check(code, "Read outfit options");
        if (value is null) return null;
        return new ModSettingsSnapshot(value.Value.Item2,
            value.Value.Item3.ToDictionary(p => p.Key, p => new List<string>(p.Value), StringComparer.Ordinal));
    });

    public async Task RegisterAsync(string outputDirectory, string expectedRoot, CancellationToken cancellation)
    {
        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(outputDirectory));
        await framework.RunOnFrameworkThread(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            CheckVersion();
            var root = pi.GetIpcSubscriber<string>("Penumbra.GetModDirectory").InvokeFunc();
            if (!ModPath.PathsEqual(root, expectedRoot)
                || !ModPath.PathsEqual(Path.GetDirectoryName(Path.GetFullPath(outputDirectory))!, root))
                throw new InvalidOperationException("Penumbra's mod directory changed. The converted files were kept; add them through Penumbra manually.");
            Check(pi.GetIpcSubscriber<string, int>("Penumbra.AddMod.V5").InvokeFunc(directoryName), "Register converted mod");
        });
        // AddMod acknowledges the call, not the load. Verify it appears in Penumbra's catalogue.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            var loaded = await framework.RunOnFrameworkThread(() =>
                pi.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList").InvokeFunc().ContainsKey(directoryName));
            if (loaded) return;
            await Task.Delay(250, cancellation);
        }
        throw new InvalidOperationException("Converted files were saved, but Penumbra has not confirmed loading the mod. Check Penumbra's mod list and logs.");
    }

    public Task EnableAsync(Guid collection, string outputDirectory, string expectedRoot, int priority, bool redraw,
        Dictionary<string, List<string>>? preservedOptions, CancellationToken cancellation) => framework.RunOnFrameworkThread(() =>
    {
        cancellation.ThrowIfCancellationRequested();
        CheckVersion();
        var root = pi.GetIpcSubscriber<string>("Penumbra.GetModDirectory").InvokeFunc();
        if (!ModPath.PathsEqual(root, expectedRoot)
            || !ModPath.PathsEqual(Path.GetDirectoryName(Path.GetFullPath(outputDirectory))!, root))
            throw new InvalidOperationException("Penumbra's mod directory changed before activation. The saved output was not enabled.");
        var mod = Path.GetFileName(Path.TrimEndingDirectorySeparator(outputDirectory));
        if (preservedOptions is not null)
            foreach (var option in preservedOptions)
                Check(pi.GetIpcSubscriber<Guid, string, string, string, IReadOnlyList<string>, int>("Penumbra.TrySetModSettings.V5")
                    .InvokeFunc(collection, mod, "", option.Key, option.Value), $"Set option {option.Key}");
        Check(pi.GetIpcSubscriber<Guid, string, string, int, int>("Penumbra.TrySetModPriority.V5")
            .InvokeFunc(collection, mod, "", priority), "Set converted mod priority");
        Check(pi.GetIpcSubscriber<Guid, string, string, bool, int>("Penumbra.TrySetMod.V5")
            .InvokeFunc(collection, mod, "", true), "Enable converted mod");
        if (redraw && objects.LocalPlayer is { } player)
            pi.GetIpcSubscriber<int, int, object>("Penumbra.RedrawObject.V5").InvokeAction(player.ObjectIndex, 0);
    });

    public static string InstalledPath(string root, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory != Path.GetFileName(directory) || directory is "." or "..")
            throw new InvalidOperationException("Penumbra returned an invalid mod directory name.");
        return Path.Combine(root, directory);
    }

    private void CheckVersion()
    {
        var (breaking, _) = pi.GetIpcSubscriber<(int, int)>("Penumbra.ApiVersion.V5").InvokeFunc();
        if (breaking != 5) throw new InvalidOperationException($"This build requires Penumbra API 5; the installed API is {breaking}.");
    }

    private static void Check(int code, string action)
    {
        if (code is 0 or 1) return;
        var detail = code switch { 2 => "collection missing", 3 => "mod missing", 4 => "option group missing", 5 => "option missing",
            9 => "file missing", 11 => "invalid argument", 17 => "Penumbra unavailable", _ => $"API error {code}" };
        throw new InvalidOperationException($"{action}: {detail}.");
    }
}
