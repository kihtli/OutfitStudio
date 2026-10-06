using System.Text.Json;
using OutfitStudio.Core.Protocol;
using OutfitStudio.Core.Services;

namespace OutfitStudio.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void RequestRoundTripsIncludingPerModelReferences()
    {
        var original = new WorkerRequest
        {
            Operation = WorkerOperation.Convert, Scope = ConversionScope.CurrentSelection,
            ModelMappings = [new("outfit/top.mdl", "body/small.mdl", "body/medium.mdl")],
            TargetModPaths = ["MAIN", "EXTRA"],
            AdditionalBodyReferences = [new("source/legs.mdl", "body/legs.mdl") { TargetModIndex = 1 }],
            SelectedOptions = new() { ["Chest"] = ["Small"] },
        };
        var json = JsonSerializer.Serialize(original, WorkerProtocol.Json);
        var copy = JsonSerializer.Deserialize<WorkerRequest>(json, WorkerProtocol.Json)!;
        Assert.Equal(original.Operation, copy.Operation);
        Assert.Equal(original.Scope, copy.Scope);
        Assert.Equal(original.ModelMappings[0], copy.ModelMappings[0]);
        Assert.Equal("Small", copy.SelectedOptions["Chest"][0]);
        Assert.Equal(2, copy.ProtocolVersion);
        Assert.Equal(original.TargetModPaths, copy.TargetModPaths);
        Assert.Equal(1, Assert.Single(copy.AdditionalBodyReferences).TargetModIndex);
    }

    [Fact]
    public async Task ProtocolMismatchRejectedBeforeOpeningFiles()
        => await Assert.ThrowsAsync<InvalidDataException>(() => ConversionService.ExecuteAsync(new WorkerRequest { ProtocolVersion = 999 }));

    [Fact]
    public async Task LegacyProtocolCannotSilentlyDiscardMultipleDestinations()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ConversionService.ExecuteAsync(new WorkerRequest
        {
            ProtocolVersion = 1, Mode = ConversionMode.DestinationSizes,
            SourceModPath = "missing", TargetModPath = "MAIN", TargetModPaths = ["MAIN", "EXTRA"], OutfitModPath = "missing",
        }));
        Assert.Contains("protocol version 2", error.Message);
        error = await Assert.ThrowsAsync<InvalidDataException>(() => ConversionService.ExecuteAsync(new WorkerRequest
        {
            ProtocolVersion = 1, SourceModPath = "missing", TargetModPath = "missing", OutfitModPath = "missing",
            AdditionalBodyReferences = [new("source.mdl", "target.mdl") { TargetModIndex = 1 }],
        }));
        Assert.Contains("protocol version 2", error.Message);
    }

    [Fact]
    public async Task InvalidDestinationListsAreRejectedBeforeOpeningFiles()
    {
        var valid = new WorkerRequest { SourceModPath = "missing", TargetModPath = "missing", OutfitModPath = "missing", Mode = ConversionMode.DestinationSizes };
        await Assert.ThrowsAsync<InvalidDataException>(() => ConversionService.ExecuteAsync(valid with { TargetModPaths = null! }));
        await Assert.ThrowsAsync<InvalidDataException>(() => ConversionService.ExecuteAsync(valid with { TargetModPaths = ["MAIN", null!] }));
        await Assert.ThrowsAsync<InvalidDataException>(() => ConversionService.ExecuteAsync(valid with { TargetModPaths = ["MAIN", " "] }));
        await Assert.ThrowsAsync<InvalidDataException>(() => ConversionService.ExecuteAsync(valid with { TargetModPaths = Enumerable.Range(0, 33).Select(i => "target-" + i).ToList() }));
        await Assert.ThrowsAsync<InvalidDataException>(() => ConversionService.ExecuteAsync(valid with
        {
            AdditionalBodyReferences = [new("source.mdl", "target.mdl") { TargetModIndex = -1 }],
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => ConversionService.ExecuteAsync(valid with
        {
            Mode = ConversionMode.ManualReferences, TargetModPaths = ["MAIN", "EXTRA"],
        }));
    }

    [Theory]
    [InlineData(float.NaN, 0, 1)]
    [InlineData(1, float.PositiveInfinity, 1)]
    [InlineData(1, 0, 0)]
    [InlineData(1, -1, 1)]
    public async Task InvalidGeometryParametersRejectedBeforeOpeningFiles(float strength, float clearance, float distance)
        => await Assert.ThrowsAsync<InvalidDataException>(() => ConversionService.ExecuteAsync(new WorkerRequest
        {
            SourceModPath = "missing", TargetModPath = "missing", OutfitModPath = "missing",
            Strength = strength, Clearance = clearance, MaxDistance = distance,
        }));
}
