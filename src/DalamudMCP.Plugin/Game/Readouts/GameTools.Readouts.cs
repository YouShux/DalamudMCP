using DalamudMCP.Shared;

namespace DalamudMCP.Plugin.Tools;

public sealed partial class GameTools
{
    private async Task<object> ReadDutyOrCurrencyAsync(string name, ToolArguments input, Func<bool> allowed, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(3000);
        var current = deadline.Token;
        AddonTools.DutyDispatch? dispatched = null;
        var dispatchStarted = 0;
        try
        {
            if (name != AddonTools.DUTY_SELECT_TOOL)
                return await services.Framework.RunOnTick(() =>
                {
                    current.ThrowIfCancellationRequested();
                    return name == GameReadouts.CURRENCY_TOOL ? readouts.CurrencyBalances(input) :
                        addons.DutyBrowser(input, name == AddonTools.DUTY_ENTRIES_TOOL);
                }, cancellationToken: current).WaitAsync(current).ConfigureAwait(false);

            dispatched = await services.Framework.RunOnTick(() => addons.DispatchDuty(input, allowed, current, () => Interlocked.CompareExchange(ref dispatchStarted, 1, 0) == 0),
                cancellationToken: current).WaitAsync(current).ConfigureAwait(false);
            if (!dispatched.Dispatched)
                return new { dispatched.Status, dispatched.Reason, requestDispatched = false, resultObserved = dispatched.Status == "already_matched", dispatched.DutyID, dispatched.GroupID };
            if (dispatched.Status != "requested")
                return new { dispatched.Status, dispatched.Reason, dispatchMayHaveStarted = true, resultObserved = false };
            var (status, reason) = await AwaitDutyReadbackAsync(observe => services.Framework.RunOnTick(() => addons.ObserveDuty(dispatched),
                cancellationToken: observe), TimeSpan.FromSeconds(2), current).ConfigureAwait(false);
            return new { status, reason, requestDispatched = true, resultObserved = status == "observed", dispatched.DutyID, dispatched.GroupID };
        }
        catch (OperationCanceledException)
        {
            var mayHaveStarted = Interlocked.CompareExchange(ref dispatchStarted, 2, 0) == 1;
            return new { status = token.IsCancellationRequested ? "cancelled" : "timeout", requestDispatched = dispatched?.Dispatched ?? (mayHaveStarted ? (bool?)null : false),
                dispatchMayHaveStarted = mayHaveStarted, resultObserved = false };
        }
    }

    internal static async Task<(string Status, string? Reason)> AwaitDutyReadbackAsync(Func<CancellationToken, Task<string>> read,
        TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
                var status = await read(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                if (status != "requested") return (status, null);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        {
            return ("requested", "readback_timeout");
        }
    }

    private static ToolDefinition[] ReadoutTools()
    {
        var coordinate = new { type = "number", minimum = -10000, maximum = 10000 };
        var route = new Dictionary<string, object>
        {
            ["x"] = coordinate, ["y"] = coordinate, ["z"] = coordinate,
            ["expected_territory_id"] = new { type = "integer", minimum = 1, maximum = uint.MaxValue },
        };
        var routeRequired = new[] { "x", "y", "z", "expected_territory_id" };
        var hotbar = new Dictionary<string, object>
        {
            ["bar"] = new { type = "integer", minimum = 1, maximum = 10 },
            ["slot"] = new { type = "integer", minimum = 1, maximum = 12 },
        };
        var positiveID = new { type = "integer", minimum = 1, maximum = uint.MaxValue };
        var action = new Dictionary<string, object>
        {
            ["action_id"] = positiveID, ["action_name"] = new { type = "string", maxLength = 128 },
            ["action_kind"] = new { type = "string", @enum = new[] { "action", "general_action" } },
            ["target_id"] = new { type = "string" },
        };
        return
        [
            Define(GameReadouts.APPLY_GEARSET_TOOL, "应用保存的配装", "按槽位或唯一完整名称应用配装。核对原配装、区域和目标职业，再确认槽位、职业和实际装备。游戏可能应用关联投影。", new()
            {
                ["slot"] = IntegerRange(0, 99), ["name"] = new { type = "string", minLength = 1, maxLength = 64 },
                ["expected_current_slot"] = IntegerRange(0, 99),
                ["expected_job_id"] = positiveID, ["expected_territory_id"] = positiveID,
            }, ["expected_current_slot", "expected_job_id", "expected_territory_id"], ToolAccess.Action),
            Define(GameReadouts.CURRENCY_TOOL, "读取角色货币余额", "读取已载入的货币容器和周限神典石数据；未知余额不计为零。", new()
            { ["include_zero"] = new { type = "boolean" }, ["offset"] = Range(0, 10000), ["limit"] = Range(1, 100) }),
            Define(AddonTools.DUTY_BROWSER_TOOL, "读取任务搜索器状态", "读取已打开的任务搜索器、分组、选择和排队状态。"),
            Define(AddonTools.DUTY_ENTRIES_TOOL, "列出当前任务分组的副本", "只列出当前窗口可辨识的行，按副本表行 ID 标识。", new()
            { ["group_id"] = Range(0, 255), ["offset"] = Range(0, 4096), ["limit"] = Range(1, 100) }),
            Define(AddonTools.DUTY_SELECT_TOOL, "请求选中任务搜索器条目", "核对窗口状态令牌并派发一次，独立回读选择结果。", new()
            {
                ["operation"] = new { type = "string", @enum = new[] { "open_duty", "select_group" } },
                ["duty_id"] = positiveID, ["group_id"] = Range(0, 9), ["expected_territory_id"] = positiveID,
                ["state_token"] = new { type = "string", format = "uuid" },
            }, ["operation", "expected_territory_id", "state_token"], ToolAccess.Action),
            Define(GameReadouts.CHARACTER_TOOL, "读取角色属性与条件", "读取当前角色的 HP、MP、采集力、制作力、同步等级、面板属性及生效的客户端条件。"),
            Define(GameReadouts.PROGRESS_TOOL, "读取职业进度", "读取客户端中各职业和特职的等级、经验及当前特职。"),
            Define(GameReadouts.PARTY_TOOL, "读取队伍成员", "读取客户端可见的队伍或团队成员、职业、区域及 HP、MP，可附带状态效果。", new()
            {
                ["include_effects"] = new { type = "boolean" },
            }),
            Define(GameReadouts.TARGETS_TOOL, "读取目标槽位", "读取当前、焦点、临时、上一个、鼠标悬停及集体拍照目标。"),
            Define(GameReadouts.EFFECTS_TOOL, "读取角色状态效果", "读取本人、当前目标或焦点目标已有的状态效果。", new()
            {
                ["actor"] = new { type = "string", @enum = new[] { "self", "current", "focus" } },
            }),
            Define(GameReadouts.TRAVEL_TOOL, "读取传送目的地", "分页读取客户端已解锁的传送目的地及显示费用。", new()
            {
                ["territory_id"] = new { type = "integer", minimum = 1, maximum = uint.MaxValue },
                ["name_contains"] = new { type = "string" },
                ["offset"] = new { type = "integer", minimum = 0, maximum = 1024 },
                ["limit"] = new { type = "integer", minimum = 1, maximum = 100 },
            }),
            Define(GameReadouts.EQUIPMENT_TOOL, "读取当前装备", "读取装备槽、基础物品、品级、耐久及幻化 ID。"),
            Define(GameReadouts.GEARSETS_TOOL, "读取装备套装", "读取本机保存的装备套装、当前槽位、装等和幻化模板，不切换装备。"),
            Define(GameReadouts.BUDDIES_TOOL, "读取搭档与宠物", "读取客户端可见的搭档、宠物和任务支援成员。"),
            Define(GameReadouts.GAUGE_TOOL, "读取当前职业量谱", "读取当前特职的公开量谱字段；不读取其他职业的内存。"),
            Define(GameReadouts.RECAST_TOOL, "读取指定技能冷却与充能", "按技能表行 ID 查询冷却计时和充能，不判定技能当前能否施放。", new()
            {
                ["action_ids"] = RowIDs(24, 1),
                ["action_kind"] = new { type = "string", @enum = new[] { "action", "general_action" } },
            }, ["action_ids"]),
            Define(GameReadouts.ACTION_CHECK_TOOL, "诊断技能是否可用", "按行 ID 或精确名称读取同一帧的目标、动作映射、客户端状态码、冷却和充能。不施放技能。", action),
            Define(GameReadouts.JOB_ACTIONS_TOOL, "列出当前职业的技能", "按同步等级汇总职业、基础职业和职能技能，可只看非纯公共冷却中正在恢复的技能。", new()
            {
                ["offset"] = new { type = "integer", minimum = 0, maximum = 20000 },
                ["limit"] = new { type = "integer", minimum = 1, maximum = 60 },
                ["recharging_only"] = new { type = "boolean" },
            }),
            Define(GameReadouts.ACTION_REQUEST_TOOL, "请求使用已核对的技能", "核对技能、职业、区域和当前目标后只请求一次。支持跳跃、冲刺、传送、返回、下坐骑及普通技能。", new(action)
            {
                ["expected_job_id"] = positiveID, ["expected_territory_id"] = positiveID,
                ["expected_adjusted_action_id"] = positiveID, ["expected_target_id"] = new { type = "string" },
            }, ["expected_job_id", "expected_territory_id", "expected_adjusted_action_id", "expected_target_id"], ToolAccess.Action),
            Define(GameReadouts.UNLOCK_TOOL, "读取角色解锁", "按类型和对应数据表行 ID 查询解锁或完成状态。称号和成就列表未载入时返回未知。", new()
            {
                ["kind"] = new { type = "string", @enum = GameReadouts.UnlockKinds },
                ["row_id"] = new { type = "integer", minimum = 1, maximum = uint.MaxValue },
            }, ["kind", "row_id"]),
            Define(GameReadouts.SHEETS_TOOL, "查询客户端数据表", "按行 ID 或名称筛选读取常用国服数据表。名称查找每页有扫描上限，即使本页为空也可按 nextOffset 续查。", new()
            {
                ["table"] = new { type = "string", @enum = GameReadouts.GameTables },
                ["row_ids"] = RowIDs(64, 0),
                ["name_contains"] = new { type = "string", maxLength = 128 },
                ["offset"] = new { type = "integer", minimum = 0, maximum = 1_000_000 },
                ["limit"] = new { type = "integer", minimum = 1, maximum = 64 },
            }, ["table"]),
            Define(GameReadouts.SHEET_LIST_TOOL, "浏览客户端数据表", "分页查找本机客户端包含的数据表。", new()
            {
                ["name_contains"] = new { type = "string", maxLength = 128 },
                ["offset"] = new { type = "integer", minimum = 0, maximum = 10000 },
                ["limit"] = new { type = "integer", minimum = 1, maximum = 100 },
            }),
            Define(GameReadouts.SHEET_FIELDS_TOOL, "读取客户端数据表列", "读取指定表的列编号、类型和偏移，以及是否包含子行。", new()
            {
                ["sheet_name"] = new { type = "string", maxLength = 128 },
                ["offset"] = new { type = "integer", minimum = 0, maximum = 10000 },
                ["limit"] = new { type = "integer", minimum = 1, maximum = 128 },
            }, ["sheet_name"]),
            Define(GameReadouts.SHEET_CELLS_TOOL, "读取客户端数据表单元格", "按表名、行 ID 和列编号读值；子行表还需指定子行。", new()
            {
                ["sheet_name"] = new { type = "string", maxLength = 128 }, ["row_ids"] = RowIDs(16, 0),
                ["columns"] = RowIDs(16, 0), ["subrow_id"] = new { type = "integer", minimum = 0, maximum = ushort.MaxValue },
            }, ["sheet_name", "row_ids", "columns"]),
            Define(GameReadouts.NAMED_FIELDS_TOOL, "列出数据表命名字段", "列出当前客户端生成的字段名、类型和可搜索字段。", new()
            {
                ["sheet_name"] = new { type = "string", maxLength = 128 },
            }, ["sheet_name"]),
            Define(GameReadouts.NAMED_ROW_TOOL, "读取数据表命名行", "按行号读取命名字段；支持显式字段列表、字段分页和子行。", new()
            {
                ["sheet_name"] = new { type = "string", maxLength = 128 },
                ["row_id"] = new { type = "integer", minimum = 0, maximum = uint.MaxValue },
                ["subrow_id"] = new { type = "integer", minimum = 0, maximum = ushort.MaxValue },
                ["fields"] = new { type = "array", minItems = 1, maxItems = 128, items = new { type = "string" } },
                ["field_offset"] = new { type = "integer", minimum = 0, maximum = 4096 },
                ["field_limit"] = new { type = "integer", minimum = 1, maximum = 128 },
            }, ["sheet_name", "row_id"]),
            Define(GameReadouts.SEARCH_FIELDS_TOOL, "搜索数据表文本字段", "按指定字段或全部文本字段搜索；可用返回的两个游标继续查询子行表。", new()
            {
                ["sheet_name"] = new { type = "string", maxLength = 128 },
                ["field_name"] = new { type = "string", maxLength = 128 },
                ["query"] = new { type = "string", maxLength = 128 }, ["exact"] = new { type = "boolean" },
                ["offset"] = new { type = "integer", minimum = 0, maximum = int.MaxValue - 1024 },
                ["subrow_offset"] = new { type = "integer", minimum = 0, maximum = ushort.MaxValue },
                ["limit"] = new { type = "integer", minimum = 1, maximum = 80 },
            }, ["sheet_name", "query"]),
            Define(DevelopmentWorkflow.TOOL, "读取当前构建的开发指引", "读取运行中 DLL 的开发指引、版本与 MVID，可附带技能文件和校验值。", new()
            {
                ["include_skill"] = new { type = "boolean" }, ["include_bundle"] = new { type = "boolean" },
                ["known_revision"] = new { type = "string", pattern = "^[A-Fa-f0-9]{64}$" },
            }),
            Define(GameReadouts.MAP_TOOL, "读取区域地图与图标", "读取角色地图和当前选择的地图；仅在地图已显示时分页读取静态图标。图标坐标不能传给导航。", new()
            {
                ["offset"] = new { type = "integer", minimum = 0, maximum = 132 },
                ["limit"] = new { type = "integer", minimum = 1, maximum = 64 },
                ["expected_map_id"] = new { type = "integer", minimum = 1, maximum = uint.MaxValue },
            }),
            Define(GameReadouts.SOCIETIES_TOOL, "读取友好部族日常状态", "读取国服客户端的剩余接取额度、部族声望和日常任务记录。"),
            Define(GameReadouts.NEARBY_BATTLE_TOOL, "读取附近战斗对象", "按距离列出客户端已加载的战斗对象、生命值和当前施法。默认只看战斗 NPC。", new()
            {
                ["radius"] = new { type = "number", minimum = 1, maximum = 100 },
                ["limit"] = new { type = "integer", minimum = 1, maximum = 50 },
                ["include_players"] = new { type = "boolean" },
            }),
            Define(GameState.NAVIGATION_TOOL, "读取导航路径状态", "查询 vnavmesh 是否在算路、移动，以及当前路线的最后一个路径点。"),
            Define(GameState.ROUTE_TOOL, "导航到当前区域坐标", "核对区域与配置距离，将坐标投影到可用网格后请求 vnavmesh 导航。已有路线时不接管。", new(route)
            {
                ["allow_flight"] = new { type = "boolean" },
            }, routeRequired, ToolAccess.Action),
            Define(GameState.STOP_TOOL, "停止已核对的导航路线", "核对区域和目标坐标后停止匹配路线；取消本工具的计算需要 request_id。", new(route)
            { ["request_id"] = new { type = "string", format = "uuid" } }, routeRequired, ToolAccess.Action),
            Define(GameState.FOCUS_TOOL, "设置焦点目标", "核对当前焦点对象 ID 后，设置当前区域的可选对象为焦点并回读。无焦点时传 0。", new()
            {
                ["game_object_id"] = new { type = "string" },
                ["expected_focus_id"] = new { type = "string" },
            }, ["game_object_id", "expected_focus_id"], ToolAccess.Action),
            Define(GameState.HOTBAR_READ_TOOL, "读取热键栏技能槽", "读取当前角色普通热键栏的槽位、命令和当前目标。", hotbar, ["bar", "slot"]),
            Define(GameState.RECHARGES_TOOL, "列出热键栏的冷却计时", "去重读取当前普通热键栏中计时仍在进行的技能和通用指令。", new()
            {
                ["limit"] = new { type = "integer", minimum = 1, maximum = 60 },
            }),
            Define(GameState.HOTBAR_REQUEST_TOOL, "请求使用热键栏技能", "核对槽位内容、职业、区域和当前目标后只提交一次；不支持宏、PvP 或游戏按键注入。", new(hotbar)
            {
                ["expected_command_id"] = positiveID,
                ["expected_apparent_id"] = new { type = "integer", minimum = 0, maximum = uint.MaxValue },
                ["expected_command_type"] = new { type = "string", @enum = new[] { "Action", "GeneralAction" } },
                ["expected_job_id"] = positiveID, ["expected_territory_id"] = positiveID,
                ["expected_target_id"] = new { type = "string" },
            }, ["bar", "slot", "expected_command_id", "expected_apparent_id", "expected_command_type", "expected_job_id", "expected_territory_id", "expected_target_id"], ToolAccess.Action),
            Define(GameState.CHAT_TOOL, "提交一条游戏聊天", "核对区域后向明确的聊天频道提交单行纯文本；请求返回不代表送达。", new()
            {
                ["channel"] = new { type = "string", @enum = GameState.ChatChannels },
                ["message"] = new { type = "string", minLength = 1, maxLength = 500 },
                ["expected_territory_id"] = positiveID,
            }, ["channel", "message", "expected_territory_id"], ToolAccess.Action),
            Define(GameWindowState.READ_TOOL, "读取游戏窗口状态", "读取当前游戏进程主窗口是否最小化、是否在前台。"),
            Define(GameWindowState.SET_TOOL, "请求最小化或还原游戏窗口", "核对原窗口状态后提交一次请求并回读；还原可能激活游戏窗口。", new()
            {
                ["expected_minimized"] = new { type = "boolean" },
                ["minimized"] = new { type = "boolean" },
            }, ["expected_minimized", "minimized"], ToolAccess.Action),
            Define(GameWindowState.KEY_TOOL, "请求前台游戏按键", "向前台游戏窗口发送有限次按下和释放；不激活窗口或移动系统焦点。", new()
            {
                ["key"] = new { type = "string" }, ["expected_territory_id"] = positiveID,
                ["hold_ms"] = new { type = "integer", minimum = 25, maximum = 1500 },
                ["repeat"] = new { type = "integer", minimum = 1, maximum = 8 },
                ["interval_ms"] = new { type = "integer", minimum = 50, maximum = 500 },
                ["dry_run"] = new { type = "boolean" },
            }, ["key", "expected_territory_id"], ToolAccess.Action),
            Define(PluginIntegration.MESSAGES_TOOL, "读取插件发布的应用消息", "按流和游标读取本实例保留的消息。发布方名称由其自报，读取不消费历史。", new()
            {
                ["stream_id"] = new { type = "string", format = "uuid" },
                ["after_sequence"] = new { type = "integer", minimum = 0 },
                ["source"] = new { type = "string", maxLength = 128 }, ["key"] = new { type = "string", maxLength = 128 },
                ["limit"] = Range(1, 100),
            }),
            Define(PluginIntegration.ENDPOINTS_TOOL, "列出保存的插件 IPC", "只读列出已保存的 IPC 别名和声明签名。"),
            Define(PluginIntegration.EDIT_ENDPOINT_TOOL, "维护保存的插件 IPC", "按别名新增或移除 IPC 声明；新增校验已加载插件及精确签名，移除核对原入口。", new()
            {
                ["operation"] = new { type = "string", @enum = new[] { "add", "remove" } },
                ["alias"] = new { type = "string", maxLength = 48 },
                ["plugin_name"] = new { type = "string", maxLength = 128 },
                ["callgate"] = new { type = "string", maxLength = 192 },
                ["expected_callgate"] = new { type = "string", maxLength = 192 },
                ["development_only"] = new { type = "boolean" },
                ["argument_kinds"] = new { type = "array", maxItems = DeclaredPluginEndpoint.MAX_ARGUMENTS,
                    items = new { type = "string", @enum = DeclaredPluginEndpoint.Kinds } },
                ["return_kind"] = new { type = "string", @enum = DeclaredPluginEndpoint.ReturnKinds },
            }, ["operation", "alias"], ToolAccess.Unsafe),
            Define(PluginIntegration.CALL_ENDPOINT_TOOL, "调用已保存的插件 IPC", "按别名调用，重新核对所属插件、声明签名与实参。返回值不是独立的结果回读。", new()
            {
                ["alias"] = new { type = "string", maxLength = 48 },
                ["arguments"] = new { type = "array", maxItems = DeclaredPluginEndpoint.MAX_ARGUMENTS,
                    items = new { type = new[] { "boolean", "integer", "number", "string", "null" } } },
            }, ["alias", "arguments"], ToolAccess.Unsafe),
            Define(ClientStateJournal.TOOL, "续读客户端状态变化", "分页读取登录、区域、战斗、职业、目标或角色资源变化，可按类型筛选。提供上次返回的流 ID 和序号可等待下一页。", new()
            {
                ["stream_id"] = new { type = "string", format = "uuid" },
                ["after_sequence"] = new { type = "integer", minimum = 0 },
                ["limit"] = new { type = "integer", minimum = 1, maximum = 100 },
                ["wait_ms"] = new { type = "integer", minimum = 0, maximum = 10000 },
                ["kinds"] = new { type = "array", minItems = 1, maxItems = ClientStateJournal.Kinds.Length,
                    uniqueItems = true, items = new { type = "string", @enum = ClientStateJournal.Kinds } },
            }),
        ];
    }

    private static object RowIDs(int max, int minimum) => new
    {
        type = "array", minItems = 1, maxItems = max, uniqueItems = true,
        items = new { type = "integer", minimum, maximum = uint.MaxValue },
    };
}
