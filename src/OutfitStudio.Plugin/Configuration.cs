using Dalamud.Configuration;

namespace OutfitStudio.Plugin;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public string WorkerPath { get; set; } = "";
    public string DotnetPath { get; set; } = "dotnet";
}
