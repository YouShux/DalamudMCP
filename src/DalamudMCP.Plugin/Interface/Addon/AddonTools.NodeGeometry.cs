using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DalamudMCP.Plugin.Tools;

internal sealed unsafe partial class AddonTools
{
    internal NodeGeometry? ReadNodeGeometry(string name, uint addonID, AddonNodeQuery query)
    {
        var window = services.GameGui.GetAddonByName(name, 1);
        if (window is not { IsNull: false, IsVisible: true, IsReady: true } ||
            services.GameGui.GetAddonByName(name, 2) is { IsNull: false, IsVisible: true }) return null;
        var addon = (AtkUnitBase*)window.Address;
        if (addon->Id != addonID || addon->NameString != name) return null;
        var scan = AddonNodeScanner.Scan(addon, query, true, true, 1);
        if (scan.Incomplete || scan.MatchCount != 1 || !NativeMemoryReader.TryRead((AtkResNode*)scan.Matches[0].Address, out var node))
            return null;
        return new(window.Address, scan.Matches[0].Address, node.Width, node.Height, node.ScreenX, node.ScreenY,
            node.Transform.M11, node.Transform.M12, node.Transform.M21, node.Transform.M22, node.Rotation, scan.Matches[0].Info.Enabled);
    }

    internal readonly record struct NodeGeometry(nint Window, nint Node, ushort Width, ushort Height, float X, float Y,
        float M11, float M12, float M21, float M22, float Rotation, bool Enabled)
    {
        internal (nint, nint) Identity => (Window, Node);

        internal double? Anchor(bool horizontal, double fraction)
        {
            if (!float.IsFinite(M11) || !float.IsFinite(M22) || M11 <= 0 || M22 <= 0 ||
                !float.IsFinite(M12) || !float.IsFinite(M21) || MathF.Abs(M12) > .0001f || MathF.Abs(M21) > .0001f)
                return null;
            var position = horizontal ? X : Y;
            var size = horizontal ? Width : Height;
            var scale = horizontal ? M11 : M22;
            return float.IsFinite(position) && size != 0 ? position + size * scale * fraction : null;
        }
    }
}
