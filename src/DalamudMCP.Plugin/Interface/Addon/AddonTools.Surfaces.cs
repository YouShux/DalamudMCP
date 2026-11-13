using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace DalamudMCP.Plugin.Tools;

internal sealed unsafe partial class AddonTools
{
    internal const string SURFACE_TOOL = "request_known_addon";
    internal static readonly string[] Surfaces = ["inventory", "map", "journal", "duty_finder"];
    private static readonly string[] InventoryNames = ["Inventory", "InventoryLarge", "InventoryExpansion"];

    public SurfaceState ReadSurface(string surface)
    {
        var names = surface switch
        {
            "inventory" => InventoryNames,
            "map" => ["AreaMap"],
            "journal" => ["Journal"],
            "duty_finder" => ["ContentsFinder"],
            _ => throw new ArgumentException("未知的游戏窗口。"),
        };
        foreach (var name in names)
        {
            var addon = services.GameGui.GetAddonByName(name, 1);
            if (!addon.IsNull && addon.IsVisible) return new(true, addon.IsReady, name);
        }
        return new(false, true, null);
    }

    public string RequestSurface(string surface, bool open, Func<bool> isAllowed)
    {
        if (!services.ClientState.IsLoggedIn || services.ObjectTable.LocalPlayer is null ||
            services.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51))
            return "player_not_ready";
        if (surface == "inventory")
        {
            var ui = UIModule.Instance();
            if (ui == null) return "native_unavailable";
            var module = &ui->UIModuleInterface;
            if (module->VirtualTable == null || (open ? module->VirtualTable->OpenInventory == null : module->VirtualTable->CloseInventory == null))
                return "native_unavailable";
            DemandAccess();
            if (open) module->OpenInventory();
            else module->CloseInventory();
            return "requested";
        }

        var id = surface switch
        {
            "map" => AgentId.Map,
            "journal" => AgentId.QuestJournal,
            "duty_finder" => AgentId.ContentsFinder,
            _ => throw new ArgumentException("未知的游戏窗口。"),
        };
        var agents = AgentModule.Instance();
        if (agents == null || AgentModule.MemberFunctionPointers.GetAgentByInternalId == null)
            return "native_unavailable";
        var agent = agents->GetAgentByInternalId(id);
        if (agent == null || agent->VirtualTable == null ||
            (open ? agent->VirtualTable->Show == null : agent->VirtualTable->Hide == null))
            return "native_unavailable";
        DemandAccess();
        if (open) agent->Show(); else agent->Hide();
        return "requested";

        void DemandAccess()
        {
            if (!isAllowed()) throw new InvalidOperationException("窗口交互权限已关闭。");
        }
    }

    internal sealed record SurfaceState(bool Visible, bool Ready, string? AddonName);
}
