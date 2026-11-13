using DalamudMCP.Shared;

namespace DalamudMCP.Plugin.Tools;

public sealed partial class GameTools
{
    private static object PacketFieldsSchema() => new
    {
        type = "array", minItems = 1, maxItems = 128,
        items = new
        {
            type = "object", additionalProperties = false, required = new[] { "name", "offset", "type" },
            properties = new Dictionary<string, object>
            {
                ["name"] = new { type = "string", minLength = 1, maxLength = 64 }, ["offset"] = Range(0, 65536),
                ["type"] = new { type = "string", @enum = new[] { "u8", "u16", "u32", "u64", "i8", "i16", "i32", "i64", "f32", "f64", "bool", "utf8", "bytes", "bitset", "object" } },
                ["byte_order"] = new { type = "string", @enum = new[] { "little", "big" } },
                ["length"] = Range(1, 65536), ["remaining"] = new { type = "boolean" },
                ["metadata"] = new { type = "object" },
                ["divisor"] = new { type = "number", exclusiveMinimum = 0, maximum = 1e12 },
                ["addend"] = new { type = "number", minimum = -1e12, maximum = 1e12 },
                ["enum_labels"] = new { type = "object", maxProperties = 1024, additionalProperties = new { type = "array", minItems = 1, maxItems = 16, items = new { type = "string", maxLength = 128 } } }, ["count"] = Range(1, 1024), ["stride"] = Range(1, 65536),
                ["fields"] = new { type = "array", minItems = 1, maxItems = 128, items = new { type = "object" } },
                ["scale"] = new { type = "number", minimum = -1e12, maximum = 1e12 },
                ["bit_offset"] = Range(0, 63), ["bit_count"] = Range(1, 64),
                ["enum"] = new { type = "object", maxProperties = 128, additionalProperties = new { type = "string", maxLength = 128 } },
            },
        },
    };

    private static object Range(int minimum, int maximum) => new { type = "integer", minimum, maximum };

    private static ToolDefinition[] DiagnosticExpansionTools()
    {
        var text = new { type = "string", minLength = 1, maxLength = 512 };
        var flag = new { type = "boolean" };
        var guid = new { type = "string", format = "uuid" };
        var direction = new { type = "string", @enum = new[] { "send", "receive" } };
        var packet = new Dictionary<string, object>
        {
            ["trace_id"] = guid, ["packet_index"] = Range(0, 511), ["offset"] = Range(0, 65536), ["count"] = Range(1, 1024),
        };
        var decode = new Dictionary<string, object>(packet)
        {
            ["fields"] = PacketFieldsSchema(), ["origin"] = new { type = "string", @enum = new[] { "payload", "captured" } },
            ["field_offset"] = Range(0, 8192), ["field_limit"] = Range(1, 128),
        };
        var parse = new Dictionary<string, object>(decode);
        parse.Remove("fields");
        parse["layout_name"] = text;
        parse["expected_layout_sha256"] = text;
        var layoutSource = new { type = "string", @enum = new[] { "all", "bundled", "local", "sdk" } };
        parse["layout_source"] = layoutSource;
        parse["accept_sdk_difference"] = flag;
        var runtime = new Dictionary<string, object>
        {
            ["operation"] = text, ["type_name"] = text, ["type_id"] = text, ["query"] = text, ["offset"] = Range(0, 100000),
        };
        var memory = new Dictionary<string, object> { ["address"] = text, ["length"] = Range(1, 65536) };
        var observation = new Dictionary<string, object> { ["duration_ms"] = Range(250, 10000), ["limit"] = Range(1, 64), ["include_text"] = flag };
        return
        [
            Define("inspect_runtime_health", "查询运行时线程状态", "读取当前进程和线程编号，区分托管线程与系统线程，不暂停进程。"),
            Define("read_runtime_calls", "读取运行时调用摘要", "按游标读取最近 100 条诊断调用名称、耗时和返回状态，不记录参数、结果值或图像。", new()
            {
                ["stream_id"] = guid, ["after_sequence"] = new { type = "integer", minimum = 0 }, ["limit"] = Range(1, 100),
            }),
            Define("start_runtime_observation", "启动限时运行时观察", "被动观察明确内存、宿主事件或已发现的 SDK 入口。托管方法前置观察需要宿主已有 Harmony；不会调用目标函数。", new()
            {
                ["kind"] = new { type = "string", @enum = new[] { "memory", "event", "native", "managed" } },
                ["duration_seconds"] = Range(1, 300), ["max_records"] = Range(1, 4096), ["interval_ms"] = Range(50, 1000),
                ["address"] = text, ["length"] = Range(1, 256), ["reference_id"] = guid, ["member_id"] = text,
                ["expected_sdk_mvid"] = guid, ["type_name"] = text,
            }, ["kind"], ToolAccess.Unsafe),
            Define("read_runtime_observation", "读取运行时观察证据", "查询观察列表或按观察 ID 分页读取参数和内存变化，不消费记录。", new()
            { ["observation_id"] = guid, ["offset"] = Range(0, 4096), ["limit"] = Range(1, 100) }, access: ToolAccess.Unsafe),
            Define("stop_runtime_observation", "停止运行时观察", "解除对应订阅或 Hook；可同时移除留存证据，清理失败时保留状态。", new()
            { ["observation_id"] = guid, ["remove"] = flag }, ["observation_id"], ToolAccess.Unsafe),
            Define("watch_runtime_value", "限时观察运行时标量", "在框架线程按精确对象引用和成员 ID 采样；可等待预期值，保留有界变化记录。", new()
            {
                ["reference_id"] = guid, ["member_id"] = text, ["expected_value"] = new { },
                ["duration_ms"] = Range(100, 5000), ["interval_ms"] = Range(50, 1000),
            }, ["reference_id", "member_id"], ToolAccess.Unsafe),
            Define("dispatch_when_node_ready", "节点满足条件时派发一次事件", "固定窗口与节点实例，满足尺寸、对齐或旋转条件时派发一次。几何条件可先预览。", new()
            {
                ["addon_name"] = text, ["expected_addon_id"] = Range(1, ushort.MaxValue),
                ["node_id"] = new { type = "integer", minimum = 0, maximum = uint.MaxValue }, ["label"] = text,
                ["component_type"] = text, ["component_path"] = new { type = "array", maxItems = 8, items = Range(0, int.MaxValue) },
                ["geometry"] = GeometrySchema(), ["expected_territory_id"] = new { type = "integer", minimum = 1, maximum = uint.MaxValue },
                ["dry_run"] = flag,
                ["event_type"] = text, ["min_width"] = Range(0, ushort.MaxValue), ["max_width"] = Range(0, ushort.MaxValue),
                ["min_height"] = Range(0, ushort.MaxValue), ["max_height"] = Range(0, ushort.MaxValue), ["timeout_ms"] = Range(100, 5000),
            }, ["addon_name", "expected_addon_id", "event_type", "component_path"], ToolAccess.Action),
            Define("inspect_opcode_refresh", "查询操作码表启动检查", "读取本次插件加载的更新检查结果，不发起新的网络请求。"),
            Define("read_zone_packet_slice", "分页读取一个原始包", "按采集 ID 和包序号读取指定字节范围的十六进制与 ASCII。", packet, ["trace_id", "packet_index"], ToolAccess.Unsafe),
            Define("decode_zone_packet", "按字段布局解码原始包", "直接解码已捕获字节；支持数值、文本、数组、嵌套、位域和枚举。缺失字节明确标出。", decode, ["trace_id", "packet_index", "fields"], ToolAccess.Unsafe),
            Define("compare_zone_packets", "比较同次采集中的两个包", "比较两个包的原始字节，保留布局和操作码差异提示。", new(packet) { ["other_packet_index"] = Range(0, 511) }, ["trace_id", "packet_index", "other_packet_index"], ToolAccess.Unsafe),
            Define("inspect_packet_layouts", "查询协议布局目录", "分页查询内置包、完整结构元数据、自有定义和 SDK 声明。定义存在不代表国服兼容。", new() { ["name"] = text, ["offset"] = Range(0, 1024), ["limit"] = Range(1, 32), ["include_fields"] = flag, ["layout_source"] = layoutSource, ["kind"] = new { type = "string", @enum = new[] { "packets", "structures" } } }),
            Define("inspect_packet_aliases", "查询协议名称对应关系", "查询不同来源的包名称、方向和通道。自动解析仅采用当前操作码来源的明确对应关系。", new() { ["name"] = text, ["direction"] = direction, ["channel"] = new { type = "string", @enum = new[] { "zone", "lobby", "chat" } }, ["offset"] = Range(0, 1024), ["limit"] = Range(1, 100) }),
            Define("inspect_protocol_updates", "读取协议来源检查状态", "查看启动或手动检查的进度、来源结果和版本比较，不发起网络请求。"),
            Define("check_packet_sources", "检查协议定义更新", "并行检查固定发布包、源码目录和名称目录；十秒超时，仅报告变化，不下载执行或替换定义。", new() { ["force"] = flag }),
            Define("save_packet_layout", "保存协议布局定义", "保存声明式布局，不执行脚本；覆盖时必须核对旧指纹。", new()
            {
                ["name"] = text, ["direction"] = direction, ["version"] = text, ["source"] = text, ["license"] = text,
                ["fields"] = PacketFieldsSchema(), ["expected_sha256"] = text,
                ["opcode_names"] = new { type = "array", minItems = 1, maxItems = 32, items = text },
            }, ["name", "direction", "version", "source", "license", "fields", "opcode_names"], ToolAccess.Unsafe),
            Define("parse_zone_packet", "按已保存布局解析原始包", "按采集时固定的操作码名称和方向选择唯一布局；也可明确指定布局和指纹。歧义或缺少定义时停止。", parse, ["trace_id", "packet_index"], ToolAccess.Unsafe),
            Define("configure_combat_history", "配置持续战斗记录", "持久开启或关闭被动战斗记录。登录后恢复；权限关闭时暂停，异常时停止重试。默认关闭。", new()
            {
                ["enabled"] = flag, ["scope"] = new { type = "string", @enum = new[] { "self", "party", "nearby" } },
                ["max_events"] = Range(100, 50000),
            }, ["enabled"], ToolAccess.Unsafe),
            Define(GameDiagnostics.MECHANIC_REPLAY, "重放战斗机制", "按归档 ID 重放事件、用声明式规则重算危险区，并对照位置和伤害记录。规则仅作计算，不运行脚本。", GameDiagnostics.MechanicReplaySchema(), ["archive_id"]),
            Define(MechanicAssignments.TOOL, "求解队伍机制站位", "按危险格、个人候选格、踩塔、分摊、散开和连线条件，求一组满足约束的站位。", MechanicAssignments.Schema(),
                ["rows", "columns", "duration_ms", "hazards", "players"]),
            Define(MechanicRoute.ROUTE, "计算限时站位路线", "按移动速度与危险时段计算到达路线；支持在当前安全格等待，不执行移动。", MechanicRoute.Schema(knockback: false),
                ["rows", "columns", "duration_ms", "hazards", "layout", "start", "goals", "speed"]),
            Define(MechanicRoute.KNOCKBACK, "计算击退或牵引落点", "按来源或指定方向计算直线位移，核对落点和可选的沿途危险格。", MechanicRoute.Schema(knockback: true),
                ["rows", "columns", "duration_ms", "hazards", "layout", "position", "distance"]),
            Define(MechanicGrid.TOOL, "计算多轮网格危险区", "按输入的危险行、列、格和生效时间计算每格危险与可站候选时段。行列从 0 开始；不依赖副本或角色状态。", MechanicGrid.Schema(),
                ["rows", "columns", "duration_ms", "hazards"]),
            Define(MECHANICS_TOOL, "计算战斗机制范围", "用客户端数据计算当前读条的危险区和点位，支持场地内栅格采样及 M4S 电网。可将快照附到当前战斗归档。", MechanicSchema()),
            Define(GameDiagnostics.READ_MECHANICS, "读取战斗机制事件", "按同一归档 ID 分页读取已观察的读条、点名、连线、地板和无目标效果。", new()
            {
                ["archive_id"] = guid, ["offset"] = Range(0, 6000), ["limit"] = Range(1, 64), ["kind"] = text,
            }, ["archive_id"]),
            Define(GameDiagnostics.READ_ACR, "读取 ACR 决策", "按战斗归档 ID 分页读取 ACR 上报的检查结果和同次调用中的客户端状态。", new()
            {
                ["archive_id"] = guid, ["offset"] = Range(0, 6000), ["limit"] = Range(1, 64),
            }, ["archive_id"]),
            Define("read_combat_activity", "读取战斗状态变化", "分页读取归档中的读条、状态、死亡和观察范围变化。状态缺失不直接判定为驱散。", new()
            {
                ["archive_id"] = guid, ["offset"] = Range(0, 50000), ["limit"] = Range(1, 128),
                ["from_ms"] = Range(0, 86400000), ["to_ms"] = Range(0, 86400000), ["actor_id"] = text, ["kind"] = text,
            }, ["archive_id"]),
            Define("observe_ui_callbacks", "限时观察全部窗口回调", "在限定时长内观察自然发生的原生窗口回调，记录窗口身份、参数和返回值，不触发回调。", observation, access: ToolAccess.Unsafe),
            Define("get_window_input_queue", "查询插件窗口输入队列", "读取排队数、当前任务、按键归属和释放状态。"),
            Define("cancel_window_input", "取消插件窗口输入", "取消剩余输入并在下一渲染帧释放本工具持有的按键。帧停滞时明确返回未确认清理。", access: ToolAccess.Action),
            Define("drag_window_control", "拖动 ImGui 控件", "按窗口和控件 ID 核对后进行有界虚拟拖动；人的输入优先，不移动系统光标。", new()
            {
                ["window_id"] = new { type = "integer", minimum = 1, maximum = uint.MaxValue }, ["control_id"] = new { type = "integer", minimum = 1, maximum = uint.MaxValue },
                ["x"] = Range(-10000, 10000), ["y"] = Range(-10000, 10000), ["end_x"] = Range(-10000, 10000), ["end_y"] = Range(-10000, 10000),
                ["hold_ms"] = Range(50, 2000),
            }, ["window_id", "control_id", "x", "y", "end_x", "end_y"], ToolAccess.Action),
            Define("hold_window_key", "限时保持 ImGui 按键", "核对导航控件后保持按键最多两秒，到期、取消或用户输入时释放。完成不代表业务结果。", new()
            {
                ["window_id"] = new { type = "integer", minimum = 1, maximum = uint.MaxValue },
                ["control_id"] = new { type = "integer", minimum = 1, maximum = uint.MaxValue },
                ["key"] = text, ["ctrl"] = flag, ["shift"] = flag, ["alt"] = flag, ["hold_ms"] = Range(50, 2000),
            }, ["window_id", "control_id", "key"], ToolAccess.Action),
            Define("inspect_runtime_catalog", "查询当前运行时目录", "独立查询已注入服务和当前 SDK 的类型、成员、原生布局。只读元数据，不调用属性或函数。", runtime, ["operation"]),
            Define("access_runtime_object", "访问已发现的运行时对象", "按有期限的引用和精确成员 ID 读取、设置或调用公开成员。属性读取也可能执行代码。", new()
            {
                ["operation"] = new { type = "string", @enum = new[] { "retain_service", "release", "describe", "get", "set", "call", "enumerate", "task", "static_get", "static_set", "static_call" } },
                ["service"] = text, ["type_id"] = text, ["reference_id"] = guid, ["member_id"] = text,
                ["offset"] = Range(0, 10000), ["limit"] = Range(1, 100), ["filter_member"] = text, ["equals"] = new { },
                ["fields"] = new { type = "array", maxItems = 16, items = text },
                ["value"] = new { }, ["expected_value"] = new { }, ["arguments"] = new { type = "array", maxItems = 16 },
            }, ["operation"], ToolAccess.Unsafe),
            Define("invoke_sdk_member", "调用已发现的 SDK 包装函数", "使用精确成员 ID、SDK MVID 和已解析入口调用标量或指针包装函数。拒绝虚函数、引用参数和未支持构建。", new()
            {
                ["expected_sdk_mvid"] = guid, ["type_name"] = text, ["member_id"] = text, ["instance_address"] = text,
                ["arguments"] = new { type = "array", maxItems = 16 },
            }, ["expected_sdk_mvid", "type_name", "member_id", "arguments"], ToolAccess.Unsafe),
            Define("access_sdk_memory", "按 SDK 字段路径访问内存", "核对 SDK MVID 后按实际字段和固定数组下标定位。不会自动解引用指针，写入必须核对原字节。", new()
            {
                ["expected_sdk_mvid"] = guid, ["type_name"] = text, ["address"] = text, ["field_path"] = text,
                ["write"] = flag, ["hex"] = text, ["expected_hex"] = text,
            }, ["expected_sdk_mvid", "type_name", "address"], ToolAccess.Unsafe),
            Define("search_memory_values", "搜索并收敛内存候选", "在明确范围内扫描标量，按值或变化继续收敛。会话有容量与期限；不完整扫描不能证明目标不存在。", new()
            {
                ["operation"] = new { type = "string", @enum = new[] { "start", "read", "refine", "release" } },
                ["search_id"] = guid, ["address"] = text, ["length"] = Range(1, 16 * 1024 * 1024),
                ["type"] = new { type = "string", @enum = new[] { "u8", "u16", "u32", "u64", "i8", "i16", "i32", "i64", "f32", "f64" } },
                ["alignment"] = Range(1, 16), ["value"] = text, ["mode"] = new { type = "string", @enum = new[] { "equal", "not_equal", "changed", "unchanged", "increased", "decreased" } },
                ["offset"] = Range(0, 16384), ["limit"] = Range(1, 100),
            }, ["operation"], ToolAccess.Unsafe),
            Define("read_process_memory", "读取当前进程字节", "读取明确地址的有界字节，无法读取时返回失败，不跟随指针。", memory, ["address"], ToolAccess.Unsafe),
            Define("write_process_memory", "核对后写入当前进程数据", "仅写可写数据页，必须提供原字节并独立回读；比较和写入不具备原子性。", new(memory)
            {
                ["length"] = Range(1, 4096), ["hex"] = new { type = "string", maxLength = 8192 },
                ["expected_hex"] = new { type = "string", maxLength = 8192 },
            }, ["address", "length", "hex", "expected_hex"], ToolAccess.Unsafe),
            Define("inspect_address_regions", "读取地址范围的页面保护", "查询当前进程的操作系统区域及访问权限，最多返回 64 个区域。", new(memory)
            { ["length"] = Range(1, int.MaxValue) }, ["address", "length"], ToolAccess.Unsafe),
            Define("follow_memory_offsets", "读取有符号偏移指针链", "逐步读取指针再加偏移，失败时返回已确认的前缀。", new()
            {
                ["address"] = text, ["offsets"] = new { type = "array", minItems = 1, maxItems = 16, items = new { type = "integer" } },
                ["value_type"] = new { type = "string", @enum = new[] { "pointer", "u8", "i8", "u16", "i16", "u32", "i32", "u64", "i64", "f32", "f64" } },
            }, ["address", "offsets"], ToolAccess.Unsafe),
            Define("inspect_process_module", "读取当前进程模块与 PE 节", "读取已加载模块基址与已核对的磁盘节映射。默认查看主模块。", new()
            { ["module_name"] = text }, access: ToolAccess.Unsafe),
            Define("scan_process_memory", "扫描明确的内存范围", "按显式范围或已加载模块和节匹配模式，最多扫描 16 MiB、返回 4096 处命中。", new(memory)
            {
                ["length"] = Range(1, int.MaxValue), ["pattern"] = text, ["module_name"] = text, ["section"] = text,
            }, ["pattern"], ToolAccess.Unsafe),
        ];
    }
}
