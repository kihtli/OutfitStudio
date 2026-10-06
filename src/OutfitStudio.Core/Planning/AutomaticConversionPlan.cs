using OutfitStudio.Core.Protocol;

namespace OutfitStudio.Core.Planning;

public sealed record AutomaticConversionPlan(
    IReadOnlyList<AutomaticGroupPlan> Groups,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> Warnings)
{
    public bool CanConvert => Groups.Count > 0 && Issues.Count == 0;
}

/// <param name="SourceGroupIndex">Original outfit group index; -1 denotes a default-data model.</param>
/// <param name="TemplateOptionIndex">Original outfit option index; -1 denotes default data.</param>
/// <param name="DestinationDefaultOptionIndex">Zero-based index in Options, before retaining any original empty options.</param>
public sealed record AutomaticGroupPlan(
    int SourceGroupIndex,
    string SourceGroupName,
    int TemplateOptionIndex,
    string TemplateOptionName,
    string Region,
    string DestinationGroupName,
    int DestinationDefaultOptionIndex,
    IReadOnlyList<AutomaticOptionPlan> Options)
{
    /// <summary>Validated original outfit options, with the canonical template first; stable across override replanning.</summary>
    public int[] ValidTemplateOptionIndices { get; init; } = [];
}

public sealed record AutomaticOptionPlan(string Name, IReadOnlyList<AutomaticModelPlan> Models);

/// <summary>A neighboring size selector required by a particular generated model.</summary>
/// <param name="GroupIndex">Zero-based index in AutomaticConversionPlan.Groups.</param>
/// <param name="OptionIndex">Zero-based destination option index, before retained non-model options.</param>
public sealed record AutomaticOptionSelection(int GroupIndex, int OptionIndex)
{
    /// <summary>OptionIndex instead identifies a retained non-model option in the original outfit group.</summary>
    public bool IsOriginalOption { get; init; }
}

public sealed record AutomaticModelPlan(
    string OutfitModelPath,
    string SourceModelPath,
    string TargetModelPath,
    IReadOnlyList<BodyReferencePair> AdditionalBodyReferences)
{
    /// <summary>Owner of TargetModelPath in the request's ordered destination list.</summary>
    public int TargetModIndex { get; init; }

    /// <summary>Neighbor selections required in addition to the containing group's option.</summary>
    public IReadOnlyList<AutomaticOptionSelection> RequiredOptions { get; init; } = [];
}
