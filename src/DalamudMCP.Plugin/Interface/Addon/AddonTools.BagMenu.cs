using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DalamudMCP.Plugin.Tools;

internal sealed unsafe partial class AddonTools
{
    internal const string REQUEST_BAG_MENU_TOOL = "request_bag_slot_menu";
    internal static readonly string[] BagWindows = ["Inventory", "InventoryLarge", "InventoryExpansion",
        "InventoryGrid0", "InventoryGrid1", "InventoryGrid2", "InventoryGrid3"];

    public object RequestBagMenu(ToolArguments input, Func<bool> isAllowed)
    {
        if (!GameDiagnostics.CurrentCnLayoutKnown)
            return new { status = "unsupported_sdk", requestDispatched = false };
        if (!services.ClientState.IsLoggedIn || !services.PlayerState.IsLoaded)
            return new { status = "unavailable", requestDispatched = false };
        var name = input.Text("addon_name");
        if (!BagWindows.Contains(name, StringComparer.Ordinal)) throw new ArgumentException("仅支持普通背包窗口。");
        var wrapper = services.GameGui.GetAddonByName(name);
        if (wrapper.IsNull || !wrapper.IsVisible || !wrapper.IsReady)
            return new { status = "bag_not_visible", requestDispatched = false };
        var existing = services.GameGui.GetAddonByName("ContextMenu");
        if (!existing.IsNull && existing.IsVisible) return new { status = "menu_already_visible", requestDispatched = false };
        var addon = (AtkUnitBase*)wrapper.Address;
        if (addon->Id != input.Element("expected_addon_id").GetUInt32())
            return new { status = "addon_changed", requestDispatched = false };
        var node = ResolveEventNode(addon, input);
        var component = AddonNodeScanner.GetComponent(node);
        if (component == null || component->OwnerNode != (AtkComponentNode*)node ||
            component->UldManager.Objects == null || ((AtkUldComponentInfo*)component->UldManager.Objects)->ComponentType != ComponentType.DragDrop)
            return new { status = "not_bag_slot", requestDispatched = false };
        var drag = (AtkComponentDragDrop*)component;
        var icon = drag->AtkComponentIcon;
        var iconNode = icon == null ? null : icon->OwnerNode;
        if (iconNode == null || iconNode->ParentNode != node ||
            drag->AtkDragDropInterface.ComponentNode != iconNode ||
            drag->UldManager.SearchNodeById(iconNode->NodeId) != (AtkResNode*)iconNode ||
            AddonNodeScanner.GetComponent((AtkResNode*)iconNode) != (AtkComponentBase*)icon ||
            !AddonNodeScanner.IsEffectivelyVisible((AtkResNode*)iconNode) || drag->AtkDragDropInterface.IsActive)
            return new { status = "slot_changed", requestDispatched = false };
        AtkEvent* selected = null;
        var seen = new HashSet<nint>();
        for (var entry = node->AtkEventManager.Event; entry != null; entry = entry->NextEvent)
        {
            if (seen.Count == 64 || !seen.Add((nint)entry)) return new { status = "event_chain_invalid", requestDispatched = false };
            if (entry->State.EventType != AtkEventType.DragDropClick) continue;
            if (selected != null || entry->Listener != (AtkEventListener*)addon)
                return new { status = "ambiguous_listener", requestDispatched = false };
            selected = entry;
        }
        var listener = (AtkEventListener*)addon;
        if (selected == null || listener->VirtualTable == null || listener->VirtualTable->ReceiveEvent == null)
            return new { status = "no_slot_listener", requestDispatched = false };
        if (input.Boolean("dry_run")) return new { status = "ready", requestDispatched = false, nodeID = node->NodeId };
        if (!isAllowed()) throw new InvalidOperationException("背包窗口操作权限已关闭。");
        var notification = new AtkEvent { Node = node, Target = (AtkEventTarget*)node, Listener = listener, Param = selected->Param };
        notification.State.EventType = AtkEventType.DragDropClick;
        var data = new AtkEventData();
        data.DragDropData.DragDropInterface = &drag->AtkDragDropInterface;
        data.DragDropData.ComponentNode = iconNode;
        data.DragDropData.MouseButtonId = 1;
        listener->ReceiveEvent(AtkEventType.DragDropClick, unchecked((int)notification.Param), &notification, &data);
        return new { status = "requested", requestDispatched = true, resultObserved = false, nodeID = node->NodeId };
    }
}
