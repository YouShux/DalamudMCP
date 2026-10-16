using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DalamudMCP.Plugin.Tools;

internal sealed unsafe class AddonTools(GameServices services)
{
    private static readonly HashSet<string> ActionAddons = new(StringComparer.Ordinal)
    {
        "SelectString", "SelectIconString", "SelectYesno", "Talk", "TelepotTown", "Teleport", "Journal", "JournalDetail", "ContentsFinder", "ContentsFinderConfirm", "Request", "InputNumeric", "ContextMenu",
    };

    public object Call(string name, ToolArguments arguments)
    {
        if (name == "get_addon_list")
            return List();
        var addonName = arguments.Text("addon_name");
        var wrapper = services.GameGui.GetAddonByName(addonName, 1);
        var addon = services.GameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        if (wrapper.IsNull || !wrapper.IsReady || addon == null)
            throw new InvalidOperationException($"窗口 {addonName} 未准备就绪。");
        if (name == "get_addon_tree")
        {
            var visited = new HashSet<nint>();
            return new { addonName, visible = wrapper.IsVisible, roots = ReadNodes(addon->RootNode, visited, 0), truncated = visited.Count >= 4096 };
        }
        if (name == "get_addon_strings")
            return Strings(addonName, addon);
        if (!services.ClientState.IsLoggedIn || !wrapper.IsVisible || !ActionAddons.Contains(addonName))
            throw new InvalidOperationException("窗口动作仅允许在角色登录后，对已就绪且可见的白名单窗口执行。");
        return name switch
        {
            "send_addon_input" => Input(addon, arguments),
            "send_addon_event" => Event(addon, arguments),
            "send_addon_callback_values" => Callback(addon, arguments),
            "select_addon_menu_item" => Select(addonName, addon, arguments),
            _ => throw new ArgumentException("未知窗口工具。"),
        };
    }

    private object List()
    {
        var manager = RaptureAtkUnitManager.Instance();
        var addons = new List<object>();
        if (manager == null)
            return addons;
        var count = Math.Min(manager->AllLoadedUnitsList.Entries.Length, manager->AllLoadedUnitsList.Count);
        for (var i = 0; i < count; i++)
        {
            var addon = manager->AllLoadedUnitsList.Entries[i].Value;
            if (addon == null)
                continue;
            var name = addon->NameString;
            var wrapper = services.GameGui.GetAddonByName(name, 1);
            addons.Add(new { addonName = name, visible = wrapper.IsVisible, ready = wrapper.IsReady, actionAllowed = ActionAddons.Contains(name) });
        }
        return addons;
    }

    private static object[] ReadNodes(AtkResNode* node, HashSet<nint> visited, int depth)
    {
        var result = new List<object>();
        while (node != null && depth <= 16 && visited.Count < 4096)
        {
            if (!visited.Add((nint)node))
                break;
            var children = new List<object>(ReadNodes(node->ChildNode, visited, depth + 1));
            var component = node->GetAsAtkComponentNode();
            if (component != null && component->Component != null && depth < 16)
                foreach (var child in component->Component->UldManager.Nodes)
                    children.AddRange(ReadNodes(child.Value, visited, depth + 1));
            var text = node->GetAsAtkTextNode();
            result.Add(new
            {
                nodeID = node->NodeId,
                type = node->GetNodeType().ToString(),
                visible = node->IsVisible(),
                x = node->X,
                y = node->Y,
                width = node->Width,
                height = node->Height,
                text = text == null ? null : text->NodeText.ToString(),
                children,
            });
            node = node->NextSiblingNode;
        }
        return result.ToArray();
    }

    private static object Strings(string name, AtkUnitBase* addon)
    {
        var strings = new List<object>();
        var visited = new HashSet<nint>();
        CollectStrings(addon->RootNode, strings, visited, 0);
        for (var i = 0; i < Math.Min((int)addon->AtkValuesCount, 4096); i++)
        {
            var value = addon->AtkValues[i];
            if (value.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString)
                strings.Add(new { source = "atkValue", index = i, text = value.String.ToString() });
        }
        return new { addonName = name, strings };
    }

    private static void CollectStrings(AtkResNode* node, List<object> output, HashSet<nint> visited, int depth)
    {
        while (node != null && depth <= 16 && visited.Count < 4096)
        {
            if (!visited.Add((nint)node))
                return;
            var text = node->GetAsAtkTextNode();
            if (text != null && text->NodeText.StringLength > 0)
                output.Add(new { source = "node", nodeID = node->NodeId, text = text->NodeText.ToString() });
            CollectStrings(node->ChildNode, output, visited, depth + 1);
            var component = node->GetAsAtkComponentNode();
            if (component != null && component->Component != null)
                foreach (var child in component->Component->UldManager.Nodes)
                    CollectStrings(child.Value, output, visited, depth + 1);
            node = node->NextSiblingNode;
        }
    }

    private static object Input(AtkUnitBase* addon, ToolArguments arguments)
    {
        var id = arguments.Integer("input_id", 0, 0, 65535);
        var auxiliary = arguments.Boolean("auxiliary_state");
        var type = arguments.Text("input_type").ToLowerInvariant();
        bool succeeded;
        switch (type)
        {
            case "dpad":
                succeeded = addon->HandleDPadInput(id, auxiliary);
                break;
            case "back":
            case "backbutton":
                succeeded = addon->HandleBackButtonInput(id, auxiliary);
                break;
            case "gamepad":
                var state = arguments.Text("input_state", auxiliary ? "held" : "down").ToLowerInvariant() switch
                {
                    "down" => InputState.Down,
                    "up" => InputState.Up,
                    "held" or "repeat" => InputState.Repeat,
                    _ => throw new ArgumentException("input_state 必须为 down、up 或 held。"),
                };
                var data = new AtkEventData.AtkInputData { InputId = id, State = state };
                succeeded = addon->HandleCustomInput(&data);
                break;
            default:
                throw new ArgumentException("input_type 必须为 gamepad、dpad 或 back。");
        }
        return new { succeeded };
    }

    private static object Callback(AtkUnitBase* addon, ToolArguments arguments)
    {
        var values = arguments.Element("values").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        if (values.Length is < 1 or > 32)
            throw new ArgumentException("values 必须包含 1 至 32 个整数。");
        var native = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
            native[i] = new AtkValue { Type = AtkValueType.Int, Int = values[i] };
        addon->FireCallback((uint)values.Length, native, false);
        return new { succeeded = true };
    }

    private static object Event(AtkUnitBase* addon, ToolArguments arguments)
    {
        if (!Enum.TryParse<AtkEventType>(arguments.Text("event_type"), true, out var type) || !Enum.IsDefined(type))
            throw new ArgumentException("不支持该 AtkEventType；请提供当前客户端定义的事件名称。");
        AtkResNode* node = null;
        if (arguments.Has("node_id"))
            node = addon->GetNodeById((uint)arguments.Integer("node_id", 0, 0, int.MaxValue));
        else if (arguments.Has("collision_index"))
        {
            var index = arguments.Integer("collision_index", 0, 0, 4095);
            if (index >= addon->CollisionNodeListCount)
                throw new ArgumentException("collision_index 超出当前窗口碰撞节点范围。");
            node = addon->CollisionNodeList[index];
        }
        else
            node = addon->FocusNode;
        if (node == null || !node->IsVisible())
            throw new InvalidOperationException("未找到可见的事件目标节点。");
        if (!arguments.Has("event_param") && !node->IsEventRegistered(type))
            throw new InvalidOperationException("节点未注册该事件，请指定已确认的 event_param。");
        var parameter = arguments.Has("event_param")
            ? arguments.Integer("event_param", 0, int.MinValue, int.MaxValue)
            : (int)node->GetEventParam(type);
        var data = new AtkEventData();
        if (type is AtkEventType.InputNavigation or AtkEventType.InputReceived)
            data.InputData = new AtkEventData.AtkInputData { InputId = parameter, State = InputState.Down };
        else
            data.MouseData = new AtkEventData.AtkMouseData { PosX = (short)Math.Clamp(node->ScreenX + node->Width / 2f, short.MinValue, short.MaxValue), PosY = (short)Math.Clamp(node->ScreenY + node->Height / 2f, short.MinValue, short.MaxValue), ButtonId = 1 };
        var dispatch = new AtkEventDispatcher.Event
        {
            State = new AtkEventState { EventType = type },
            EventData = data,
        };
        var handled = node->DispatchEvent(&dispatch);
        if (!handled)
        {
            var native = new AtkEvent { Node = node, Target = (AtkEventTarget*)node, Listener = (AtkEventListener*)addon, Param = (uint)parameter, State = new AtkEventState { EventType = type } };
            addon->ReceiveEvent(type, parameter, &native, &data);
        }
        return new { succeeded = true, dispatched = handled, nodeID = node->NodeId };
    }

    private object Select(string name, AtkUnitBase* addon, ToolArguments arguments)
    {
        var label = arguments.Text("label");
        var contains = arguments.Boolean("contains_match");
        var candidates = new List<(int Index, string Label)>();
        if (name is "SelectString" or "SelectIconString")
        {
            var popup = name == "SelectString" ? (PopupMenu*)&((AddonSelectString*)addon)->PopupMenu : (PopupMenu*)&((AddonSelectIconString*)addon)->PopupMenu;
            if (popup->EntryNames == null || popup->EntryCount is < 1 or > 1024)
                throw new InvalidOperationException("菜单条目未准备就绪。");
            for (var i = 0; i < popup->EntryCount; i++)
                candidates.Add((i, popup->EntryNames[i].ToString()));
        }
        else if (name == "TelepotTown")
        {
            var agent = AgentTelepotTown.Instance();
            if (agent == null || !agent->IsAgentActive() || agent->Data == null)
                throw new InvalidOperationException("以太传送网未准备就绪。");
            var data = agent->Data;
            var count = Math.Min(data->Entries.Length, data->AetheryteCount);
            for (var i = 0; i < count; i++)
            {
                var entry = data->Entries[i];
                if (entry.IsLocked || entry.IsUnusable || entry.IsCurrent)
                    continue;
                var place = services.DataManager.GetExcelSheet<Lumina.Excel.Sheets.PlaceName>().GetRowOrDefault(entry.PlaceNameId);
                candidates.Add((i, place?.Name.ToString() ?? entry.AetheryteId.ToString()));
            }
        }
        else
            throw new ArgumentException("此窗口不支持按文本选择菜单。");
        var matches = candidates.Where(candidate => contains ? candidate.Label.Contains(label, StringComparison.OrdinalIgnoreCase) : candidate.Label.Equals(label, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException(matches.Length == 0 ? "没有匹配的菜单条目。" : "菜单文本匹配多个条目，请使用完整文本。");
        var selected = matches[0];
        var succeeded = true;
        if (name == "TelepotTown")
            AgentTelepotTown.Instance()->TeleportToAetheryte((byte)selected.Index);
        else
            succeeded = addon->FireCallbackInt(selected.Index);
        return new { succeeded, selected.Index, selected.Label };
    }
}
