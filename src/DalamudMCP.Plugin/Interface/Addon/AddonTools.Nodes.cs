using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DalamudMCP.Plugin.Tools;

internal sealed unsafe partial class AddonTools
{
    private static object QueryNodes(string addonName, AtkUnitBase* addon, bool visible, ToolArguments arguments)
    {
        var query = AddonNodeQuery.Parse(arguments, true);
        var max = arguments.Integer("max_results", 50, 1, 100);
        var scan = AddonNodeScanner.Scan(addon, query, visible, arguments.Boolean("visible_only", true), max);
        return new
        {
            addonName,
            scan.ScopeStatus,
            scan.Incomplete,
            scannedNodes = scan.Scanned,
            matchedNodes = scan.MatchCount,
            truncated = scan.MatchCount > scan.Matches.Count,
            nodes = scan.Matches.Select(match => match.Info).ToArray(),
        };
    }

    private static AtkResNode* ResolveEventNode(AtkUnitBase* addon, ToolArguments arguments)
    {
        if (arguments.Has("collision_index") || arguments.Has("event_param"))
            throw new ArgumentException("按标签或组件路径定位时，只允许目标节点已注册的事件；不能传入 collision_index 或 event_param。");
        var query = AddonNodeQuery.Parse(arguments, false);
        if (query.NodeID is null && query.Label is null)
            throw new ArgumentException("发送窗口事件需要 node_id 或 label。");
        var scan = AddonNodeScanner.Scan(addon, query, true, true, 1);
        if (scan.Incomplete || scan.ScopeStatus == "ambiguous" || scan.MatchCount != 1)
            throw new InvalidOperationException("未能唯一定位可见节点；请核对标签、组件类型和组件路径。");
        var target = scan.Matches[0];
        if (!target.Info.Enabled)
            throw new InvalidOperationException("目标组件不可用。");
        return (AtkResNode*)target.Address;
    }

    public ObservationSample ReadObservation(AddonObservation observation)
    {
        var wrapper = services.GameGui.GetAddonByName(observation.AddonName, 1);
        if (wrapper.IsNull || !wrapper.IsVisible)
            return new("hidden", false);
        if (!wrapper.IsReady)
            return new("not_ready", null);
        if (!observation.Query.HasNodeSelector)
            return new("visible", true);
        var addon = services.GameGui.GetAddonByName<AtkUnitBase>(observation.AddonName, 1);
        if (addon == null) return new("not_ready", null);
        var scan = AddonNodeScanner.Scan(addon, observation.Query, true, false, 1);
        if (scan.Incomplete) return new("incomplete", null);
        if (scan.ScopeStatus == "ambiguous" || scan.MatchCount > 1) return new("ambiguous", null);
        if (scan.MatchCount == 0) return new("not_found", false);
        var node = scan.Matches[0].Info;
        return new(node.Visible ? "visible" : "hidden", node.Visible, node);
    }

    internal NPCDialogueSample? ReadNPCDialogue(string name, string? expectedText)
    {
        var wrapper = services.GameGui.GetAddonByName(name, 1);
        if (wrapper is not { IsNull: false, IsVisible: true, IsReady: true }) return null;
        var addon = (AtkUnitBase*)wrapper.Address;
        if (!addon->NameString.Equals(name, StringComparison.Ordinal) || addon->Id == 0) return null;
        if (expectedText is null) return new(addon->Id, wrapper.Address, true, null);
        var scan = AddonNodeScanner.Scan(addon, new(null, expectedText, null, null, true), true, true, 1);
        var match = scan.Matches.FirstOrDefault();
        return new(addon->Id, wrapper.Address, !scan.Incomplete && match is not null, match?.Info.Label);
    }

    internal sealed record NPCDialogueSample(ushort AddonID, nint Address, bool Matches, string? Text);

    internal sealed record ObservationSample(string Status, bool? Visible, AddonNodeScanner.NodeInfo? Node = null);
}
