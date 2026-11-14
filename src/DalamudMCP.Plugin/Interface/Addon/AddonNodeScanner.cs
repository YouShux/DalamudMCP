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

        var visited = new HashSet<nint>(path is not null ? Math.Min((int)manager->NodeListCount, MAX_NODES) : 0);
        Span<uint> componentPath = stackalloc uint[AddonNodeQuery.MAX_COMPONENT_DEPTH];
        ScanScope(manager, componentPath, 0, scopeVisible, scopeEnabled);
        return result;

        void ScanScope(AtkUldManager* scope, Span<uint> branchPath, int depth, bool parentVisible, bool parentEnabled)
        {
            if (scope->NodeList == null && scope->NodeListCount > 0)
            {
                result.Incomplete = true;
                return;
            }
            uint[]? matchedPath = null;
            uint? firstComponentID = null;
            HashSet<uint>? componentIDs = null;
            foreach (var item in scope->Nodes)
            {
                if (++result.Scanned > MAX_NODES)
                {
                    result.Incomplete = true;
                    return;
                }
                var node = item.Value;
                if (node == null || !visited.Add((nint)node)) continue;
                if (path is null && !visibleOnly && visited.Count == LARGE_SCAN_RESERVE_THRESHOLD)
                    visited.EnsureCapacity(LARGE_SCAN_RESERVED_NODES);
                var visible = parentVisible && IsEffectivelyVisible(node);
                if (visibleOnly && !visible) continue;
                var component = GetComponent(node);
                var enabled = parentEnabled && (component == null || (node->NodeFlags & NodeFlags.Enabled) != 0);
                if (!query.NodeID.HasValue || query.NodeID.Value == node->NodeId)
                {
                    var type = GetComponentType(component);
                    var typeName = type?.ToString();
                    if (query.ComponentType is null || string.Equals(query.ComponentType, typeName, StringComparison.OrdinalIgnoreCase))
                    {
                        var store = result.Matches.Count < maxResults;
                        var label = store || query.Label is not null ? GetLabel(node, component, type) : null;
                        if (query.Matches(node->NodeId, label, typeName))
                        {
                            result.MatchCount++;
                            if (store)
                            {
                                var nodeType = IsComponent(node) ? NodeType.Component : node->Type;
                                bool? isChecked = type == ComponentType.CheckBox && component != null
                                    ? ((AtkComponentCheckBox*)component)->IsChecked : null;
                                result.Matches.Add(new((nint)node, new(node->NodeId, matchedPath ??= path ?? branchPath[..depth].ToArray(), nodeType.ToString(), typeName, label, visible, enabled, isChecked)));
                            }
                        }
                    }
                }
                if (path is not null || component == null) continue;
                if (depth == branchPath.Length || !AddComponentID(node->NodeId))
                {
                    result.Incomplete = true;
                    continue;
                }
                branchPath[depth] = node->NodeId;
                ScanScope(&component->UldManager, branchPath, depth + 1, visible, enabled);
                if (result.Scanned > MAX_NODES) return;
            }

            bool AddComponentID(uint id)
            {
                if (componentIDs is not null) return componentIDs.Add(id);
                if (firstComponentID is not uint first)
                {
                    firstComponentID = id;
                    return true;
                }
                if (first == id) return false;
                componentIDs = [first, id];
                return true;
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
    private const int LARGE_SCAN_RESERVE_THRESHOLD = 400;
    private const int LARGE_SCAN_RESERVED_NODES = 1536;
    private const ushort MIN_COMPONENT_NODE_TYPE = 1000;
}
