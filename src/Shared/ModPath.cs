using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OutfitStudio.Paths;

/// <summary>Resolves user-chosen filesystem anchors, never paths supplied by mod metadata.</summary>
public static class ModPath
{
    public static string ResolveAnchor(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var absolute = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) return ResolveWindows(absolute);
        int links = 0;
        return ResolveUnix(absolute, ref links);
    }

    public static bool PathsEqual(string first, string second) => string.Equals(
        Path.TrimEndingDirectorySeparator(ResolveAnchor(first)),
        Path.TrimEndingDirectorySeparator(ResolveAnchor(second)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string ResolveUnix(string absolute, ref int links)
    {
        var root = Path.GetPathRoot(absolute)!;
        var current = root;
        foreach (var segment in absolute[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var target = new FileInfo(current).LinkTarget;
            if (target is null) continue;
            if (++links > 64) throw new InvalidDataException($"Too many symbolic links in the chosen location: {absolute}");
            var next = Path.GetFullPath(target, Path.GetDirectoryName(current)!);
            // A link's target may itself contain linked parent directories. Start at its root
            // again, rather than treating ResolveLinkTarget(true) as full canonicalization.
            var resolved = ResolveUnix(next, ref links);
            if (!Directory.Exists(resolved) && !File.Exists(resolved))
                throw new InvalidDataException($"The chosen location contains a broken link: {current}");
            current = resolved;
        }
        return Path.GetFullPath(current);
    }

    private static string ResolveWindows(string absolute)
    {
        // Wine can mark Unix directory links as reparse points without a LinkTarget that
        // .NET understands. Open the directory normally and ask Windows for its final path.
        // This also handles Windows junctions and symlinks without decoding reparse buffers.
        var suffix = new Stack<string>();
        var existing = absolute;
        while (true)
        {
            using var handle = CreateFileW(existing, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                var buffer = new StringBuilder(512);
                var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0) throw PathError(existing);
                if (length >= buffer.Capacity)
                {
                    buffer.EnsureCapacity(checked((int)length + 1));
                    length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                    if (length == 0 || length >= buffer.Capacity) throw PathError(existing);
                }
                var final = buffer.ToString();
                if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) final = @"\\" + final[8..];
                else if (final.StartsWith(@"\\?\", StringComparison.Ordinal) && final.Length > 5 && final[5] == ':') final = final[4..];
                foreach (var part in suffix) final = Path.Combine(final, part);
                return Path.GetFullPath(final);
            }
            int error = Marshal.GetLastWin32Error();
            if (error is not (2 or 3)) throw PathError(existing, error);
            // New output directories need not exist yet; resolve their nearest existing parent.
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(existing));
            if (string.IsNullOrEmpty(parent)) throw PathError(existing, error);
            suffix.Push(Path.GetFileName(Path.TrimEndingDirectorySeparator(existing)));
            existing = parent;
        }
    }

    private static IOException PathError(string path, int? error = null)
        => new($"Cannot resolve the chosen location '{path}': {new Win32Exception(error ?? Marshal.GetLastWin32Error()).Message}");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
}
