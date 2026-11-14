# 角色、队伍与任务

[返回 README](../README.md) · [库存查询与战斗记录](diagnostics.md)

## 角色与队伍

| 要查什么 | 调用方式 | 返回内容 |
| --- | --- | --- |
| 当前区域、职业和坐标 | `get_player_context`，参数为 `{"compact":true}` | `territoryName`、`jobName` 和 `position`（世界坐标 `x/y/z`）。 |
| 完整角色状态 | `get_player_context`，参数为 `{}` | 角色、职业、等级、区域、世界坐标、是否在战斗及当前目标。 |
| 属性与职业进度 | `inspect_character_stats` 或 `inspect_job_progress`，参数为 `{}` | 当前面板属性、同步等级与客户端条件，或各职业等级及经验。 |
| 队伍与状态效果 | `inspect_party_roster`，例如 `{"include_effects":true}` | 客户端可见的队伍或团队成员、职业、区域、HP、MP 和可读取的状态。 |
| 本人或某个目标的状态 | `inspect_actor_effects`，例如 `{"actor":"focus"}` | 可用 `self`、`current`、`focus` 选择本人、当前目标或焦点目标。 |

登录、切区或复活期间，角色及部分数据可能尚未就绪。读取失败时先调用 `get_session_status` 核对 `loggedIn` 和 `localPlayerAvailable`。

## 任务与地图目标

1. **核对任务进度。** 调用 `get_quest_status`，传任务表行 ID `quest_id`，或用 `query` 按客户端语言的任务名称片段查询。查看 `matches` 中的 `questID`、`accepted`、`completed` 和 `sequence`；有多个匹配时，先核对具体任务。
2. **查看追踪目标。** 调用 `get_current_quest_objective`，从 `quests` 查看已追踪任务的阶段、`markers` 地图标记及 `links` 地图跳转信息。没有标记时，不能据此判断任务没有目标。
3. **查看当前地图的可接任务。** 调用 `get_available_quests`，可用 `name_contains` 和 `max_results` 筛选。结果只包括客户端当前显示的可接任务标记，未显示的任务不在本次查询范围内。

## 技能可用性

用 `list_current_job_actions` 查看当前职业和同步等级下的技能；加 `{"recharging_only":true}` 可筛选正在恢复且有独立冷却的技能。用返回的 `nextOffset` 继续分页。

要检查单个技能，用 `inspect_action_readiness` 传技能行 ID `action_id` 或精确的 `action_name`。查看 `availableNow`、`reason` 和 `nativeStatus`，可对照目标、读条、动画锁、冷却或充能数据。结果反映查询时的客户端状态；此调用不施放技能。

## 装备与货币

- `inspect_equipped_items` 读取当前装备槽、基础物品 ID、品级、耐久和幻化信息。
- `inspect_saved_loadouts` 查看本机保存的配装、当前槽位和关联幻化模板。若需要换装，`apply_saved_loadout` 需要游戏交互权限；指定 `slot` 或唯一完整 `name`，并核对 `expected_current_slot`、目标配装的 `expected_job_id` 和 `expected_territory_id`。用返回的 `resultObserved` 核对是否实际换装。
- `inspect_currency_balances` 查看客户端已加载的货币余额和周限神典石信息，可用 `include_zero` 显示已知零余额。未取得的余额不能按零统计。
