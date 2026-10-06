namespace OutfitStudio.Core.Models;

/// <summary>Distances are native FFXIV model units. Body references must share the same bind pose.</summary>
public sealed record ConversionSettings
{
    public float Strength { get; init; } = 1;
    public float Clearance { get; init; }
    public float MaximumDistance { get; init; } = 0.25f;
    public bool AllowUvCorrespondence { get; init; } = true;
}

public sealed record ConversionResult(byte[] ModelData, int ConvertedVertices, int UnchangedVertices,
    string CorrespondenceMethod, IReadOnlyList<string> Warnings);

public sealed record ModelInspection(uint Version, int LodCount, int MeshCount, int VertexCount,
    int TriangleCount, int ShapeCount, IReadOnlyList<string> Materials);

public sealed class ModelConversionException(string message) : Exception(message);
