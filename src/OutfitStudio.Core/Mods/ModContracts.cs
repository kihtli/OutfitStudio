using System.Text.Json.Nodes;

namespace OutfitStudio.Core.Mods;

public enum ModConversionScope
{
    AllOptions,
    SelectedOptions,
}

/// <summary>A physical model and every game path pointing at it in the requested scope.</summary>
public sealed record ModModelFile(string RelativePath, string FullPath, IReadOnlyList<string> GamePaths);

public sealed record ModOptionInfo(string Name, int Priority);

public sealed record ModGroupInfo(string Name, string Type, int Priority, ulong DefaultSettings,
    IReadOnlyList<ModOptionInfo> Options);

/// <summary>One new model file generated from an included outfit model.</summary>
public sealed record ModGeneratedModel(string Id, string SourceRelativePath, string OutputRelativePath);

/// <summary>Replaces a named source group in-place, or appends a group when the name is null.</summary>
public sealed record ModGroupReplacement(string? SourceGroupName, JsonObject Group);

/// <summary>Reviewed metadata and model jobs for generating destination-body size options.</summary>
public sealed record ModRegenerationPlan(
    IReadOnlyList<ModGroupReplacement> GroupReplacements,
    IReadOnlyList<ModGeneratedModel> Models,
    JsonObject? DefaultData = null);

/// <summary>Limits apply before extraction or cloning; no archive content is executed.</summary>
public sealed record ModReadLimits(
    int MaxFiles = 100_000,
    long MaxTotalBytes = 20L * 1024 * 1024 * 1024,
    long MaxFileBytes = 4L * 1024 * 1024 * 1024,
    int MaxMetadataBytes = 32 * 1024 * 1024);
