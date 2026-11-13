using DalamudMCP.Shared;

namespace DalamudMCP.Plugin.Tools;

public sealed partial class GameTools
{
    private static Dictionary<string, object> CombatAnalysisSchema(bool export = false)
    {
        var schema = new Dictionary<string, object>
        {
            ["archive_id"] = new { type = "string", format = "uuid" },
            ["action_id"] = new { type = "integer", minimum = 1, maximum = uint.MaxValue },
            ["source_id"] = new { type = "string" },
            ["from_ms"] = Range(-1500, 86400000), ["to_ms"] = Range(-1500, 86400000),
            ["group_by"] = new { type = "string", @enum = new[] { "source_action", "source", "action" } },
        };
        if (!export)
        {
            schema["offset"] = Range(0, 50000);
            schema["limit"] = Range(1, 100);
            schema["timeline_limit"] = Range(0, 100);
        }
        return schema;
    }

    private int dropdownAdjustment;
    private int nativeObservationWaits;
    private int nativeSurfaceRequest;

    private async Task<object> RequestNativeSurfaceAsync(ToolArguments input, Func<bool> isAllowed, CancellationToken token)
    {
        var surface = input.Text("surface");
        var visible = input.Boolean("visible");
        var expected = input.Boolean("expected_visible");
        var timeout = input.Integer("timeout_ms", 2000, 250, 5000);
        if (!(GameDiagnostics.NativeLayoutKnown || surface == "duty_finder" && GameDiagnostics.CurrentCnLayoutKnown))
            return new { status = "unsupported_sdk" };
        if (Interlocked.CompareExchange(ref nativeSurfaceRequest, 1, 0) != 0) return new { status = "busy" };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        AddonTools.SurfaceState? last = null;
        var started = 0;
        try
        {
            var initial = await Tick(() =>
            {
                var before = addons.ReadSurface(surface);
                if (!before.Ready || before.Visible != expected) return (before, "surface_changed");
                if (before.Visible == visible) return (before, "already_matched");
                return (before, addons.RequestSurface(surface, visible, () =>
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!isAllowed()) return false;
                    Volatile.Write(ref started, 1);
                    return true;
                }));
            }).ConfigureAwait(false);
            last = initial.before;
            if (initial.Item2 != "requested") return Result(initial.Item2);
            var streak = 0;
            while (true)
            {
                last = await Tick(() => addons.ReadSurface(surface)).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (last.Ready && last.Visible == visible)
                {
                    if (++streak == 2) return Result("observed");
                }
                else streak = 0;
                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return Result("timeout");
        }
        finally { Volatile.Write(ref nativeSurfaceRequest, 0); }

        async Task<T> Tick<T>(Func<T> action) => await services.Framework.RunOnTick(() =>
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (services.Framework.IsFrameworkUnloading) throw new InvalidOperationException("卫月框架正在卸载。");
            return action();
        }, delayTicks: 1, cancellationToken: deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);

        object Result(string status) => new { status, surface, sample = last,
            dispatchMayHaveStarted = Volatile.Read(ref started) != 0, resultObserved = status is "observed" or "already_matched" };
    }

    private async Task<object> WaitForNativeControlAsync(ToolArguments input, CancellationToken token)
    {
        var selector = AddonObservation.ParseSurface(input);
        var timeout = input.Integer("timeout_ms", 2000, 250, 5000);
        if (!GameDiagnostics.NativeLayoutKnown) return new { status = "unsupported_sdk" };
        if (Interlocked.Increment(ref nativeObservationWaits) > 4)
        {
            Interlocked.Decrement(ref nativeObservationWaits);
            return new { status = "busy" };
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var streak = 0;
        AddonTools.ObservationSample? last = null;
        try
        {
            while (true)
            {
                last = await services.Framework.RunOnTick(() =>
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (services.Framework.IsFrameworkUnloading) throw new InvalidOperationException("卫月框架正在卸载。");
                    return addons.ReadObservation(selector);
                }, delayTicks: 1, cancellationToken: deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (selector.IsMatch(last.Visible, last.Node?.Checked) == true)
                {
                    if (++streak == 2) return new { status = "observed", resultObserved = true, sample = last };
                }
                else streak = 0;
                await Task.Delay(75, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return new { status = "timeout", resultObserved = false, sample = last };
        }
        finally { Interlocked.Decrement(ref nativeObservationWaits); }
    }

    private async Task<object> AdjustDropdownAsync(ToolArguments input, Func<bool> isAllowed, CancellationToken token)
    {
        var desired = input.Boolean("open");
        var expected = input.Boolean("expected_open");
        if (Interlocked.CompareExchange(ref dropdownAdjustment, 1, 0) != 0)
            return new { status = "busy", requestDispatched = false };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(2000);
        var started = 0;
        bool? actual = null;
        try
        {
            var baseline = await Tick(() =>
            {
                var before = addons.ReadDropdown(input);
                if (before.Pending) return (before, "transition_pending");
                if (before.Open != expected) return (before, "state_changed");
                if (before.Open == desired) return (before, "already_matched");
                deadline.Token.ThrowIfCancellationRequested();
                var status = addons.SetDropdown(input, before, desired, () =>
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!isAllowed()) return false;
                    Volatile.Write(ref started, 1);
                    return true;
                });
                return (before, status);
            }).ConfigureAwait(false);
            actual = baseline.before.Open;
            if (baseline.Item2 != "requested") return Result(baseline.Item2);
            while (true)
            {
                AddonTools.ChoiceSample current;
                try { current = await Tick(() => addons.ReadDropdown(input), 1).ConfigureAwait(false); }
                catch (InvalidOperationException) { return Result("target_changed"); }
                if (!baseline.before.SameControl(current.Addon, current.AddonID, current.Node, current.Component))
                    return Result("target_changed");
                actual = current.Open;
                if (!current.Pending && current.Open == desired) return Result("observed");
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        {
            return Result("timeout");
        }
        finally { Volatile.Write(ref dropdownAdjustment, 0); }

        async Task<T> Tick<T>(Func<T> action, int ticks = 0) =>
            await services.Framework.RunOnTick(() =>
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (services.Framework.IsFrameworkUnloading || !services.Framework.IsInFrameworkUpdateThread)
                    throw new InvalidOperationException("卫月框架线程不可用。");
                return action();
            }, delayTicks: ticks, cancellationToken: deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);

        object Result(string status) => new { status, dispatchMayHaveStarted = Volatile.Read(ref started) != 0,
            open = actual, resultObserved = status is "observed" or "already_matched" };
    }

    private static IEnumerable<ToolDefinition> ExtraTools(Dictionary<string, object> nodeSelector)
    {
        var text = new { type = "string" };
        var id = new { type = "integer", minimum = 1, maximum = uint.MaxValue };
        var guid = new { type = "string", format = "uuid" };
        var flag = new { type = "boolean" };
        var control = new Dictionary<string, object>(nodeSelector)
        {
            ["addon_name"] = text, ["offset"] = IntegerRange(0, 4096), ["limit"] = IntegerRange(1, 100),
        };
        var version = new Dictionary<string, object>
        {
            ["plugin_name"] = text, ["repository_url"] = text, ["expected_version"] = text,
        };
        var capture = new Dictionary<string, object>
        {
            ["frames"] = IntegerRange(1, 300), ["timeout_ms"] = IntegerRange(250, 8000), ["include_party"] = flag,
            ["include_action_effects"] = flag,
            ["action_effect_scope"] = new { type = "string", @enum = new[] { "self", "party", "nearby" } },
            ["include_job_meter"] = flag,
            ["action_ids"] = new { type = "array", minItems = 1, maxItems = 8, items = id },
        };
        var recorder = new Dictionary<string, object>
        {
            ["enabled"] = flag, ["duration_seconds"] = IntegerRange(0, 1800), ["max_events"] = IntegerRange(100, 50000), ["sample_interval_ms"] = IntegerRange(50, 1000),
            ["include_party"] = flag, ["include_action_effects"] = flag, ["action_effect_scope"] = capture["action_effect_scope"],
        };
        return
        [
            Define("find_repository_plugins", "查询仓库插件", "分页查询卫月已加载的仓库目录、正式和测试版本、来源及安装状态，不刷新仓库。", new()
            {
                ["query"] = text, ["repository_url"] = text, ["offset"] = IntegerRange(0, 10000), ["limit"] = IntegerRange(1, 100),
            }),
            Define("inspect_repository_sources", "查询仓库配置和运行状态", "读取主仓库、预设仓库和用户仓库的配置与运行状态。"),
            Define("change_repository_source", "管理第三方仓库", "新增、删除、启停或刷新仓库。删除和启停须核对原启用状态；不安装任何插件。", new()
            {
                ["operation"] = new { type = "string", @enum = new[] { "add", "remove", "set_enabled", "refresh" } },
                ["repository_url"] = text, ["expected_enabled"] = flag, ["enabled"] = flag,
            }, ["operation"], ToolAccess.Unsafe),
            Define("install_repository_plugin", "安装指定仓库版本", "从已启用的第三方 HTTPS 仓库安装指定版本；测试版需显式选择且卫月已允许测试版。", new(version) { ["testing"] = flag },
                ["plugin_name", "repository_url", "expected_version"], ToolAccess.Unsafe),
            Define("remove_repository_plugin", "卸载指定仓库版本", "卸载同源同版本的第三方插件，由卫月安排文件清理，保留配置；用返回的 id 查询进度。", version,
                ["plugin_name", "repository_url", "expected_version"], ToolAccess.Unsafe),
            Define("get_repository_job", "查询插件管理进度", "按插件或仓库管理任务返回的 ID 查询进度与核对结果。", new() { ["operation_id"] = guid }, ["operation_id"]),
            Define("read_addon_control", "读取窗口控件", "按完整标签或节点 ID、组件路径读取文本、数字、复选框、下拉框或列表。", control, ["addon_name"]),
            Define("watch_addon_state", "等待原生窗口或控件状态", "只读等待窗口或节点可见性、或复选框状态；连续两次采样满足条件才返回确认。", new(nodeSelector)
            {
                ["addon_name"] = text, ["visible"] = flag, ["checked"] = flag,
                ["timeout_ms"] = IntegerRange(250, 5000),
            }, ["addon_name"]),
            Define(AddonTools.SURFACE_TOOL, "显示或关闭已支持的游戏窗口", "核对原可见状态后，只派发一次背包、地图、任务日志或任务搜索器的开关请求，并独立回读。", new()
            {
                ["surface"] = new { type = "string", @enum = AddonTools.Surfaces },
                ["visible"] = flag, ["expected_visible"] = flag, ["timeout_ms"] = IntegerRange(250, 5000),
            }, ["surface", "visible", "expected_visible"], ToolAccess.Action),
            Define("edit_addon_control", "编辑窗口控件", "核对窗口 ID、组件和原值后编辑文本、数字、复选框或选中项，并回读。扩展范围需要单独开启设置。", new(control)
            {
                ["expected_addon_id"] = id, ["value"] = new { type = new[] { "string", "integer", "boolean" } },
                ["expected_value"] = new { type = new[] { "string", "integer", "boolean" } }, ["row_label"] = text,
            }, ["addon_name", "expected_addon_id", "expected_value", "value"], ToolAccess.Action),
            Define("adjust_dropdown_panel", "展开或收起下拉框", "核对窗口和原状态后发出一次下拉开关通知，再等稳定状态。", new(nodeSelector)
            {
                ["addon_name"] = text, ["expected_addon_id"] = id, ["expected_open"] = flag, ["open"] = flag,
            }, ["addon_name", "expected_addon_id", "expected_open", "open"], ToolAccess.Action),
            Define(AddonTools.READ_LIST_ROW_TOOL, "核对原生列表渲染行", "读取可见普通或树形列表渲染行的真实索引、当前选中行和可见文字。", new()
            {
                ["addon_name"] = text, ["node_id"] = id, ["expected_addon_id"] = id, ["component_path"] = nodeSelector["component_path"],
            }, ["addon_name", "node_id", "component_path"]),
            Define(AddonTools.ACTIVATE_LIST_ROW_TOOL, "激活已核对的原生列表行", "核对列表渲染行的真实索引、原选中行、完整文字和注册监听器，再激活一次。", new()
            {
                ["addon_name"] = text, ["node_id"] = id, ["expected_addon_id"] = id, ["component_path"] = nodeSelector["component_path"],
                ["expected_territory_id"] = id, ["row_index"] = IntegerRange(0, 4095), ["expected_index"] = IntegerRange(-1, 4095),
                ["row_label"] = text, ["dry_run"] = flag,
            }, ["addon_name", "node_id", "component_path", "expected_addon_id", "expected_territory_id", "row_index", "expected_index", "row_label"], ToolAccess.Action),
            Define("activate_addon_row", "激活原生列表行", "核对窗口、原选中索引、目标索引和标签，向可见行派发一次激活事件。", new(nodeSelector)
            {
                ["addon_name"] = text, ["expected_addon_id"] = id, ["expected_index"] = IntegerRange(-1, 4095),
                ["row_index"] = IntegerRange(0, 4095), ["row_label"] = text,
            }, ["addon_name", "expected_addon_id", "expected_index", "row_index", "row_label"], ToolAccess.Action),
            Define("queue_imgui_text", "输入插件窗口文本", "给当前聚焦且 ID 匹配的 ImGui 文本框排入纯文本，不发送按键或提交。排入不代表字段接受。", new()
            {
                ["window_id"] = id, ["field_id"] = id, ["text"] = text,
            }, ["window_id", "field_id", "text"], ToolAccess.Action),
            Define("queue_imgui_gesture", "派发 ImGui 单次手势", "核对当前悬停或导航控件后，排入单次点击、滚轮或导航键。不会移动系统鼠标；排入不代表控件已处理。", new()
            {
                ["window_id"] = id, ["control_id"] = id,
                ["x"] = IntegerRange(-10000, 10000), ["y"] = IntegerRange(-10000, 10000),
                ["gesture"] = new { type = "string", @enum = new[] { "click", "right_click", "middle_click", "up", "down", "left", "right", "enter", "escape", "wheel_up", "wheel_down" } },
            }, ["window_id", "control_id", "gesture"], ToolAccess.Action),
            Define("probe_imgui_point", "探测 ImGui 窗口坐标", "在当前悬停的 ImGui 窗口内探测一个坐标并回读控件 ID。不会点击或移动系统鼠标。", new()
            {
                ["window_id"] = id, ["x"] = IntegerRange(-10000, 10000), ["y"] = IntegerRange(-10000, 10000),
            }, ["window_id", "x", "y"], ToolAccess.Action),
            Define("sample_imgui_hit_points", "批量探测 ImGui 命中点", "在当前悬停窗口中依次探测最多 12 个点，返回各点悬停控件，不点击。", new()
            {
                ["window_id"] = id,
                ["points"] = new { type = "array", minItems = 1, maxItems = 12, items = new
                { type = "array", minItems = 2, maxItems = 2, items = IntegerRange(-10000, 10000) } },
            }, ["window_id", "points"], ToolAccess.Action),
            Define("queue_window_chord", "发送 ImGui 按键或组合键", "核对导航控件后排入一次键盘输入，可带修饰键。界面结果须另行回读。", new()
            {
                ["window_id"] = id, ["control_id"] = id, ["key"] = new { type = "string", maxLength = 24 },
                ["ctrl"] = flag, ["shift"] = flag, ["alt"] = flag,
            }, ["window_id", "control_id", "key"], ToolAccess.Action),
            Define("locate_inventory_item", "查找物品所在容器", "按基础物品 ID 或名称查询库存。scope=retainer 可列出物品，include_cached 可读取本会话已访问雇员的快照。", new()
            {
                ["item_id"] = id, ["item_name"] = new { type = "string", minLength = 1, maxLength = 128 },
                ["contains_match"] = flag, ["high_quality"] = flag, ["include_cached"] = flag,
                ["scope"] = new { type = "string", @enum = new[] { "all", "carried", "equipped", "armory", "saddlebag", "retainer" } },
                ["offset"] = IntegerRange(0, 4096), ["limit"] = IntegerRange(1, 200),
            }),
            Define(GameDiagnostics.INVENTORY_CONTEXT_TOOL, "核对已打开的背包菜单", "将当前可见的普通背包物品菜单与实际库存槽位和物品关联。不打开菜单或选择条目。"),
            Define(AddonTools.REQUEST_BAG_MENU_TOOL, "打开已定位的背包菜单", "向已核对背包格派发一次右键；再用 inspect_open_bag_menu 核对实际物品。", new(nodeSelector)
            {
                ["addon_name"] = text, ["expected_addon_id"] = id, ["dry_run"] = flag,
            }, ["addon_name", "expected_addon_id"], ToolAccess.Action),
            Define("sample_combat_window", "采样战斗帧", "有限时地采集本人、目标和可选队伍成员的战斗状态。", capture),
            Define("archive_combat_window", "保存战斗帧归档", "采样并把结果写入本地有限数量的归档文件。", capture, access: ToolAccess.Action),
            Define("set_combat_recorder", "开始或停止自动战斗归档", "按战斗开始和结束保存采样，duration_seconds=0 持续到停止或会话变化；持久记录使用 configure_combat_history。", recorder, ["enabled"], ToolAccess.Action),
            Define(GameDiagnostics.DISPATCH_RECORDER_TOOL, "记录客户端派发的战斗效果", "限时开启战斗事件归档，观察客户端已解析的动作效果；重载后不恢复。", recorder,
                ["enabled"], ToolAccess.Unsafe),
            Define("get_combat_recorder", "查询自动战斗归档状态", "读取当前录制状态、本场 ID、丢弃数和上次归档结果。"),
            Define(GameDiagnostics.WAIT_COMBAT_TOOL, "等待指定战斗结束或归档", "按录制器给出的本场 ID 等待自然结束或自动归档，最长 20 秒；超时返回该场状态。归档不代表通关。", new()
            {
                ["combat_id"] = guid, ["until"] = new { type = "string", @enum = new[] { "ended", "archived" } },
                ["timeout_ms"] = IntegerRange(0, 20000),
            }, ["combat_id"]),
            Define("read_combat_capture", "读取战斗归档", "列出有限数量的本地战斗归档，或按 ID 读取一次采样。", new() { ["archive_id"] = guid, ["offset"] = Range(0, 18000), ["limit"] = Range(1, 100) }),
            Define(GameDiagnostics.COMBAT_TIMELINE_TOOL, "读取战斗效果时间线", "按时间分页读取归档中已观察到的动作效果，可按来源或技能筛选；不推断命中或施放次数。", new()
            {
                ["archive_id"] = guid, ["action_id"] = id, ["source_id"] = new { type = "string" },
                ["from_ms"] = Range(-1500, 86400000), ["to_ms"] = Range(-1500, 86400000),
                ["offset"] = IntegerRange(0, 50000), ["limit"] = IntegerRange(1, 100),
            }, ["archive_id"]),
            Define(GameDiagnostics.ANALYZE_COMBAT_TOOL, "分析归档战斗效果", "按来源或技能分组汇总观察到的效果和批次间隔，支持分页。", CombatAnalysisSchema(), ["archive_id"]),
            Define(GameDiagnostics.EXPORT_COMBAT_TOOL, "导出归档战斗效果", "在插件配置目录保存筛选后的 JSON 与分组统计，并返回文件哈希。", CombatAnalysisSchema(true), ["archive_id"], ToolAccess.Unsafe),
            Define("summarize_combat_capture", "汇总战斗归档", "按归档 ID 汇总可观察到的各角色血量变化和施法起点；采样差值不是伤害或治疗归因。", new() { ["archive_id"] = guid }, ["archive_id"]),
            Define("inspect_client_connection", "读取客户端连接状态", "读取已核对 SDK 上的网络模块初始化、实例和框架更新状态，不抓包。"),
            Define("inspect_opcode_catalog", "查询公开地区操作码目录", "列出来源声明的地区，或按地区、方向、编号及名称分页查询已载入的目录。", new()
            {
                ["region"] = new { type = "string", maxLength = 32 }, ["direction"] = new { type = "string", @enum = new[] { "send", "receive" } },
                ["opcode"] = Range(1, ushort.MaxValue), ["name"] = new { type = "string", maxLength = 128 },
                ["offset"] = Range(0, 16384), ["limit"] = Range(1, 100),
            }),
            Define("refresh_opcode_catalog", "刷新公开地区操作码目录", "从固定来源校验并保存地区目录，不改变已启动采集的名称快照。", new()
            { ["baseline_sha256"] = new { type = "string", pattern = "^[A-Fa-f0-9]{64}$" } }, access: ToolAccess.Unsafe),
            Define("check_cn_opcode_catalog", "检查国服公开操作码表是否更新", "按需用 HTTPS 读取固定公开来源的国服操作码表，与上次指纹比较。不会更新运行时操作码或验证客户端兼容性。", new()
            {
                ["baseline_sha256"] = new { type = "string", pattern = "^[A-Fa-f0-9]{64}$" },
            }),
            Define("find_cn_zone_opcode", "查询国服区服操作码", "按发送或接收方向查公开表，可按编号或部分名称筛选。使用最近一次成功获取的表；结果不证明游戏当前映射。", new()
            {
                ["direction"] = new { type = "string", @enum = new[] { "send", "receive" } },
                ["opcode"] = IntegerRange(1, ushort.MaxValue), ["name"] = new { type = "string", minLength = 1, maxLength = 128 },
                ["offset"] = IntegerRange(0, 8192), ["limit"] = IntegerRange(1, 100),
            }, ["direction"]),
            Define("sample_client_connection", "采样客户端连接状态", "在有限时间内统计当前进程的 TCP 状态及客户端状态，不输出端点或数据包。", new()
            {
                ["samples"] = IntegerRange(1, 20), ["interval_ms"] = IntegerRange(250, 2000),
            }),
            Define(GameDiagnostics.DECODE_TOOL, "离线解码 IPC 样本", "按调用方提供的字段布局解码有界十六进制样本。不连接服务器或读取实时包。", new()
            {
                ["hex"] = new { type = "string", minLength = 2, maxLength = 131072 },
                ["fields"] = PacketFieldsSchema(), ["base_offset"] = Range(0, 65536),
                ["field_offset"] = Range(0, 8192), ["field_limit"] = Range(1, 128),
            }, ["hex", "fields"]),
            Define(GameDiagnostics.COMPARE_SAMPLE_TOOL, "比较两个 IPC 样本", "分页比较调用方提供的两个样本，区分不同字节和缺少的尾部。", new()
            {
                ["first_hex"] = new { type = "string", minLength = 2, maxLength = 131072 },
                ["second_hex"] = new { type = "string", minLength = 2, maxLength = 131072 },
                ["offset"] = IntegerRange(0, 65536), ["count"] = IntegerRange(1, 256),
            }, ["first_hex", "second_hex"]),
            Define(GameDiagnostics.ZONE_CAPTURE_TOOL, "设置服务端回调采集", "限时观察入站回调元数据，或按命令 ID 采集负载和出站参数。不发送或改写消息。", new()
            {
                ["enabled"] = flag, ["metadata_only"] = flag, ["duration_seconds"] = IntegerRange(1, 60),
                ["max_records"] = IntegerRange(1, 128), ["max_bytes"] = IntegerRange(8, 1024),
                ["direction"] = new { type = "string", @enum = new[] { "send", "receive", "both" } },
                ["command_ids"] = new { type = "array", minItems = 1, maxItems = 32, items = IntegerRange(0, int.MaxValue) },
            }, ["enabled"], ToolAccess.Unsafe),
            Define(GameDiagnostics.ZONE_READ_TOOL, "读取服务端回调采集", "分页读取采集状态和元数据；可按采集 ID 显式读取负载。", new()
            {
                ["capture_id"] = guid, ["include_payload"] = flag, ["offset"] = IntegerRange(0, 128), ["limit"] = IntegerRange(1, 32),
            }, access: ToolAccess.Unsafe),
            Define(GameDiagnostics.ZONE_TRACE_STATUS_TOOL, "检查游戏收发观察支持", "检查元数据入口和国服原始包入口是否可用，不启动监听。"),
            Define(GameDiagnostics.ZONE_TRACE_CONTROL_TOOL, "设置游戏收发观察", "限时观察区服收发，默认只记录元数据。仅核对过的国服构建可明确开启原始包，不主动发送。", new()
            {
                ["enabled"] = flag, ["duration_seconds"] = IntegerRange(1, 60), ["max_events"] = IntegerRange(1, 512),
                ["raw_payload"] = flag, ["max_bytes"] = IntegerRange(32, 4096),
                ["opcodes"] = new { type = "array", minItems = 1, maxItems = 128, items = IntegerRange(0, ushort.MaxValue) },
                ["direction"] = new { type = "string", @enum = new[] { "send", "receive", "both" } },
            }, ["enabled"], ToolAccess.Unsafe),
            Define(GameDiagnostics.ZONE_TRACE_READ_TOOL, "读取游戏收发观察", "分页读取当前或最近一次收发事件。不包含端点、对象 ID 或负载。", new()
            {
                ["direction"] = new { type = "string", @enum = new[] { "send", "receive" } }, ["opcode"] = IntegerRange(0, ushort.MaxValue),
                ["trace_id"] = guid, ["offset"] = IntegerRange(0, 512), ["limit"] = IntegerRange(1, 100),
            }),
            Define(GameDiagnostics.ZONE_BYTES_TOOL, "读取国服区服原始包", "按观察 ID 分页读取被动采样的十六进制字节及长度。出站为业务负载，入站包含 IPC 头。", new()
            {
                ["direction"] = new { type = "string", @enum = new[] { "send", "receive" } }, ["opcode"] = IntegerRange(0, ushort.MaxValue),
                ["trace_id"] = guid, ["offset"] = IntegerRange(0, 512), ["limit"] = IntegerRange(1, 32),
            }, ["trace_id"], ToolAccess.Unsafe),
        ];

        static object IntegerRange(int minimum, int maximum) => new { type = "integer", minimum, maximum };
    }
}
