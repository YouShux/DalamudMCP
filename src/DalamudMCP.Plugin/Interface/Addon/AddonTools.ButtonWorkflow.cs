using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DalamudMCP.Plugin.Tools;

internal sealed unsafe partial class AddonTools
{
    internal bool AddonVisible(string name) => services.GameGui.GetAddonByName(name, 1) is { IsNull: false, IsVisible: true };

    internal string DispatchRegisteredButton(string name, ushort id, AddonNodeQuery query, string? prompt, bool preview, Func<bool> begin,
        AtkEventType eventType = AtkEventType.ButtonClick)
    {
        var window = services.GameGui.GetAddonByName(name, 1);
        if (window is not { IsNull: false, IsVisible: true, IsReady: true } ||
            services.GameGui.GetAddonByName(name, 2) is { IsNull: false, IsVisible: true }) return "window_unavailable";
        var addon = (AtkUnitBase*)window.Address;
        if (addon->Id != id || !addon->NameString.Equals(name, StringComparison.Ordinal)) return "window_changed";
        if (prompt is not null)
        {
            var node = ((AddonSelectYesno*)addon)->PromptText;
            if (node == null || node->NodeText.BufUsed is <= 1 or > 8192 || !node->NodeText.StringPtr.HasValue ||
                !AddonNodeScanner.IsEffectivelyVisible((AtkResNode*)node) ||
                !new Lumina.Text.ReadOnly.ReadOnlySeStringSpan(node->NodeText.AsSpan()).ExtractText().Trim().Equals(prompt, StringComparison.Ordinal))
                return "prompt_mismatch";
        }
        var scan = AddonNodeScanner.Scan(addon, query, true, true, 1);
        if (scan.Incomplete || scan.MatchCount != 1 || !scan.Matches[0].Info.Enabled) return "button_unavailable";
        var owner = (AtkResNode*)scan.Matches[0].Address;
        var receiver = (AtkEventListener*)addon;
        AtkEventListener* windowReceiver = null;
        if (owner->ParentNode != null && AddonNodeScanner.GetComponent(owner->ParentNode) is var parent &&
            AddonNodeScanner.GetComponentType(parent) == ComponentType.Window && ((AtkComponentWindow*)parent)->OwnerUnitBase == addon)
            windowReceiver = (AtkEventListener*)parent;
        // 标题栏按钮可能由窗口组件接收事件。
        if (windowReceiver == null && query.ComponentPath is { Length: 1 } path && addon->GetNodeById(path[0]) is var parentNode &&
            parentNode != null && AddonNodeScanner.GetComponent(parentNode) is var windowComponent &&
            AddonNodeScanner.GetComponentType(windowComponent) == ComponentType.Window && ((AtkComponentWindow*)windowComponent)->OwnerUnitBase == addon)
            windowReceiver = (AtkEventListener*)windowComponent;
        AtkEvent* listener = null;
        var count = 0;
        for (var item = owner->AtkEventManager.Event; item != null; item = item->NextEvent)
        {
            if (++count > 64) return "event_unavailable";
            if (item->State.EventType != eventType) continue;
            if (listener != null) return "event_ambiguous";
            if (item->Listener == null || item->Listener != receiver && item->Listener != windowReceiver ||
                (item->State.StateFlags & AtkEventStateFlags.IsGlobalEvent) != 0) return "event_unavailable";
            listener = item;
        }
        if (listener == null || listener->Listener->VirtualTable == null || listener->Listener->VirtualTable->ReceiveEvent == null)
            return "event_unavailable";
        if (preview) return "ready";
        if (!begin()) return "cancelled_before_dispatch";
        var notification = *listener;
        notification.NextEvent = null;
        var data = new AtkEventData();
        notification.Listener->ReceiveEvent(eventType, unchecked((int)notification.Param), &notification, &data);
        return "requested";
    }
}
