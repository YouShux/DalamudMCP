using System.Text.Json;
using DalamudMCP.Shared;

namespace DalamudMCP.Plugin.Tools;

public sealed class GameTools : IDisposable
{
    private readonly GameServices services;
    private readonly GameState state;
    private readonly AddonTools addons;
    private readonly PluginIntegration integration;
    private readonly ChatHistory chat;
    private readonly CancellationTokenSource lifetime = new();

    public IReadOnlyList<ToolDefinition> Tools { get; }

    public GameTools(GameServices services)
    {
        this.services = services;
        state = new(services);
        addons = new(services);
        integration = new(services);
        chat = new(services);
        Tools = CreateTools();
    }

    public async ValueTask<JsonElement> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        var definition = Tools.FirstOrDefault(tool => tool.Name == name) ?? throw new ArgumentException($"未知工具 {name}。");
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("工具参数必须为对象。");
        var properties = definition.InputSchema.GetProperty("properties");
        var validNames = properties.EnumerateObject().Select(property => ToolArguments.Normalize(property.Name)).ToHashSet();
        foreach (var argument in arguments.EnumerateObject())
            if (!validNames.Contains(ToolArguments.Normalize(argument.Name)))
                throw new ArgumentException($"未知参数 {argument.Name}。");

        var input = new ToolArguments(arguments);
        foreach (var required in definition.InputSchema.GetProperty("required").EnumerateArray())
            if (!input.Has(required.GetString() ?? string.Empty))
                throw new ArgumentException($"缺少参数 {required.GetString()}。");

        if (name == "capture_game_screenshot")
            return GameScreenshot.Capture(input.Text("capture_area", "client"));
        if (name == "reload_plugin")
            return ToolJson.Success(await integration.ReloadAsync(input.Text("plugin_name"), linked.Token));
        if (name == "unsafe_invoke_plugin_ipc" && !input.Boolean("run_on_framework_thread", true))
            return ToolJson.Success(integration.Call(name, input));

        object Execute()
        {
            linked.Token.ThrowIfCancellationRequested();
            return name switch
            {
                "get_chat_log" => chat.Read(input),
                "get_session_status" => new
                {
                    loggedIn = services.ClientState.IsLoggedIn,
                    territoryID = services.ClientState.TerritoryType,
                    localPlayerAvailable = services.ObjectTable.LocalPlayer is not null,
                    tools = Tools.Count,
                },
                "get_addon_list" or "get_addon_tree" or "get_addon_strings" or "send_addon_input" or
                    "send_addon_event" or "send_addon_callback_values" or "select_addon_menu_item" => addons.Call(name, input),
                "unsafe_invoke_plugin_ipc" or "invoke_plugin_ipc" or "slash_command" or
                    "plugin_data_subscribe" or "plugin_data_unsubscribe" or "plugin_data_poll" => integration.Call(name, input),
                _ => state.Call(name, input),
            };
        }

        var result = services.Framework.IsInFrameworkUpdateThread
            ? Execute()
            : await services.Framework.RunOnFrameworkThread(Execute).WaitAsync(linked.Token).ConfigureAwait(false);
        if (name == "teleport_to_aetheryte" && result is Task<bool> teleportRequest)
        {
            var accepted = await teleportRequest.WaitAsync(linked.Token).ConfigureAwait(false);
            result = new
            {
                succeeded = accepted,
                destination = input.Text("query"),
                provider = "OmniToolbox",
                message = accepted ? "传送请求已接受，请查询区域和坐标确认到达。" : "Omni 未接受传送请求，请检查目的地、模块开关、验证状态或当前传送任务。"
            };
        }
        return ToolJson.Success(result);
    }

    public void Dispose()
    {
        lifetime.Cancel();
        state.Dispose();
        chat.Dispose();
        integration.Dispose();
        lifetime.Dispose();
    }

    private static ToolDefinition[] CreateTools()
    {
        var text = new { type = "string", minLength = 1, maxLength = 4096 };
        var flag = new { type = "boolean" };
        var distance = new { type = "number", minimum = 1, maximum = 1000 };
        var count = new { type = "integer", minimum = 1, maximum = 1000 };
        var addon = new Dictionary<string, object> { ["addon_name"] = text };
        var nearby = new Dictionary<string, object> { ["max_distance"] = distance, ["name_contains"] = text, ["include_players"] = flag };
        var unsafeIPC = new Dictionary<string, object>
        {
            ["callgate"] = text,
            ["result_kind"] = new { type = "string", @enum = new[] { "bool", "int", "uint", "long", "ulong", "float", "double", "string" } },
            ["argument_kinds"] = text,
            ["arguments_json"] = text,
            ["run_on_framework_thread"] = flag,
        };
        return
        [
            Define("get_player_context", "玩家状态", "读取当前角色、职业、位置及目标。"),
            Define("get_duty_context", "副本状态", "读取当前区域及真实副本完成状态。"),
            Define("get_inventory_summary", "背包概览", "读取已加载的背包、装备、兵装库及金币。"),
            Define("get_addon_list", "游戏窗口", "列出游戏当前加载的原生窗口。"),
            Define("get_addon_tree", "窗口节点", "读取指定原生窗口的节点树，最多 4096 个节点、16 层。", addon, ["addon_name"]),
            Define("get_addon_strings", "窗口文本", "读取窗口文本节点及 AtkValue 字符串。", addon, ["addon_name"]),
            Define("get_fate_context", "危命任务", "读取附近危命任务、进度及剩余时间。", new() { ["max_distance"] = distance, ["name_contains"] = text }),
            Define("get_nearby_interactables", "附近对象", "按距离读取可选中对象。", nearby),
            Define("get_quest_status", "任务进度", "按任务 ID 或名称查询接受、完成及阶段。", new() { ["quest_id"] = new { type = "integer", minimum = 1, maximum = uint.MaxValue }, ["query"] = text, ["max_results"] = count }),
            Define("get_available_quests", "可接任务", "读取当前地图实际显示的可接任务标记。", new() { ["name_contains"] = text, ["max_results"] = count }),
            Define("get_current_quest_objective", "追踪任务目标", "读取追踪任务阶段、地图目标及地图跳转信息。"),
            Define("get_chat_log", "聊天记录", "读取插件加载后保留的最近 1000 条聊天记录。", new() { ["channels"] = new { type = "array", maxItems = 100, items = text }, ["since"] = new { type = "string", format = "date-time" }, ["max_count"] = count }),
            Define("capture_game_screenshot", "游戏截图", "截取当前游戏客户端或窗口并直接返回 PNG 图片。", new() { ["capture_area"] = new { type = "string", @enum = new[] { "client", "window" } } }),
            Define("get_session_status", "游戏连接状态", "读取登录状态及游戏工具数量。"),
            Define("target_object", "选中对象", "通过对象 ID 选中当前区域对象。", new() { ["game_object_id"] = text }, ["game_object_id"], ToolAccess.Action),
            Define("interact_with_target", "交互目标", "与当前目标交互，可校验预期目标 ID。", new() { ["expected_game_object_id"] = text, ["check_line_of_sight"] = flag }, access: ToolAccess.Action),
            Define("move_to_entity", "移动到对象", "通过 vnavmesh 寻路移动到指定对象，不模拟按键。", new() { ["game_object_id"] = text, ["allow_flight"] = flag }, ["game_object_id"], ToolAccess.Action),
            Define("move_to_nearby_interactable", "移动到附近对象", "按名称找到最近可交互对象，通过 vnavmesh 移动。", new(nearby) { ["allow_flight"] = flag }, ["name_contains"], ToolAccess.Action),
            Define("teleport_to_aetheryte", "传送水晶", "按当前语言名称或 ID 传送已解锁的大水晶；城内以太网复用 Omni，需开启“更好的传送管理”。Omni 返回成功表示请求已接受，到达情况请查询区域和坐标。", new() { ["query"] = text }, ["query"], ToolAccess.Action),
            Define("use_duty_action", "使用任务技能", "使用当前副本任务技能的第 1 或 2 个槽位。", new() { ["slot"] = new { type = "integer", minimum = 1, maximum = 2 } }, ["slot"], ToolAccess.Action),
            Define("send_addon_input", "窗口输入", "向白名单窗口发送游戏内输入。", new(addon) { ["input_type"] = new { type = "string", @enum = new[] { "gamepad", "dpad", "back" } }, ["input_id"] = new { type = "integer", minimum = 0, maximum = 65535 }, ["auxiliary_state"] = flag, ["input_state"] = new { type = "string", @enum = new[] { "down", "up", "held" } } }, ["addon_name", "input_type", "input_id"], ToolAccess.Action),
            Define("send_addon_event", "窗口事件", "向白名单窗口节点发送游戏内事件，不模拟系统点击。", new(addon) { ["event_type"] = text, ["event_param"] = new { type = "integer" }, ["collision_index"] = new { type = "integer", minimum = 0, maximum = 4095 }, ["node_id"] = new { type = "integer", minimum = 0, maximum = int.MaxValue } }, ["addon_name", "event_type"], ToolAccess.Action),
            Define("send_addon_callback_values", "窗口回调", "向白名单窗口发送 1 至 32 个整数回调参数。", new(addon) { ["values"] = new { type = "array", minItems = 1, maxItems = 32, items = new { type = "integer" } } }, ["addon_name", "values"], ToolAccess.Action),
            Define("select_addon_menu_item", "选择菜单", "按唯一文本选择 SelectString、SelectIconString 或 TelepotTown 条目。", new(addon) { ["label"] = text, ["contains_match"] = flag }, ["addon_name", "label"], ToolAccess.Action),
            Define("unsafe_invoke_plugin_ipc", "调用任意 IPC", "按明确参数类型调用任意已注册插件 IPC。", unsafeIPC, ["callgate", "result_kind"], ToolAccess.Unsafe),
            Define("reload_plugin", "重载开发插件", "重载指定开发插件；禁止重载本插件。", new() { ["plugin_name"] = text }, ["plugin_name"], ToolAccess.Unsafe),
            Define("slash_command", "执行插件命令", "执行已注册的 Dalamud 斜杠命令，不向服务器发送聊天命令。", new() { ["command"] = new { type = "string", minLength = 1, maxLength = 256 } }, ["command"], ToolAccess.Unsafe),
            Define("invoke_plugin_ipc", "调用插件 MCP 接口", "调用 PluginName.MCP.Method 插件约定 IPC。", new() { ["plugin_name"] = text, ["method"] = text, ["arguments_json"] = text }, ["plugin_name", "method"], ToolAccess.Unsafe),
            Define("plugin_data_subscribe", "订阅插件数据", "创建有界回传通道，最多 32 个通道，每通道 1000 条。", new() { ["plugin_name"] = text, ["channel"] = text }, ["plugin_name", "channel"], ToolAccess.Unsafe),
            Define("plugin_data_unsubscribe", "取消数据订阅", "注销插件数据回传通道。", new() { ["channel"] = text }, ["channel"], ToolAccess.Unsafe),
            Define("plugin_data_poll", "读取订阅数据", "取出并消费通道中排队的数据。", new() { ["channel"] = text, ["max_items"] = count }, ["channel"], ToolAccess.Unsafe),
        ];
    }

    private static ToolDefinition Define(string name, string title, string description, Dictionary<string, object>? properties = null, string[]? required = null, ToolAccess access = ToolAccess.Read)
        => new(name, title, description, ToolJson.Element(new { type = "object", properties = properties ?? [], required = required ?? [], additionalProperties = false }), access);
}
