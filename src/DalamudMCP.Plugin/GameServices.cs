using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace DalamudMCP.Plugin;

public sealed record GameServices(
    IDalamudPluginInterface PluginInterface,
    IFramework Framework,
    IClientState ClientState,
    ICondition Condition,
    IObjectTable ObjectTable,
    IPlayerState PlayerState,
    IGameInventory GameInventory,
    IFateTable FateTable,
    IDataManager DataManager,
    IGameGui GameGui,
    ITargetManager TargetManager,
    IChatGui ChatGui,
    IDutyState DutyState,
    ICommandManager CommandManager,
    IPluginLog Log);
