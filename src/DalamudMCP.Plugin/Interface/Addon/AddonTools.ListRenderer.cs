using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text.ReadOnly;

namespace DalamudMCP.Plugin.Tools;

internal sealed unsafe partial class AddonTools
{
    internal const string READ_LIST_ROW_TOOL = "inspect_addon_list_row";
    internal const string ACTIVATE_LIST_ROW_TOOL = "request_addon_list_row";

    internal object ListRenderer(ToolArguments input, bool activate, Func<bool> allowed)
    {
        var name = input.Text("addon_name");
        var query = AddonNodeQuery.Parse(input, false);
        if (query.NodeID is null || query.ComponentPath is not { Length: > 0 } path)
            throw new ArgumentException("指定行渲染节点 ID 和以所属列表结尾的组件路径。");
        if (!GameDiagnostics.CurrentCnLayoutKnown) return Result("unsupported_sdk");
        if (!services.ClientState.IsLoggedIn || !services.PlayerState.IsLoaded ||
            services.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51)) return Result("context_changed");
        var territory = services.ClientState.TerritoryType;
        if (activate && territory != input.Element("expected_territory_id").GetUInt32()) return Result("context_changed");
        var row = ResolveControl(name, query with { ComponentType = "ListItemRenderer" });
        var parent = ResolveControl(name, new(path[^1], null, null, path[..^1], false));
        if (row is not { Type: ComponentType.ListItemRenderer } target ||
            parent is not { Type: ComponentType.List or ComponentType.TreeList } owner || target.Addon != owner.Addon ||
            services.GameGui.GetAddonByName(name, 2) is { IsNull: false, IsVisible: true })
            return Result("row_unavailable");
        if (input.Has("expected_addon_id") && input.Element("expected_addon_id").GetUInt32() != target.AddonID)
            return Result("window_changed");
        var list = (AtkComponentList*)owner.Component;
        var renderer = (AtkComponentListItemRenderer*)target.Component;
        var index = renderer->ListItemIndex;
        var count = list->ListLength;
        if (count is < 1 or > 4096 || index < 0 || index >= count || list->SelectedItemIndex < -1 ||
            list->SelectedItemIndex >= count || !NativeMemoryReader.TryRead(list->VirtualTable, out var methods) ||
            methods.GetItemRenderer == null || methods.GetItemDisabledState == null || list->GetItemRenderer(index) != renderer)
            return Result("row_unavailable");
        var selected = list->SelectedItemIndex;
        var layoutPending = owner.Type == ComponentType.TreeList && ((AtkComponentTreeList*)list)->LayoutRefreshPending;
        var enabled = owner.Info.Enabled && target.Info.Enabled && !list->GetItemDisabledState(index);
        var labels = RendererTexts(renderer);
        if (labels is null) return Result("text_unavailable");
        object Read(string status, bool dispatched = false, bool selectedDispatched = false) => new
        {
            status, addonName = name, addonID = target.AddonID, territoryID = territory, rowNodeID = query.NodeID,
            componentPath = path, listType = owner.Type.ToString(), rowIndex = index, selectedIndex = selected, count,
            enabled, layoutPending, labels, requestDispatched = dispatched, selectionDispatched = selectedDispatched,
            resultObserved = false,
        };
        if (!activate) return Read("ok");
        var label = PlainText(input.Element("row_label"));
        if (input.Integer("row_index", -1, 0, 4095) != index ||
            input.Integer("expected_index", -2, -1, 4095) != selected || labels.Count(text => text == label) != 1)
            return Read("row_changed");
        if (!enabled || layoutPending || methods.SelectItem == null || AtkComponentList.MemberFunctionPointers.DispatchItemEvent == null ||
            !HasListListener((AtkResNode*)owner.Node, (AtkUnitBase*)owner.Addon)) return Read("event_unavailable");
        if (input.Boolean("dry_run")) return Read("ready");
        if (!allowed()) return Read("permission_changed");
        var selecting = selected != index;
        if (selecting) list->SelectItem(index, false);
        if (ResolveControl(name, query with { ComponentType = "ListItemRenderer" }) is not { } after ||
            after.Addon != target.Addon || after.AddonID != target.AddonID || after.Node != target.Node || after.Component != target.Component ||
            ResolveControl(name, new(path[^1], null, null, path[..^1], false)) is not { } afterOwner ||
            afterOwner.Node != owner.Node || afterOwner.Component != owner.Component || !after.Info.Enabled ||
            renderer->ListItemIndex != index || list->SelectedItemIndex != index || list->GetItemRenderer(index) != renderer ||
            list->GetItemDisabledState(index) || owner.Type == ComponentType.TreeList && ((AtkComponentTreeList*)list)->LayoutRefreshPending ||
            RendererTexts(renderer)?.Count(text => text == label) != 1 || !HasListListener((AtkResNode*)owner.Node, (AtkUnitBase*)owner.Addon))
            return Read("row_changed_after_selection", selectedDispatched: selecting);
        if (!allowed()) return Read("permission_changed", selectedDispatched: selecting);
        list->DispatchItemEvent(index, AtkEventType.ListItemClick);
        return Read("requested", true, selecting);
    }

    private static string[]? RendererTexts(AtkComponentListItemRenderer* renderer)
    {
        if (renderer->UldManager.NodeList == null || renderer->UldManager.NodeListCount > 256) return null;
        var texts = new List<string>();
        var seen = new HashSet<nint>();
        foreach (var entry in renderer->UldManager.Nodes)
        {
            var node = entry.Value;
            if (node == null || node->Type != NodeType.Text || !seen.Add((nint)node) ||
                !AddonNodeScanner.IsEffectivelyVisible(node)) continue;
            var text = ((AtkTextNode*)node)->NodeText;
            if (text.BufUsed > 1025 || !text.StringPtr.HasValue || texts.Count == 16) return null;
            if (text.BufUsed > 1) texts.Add(new ReadOnlySeStringSpan(text.AsSpan()).ExtractText().Trim());
        }
        return [.. texts];
    }

    private static bool HasListListener(AtkResNode* node, AtkUnitBase* addon)
    {
        var found = false;
        var count = 0;
        for (var item = node->AtkEventManager.Event; item != null;)
        {
            if (++count > 64 || !NativeMemoryReader.TryRead(item, out var entry)) return false;
            item = entry.NextEvent;
            if (entry.State.EventType != AtkEventType.ListItemClick) continue;
            if (found || entry.Listener != (AtkEventListener*)addon ||
                (entry.State.StateFlags & AtkEventStateFlags.IsGlobalEvent) != 0) return false;
            found = true;
        }
        return found;
    }

    private static object Result(string status) => new { status, requestDispatched = false, resultObserved = false };
}
