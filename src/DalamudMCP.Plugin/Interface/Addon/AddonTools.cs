using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DalamudMCP.Plugin.Tools;

internal sealed unsafe partial class AddonTools(GameServices services)
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
        if (name == SNAPSHOT_TOOL) return NodeSnapshot(addonName, arguments, out _);
        var wrapper = services.GameGui.GetAddonByName(addonName, 1);
        var addon = services.GameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        if (name == "inspect_menu_choice")
        {
            if (!GameDiagnostics.CurrentCnLayoutKnown) return new { status = "unsupported_sdk" };
            if (addonName is not ("SelectString" or "SelectIconString" or "TelepotTown" or "Teleport"))
                return new { status = "unsupported_menu", addonName };
            if (wrapper.IsNull || !wrapper.IsReady || !wrapper.IsVisible || addon == null)
                return new { status = "not_visible", addonName };
            return addonName == "Teleport" ? PreviewTeleport((AddonTeleport*)addon, arguments) : Select(addonName, addon, arguments, preview: true);
        }
        if (wrapper.IsNull || !wrapper.IsReady || addon == null)
            throw new InvalidOperationException($"窗口 {addonName} 未准备就绪。");
        if (name == "get_addon_tree")
        {
            var visited = new HashSet<nint>();
            return new { addonName, visible = wrapper.IsVisible, roots = ReadNodes(addon->RootNode, visited, 0), truncated = visited.Count >= 4096 };
        }
        if (name == "get_addon_strings")
            return Strings(addonName, addon);
        if (name == "get_addon_nodes")
            return QueryNodes(addonName, addon, wrapper.IsVisible, arguments);
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
        return new { succeeded, requestAccepted = succeeded, resultObserved = false };
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
        return new { succeeded = true, requestDispatched = true, resultObserved = false };
    }

    private static EventResult Event(AtkUnitBase* addon, ToolArguments arguments)
    {
        if (!Enum.TryParse<AtkEventType>(arguments.Text("event_type"), true, out var type) || !Enum.IsDefined(type))
            throw new ArgumentException("不支持该 AtkEventType；请提供当前客户端定义的事件名称。");
        var scoped = arguments.Has("label") || arguments.Has("component_path") || arguments.Has("component_type") || arguments.Has("observe");
        AtkResNode* node;
        if (scoped)
            node = ResolveEventNode(addon, arguments);
        else if (arguments.Has("node_id"))
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
        var nodeID = node->NodeId;
        var handled = node->DispatchEvent(&dispatch);
        if (!handled && !scoped)
        {
            var native = new AtkEvent { Node = node, Target = (AtkEventTarget*)node, Listener = (AtkEventListener*)addon, Param = (uint)parameter, State = new AtkEventState { EventType = type } };
            addon->ReceiveEvent(type, parameter, &native, &data);
        }
        return new(handled || !scoped, handled, true, false, nodeID);
    }

    internal sealed record EventResult(bool Succeeded, bool Dispatched, bool RequestDispatched, bool ResultObserved, uint NodeID);

    private static object PreviewTeleport(AddonTeleport* addon, ToolArguments input)
    {
        const string addonName = "Teleport";
        var label = input.Text("label");
        var contains = input.Boolean("contains_match");
        var tree = addon->TeleportTreeList;
        var size = tree == null ? 0 : tree->Items.LongCount;
        if (tree == null || tree->LayoutRefreshPending || size is < 1 or > 1024 || tree->ItemCount != size)
        {
            if (!GameDiagnostics.CurrentCnLayoutKnown) return new { status = "list_not_ready", addonName, dispatched = false };
            // 国服此版本的 SDK 无法从树字段读出有效的列表行。
            var scan = AddonNodeScanner.Scan((AtkUnitBase*)addon, new(null, label, null, null, !contains), true, true, 20);
            return new { status = scan.Incomplete ? "scan_incomplete" : scan.MatchCount == 1 ? "visible_text_only" :
                scan.MatchCount == 0 ? "not_found" : "ambiguous", addonName, addonID = addon->AtkUnitBase.Id,
                matched = scan.MatchCount, dispatched = false, indexKind = "unavailable", scanIncomplete = scan.Incomplete,
                candidates = scan.Matches.Select(item => item.Info).ToArray() };
        }
        var candidates = new List<object>(20);
        var count = 0;
        var incomplete = false;
        Span<byte> buffer = stackalloc byte[512];
        for (var index = 0; index < size; index++)
        {
            var item = tree->Items[index].Value;
            if (item == null) { incomplete = true; continue; }
            if (item->IsHidden || (item->State & TreeListItemState.Disabled) != 0 ||
                (item->Type & (TreeListItemType.Group | TreeListItemType.SectionHeader)) != 0) continue;
            var length = item->StringValues.LongCount is > 0 and <= 1024
                ? NativeMemoryReader.ReadCString((nint)item->StringValues[0].Value, buffer) : -1;
            if (length < 0) { incomplete = true; continue; }
            var text = new Lumina.Text.ReadOnly.ReadOnlySeStringSpan(buffer[..length]).ExtractText();
            if (!(contains ? text.Contains(label, StringComparison.OrdinalIgnoreCase) : text.Equals(label, StringComparison.OrdinalIgnoreCase))) continue;
            if (++count <= 20) candidates.Add(new { index, label = text });
        }
        return new
        {
            status = count > 1 ? "ambiguous" : incomplete ? "scan_incomplete" : count == 1 ? "matched" : "not_found",
            addonName, addonID = addon->AtkUnitBase.Id, selectedTab = addon->SelectedTab, matched = count, dispatched = false,
            indexKind = "teleport_tree_row", scanIncomplete = incomplete, candidates,
        };
    }

    private object Select(string name, AtkUnitBase* addon, ToolArguments arguments, bool preview = false)
    {
        var label = arguments.Text("label");
        return Select(name, addon, label, arguments.Boolean("contains_match"), preview);
    }

    internal string DispatchExactNPCMenu(string name, NPCDialogueSample observed, string label, Func<bool> begin)
    {
        if (name is not ("SelectString" or "SelectIconString") || ReadNPCDialogue(name, observed.Text) is not { } current ||
            !current.Matches || current.AddonID != observed.AddonID || current.Address != observed.Address)
            return "menu_context_changed";
        return Select(name, (AtkUnitBase*)current.Address, label, false, false, begin) is MenuSelection selection
            ? selection.Succeeded ? "requested" : selection.RequestDispatched ? "menu_not_accepted" : "menu_not_dispatched"
            : "menu_not_dispatched";
    }

    private object Select(string name, AtkUnitBase* addon, string label, bool contains, bool preview, Func<bool>? begin = null)
    {
        var candidates = new List<(int Index, string Label)>();
        if (name is "SelectString" or "SelectIconString")
        {
            var popup = name == "SelectString" ? (PopupMenu*)&((AddonSelectString*)addon)->PopupMenu : (PopupMenu*)&((AddonSelectIconString*)addon)->PopupMenu;
            if (popup->EntryNames == null || popup->EntryCount is < 1 or > 1024)
            {
                if (begin is not null) return new MenuSelection(false, false, false, -1, label);
                throw new InvalidOperationException("菜单条目未准备就绪。");
            }
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
        var matches = candidates.Where(candidate => contains ? candidate.Label.Contains(label, StringComparison.OrdinalIgnoreCase) : candidate.Label.Equals(label, begin is null ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToArray();
        if (preview)
            return new
            {
                status = matches.Length == 1 ? "matched" : matches.Length == 0 ? "not_found" : "ambiguous",
                addonName = name, addonID = addon->Id, matched = matches.Length, dispatched = false,
                candidates = matches.Take(20).Select(candidate => new { candidate.Index, candidate.Label }).ToArray(),
            };
        if (begin is not null && (matches.Length != 1 || !begin())) return new MenuSelection(false, false, false, -1, label);
        if (matches.Length != 1)
            throw new InvalidOperationException(matches.Length == 0 ? "没有匹配的菜单条目。" : "菜单文本匹配多个条目，请使用完整文本。");
        var selected = matches[0];
        var succeeded = true;
        if (name == "TelepotTown")
            AgentTelepotTown.Instance()->TeleportToAetheryte((byte)selected.Index);
        else
            succeeded = addon->FireCallbackInt(selected.Index);
        return new MenuSelection(succeeded, true, false, selected.Index, selected.Label);
    }

    private sealed record MenuSelection(bool Succeeded, bool RequestDispatched, bool ResultObserved, int Index, string Label);
}
