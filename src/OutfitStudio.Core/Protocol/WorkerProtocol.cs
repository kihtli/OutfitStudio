using System.Text.Json;
using System.Text.Json.Serialization;

namespace OutfitStudio.Core.Protocol;

public static class WorkerProtocol
{
    public const int Version = 2;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

public enum WorkerOperation { Analyze, Convert }
public enum ConversionScope { AllOptions, CurrentSelection }
public enum ConversionMode { ManualReferences, DestinationSizes }

public sealed record WorkerRequest
{
    public int ProtocolVersion { get; init; } = WorkerProtocol.Version;
    public WorkerOperation Operation { get; init; }
    public ConversionMode Mode { get; init; } = ConversionMode.ManualReferences;
    public string SourceModPath { get; init; } = "";
    public string TargetModPath { get; init; } = "";
    /// <summary>Full ordered destination list when nonempty; otherwise TargetModPath is used.</summary>
    public List<string> TargetModPaths { get; init; } = [];
    public string OutfitModPath { get; init; } = "";
    public string? SourceModelPath { get; init; }
    public string? TargetModelPath { get; init; }
    public List<ModelConversionMapping> ModelMappings { get; init; } = [];
    public List<BodyReferencePair> AdditionalBodyReferences { get; init; } = [];
    public string OutputRoot { get; init; } = "";
    public string OutputName { get; init; } = "Converted Outfit";
    public ConversionScope Scope { get; init; } = ConversionScope.AllOptions;
    public Dictionary<string, List<string>> SelectedOptions { get; init; } = new();
    public float Strength { get; init; } = 1f;
    public float Clearance { get; init; } = 0.0025f;
    public float MaxDistance { get; init; } = 0.25f;
}

public sealed record ModelCandidate(string RelativePath, string[] GamePaths, int VertexCount, int TriangleCount);
public sealed record ModelConversionMapping(string OutfitModelPath, string SourceModelPath, string TargetModelPath);
public sealed record BodyReferencePair(string SourceModelPath, string TargetModelPath)
{
    public int TargetModIndex { get; init; }
}
public sealed record DestinationSizeGroup(string Name, string SourceSize, string[] Options);
public sealed record AutomaticPlanSummary
{
    public bool Ready { get; init; }
    public DestinationSizeGroup[] DestinationGroups { get; init; } = [];
    public string[] SourceSizeGroups { get; init; } = [];
    public string[] PreservedGroups { get; init; } = [];
    public int ModelJobs { get; init; }
    public string[] Notes { get; init; } = [];
    public string[] BlockingReasons { get; init; } = [];
}
public sealed record WorkerResponse
{
    public int ProtocolVersion { get; init; } = WorkerProtocol.Version;
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? OutputDirectory { get; init; }
    public ModelCandidate[] SourceModels { get; init; } = [];
    public ModelCandidate[] TargetModels { get; init; } = [];
    public ModelCandidate[] OutfitModels { get; init; } = [];
    public int OutfitModelCount { get; init; }
    public int ConvertedModelCount { get; init; }
    public int ConvertedVertexCount { get; init; }
    public string[] Warnings { get; init; } = [];
    public bool? BodiesCompatible { get; init; }
    public string? CompatibilityMessage { get; init; }
    public AutomaticPlanSummary? AutomaticPlan { get; init; }
    /// <summary>Only unchanged non-size option selections may be copied into generated output.</summary>
    public string[] PreservedOptionGroups { get; init; } = [];
}

public sealed record WorkerProgress(string Stage, int Completed, int Total, string Message);
