# 诊断与数据

[返回 README](../README.md)

遇到权限、游戏状态或 SDK 兼容性限制时，先查看返回状态，再决定下一步。

## 运行时探索

用 `inspect_runtime_catalog` / `{"operation":"services"}` 可查看服务名称和类型；查找 SDK 类型时用 `operation=types` 与 `query`，再用 `operation=describe` 查看精确类型的公开成员和已知布局。查询返回的是元数据；运行时对象访问和底层观察另受底层调试权限与当前 SDK 限制。

## 插件功能发现

从 `list_plugins` 取得内部名，再以 `plugin_name` 查询 `list_plugin_commands`、`list_plugin_ipc` 或 `get_plugin_state`。命令帮助、IPC 签名和插件主动发布的状态各有用途：IPC 列表不执行端点，也不证明端点是只读的。只检查公开界面入口时，可结合插件条目返回的入口信息和 `get_window_snapshot`；窗口快照用法见[界面与输入](interface.md)。

## 运行日志

复现问题后，用 `get_dalamud_log` 查看最近的消息。例如按插件来源与错误关键字筛选：

```json
{"source":"<插件名>","contains":"Exception","max_count":20}
```

- `source` 和 `contains` 分别对来源与消息做不区分大小写的子串匹配。未找到结果时，可先取消筛选检查日志来源。
- `max_count` 可设为 1 至 100。返回的 `entries` 带日志 ID、时间、级别、来源和消息。这里只保留 DalamudMCP 加载后的近期日志，最多 256 条。
- 连续跟踪时，把上次返回的 `streamId` 和 `nextAfterId` 分别传给 `stream_id` 和 `after_id`。`hasMore=true` 时继续读取；`stream_changed` 时重新取基线；`gap=true` 表示有旧条目可能已被淘汰。

## 物品持有量

`locate_inventory_item` 接受基础 `item_id` 或本地化 `item_name`，例如 `{"item_name":"<物品名>","scope":"carried"}`。用 `locations` 检查容器和格位，按 `observedNormal`、`observedHighQuality`、`observedCollectible` 分别统计。先看 `coverage` 再解读数量；雇员只有在可确认且容器已加载时才能实时读取，`include_cached=true` 可带回本会话已有的雇员快照与观测时间。

## 战斗记录

1. 在「设置」启用「持续记录战斗」并开启所需的底层调试权限。角色登录后被动记录，脱战归档；底层调试关闭时会暂停。
2. 用 `get_combat_recorder` 查看 `activeCombatID`。要等待该场归档，把它作为 `combat_id` 传给 `wait_combat_recording`，并设 `"until":"archived"`。检查返回的阶段及归档 ID；归档与副本通关是不同结果。
3. 调用 `read_combat_capture`（参数为 `{}`）列出已有归档。选定一场后，把 `archiveID` 填到下列工具的 `archive_id`。

| 想回看什么 | 工具与参数 |
| --- | --- |
| 采样帧 | `read_combat_capture` 传入 `archive_id`，用 `offset`、`limit` 分页读帧。 |
| 技能效果 | `read_combat_timeline`；可按 `action_id`、`source_id` 或 `from_ms`、`to_ms` 限定范围。 |
| 读条、状态和死亡 | `read_combat_activity`；可用 `actor_id`、`kind` 及时间范围筛选。 |
| 机制事件 | `read_combat_mechanics` 可读取已观测的读条位置和朝向、点名、连线、场地效果和无目标效果。可用 `kind` 筛选事件类型，用 `nextOffset` 继续分页。 |
| 导出 JSON | `export_combat_capture` 在底层调试权限开启时将筛选后的效果及统计写入插件配置目录。 |

效果和时间线受观察范围、归档选项及数据缺失影响。「没有观察到」不能作为「没有发生」的证据。要根据当前读条或归档事件推算站位，见[危险区与安全区计算](mechanics.md)。

## ACR 决策

先启用战斗记录，确认所用 ACR 支持向 DalamudMCP 上报。战斗归档后，调用 `read_combat_acr_decisions`，例如 `{"archive_id":"<实际归档 ID>","limit":20}`。此处使用 `read_combat_capture` 返回的 `archiveID`。

用 `events` 查看上报内容和同次采集的角色、目标及技能状态；用 `nextOffset` 继续分页。`not_recorded` 表示归档没有机制证据；即使返回 `ok`，`matched=0` 也只表示该归档没有匹配的 ACR 上报。`truncated=true` 时记录可能不完整。若要确认技能是否实际生效，可对照同场的 `read_combat_timeline`。

## 网络观察

先用 `inspect_client_connection` 看客户端连接状态，再用 `inspect_zone_traffic_support` 查看当前构建的支持情况。这两步不会开始监听。区服观察默认记录元数据；启动观察需要底层调试权限。已有观察用 `read_zone_traffic_trace` 分页读取，元数据不含负载。原始负载仅在已核对的国服构建上受支持。

## 包体结构

例如用 `inspect_packet_layouts` / `{"kind":"packets","include_fields":true,"limit":5}` 查询布局和字段，或用 `inspect_packet_aliases` 核对不同来源的名称、方向与通道。布局来自内置定义、自有定义或 SDK 声明，命中目录不等于与当前国服客户端兼容。各数据的固定版本见[数据锁定清单](../scripts/packet-sources.lock.json)。
