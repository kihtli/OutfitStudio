using System.IO.Compression;
using OutfitStudio.Paths;

namespace OutfitStudio.Core.Mods;

internal static class SafeModFiles
{
    /// <summary>
    /// A user-selected location may sit beneath an OS/user directory link (for example a
    /// relocated Games folder). Resolve that anchor before enforcing containment; never call
    /// this on an untrusted path taken from metadata or from inside an archive.
    /// </summary>
    public static string ResolveAnchor(string path) => ModPath.ResolveAnchor(path);

    // Penumbra runs on Windows. Reject Windows aliases and drive/stream syntax even on Linux.
    public static string Relative(string path)
    {
        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/');
        if (segments.Length == 0 || segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) || segment is "." or ".."
                || segment.EndsWith(' ') || segment.EndsWith('.')
                || segment.Any(c => c < 32 || "<>:\"|?*".Contains(c)) || Reserved(segment)))
            throw new InvalidDataException($"Unsafe mod-relative path: {path}");
        return string.Join('/', segments);
    }

    private static bool Reserved(string segment)
    {
        var name = segment.Split('.')[0];
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (name.Length == 4 && name[3] is >= '1' and <= '9'
                && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)));
    }

    public static string Contained(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, Relative(relative).Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInside(root, full))
            throw new InvalidDataException($"Path escapes the mod: {relative}");
        return full;
    }

    public static bool IsInside(string parent, string child)
    {
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        child = Path.GetFullPath(child);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, comparison) || child.Equals(parent, comparison);
    }

    public static void NoLinks(string path, string trustedRoot)
    {
        var current = Path.GetFullPath(path);
        trustedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedRoot));
        if (!IsInside(trustedRoot, current)) throw new InvalidDataException($"Path escapes its filesystem anchor: {path}");
        // The chosen root and its ancestors may legitimately be links (especially Wine's
        // drive mappings). Only inspect descendants controlled by a mod or archive.
        while (!string.Equals(Path.TrimEndingDirectorySeparator(current), trustedRoot,
                   OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            // LinkTarget also catches a dangling symbolic link, for which Exists is false.
            var info = new FileInfo(current);
            if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException($"A link inside the mod is not supported: {current}. Links at the chosen mod root are supported.");
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"A link inside the mod is not supported: {current}. Links at the chosen mod root are supported.");
            current = Path.GetDirectoryName(current) ?? throw new InvalidDataException($"Path escapes its filesystem anchor: {path}");
        }
    }

    public static IReadOnlyList<string> Enumerate(string root, ModReadLimits limits)
    {
        var found = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        long size = 0;
        var entryCount = 0;
        while (pending.TryPop(out var directory))
        {
            NoLinks(directory, root);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entryCount > limits.MaxFiles)
                    throw new InvalidDataException("The mod exceeds the maximum number of files and directories.");
                NoLinks(path, root);
                var relative = Relative(Path.GetRelativePath(root, path));
                if (!names.Add(relative))
                    throw new InvalidDataException($"Duplicate path on Windows: {relative}");
                if (Directory.Exists(path))
                    pending.Push(path);
                else
                {
                    var length = new FileInfo(path).Length;
                    CheckSize(length, ref size, limits);
                    found.Add(relative);
                }
            }
        }
        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    public static void Extract(string archivePath, string destination, ModReadLimits limits, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > limits.MaxFiles)
            throw new InvalidDataException("The PMP exceeds the maximum number of entries.");
        var entries = new List<(ZipArchiveEntry Entry, string Relative, bool Directory)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            var name = Relative(isDirectory ? entry.FullName[..^1] : entry.FullName);
            if (!names.Add(name))
                throw new InvalidDataException($"Duplicate PMP entry: {name}");
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType is not (0 or 0x8000 or 0x4000)
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Links and special files are not allowed in PMP archives: {name}");
            if (!isDirectory)
                CheckSize(entry.Length, ref size, limits);
            entries.Add((entry, name, isDirectory));
        }
        foreach (var (entry, relative, isDirectory) in entries)
        {
            token.ThrowIfCancellationRequested();
            var path = Contained(destination, relative);
            NoLinks(path, destination);
            if (isDirectory)
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var source = entry.Open();
            using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CopyBounded(source, target, entry.Length, token);
        }
    }

    public static async Task CopyAsync(string sourcePath, string targetPath, long maxBytes, CancellationToken token, string sourceRoot, string targetRoot)
    {
        NoLinks(sourcePath, sourceRoot);
        NoLinks(targetPath, targetRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length > maxBytes)
            throw new InvalidDataException($"File exceeds the size limit: {sourcePath}");
        await using var target = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous);
        var buffer = new byte[128 * 1024];
        long copied = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            copied = checked(copied + count);
            if (copied > maxBytes)
                throw new InvalidDataException($"File changed beyond its allowed size during copying: {sourcePath}");
            await target.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }

    private static void CopyBounded(Stream source, Stream target, long expectedLength, CancellationToken token)
    {
        var buffer = new byte[128 * 1024];
        long copied = 0;
        int count;
        while ((count = source.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            copied = checked(copied + count);
            if (copied > expectedLength)
                throw new InvalidDataException("A PMP entry expands beyond its declared size.");
            target.Write(buffer, 0, count);
        }
        if (copied != expectedLength)
            throw new InvalidDataException("A PMP entry does not match its declared size.");
    }

    private static void CheckSize(long length, ref long total, ModReadLimits limits)
    {
        if (length < 0 || length > limits.MaxFileBytes)
            throw new InvalidDataException("A mod file exceeds the configured size limit.");
        total = checked(total + length);
        if (total > limits.MaxTotalBytes)
            throw new InvalidDataException("The mod exceeds the configured total size limit.");
    }
}
