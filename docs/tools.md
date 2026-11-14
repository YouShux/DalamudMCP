# 工具参考

[返回 README](../README.md)

本页按用途列出 DalamudMCP 自有工具。实际可调用的名称、参数、必填项和工具说明以当前连接的 `tools/list` 为准；单项开关、权限、卫月调试连接以及客户端状态会影响可用工具和返回结果。

## 按需启用工具

在游戏内输入 `/dalamudmcp`，打开「工具」页，可按来源与用途筛选，也可搜索名称或用途说明。单项工具由「启用」复选框控制。

「全部开启」和「全部关闭」作用于当前来源和用途，包括被搜索条件隐藏的条目。选择「全部用途」时，操作涵盖当前来源下的所有工具。已连接的卫月内置工具归入「底层诊断」。批量开启只勾选工具；游戏交互和底层调试权限仍在「设置」页单独控制。

关闭工具后，服务不会在后续的 `tools/list` 中返回该工具，也会拒绝直接调用。若客户端还显示旧目录，刷新工具列表或重新连接。部分客户端会把所有可用工具的说明与参数定义传给模型，有些则按需加载；关闭不用的工具能缩小服务返回的目录，实际对上下文的影响取决于客户端。

| 场景 | 建议启用 |
| --- | --- |
| 日常查询 | 可先保留 `get_session_status`、`get_player_context`、`get_addon_list`、`get_addon_strings` 和 `locate_inventory_item`；需要看画面时再开截图。 |
| 任务、传送与导航 | 按需开任务进度、追踪目标和水晶查询。需要实际移动时，再开对应工具及「允许游戏交互」。 |
| 界面检查与操作 | 优先开文字、节点和窗口快照查询；需要输入时，再开对应的界面操作工具。 |
| 战斗与机制 | 按需开启归档、时间线和站位计算；排查选招时开 ACR 决策读取。需要录制新战斗时，另按记录方式开启所需权限。 |
| 插件开发 | 按需开启日志、插件发现和构建核验；排查结束后可以关闭不常用的观察工具。 |
| 底层诊断 | 协议、进程和卫月调试工具平时可关闭，有明确排查任务时再启用所需项。 |

## 权限与结果

| 权限 | 设置 |
| --- | --- |
| 只读 | 无需开启「允许游戏交互」或「允许底层调试」。部分工具的附加操作另有条件。 |
| 游戏交互 | 在插件的「设置」页开启「允许游戏交互」。 |
| 底层调试 | 在插件的「设置」页开启「允许底层调试」。 |

多数自有工具把数据放在 `structuredContent.data`；截图等工具会返回对应的媒体内容。收到 `isError=false` 后，还要检查业务状态：`requested` 表示已经提交请求，需要独立回读确认。分页时使用返回的游标；例如把 `nextOffset` 传为下一次的 `offset`，日志续读则使用 `streamId` 和 `nextAfterId`。

使用示例见[界面与输入](interface.md)、[角色、队伍与任务](game-state.md)、[日志、战斗与 ACR](diagnostics.md)、[危险区与安全区计算](mechanics.md)和[传送与到达核对](travel.md)。

## 会话、日志与角色状态

| 工具 | 说明 | 权限 |
| --- | --- | --- |
| `get_session_status` | 读取登录状态、游戏工具数量及当前运行的 DalamudMCP 构建标识。 | 只读 |
| `poll_client_changes` | 分页读取登录、区域、战斗、职业、目标或角色资源变化，可按类型筛选。提供上次返回的流 ID 和序号可等待下一页。 | 只读 |
| `get_dalamud_log` | 读取本插件加载后保留的卫月运行日志。可按来源或文本筛选；增量读取使用上次返回的 `streamId` 和 `nextAfterId`。 | 只读 |
| `get_chat_log` | 读取插件加载后保留的最近 1000 条聊天记录。 | 只读 |
| `get_player_context` | 读取角色状态；`compact=true` 只返回区域、职业和世界坐标。 | 只读 |
| `inspect_character_stats` | 读取当前角色的 HP、MP、采集力、制作力、同步等级、面板属性及生效的客户端条件。 | 只读 |
| `inspect_job_progress` | 读取客户端中各职业和特职的等级、经验及当前特职。 | 只读 |
| `inspect_character_controls` | 读取当前咏唱、硬目标、朝向、自动前进及可确认的复活来源。 | 只读 |
| `request_character_control` | 核对区域、职业及当前状态，再取消咏唱、接受复活、设置自动前进或转向当前目标。 | 游戏交互 |
| `inspect_party_roster` | 读取客户端可见的队伍或团队成员、职业、区域及 HP、MP，可附带状态效果。 | 只读 |
| `inspect_companion_roster` | 读取客户端可见的搭档、宠物和任务支援成员。 | 只读 |
| `inspect_nearby_battle_actors` | 按距离列出客户端已加载的战斗对象、生命值和当前施法。默认只看战斗 NPC。 | 只读 |
| `inspect_target_slots` | 读取当前、焦点、临时、上一个、鼠标悬停及集体拍照目标。 | 只读 |
| `inspect_actor_effects` | 读取本人、当前目标或焦点目标已有的状态效果。 | 只读 |

## 任务、地图与副本

| 工具 | 说明 | 权限 |
| --- | --- | --- |
| `get_quest_status` | 按任务 ID 或名称查询接受、完成及阶段。 | 只读 |
| `get_available_quests` | 读取当前地图实际显示的可接任务标记。 | 只读 |
| `get_current_quest_objective` | 读取追踪任务阶段、地图目标及地图跳转信息。 | 只读 |
| `get_fate_context` | 读取附近危命任务、进度及剩余时间。 | 只读 |
| `inspect_area_map` | 读取角色地图和当前选择的地图；仅在地图已显示时分页读取静态图标。图标坐标不能传给导航。 | 只读 |
| `inspect_society_dailies` | 读取国服客户端的剩余接取额度、部族声望和日常任务记录。 | 只读 |
| `inspect_character_unlock` | 按类型和对应数据表行 ID 查询解锁或完成状态。称号和成就列表未载入时返回未知。 | 只读 |
| `get_duty_context` | 读取当前区域及真实副本完成状态。 | 只读 |
| `inspect_duty_browser` | 读取已打开的任务搜索器、分组、选择和排队状态。 | 只读 |
| `list_duty_entries` | 只列出当前窗口可辨识的行，按副本表行 ID 标识。 | 只读 |
| `request_duty_selection` | 核对窗口状态令牌并派发一次，独立回读选择结果。 | 游戏交互 |
| `use_duty_action` | 使用当前副本任务技能的第 1 或 2 个槽位。 | 游戏交互 |

## 库存、装备与货币

| 工具 | 说明 | 权限 |
| --- | --- | --- |
| `get_inventory_summary` | 读取已加载的背包、装备、兵装库及金币。 | 只读 |
| `locate_inventory_item` | 按基础物品 ID 或名称查询库存。scope=retainer 可列出物品，include_cached 可读取本会话已访问雇员的快照。 | 只读 |
| `inspect_open_bag_menu` | 将当前可见的普通背包物品菜单与实际库存槽位和物品关联。不打开菜单或选择条目。 | 只读 |
| `request_bag_slot_menu` | 向已核对背包格派发一次右键；再用 inspect_open_bag_menu 核对实际物品。 | 游戏交互 |
| `inspect_equipped_items` | 读取装备槽、基础物品、品级、耐久及幻化 ID。 | 只读 |
| `inspect_saved_loadouts` | 读取本机保存的装备套装、当前槽位、装等和幻化模板，不切换装备。 | 只读 |
| `apply_saved_loadout` | 按槽位或唯一完整名称应用配装。核对原配装、区域和目标职业，再确认槽位、职业和实际装备。游戏可能应用关联投影。 | 游戏交互 |
| `inspect_currency_balances` | 读取已载入的货币容器和周限神典石数据；未知余额不计为零。 | 只读 |

## 技能、热键栏与聊天

| 工具 | 说明 | 权限 |
| --- | --- | --- |
| `inspect_job_meter` | 读取当前特职的公开量谱字段；不读取其他职业的内存。 | 只读 |
| `inspect_action_recasts` | 按技能表行 ID 查询冷却计时和充能，不判定技能当前能否施放。 | 只读 |
| `inspect_action_readiness` | 按行 ID 或精确名称读取同一帧的目标、动作映射、客户端状态码、冷却和充能。不施放技能。 | 只读 |
| `list_current_job_actions` | 按同步等级汇总职业、基础职业和职能技能，可只看非纯公共冷却中正在恢复的技能。 | 只读 |
| `request_checked_action` | 核对技能、职业、区域和当前目标后只请求一次。支持跳跃、冲刺、传送、返回、下坐骑及普通技能。 | 游戏交互 |
| `inspect_hotbar_slot` | 读取当前角色普通热键栏的槽位、命令和当前目标。 | 只读 |
| `list_hotbar_recharges` | 去重读取当前普通热键栏中计时仍在进行的技能和通用指令。 | 只读 |
| `request_hotbar_slot` | 核对槽位内容、职业、区域和当前目标后只提交一次；不支持宏、PvP 或游戏按键注入。 | 游戏交互 |
| `submit_game_chat_line` | 核对区域后向明确的聊天频道提交单行纯文本；请求返回不代表送达。 | 游戏交互 |

## 目标、传送与导航

| 工具 | 说明 | 权限 |
| --- | --- | --- |
| `get_nearby_interactables` | 按距离读取可选中对象。 | 只读 |
| `target_object` | 通过对象 ID 选中当前区域对象。 | 游戏交互 |
| `set_focus_actor` | 核对当前焦点对象 ID 后，设置当前区域的可选对象为焦点并回读。无焦点时传 0。 | 游戏交互 |
| `interact_with_target` | 与当前目标交互，可校验预期目标 ID。 | 游戏交互 |
| `request_npc_interaction` | 核对区域与事件 NPC；可选择短距离接近。交互一次后核对窗口，可按精确标签选择一次并核对下一界面。 | 游戏交互 |
| `inspect_unlocked_travel` | 分页读取客户端已解锁的传送目的地及显示费用。 | 只读 |
| `teleport_to_aetheryte` | 按当前语言名称或 ID 传送已解锁的大水晶；城内以太网复用 Omni，需开启“更好的传送管理”。Omni 返回成功表示请求已接受，到达情况请查询区域和坐标。 | 游戏交互 |
| `wait_game_arrival` | 根据移动前记录的起点与目标，等角色连续两次位于目标区域或指定坐标内。不会派发移动。 | 只读 |
| `move_to_entity` | 通过 vnavmesh 寻路移动到指定对象，不模拟按键。 | 游戏交互 |
| `move_to_nearby_interactable` | 按名称找到最近可交互对象，通过 vnavmesh 移动。 | 游戏交互 |
| `inspect_navigation_path` | 查询 vnavmesh 是否在算路、移动，以及当前路线的最后一个路径点。 | 只读 |
| `route_to_coordinate` | 核对区域与配置距离，将坐标投影到可用网格后请求 vnavmesh 导航。已有路线时不接管。 | 游戏交互 |
| `stop_navigation_path` | 核对区域和目标坐标后停止匹配路线；取消本工具的计算需要 `request_id`。 | 游戏交互 |

## 原生窗口和控件

| 工具 | 说明 | 权限 |
| --- | --- | --- |
| `get_addon_list` | 列出游戏当前加载的原生窗口。 | 只读 |
| `get_addon_strings` | 读取窗口文本节点及 AtkValue 字符串。 | 只读 |
| `get_addon_tree` | 读取指定原生窗口的节点树，最多 4096 个节点、16 层。 | 只读 |
| `get_addon_nodes` | 按标签、节点 ID、组件类型或组件路径查询原生窗口节点；复选框同时返回勾选状态。 | 只读 |
| `inspect_addon_node` | 用节点 ID 或完整标签定位唯一原生节点，一次读取几何、可见性、注册事件、动画和图像分片。 | 只读 |
| `sample_addon_node_frames` | 跨框架帧观察同一原生节点的位置、可见性和动画；节点换实例时停止。 | 只读 |
| `read_addon_control` | 按完整标签或节点 ID、组件路径读取文本、数字、复选框、下拉框或列表。 | 只读 |
| `watch_addon_state` | 只读等待窗口或节点可见性、或复选框状态；连续两次采样满足条件才返回确认。 | 只读 |
| `inspect_menu_choice` | 只读匹配已显示的菜单条目，返回选项索引、文本和歧义，不派发选择。 | 只读 |
| `inspect_addon_list_row` | 读取可见普通或树形列表渲染行的真实索引、当前选中行和可见文字。 | 只读 |
| `request_known_addon` | 核对原可见状态后，只派发一次背包、地图、任务日志或任务搜索器的开关请求，并独立回读。 | 游戏交互 |
| `select_addon_menu_item` | 按唯一文本选择 SelectString、SelectIconString 或 TelepotTown 条目。 | 游戏交互 |
| `edit_addon_control` | 核对窗口 ID、组件和原值后编辑文本、数字、复选框或选中项，并回读。扩展范围需要单独开启设置。 | 游戏交互 |
| `adjust_dropdown_panel` | 核对窗口和原状态后发出一次下拉开关通知，再等稳定状态。 | 游戏交互 |
| `request_addon_list_row` | 核对列表渲染行的真实索引、原选中行、完整文字和注册监听器，再激活一次。 | 游戏交互 |
| `activate_addon_row` | 核对窗口、原选中索引、目标索引和标签，向可见行派发一次激活事件。 | 游戏交互 |
| `request_addon_button` | 核对当前窗口、按钮及其注册的原生事件，派发一次，再核对窗口变化或到达位置。确认框需给出完整提示。 | 游戏交互 |
| `dispatch_when_node_ready` | 固定窗口与节点实例，满足尺寸、对齐或旋转条件时派发一次。几何条件可先预览。 | 游戏交互 |
| `send_addon_input` | 向白名单窗口发送游戏内输入。 | 游戏交互 |
| `send_addon_event` | 向白名单窗口节点发送事件，可按唯一标签或组件路径定位，并等待指定可见性或复选框勾选状态。 | 游戏交互 |
| `send_addon_callback_values` | 向白名单窗口发送 1 至 32 个整数回调参数。 | 游戏交互 |
| `observe_addon_events` | 观察窗口接收事件之前或之后的类型和参数；可筛选事件明确携带的节点。不触发输入。 | 只读 |
| `observe_addon_callbacks` | 核对窗口 ID 后限时记录回调参数和返回值；文本默认关闭。不触发任何回调。 | 底层调试 |
| `observe_addon_node_listener` | 按节点与已注册的事件定位一个监听器，限时记录匹配事件，可显式读取 SDK 定义的原始事件结构。 | 底层调试 |
| `observe_ui_callbacks` | 在限定时长内观察自然发生的原生窗口回调，记录窗口身份、参数和返回值，不触发回调。 | 底层调试 |

## 截图、插件窗口与输入

| 工具 | 说明 | 权限 |
| --- | --- | --- |
| `capture_game_screenshot` | 读取游戏渲染帧或用 screenshot_id 对同一帧裁剪，返回 PNG。内存缓存最多 4 帧、60 秒，不写文件。 | 只读 |
| `inspect_game_window` | 读取当前游戏进程主窗口是否最小化、是否在前台。 | 只读 |
| `request_game_window_state` | 核对原窗口状态后提交一次请求并回读；还原可能激活游戏窗口。 | 游戏交互 |
| `request_foreground_game_key` | 向前台游戏窗口发送有限次按下和释放；不激活窗口或移动系统焦点。 | 游戏交互 |
| `get_window_snapshot` | 查询 ImGui 窗口的当前帧状态；可按完整名称或 ID 匹配。 | 只读 |
| `wait_for_window_visibility` | 按完整名称或 ID 等待 ImGui 窗口在连续两帧满足指定状态。 | 只读 |
| `probe_imgui_point` | 在当前悬停的 ImGui 窗口内探测一个坐标并回读控件 ID。不会点击或移动系统鼠标。 | 游戏交互 |
| `sample_imgui_hit_points` | 在当前悬停窗口中依次探测最多 12 个点，返回各点悬停控件，不点击。 | 游戏交互 |
| `queue_imgui_text` | 给当前聚焦且 ID 匹配的 ImGui 文本框排入纯文本，不发送按键或提交。排入不代表字段接受。 | 游戏交互 |
| `queue_imgui_gesture` | 核对当前悬停或导航控件后，排入单次点击、滚轮或导航键。不会移动系统鼠标；排入不代表控件已处理。 | 游戏交互 |
| `queue_window_chord` | 核对导航控件后排入一次键盘输入，可带修饰键。界面结果须另行回读。 | 游戏交互 |
| `drag_window_control` | 按窗口和控件 ID 核对后进行有界虚拟拖动；人的输入优先，不移动系统光标。 | 游戏交互 |
| `hold_window_key` | 核对导航控件后保持按键最多两秒，到期、取消或用户输入时释放。完成不代表业务结果。 | 游戏交互 |
| `get_window_input_queue` | 读取排队数、当前任务、按键归属和释放状态。 | 只读 |
| `cancel_window_input` | 取消剩余输入并在下一渲染帧释放本工具持有的按键。帧停滞时明确返回未确认清理。 | 游戏交互 |
| `queue_input_sequence` | 向已核对的前台游戏窗口或插件控件派发有界输入。人工输入会停止剩余步骤。 | 游戏交互 |
| `get_input_sequence` | 查询派发步骤、业务回读和输入释放状态。 | 只读 |
| `cancel_input_sequence` | 停止序列并释放工具持有的输入。 | 游戏交互 |

## 战斗记录与结果

| 工具 | 说明 | 权限 |
| --- | --- | --- |
| `sample_combat_window` | 有限时地采集本人、目标和可选队伍成员的战斗状态。 | 只读 |
| `archive_combat_window` | 采样并把结果写入本地有限数量的归档文件。 | 游戏交互 |
| `set_combat_recorder` | 按战斗开始和结束保存采样，duration_seconds=0 持续到停止或会话变化；持久记录使用 configure_combat_history。 | 游戏交互 |
| `set_combat_dispatch_recorder` | 限时开启战斗事件归档，观察客户端已解析的动作效果；重载后不恢复。 | 底层调试 |
| `configure_combat_history` | 持久开启或关闭被动战斗记录。登录后恢复；权限关闭时暂停，异常时停止重试。默认关闭。 | 底层调试 |
| `get_combat_recorder` | 读取当前录制状态、本场 ID、丢弃数和上次归档结果。 | 只读 |
| `wait_combat_recording` | 按录制器给出的本场 ID 等待自然结束或自动归档，最长 20 秒；超时返回该场状态。归档不代表通关。 | 只读 |
| `read_combat_capture` | 列出有限数量的本地战斗归档，或按 ID 读取一次采样。 | 只读 |
| `read_combat_timeline` | 按时间分页读取归档中已观察到的动作效果，可按来源或技能筛选；不推断命中或施放次数。 | 只读 |
| `read_combat_activity` | 分页读取归档中的读条、状态、死亡和观察范围变化。状态缺失不直接判定为驱散。 | 只读 |
| `read_combat_mechanics` | 按同一归档 ID 分页读取已观察的读条、点名、连线、地板和无目标效果。 | 只读 |
| `read_combat_acr_decisions` | 按战斗归档 ID 分页读取 ACR 上报的检查结果和同次调用中的客户端状态。 | 只读 |
| `analyze_combat_capture` | 按来源或技能分组汇总观察到的效果和批次间隔，支持分页。 | 只读 |
| `summarize_combat_capture` | 按归档 ID 汇总可观察到的各角色血量变化和施法起点；采样差值不是伤害或治疗归因。 | 只读 |
| `export_combat_capture` | 在插件配置目录保存筛选后的 JSON 与分组统计，并返回文件哈希。 | 底层调试 |
| `create_battle_checkpoint` | 在指定战斗录制中记录当前区域及信号起点，供独立核对通关。 | 只读 |
| `wait_battle_result` | 按本场战斗和观察起点等待客户端通关事件、完整系统文本或窗口原文。战斗结束或归档不代表通关。 | 只读 |

## 危险区与机制计算

| 工具 | 说明 | 权限 |
| --- | --- | --- |
| `inspect_combat_mechanics` | 用客户端数据计算当前读条的危险区和点位，支持场地内栅格采样及 M4S 电网。可将快照附到当前战斗归档。 | 只读 |
| `solve_mechanic_grid` | 按输入的危险行、列、格和生效时间计算每格危险与可站候选时段。行列从 0 开始；不依赖副本或角色状态。 | 只读 |
| `solve_mechanic_assignments` | 按危险格、个人候选格、踩塔、分摊、散开和连线条件，求一组满足约束的站位。 | 只读 |
| `plan_mechanic_route` | 按移动速度与危险时段计算到达路线；支持在当前安全格等待，不执行移动。 | 只读 |
| `evaluate_mechanic_knockback` | 按来源或指定方向计算直线位移，核对落点和可选的沿途危险格。 | 只读 |
| `replay_combat_mechanics` | 按归档 ID 重放事件、用声明式规则重算危险区，并对照位置和伤害记录。规则仅作计算，不运行脚本。 | 只读 |

## 客户端数据表

| 工具 | 说明 | 权限 |
| --- | --- | --- |
| `lookup_game_rows` | 按行 ID 或名称筛选读取常用国服数据表。名称查找每页有扫描上限，即使本页为空也可按 `nextOffset` 续查。 | 只读 |
| `browse_client_sheets` | 分页查找本机客户端包含的数据表。 | 只读 |
| `describe_client_sheet` | 读取指定表的列编号、类型和偏移，以及是否包含子行。 | 只读 |
| `read_client_sheet_cells` | 按表名、行 ID 和列编号读值；子行表还需指定子行。 | 只读 |
| `list_sheet_properties` | 列出当前客户端生成的字段名、类型和可搜索字段。 | 只读 |
| `read_sheet_record` | 按行号读取命名字段；支持显式字段列表、字段分页和子行。 | 只读 |
| `find_sheet_text` | 按指定字段或全部文本字段搜索；可用返回的两个游标继续查询子行表。 | 只读 |

## 插件、构建与仓库

| 工具 | 说明 | 权限 |
| --- | --- | --- |
| `list_plugins` | 分页列出插件内部名、版本、加载状态和公开界面入口。 | 只读 |
| `list_plugin_commands` | 列出公开斜杠命令及帮助，可按插件处理器程序集筛选。 | 只读 |
| `list_plugin_ipc` | 列出已注册的 IPC 函数或动作及签名。不会调用端点，也不会从名称推断其是否只读。 | 只读 |
| `get_plugin_state` | 读取插件加载状态及其主动发布的状态快照。不会调用目标插件的函数。 | 只读 |
| `show_plugin_window` | 调用已加载插件公开的主界面或设置页入口一次。返回 requested 表示回调已返回，请用只读窗口工具确认是否可见。 | 游戏交互 |
| `get_dev_build` | 按注册的 DLL 绝对路径查询开发插件、自动重载和构建标识。单次匹配不表示新构建已就绪。 | 只读 |
| `wait_for_dev_build` | 只读等待已注册的开发插件加载指定构建。编译成功后传入产物的 MVID，仅在 status 为 ready 且 buildVerified 为 true 时继续验收。 | 只读 |
| `load_dev_dll` | 按绝对路径注册并加载指定构建的本地开发插件，并验证运行 MVID。只应加载自己信任的 DLL。 | 底层调试 |
| `reload_plugin` | 重载指定开发插件；禁止重载本插件。 | 底层调试 |
| `read_development_workflow` | 读取运行中 DLL 的开发指引、版本与 MVID，可附带技能文件和校验值。 | 只读 |
| `find_repository_plugins` | 分页查询卫月已加载的仓库目录、正式和测试版本、来源及安装状态，不刷新仓库。 | 只读 |
| `inspect_repository_sources` | 读取主仓库、预设仓库和用户仓库的配置与运行状态。 | 只读 |
| `change_repository_source` | 新增、删除、启停或刷新仓库。删除和启停须核对原启用状态；不安装任何插件。 | 底层调试 |
| `install_repository_plugin` | 从已启用的第三方 HTTPS 仓库安装指定版本；测试版需显式选择且卫月已允许测试版。 | 底层调试 |
| `remove_repository_plugin` | 卸载同源同版本的第三方插件，由卫月安排文件清理，保留配置；用返回的 id 查询进度。 | 底层调试 |
| `get_repository_job` | 按插件或仓库管理任务返回的 ID 查询进度与核对结果。 | 只读 |

## 插件命令、IPC 与消息

| 工具 | 说明 | 权限 |
| --- | --- | --- |
| `slash_command` | 执行已注册的 Dalamud 斜杠命令，不向服务器发送聊天命令。 | 底层调试 |
| `list_saved_plugin_endpoints` | 只读列出已保存的 IPC 别名和声明签名。 | 只读 |
| `edit_saved_plugin_endpoint` | 按别名新增或移除 IPC 声明；新增校验已加载插件及精确签名，移除核对原入口。 | 底层调试 |
| `call_saved_plugin_endpoint` | 按别名调用，重新核对所属插件、声明签名与实参。返回值不是独立的结果回读。 | 底层调试 |
| `unsafe_invoke_plugin_ipc` | 按明确参数类型调用任意已注册插件 IPC。 | 底层调试 |
| `invoke_plugin_ipc` | 调用 PluginName.MCP.Method 插件约定 IPC。 | 底层调试 |
| `read_plugin_messages` | 按流和游标读取本实例保留的消息。发布方名称由其自报，读取不消费历史。 | 只读 |
| `plugin_data_subscribe` | 创建有界回传通道，最多 32 个通道，每通道 1000 条。 | 底层调试 |
| `plugin_data_poll` | 取出并消费通道中排队的数据。 | 底层调试 |
| `plugin_data_unsubscribe` | 注销插件数据回传通道。 | 底层调试 |

## 运行时检查

| 工具 | 说明 | 权限 |
| --- | --- | --- |
| `inspect_runtime_health` | 读取当前进程和线程编号，区分托管线程与系统线程，不暂停进程。 | 只读 |
| `read_runtime_calls` | 按游标读取最近 100 条诊断调用名称、耗时和返回状态，不记录参数、结果值或图像。 | 只读 |
| `inspect_runtime_catalog` | 独立查询已注入服务和当前 SDK 的类型、成员、原生布局。只读元数据，不调用属性或函数。 | 只读 |
| `start_runtime_observation` | 被动观察明确内存、宿主事件或已发现的 SDK 入口。托管方法前置观察需要宿主已有 Harmony；不会调用目标函数。 | 底层调试 |
| `read_runtime_observation` | 查询观察列表或按观察 ID 分页读取参数和内存变化，不消费记录。 | 底层调试 |
| `stop_runtime_observation` | 解除对应订阅或 Hook；可同时移除留存证据，清理失败时保留状态。 | 底层调试 |
| `watch_runtime_value` | 在框架线程按精确对象引用和成员 ID 采样；可等待预期值，保留有界变化记录。 | 底层调试 |
| `access_runtime_object` | 按有期限的引用和精确成员 ID 读取、设置或调用公开成员。属性读取也可能执行代码。 | 底层调试 |
| `invoke_sdk_member` | 使用精确成员 ID、SDK MVID 和已解析入口调用标量或指针包装函数。拒绝虚函数、引用参数和未支持构建。 | 底层调试 |
| `access_sdk_memory` | 核对 SDK MVID 后按实际字段和固定数组下标定位。不会自动解引用指针，写入必须核对原字节。 | 底层调试 |
| `search_memory_values` | 在明确范围内扫描标量，按值或变化继续收敛。会话有容量与期限；不完整扫描不能证明目标不存在。 | 底层调试 |
| `read_process_memory` | 读取明确地址的有界字节，无法读取时返回失败，不跟随指针。 | 底层调试 |
| `write_process_memory` | 仅写可写数据页，必须提供原字节并独立回读；比较和写入不具备原子性。 | 底层调试 |
| `inspect_address_regions` | 查询当前进程的操作系统区域及访问权限，最多返回 64 个区域。 | 底层调试 |
| `follow_memory_offsets` | 逐步读取指针再加偏移，失败时返回已确认的前缀。 | 底层调试 |
| `inspect_process_module` | 读取已加载模块基址与已核对的磁盘节映射。默认查看主模块。 | 底层调试 |
| `scan_process_memory` | 按显式范围或已加载模块和节匹配模式，最多扫描 16 MiB、返回 4096 处命中。 | 底层调试 |

## 连接、网络观察与协议

| 工具 | 说明 | 权限 |
| --- | --- | --- |
| `inspect_client_connection` | 读取已核对 SDK 上的网络模块初始化、实例和框架更新状态，不抓包。 | 只读 |
| `sample_client_connection` | 在有限时间内统计当前进程的 TCP 状态及客户端状态，不输出端点或数据包。 | 只读 |
| `inspect_zone_traffic_support` | 检查元数据入口和国服原始包入口是否可用，不启动监听。 | 只读 |
| `set_zone_traffic_trace` | 限时观察区服收发，默认只记录元数据。仅核对过的国服构建可明确开启原始包，不主动发送。 | 底层调试 |
| `read_zone_traffic_trace` | 分页读取当前或最近一次收发事件。不包含端点、对象 ID 或负载。 | 只读 |
| `read_zone_packet_bytes` | 按观察 ID 分页读取被动采样的十六进制字节及长度。出站为业务负载，入站包含 IPC 头。 | 底层调试 |
| `read_zone_packet_slice` | 按采集 ID 和包序号读取指定字节范围的十六进制与 ASCII。 | 底层调试 |
| `set_zone_callback_capture` | 限时观察入站回调元数据，或按命令 ID 采集负载和出站参数。不发送或改写消息。 | 底层调试 |
| `read_zone_callback_capture` | 分页读取采集状态和元数据；可按采集 ID 显式读取负载。 | 底层调试 |
| `inspect_opcode_catalog` | 列出来源声明的地区，或按地区、方向、编号及名称分页查询已载入的目录。 | 只读 |
| `inspect_opcode_refresh` | 读取本次插件加载的更新检查结果，不发起新的网络请求。 | 只读 |
| `check_cn_opcode_catalog` | 按需用 HTTPS 读取固定公开来源的国服操作码表，与上次指纹比较。不会更新运行时操作码或验证客户端兼容性。 | 只读 |
| `find_cn_zone_opcode` | 按发送或接收方向查公开表，可按编号或部分名称筛选。使用最近一次成功获取的表；结果不证明游戏当前映射。 | 只读 |
| `refresh_opcode_catalog` | 从固定来源校验并保存地区目录，不改变已启动采集的名称快照。 | 底层调试 |
| `inspect_packet_layouts` | 分页查询内置包、完整结构元数据、自有定义和 SDK 声明。定义存在不代表国服兼容。 | 只读 |
| `inspect_packet_aliases` | 查询不同来源的包名称、方向和通道。自动解析仅采用当前操作码来源的明确对应关系。 | 只读 |
| `inspect_protocol_updates` | 查看启动或手动检查的进度、来源结果和版本比较，不发起网络请求。 | 只读 |
| `check_packet_sources` | 并行检查固定发布包、源码目录和名称目录；十秒超时，仅报告变化，不下载执行或替换定义。 | 只读 |
| `save_packet_layout` | 保存声明式布局，不执行脚本；覆盖时必须核对旧指纹。 | 底层调试 |
| `parse_zone_packet` | 按采集时固定的操作码名称和方向选择唯一布局；也可明确指定布局和指纹。歧义或缺少定义时停止。 | 底层调试 |
| `decode_zone_packet` | 直接解码已捕获字节；支持数值、文本、数组、嵌套、位域和枚举。缺失字节明确标出。 | 底层调试 |
| `compare_zone_packets` | 比较两个包的原始字节，保留布局和操作码差异提示。 | 底层调试 |
| `decode_ipc_sample` | 按调用方提供的字段布局解码有界十六进制样本。不连接服务器或读取实时包。 | 只读 |
| `compare_ipc_samples` | 分页比较调用方提供的两个样本，区分不同字节和缺少的尾部。 | 只读 |

## 卫月内置调试工具

这部分工具由网关连接卫月后动态读取。连接成功后，在游戏内打开 `/dalamudmcp` 的「工具」页并选择「卫月调试」，查看当前工具；客户端也可从 `tools/list` 查看对应的 `description` 和 `inputSchema`。来自卫月的工具说明带有 `[Dalamud 原生]` 前缀。网关会按权限筛选工具和可用操作；同名时使用 DalamudMCP 自有工具。
