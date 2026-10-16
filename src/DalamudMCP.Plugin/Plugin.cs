using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DalamudMCP.Plugin.Tools;

namespace DalamudMCP.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly GameTools tools;
    private readonly PipeServer pipe;
    private readonly ServerController server;
    private readonly ConfigWindow window;

    public Plugin(IDalamudPluginInterface pluginInterface, IFramework framework, IClientState clientState,
        ICondition condition, IObjectTable objectTable, IPlayerState playerState, IGameInventory gameInventory,
        IFateTable fateTable, IDataManager dataManager, IGameGui gameGui, ITargetManager targetManager,
        IChatGui chatGui, IDutyState dutyState, ICommandManager commandManager, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        commands = commandManager;
        var settings = new SettingsStore(pluginInterface, log);
        tools = new GameTools(new GameServices(pluginInterface, framework, clientState, condition, objectTable,
            playerState, gameInventory, fateTable, dataManager, gameGui, targetManager, chatGui, dutyState, commandManager, log));
        pipe = new PipeServer(tools, settings, log);
        server = new ServerController(pluginInterface, settings, pipe.Name, log);
        window = new ConfigWindow(settings, server, tools.Tools);
        commands.AddHandler("/dalamudmcp", new CommandInfo(OnCommand) { HelpMessage = "打开 DalamudMCP 设置。" });
        pluginInterface.UiBuilder.Draw += window.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += window.Open;
        pluginInterface.UiBuilder.OpenMainUi += window.Open;
        if (settings.Current.AutoStart) server.Start();
    }

    private void OnCommand(string command, string arguments) => window.Open();

    public void Dispose()
    {
        pluginInterface.UiBuilder.Draw -= window.Draw;
        pluginInterface.UiBuilder.OpenConfigUi -= window.Open;
        pluginInterface.UiBuilder.OpenMainUi -= window.Open;
        commands.RemoveHandler("/dalamudmcp");
        pipe.Dispose();
        server.Dispose();
        tools.Dispose();
    }
}
