using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DalamudMCP.Plugin.Tools;

internal sealed unsafe partial class AddonTools
{
    public ChoiceSample ReadDropdown(ToolArguments input)
    {
        var target = ResolveChoice(input, ComponentType.DropDownList);
        var dropdown = (AtkComponentDropDownList*)target.Component;
        return new(target.Addon, target.AddonID, target.Node, target.Component, dropdown->IsOpen, dropdown->OpenStateChangePending);
    }

    public string SetDropdown(ToolArguments input, ChoiceSample before, bool desired, Func<bool> isAllowed)
    {
        var target = ResolveChoice(input, ComponentType.DropDownList);
        if (!before.SameControl(target.Addon, target.AddonID, target.Node, target.Component)) return "target_changed";
        var control = (AtkComponentDropDownList*)target.Component;
        if (control->OpenStateChangePending || control->IsOpen != before.Open) return "state_changed";
        var box = control->Checkbox;
        var owner = box == null ? null : box->OwnerNode;
        if (owner == null || owner->ParentNode != (AtkResNode*)target.Node ||
            AddonNodeScanner.GetComponent((AtkResNode*)owner) != (AtkComponentBase*)box ||
            !AddonNodeScanner.IsEffectivelyVisible((AtkResNode*)owner) || (owner->NodeFlags & NodeFlags.Enabled) == 0)
            throw new InvalidOperationException("下拉框的开关归属、可见性或启用状态无法确认。");

        AtkEvent* matched = null;
        var seen = new HashSet<nint>();
        for (var current = owner->AtkEventManager.Event; current != null; current = current->NextEvent)
        {
            if (seen.Count >= 64 || !seen.Add((nint)current)) throw new InvalidOperationException("事件链不完整。");
            if (current->State.EventType != AtkEventType.ButtonClick) continue;
            if (matched != null || current->Listener != (AtkEventListener*)control)
                throw new InvalidOperationException("下拉框开关没有唯一的自身监听器。");
            matched = current;
        }
        if (matched == null || control->VirtualTable == null ||
            ((AtkEventListener*)control)->VirtualTable == null || ((AtkEventListener*)control)->VirtualTable->ReceiveEvent == null ||
            AtkComponentButton.MemberFunctionPointers.SetChecked == null)
            throw new InvalidOperationException("当前 SDK 未提供已注册的下拉开关入口。");
        if (!isAllowed()) throw new InvalidOperationException("窗口编辑权限已关闭。");
        var notification = *matched;
        notification.NextEvent = null;
        var data = new AtkEventData();
        if (box->IsChecked != desired) box->SetChecked(desired);
        ControlTarget after;
        try { after = ResolveChoice(input, ComponentType.DropDownList); }
        catch (InvalidOperationException) { return "dispatch_unconfirmed"; }
        if (!before.SameControl(after.Addon, after.AddonID, after.Node, after.Component) || control->Checkbox != box ||
            box->OwnerNode != owner || box->IsChecked != desired) return "dispatch_unconfirmed";
        ((AtkEventListener*)control)->ReceiveEvent(AtkEventType.ButtonClick, unchecked((int)notification.Param), &notification, &data);
        return "requested";
    }

    public object ActivateRow(ToolArguments input, Func<bool> isAllowed)
    {
        var target = ResolveChoice(input);
        var list = ListFor(target);
        if (list == null || list->ListLength is < 1 or > 4096 || list->VirtualTable == null ||
            list->VirtualTable->GetItemRenderer == null || list->VirtualTable->GetItemDisabledState == null ||
            AtkComponentList.MemberFunctionPointers.DispatchItemEvent == null)
            throw new InvalidOperationException("当前 SDK 或控件没有可确认的列表行入口。");
        var expected = input.Integer("expected_index", 0, -1, 4095);
        var index = input.Integer("row_index", 0, 0, 4095);
        var label = PlainText(input.Element("row_label"));
        if (list->SelectedItemIndex != expected || index >= list->ListLength || RowLabel(list, index) != label)
            return new { status = "row_changed", requestDispatched = false };
        if (target.Type == ComponentType.DropDownList)
        {
            var dropdown = (AtkComponentDropDownList*)target.Component;
            if (!dropdown->IsOpen || dropdown->OpenStateChangePending) return new { status = "dropdown_not_open", requestDispatched = false };
        }
        var renderer = list->GetItemRenderer(index);
        if (renderer == null || renderer->ListItemIndex != index || renderer->OwnerNode == null ||
            AddonNodeScanner.GetComponent((AtkResNode*)renderer->OwnerNode) != (AtkComponentBase*)renderer ||
            !AddonNodeScanner.IsEffectivelyVisible((AtkResNode*)renderer->OwnerNode) ||
            (renderer->OwnerNode->NodeFlags & NodeFlags.Enabled) == 0 || list->GetItemDisabledState(index))
            return new { status = "row_unavailable", requestDispatched = false };
        if (!isAllowed()) throw new InvalidOperationException("窗口编辑权限已关闭。");
        list->DispatchItemEvent(index, AtkEventType.ListItemClick);
        return new { status = "requested", requestDispatched = true, resultObserved = false, rowIndex = index, rowLabel = label };
    }

    private static bool StableChoiceRow(AtkComponentList* list, int index, string label)
    {
        if (list == null || list->ListLength is < 1 or > 4096 || index < 0 || index >= list->ListLength ||
            list->VirtualTable == null || list->VirtualTable->GetItemRenderer == null) return false;
        var renderer = list->GetItemRenderer(index);
        if (renderer == null || renderer->ListItemIndex != index || RowLabel(list, index) != label) return false;
        for (var i = 0; i < list->ListLength; i++)
            if (i != index && RowLabel(list, i) == label) return false;
        return true;
    }

    private ControlTarget ResolveChoice(ToolArguments input, ComponentType? required = null)
    {
        var name = input.Text("addon_name");
        var query = AddonNodeQuery.Parse(input, false);
        if (query.NodeID is null && query.Label is null) throw new ArgumentException("需要节点 ID 或完整标签。");
        var retainerList = name == "RetainerList" && required is null && GameDiagnostics.CurrentCnLayoutKnown;
        if ((!GameDiagnostics.NativeLayoutKnown || !ActionAddons.Contains(name)) && !retainerList || !services.ClientState.IsLoggedIn)
            throw new InvalidOperationException("选择操作仅支持已核对 SDK 中已登录、白名单窗口内的控件。");
        var found = ResolveControl(name, query);
        if (found is not { } target || target.AddonID != input.Element("expected_addon_id").GetUInt32() ||
            !target.Info.Enabled || required.HasValue && target.Type != required)
            throw new InvalidOperationException("没有匹配的已启用控件，或窗口已变化。");
        return target;
    }

    internal readonly record struct ChoiceSample(nint Addon, uint AddonID, nint Node, nint Component, bool Open, bool Pending)
    {
        public bool SameControl(nint addon, uint addonID, nint node, nint component) =>
            Addon == addon && AddonID == addonID && Node == node && Component == component;
    }
}
