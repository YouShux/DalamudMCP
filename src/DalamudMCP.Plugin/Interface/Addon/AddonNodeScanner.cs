using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DalamudMCP.Plugin.Tools;

internal static unsafe class AddonNodeScanner
{
    public static NodeScan Scan(AtkUnitBase* addon, AddonNodeQuery query, bool addonVisible, bool visibleOnly, int maxResults)
    {
        var result = new NodeScan();
        var manager = &addon->UldManager;
        var path = query.ComponentPath;
        var scopeVisible = addonVisible;
        var scopeEnabled = true;
        if (path is not null)
        {
            var scopes = new HashSet<nint> { (nint)manager };
            foreach (var id in path)
            {
                if (manager->NodeList == null && manager->NodeListCount > 0)
                {
                    result.Incomplete = true;
                    return result;
                }
                AtkResNode* found = null;
                foreach (var item in manager->Nodes)
                {
                    if (++result.Scanned > MAX_NODES)
                    {
                        result.Incomplete = true;
                        return result;
                    }
                    var node = item.Value;
                    if (node == null || node->NodeId != id || node == found) continue;
                    if (found != null)
                    {
                        result.ScopeStatus = "ambiguous";
                        return result;
                    }
                    found = node;
                }
                if (found == null)
                {
                    result.ScopeStatus = "not_found";
                    return result;
                }
                var component = GetComponent(found);
                if (component == null || !scopes.Add((nint)(&component->UldManager)))
                {
                    result.Incomplete = true;
                    return result;
                }
                scopeVisible &= IsEffectivelyVisible(found);
                scopeEnabled &= (found->NodeFlags & NodeFlags.Enabled) != 0;
                manager = &component->UldManager;
            }
        }

        var visited = new HashSet<nint>();
        ScanScope(manager, path ?? [], scopeVisible, scopeEnabled, path is null);
        return result;

        void ScanScope(AtkUldManager* scope, uint[] componentPath, bool parentVisible, bool parentEnabled, bool recurse)
        {
            if (scope->NodeList == null && scope->NodeListCount > 0)
            {
                result.Incomplete = true;
                return;
            }
            var componentIDs = new HashSet<uint>();
            foreach (var item in scope->Nodes)
            {
                if (++result.Scanned > MAX_NODES)
                {
                    result.Incomplete = true;
                    return;
                }
                var node = item.Value;
                if (node == null || !visited.Add((nint)node)) continue;
                var visible = parentVisible && IsEffectivelyVisible(node);
                if (visibleOnly && !visible) continue;
                var component = GetComponent(node);
                var enabled = parentEnabled && (component == null || (node->NodeFlags & NodeFlags.Enabled) != 0);
                var type = GetComponentType(component);
                var typeName = type?.ToString();
                if (!query.NodeID.HasValue || query.NodeID.Value == node->NodeId)
                {
                    var label = GetLabel(node, component, type);
                    if (query.Matches(node->NodeId, label, typeName))
                    {
                        result.MatchCount++;
                        if (result.Matches.Count < maxResults)
                        {
                            var nodeType = IsComponent(node) ? NodeType.Component : node->Type;
                            bool? isChecked = type == ComponentType.CheckBox && component != null
                                ? ((AtkComponentCheckBox*)component)->IsChecked : null;
                            result.Matches.Add(new((nint)node, new(node->NodeId, componentPath, nodeType.ToString(), typeName, label, visible, enabled, isChecked)));
                        }
                    }
                }
                if (!recurse || component == null) continue;
                if (componentPath.Length == AddonNodeQuery.MAX_COMPONENT_DEPTH || !componentIDs.Add(node->NodeId))
                {
                    result.Incomplete = true;
                    continue;
                }
                ScanScope(&component->UldManager, [.. componentPath, node->NodeId], visible, enabled, true);
                if (result.Scanned > MAX_NODES) return;
            }
        }
    }

    internal static AtkComponentBase* GetComponent(AtkResNode* node)
    {
        if (!IsComponent(node)) return null;
        var owner = (AtkComponentNode*)node;
        return owner->Component != null && owner->Component->OwnerNode == owner ? owner->Component : null;
    }

    private static bool IsComponent(AtkResNode* node) => (ushort)node->Type >= MIN_COMPONENT_NODE_TYPE;

    internal static ComponentType? GetComponentType(AtkComponentBase* component)
    {
        if (component == null || component->UldManager.BaseType != AtkUldManagerBaseType.Component || component->UldManager.Objects == null)
            return null;
        return ((AtkUldComponentInfo*)component->UldManager.Objects)->ComponentType;
    }

    internal static bool IsEffectivelyVisible(AtkResNode* node)
    {
        for (var depth = 0; node != null && depth < 64; depth++, node = node->ParentNode)
            if ((node->NodeFlags & NodeFlags.Visible) == 0) return false;
        return node == null;
    }

    private static string? GetLabel(AtkResNode* node, AtkComponentBase* component, ComponentType? type)
    {
        var text = node->Type == NodeType.Text ? (AtkTextNode*)node : null;
        if (component != null)
        {
            if (type == ComponentType.Button) text = ((AtkComponentButton*)component)->ButtonTextNode;
            else if (type == ComponentType.CheckBox) text = ((AtkComponentCheckBox*)component)->ButtonTextNode;
        }
        if (text == null || text->NodeText.BufUsed is <= 1 or > 4097 || !text->NodeText.StringPtr.HasValue) return null;
        return text->NodeText.ToString().Trim();
    }

    internal sealed record NodeInfo(uint NodeID, uint[] ComponentPath, string NodeType, string? ComponentType, string? Label, bool Visible, bool Enabled, bool? Checked);
    internal sealed record NodeMatch(nint Address, NodeInfo Info);

    internal sealed class NodeScan
    {
        public string ScopeStatus { get; set; } = "resolved";
        public int Scanned { get; set; }
        public bool Incomplete { get; set; }
        public int MatchCount { get; set; }
        public List<NodeMatch> Matches { get; } = [];
    }

    private const int MAX_NODES = 4096;
    private const ushort MIN_COMPONENT_NODE_TYPE = 1000;
}
