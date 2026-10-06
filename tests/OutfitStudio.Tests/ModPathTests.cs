using System.IO.Compression;
using OutfitStudio.Core.Mods;
using OutfitStudio.Paths;

namespace OutfitStudio.Tests;

public sealed class ModPathTests
{
    [Fact]
    public void FilesystemRootIsAValidContainmentAnchor()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.True(SafeModFiles.IsInside(root, Path.Combine(root, "outfitstudio", "stage")));
        Assert.True(SafeModFiles.IsInside(root, root));
        Assert.False(SafeModFiles.IsInside(Path.Combine(root, "outfit"), Path.Combine(root, "outfit-extra")));
        Assert.Equal(OperatingSystem.IsWindows(),
            SafeModFiles.IsInside(Path.Combine(root, "Outfit"), Path.Combine(root, "outfit", "stage")));
    }

    [PosixFact]
    public void ResolvesLinksInsideTheParentsOfALinkTarget()
    {
        using var scene = new Links();
        Directory.CreateSymbolicLink(scene.At("drive"), scene.Physical);
        Directory.CreateSymbolicLink(scene.At("penumbra"), scene.At("drive/mods"));
        Directory.CreateDirectory(scene.At("physical/mods"));
        Assert.True(ModPath.PathsEqual(scene.At("penumbra"), scene.At("physical/mods")));
        Assert.Equal(scene.At("physical/mods/new/output"), ModPath.ResolveAnchor(scene.At("penumbra/new/output")));
    }

    [PosixFact]
    public async Task LinkedLibraryAllowsReadingAndPublishingThroughDifferentAliases()
    {
        using var scene = new Links();
        var library = scene.At("physical/mods");
        var input = Path.Combine(library, "outfit");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "meta.json"), """{"FileVersion":4,"Name":"Linked outfit","DefaultData":{"Files":{"chara/a.mdl":"a.mdl"}}}""");
        File.WriteAllText(Path.Combine(input, "a.mdl"), "original");
        Directory.CreateSymbolicLink(scene.At("drive"), scene.Physical);
        Directory.CreateSymbolicLink(scene.At("penumbra"), scene.At("drive/mods"));
        using var mod = PenumbraMod.Open(scene.At("penumbra/outfit"));
        var output = await mod.CloneAndConvertAsync(scene.At("drive/mods"), "Conversion",
            (_, path, ct) => File.WriteAllTextAsync(path, "converted", ct));
        Assert.True(ModPath.PathsEqual(Path.GetDirectoryName(output)!, scene.At("penumbra")));
        Assert.Equal("converted", File.ReadAllText(Path.Combine(output, "a.mdl")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(input, "a.mdl")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mod.CloneAndConvertAsync(scene.At("penumbra/outfit/nested"), "Unsafe",
            (_, path, ct) => File.WriteAllTextAsync(path, "bad", ct)));
    }

    [PosixFact]
    public void RootLinkDoesNotPermitLinksInsideTheMod()
    {
        using var scene = new Links();
        File.WriteAllText(scene.At("physical/meta.json"), """{"FileVersion":4,"Name":"Internal link"}""");
        Directory.CreateSymbolicLink(scene.At("penumbra"), scene.Physical);
        File.WriteAllText(scene.At("outside"), "unrelated");
        File.CreateSymbolicLink(scene.At("physical/asset.mdl"), scene.At("outside"));
        Assert.Throws<InvalidDataException>(() => PenumbraMod.Open(scene.At("penumbra")));
    }

    [PosixFact]
    public void CyclicAndBrokenRootLinksGiveBoundedErrors()
    {
        using var scene = new Links();
        Directory.CreateSymbolicLink(scene.At("loop-a"), "loop-b");
        Directory.CreateSymbolicLink(scene.At("loop-b"), "loop-a");
        Assert.Throws<InvalidDataException>(() => ModPath.ResolveAnchor(scene.At("loop-a")));
        Directory.CreateSymbolicLink(scene.At("broken"), "missing");
        Assert.Throws<InvalidDataException>(() => ModPath.ResolveAnchor(scene.At("broken")));
    }

    [PosixFact]
    public void PmpCanBeOpenedThroughLinkedAncestors()
    {
        using var scene = new Links();
        using (var zip = ZipFile.Open(scene.At("physical/input.pmp"), ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("meta.json").Open());
            writer.Write("""{"FileVersion":4,"Name":"Linked archive"}""");
        }
        Directory.CreateSymbolicLink(scene.At("drive"), scene.Physical);
        Directory.CreateSymbolicLink(scene.At("input.pmp"), scene.At("drive/input.pmp"));
        using var mod = PenumbraMod.Open(scene.At("input.pmp"));
        Assert.Equal("Linked archive", mod.Name);
    }

    private sealed class Links : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "OutfitStudio-links-" + Guid.NewGuid().ToString("N"));
        public string Physical => At("physical");
        public Links() => Directory.CreateDirectory(Physical);
        public string At(string relative) => Path.Combine(root, relative);
        public void Dispose() => Directory.Delete(root, true);
    }
}
