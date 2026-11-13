using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DalamudMCP.Plugin.Tools;

internal sealed unsafe partial class AddonTools
{
    internal const string SNAPSHOT_TOOL = "inspect_addon_node";
    internal const string SAMPLE_NODES_TOOL = "sample_addon_node_frames";

    internal object NodeSnapshot(string name, ToolArguments input, out (nint Window, nint Node) identity)
    {
        identity = default;
        if (!GameDiagnostics.CurrentCnLayoutKnown) return new { status = "unsupported_sdk" };
        var query = AddonNodeQuery.Parse(input, false);
        if (query.NodeID is null && query.Label is null) throw new ArgumentException("请提供 node_id 或完整 label。");
        var wrapper = services.GameGui.GetAddonByName(name, 1);
        if (wrapper.IsNull || !wrapper.IsReady) return new { status = "addon_not_ready", addonName = name };
        var addon = (AtkUnitBase*)wrapper.Address;
        if (addon == null || addon->NameString != name) return new { status = "addon_not_ready", addonName = name };
        var addonID = addon->Id;
        if (input.Has("expected_addon_id") && input.Element("expected_addon_id").GetUInt32() != addonID)
            return new { status = "addon_changed", addonName = name, addonID };

        var scan = AddonNodeScanner.Scan(addon, query, wrapper.IsVisible, false, 1);
        if (scan.Incomplete || scan.ScopeStatus == "ambiguous" || scan.MatchCount != 1)
            return new { status = scan.Incomplete ? "incomplete" : scan.ScopeStatus == "ambiguous" || scan.MatchCount > 1
                    ? "ambiguous" : "not_found", addonName = name, addonID, matches = scan.MatchCount };
        var match = scan.Matches[0];
        if (!NativeMemoryReader.TryRead((AtkResNode*)match.Address, out var node))
            return new { status = "unreadable", addonName = name, addonID };
        identity = ((nint)addon, match.Address);

        var events = new List<object>();
        var seen = new HashSet<nint>();
        var next = node.AtkEventManager.Event;
        while (next != null && events.Count < 32 && seen.Add((nint)next) && NativeMemoryReader.TryRead(next, out var entry))
        {
            events.Add(new { type = entry.State.EventType.ToString(), parameter = entry.Param });
            next = entry.NextEvent;
        }

        var timeline = NativeMemoryReader.TryRead(node.Timeline, out var animation) ? new
        {
            frameTime = Finite(animation.FrameTime), parentFrameTime = Finite(animation.ParentFrameTime),
            activeLabelID = animation.ActiveLabelId, labelEndFrame = animation.LabelEndFrameIdx,
        } : null;

        return new
        {
            status = "ok", addonName = name, addonID, capturedAt = DateTimeOffset.UtcNow,
            node = match.Info, addonVisible = wrapper.IsVisible, selfVisible = (node.NodeFlags & NodeFlags.Visible) != 0,
            parentNodeID = NativeMemoryReader.TryRead(node.ParentNode, out var parent) ? (uint?)parent.NodeId : null,
            position = new { x = Finite(node.X), y = Finite(node.Y), screenX = Finite(node.ScreenX), screenY = Finite(node.ScreenY) },
            size = new { node.Width, node.Height },
            transform = new { scaleX = Finite(node.ScaleX), scaleY = Finite(node.ScaleY), rotation = Finite(node.Rotation),
                originX = Finite(node.OriginX), originY = Finite(node.OriginY), m11 = Finite(node.Transform.M11),
                m12 = Finite(node.Transform.M12), m21 = Finite(node.Transform.M21), m22 = Finite(node.Transform.M22) },
            events, eventsIncomplete = next != null, timeline,
            image = node.Type == NodeType.Image ? ImagePart((AtkImageNode*)match.Address) : null,
        };
    }

    private static object? ImagePart(AtkImageNode* address)
    {
        if (!NativeMemoryReader.TryRead(address, out var image) ||
            !NativeMemoryReader.TryRead(image.PartsList, out var list) || list.Parts == null ||
            list.PartCount is 0 or > 4096 || image.PartId >= list.PartCount ||
            !NativeMemoryReader.TryRead(list.Parts + image.PartId, out var part)) return null;
        return new { partsListID = list.Id, partID = image.PartId, list.PartCount, part.U, part.V, part.Width, part.Height,
            assetID = NativeMemoryReader.TryRead(part.UldAsset, out var asset) ? (uint?)asset.Id : null };
    }

    private static float? Finite(float value) => float.IsFinite(value) ? value : null;
}
