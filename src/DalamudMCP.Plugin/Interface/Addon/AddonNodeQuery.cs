using System.Text.Json;

namespace DalamudMCP.Plugin.Tools;

internal sealed record AddonNodeQuery(uint? NodeID, string? Label, string? ComponentType, uint[]? ComponentPath, bool ContainsMatch)
{
    public bool HasNodeSelector => NodeID.HasValue || Label is not null || ComponentType is not null || ComponentPath is not null;

    public static AddonNodeQuery Parse(ToolArguments arguments, bool allowContains)
    {
        uint[]? path = null;
        if (arguments.Has("component_path"))
        {
            var value = arguments.Element("component_path");
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MAX_COMPONENT_DEPTH)
                throw new ArgumentException($"component_path 必须是最多 {MAX_COMPONENT_DEPTH} 项的组件 ID 数组。");
            path = new uint[value.GetArrayLength()];
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetUInt32(out var id))
                    throw new ArgumentException("component_path 只能包含有效的组件 ID。");
                path[index++] = id;
            }
        }
        uint? nodeID = null;
        if (arguments.Has("node_id"))
        {
            var value = arguments.Element("node_id");
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out var id))
                throw new ArgumentException("node_id 必须为有效的节点 ID。");
            nodeID = id;
        }
        var label = arguments.Has("label") ? arguments.Text("label") : null;
        var componentType = arguments.Has("component_type") ? arguments.Text("component_type") : null;
        var contains = allowContains && arguments.Boolean("contains_match");
        if (contains && label is null) throw new ArgumentException("contains_match 需要 label。");
        return new(nodeID, label, componentType, path, contains);
    }

    public bool Matches(uint nodeID, string? label, string? componentType) =>
        (!NodeID.HasValue || NodeID.Value == nodeID) &&
        (ComponentType is null || string.Equals(ComponentType, componentType, StringComparison.OrdinalIgnoreCase)) &&
        (Label is null || label is not null && (ContainsMatch
            ? label.Contains(Label, StringComparison.OrdinalIgnoreCase)
            : label.Equals(Label, StringComparison.OrdinalIgnoreCase)));

    public const int MAX_COMPONENT_DEPTH = 8;
}

internal sealed record AddonObservation(string AddonName, bool ExpectedVisible, bool? ExpectedChecked, AddonNodeQuery Query)
{
    public static AddonObservation Parse(ToolArguments arguments)
    {
        var value = arguments.Element("observe");
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("observe 必须是对象。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var name = ToolArguments.Normalize(property.Name);
            if (name is not ("addonname" or "visible" or "checked" or "nodeid" or "label" or "componenttype" or "componentpath") || !names.Add(name))
                throw new ArgumentException($"observe 参数未知或重复：{property.Name}。");
        }
        return ParseSurface(new ToolArguments(value), arguments.Text("addon_name"));
    }

    public static AddonObservation ParseSurface(ToolArguments input, string? fallbackAddon = null)
    {
        var query = AddonNodeQuery.Parse(input, false);
        if (query.HasNodeSelector && query.NodeID is null && query.Label is null)
            throw new ArgumentException("观察节点需要 node_id 或 label。");
        bool? expectedChecked = input.Has("checked") ? input.Boolean("checked") : null;
        if (expectedChecked.HasValue)
        {
            if (!query.HasNodeSelector || input.Has("visible") ||
                query.ComponentType is not null && !query.ComponentType.Equals("CheckBox", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("观察 checked 需要复选框的 node_id 或 label，不能同时指定 visible。");
            query = query with { ComponentType = "CheckBox" };
        }
        return new(input.Text("addon_name", fallbackAddon), input.Boolean("visible", true), expectedChecked, query);
    }

    public bool? IsMatch(bool? visible, bool? isChecked)
    {
        if (ExpectedChecked is bool expected)
            return visible == true && isChecked is bool actual ? actual == expected : null;
        return visible is bool visibleValue ? visibleValue == ExpectedVisible : null;
    }
}
