using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace OutfitStudio.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly WindowSystem windows = new("OutfitStudio");
    private readonly StudioWindow window;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IFramework framework,
        IPluginLog log, IObjectTable objects)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        var configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var penumbra = new PenumbraBridge(pluginInterface, framework, objects);
        var worker = new WorkerClient(pluginInterface.AssemblyLocation.DirectoryName!, pluginInterface.GetPluginConfigDirectory());
        window = new StudioWindow(penumbra, worker, configuration, () => pluginInterface.SavePluginConfig(configuration), log);
        windows.AddWindow(window);
        commands.AddHandler("/outfitstudio", new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Outfit Studio to convert an installed Penumbra outfit between compatible body meshes.",
        });
        pluginInterface.UiBuilder.Draw += windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi += Open;
        pluginInterface.UiBuilder.OpenConfigUi += Open;
    }

    private void OnCommand(string command, string arguments) => Open();
    private void Open() { window.IsOpen = true; window.RefreshIfNeeded(); }

    public void Dispose()
    {
        pluginInterface.UiBuilder.Draw -= windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi -= Open;
        pluginInterface.UiBuilder.OpenConfigUi -= Open;
        commands.RemoveHandler("/outfitstudio");
        window.Dispose();
        windows.RemoveAllWindows();
    }
}
