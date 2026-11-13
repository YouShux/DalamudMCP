# 传送与到达核对

[返回 README](../README.md)

先查询目的地，再提交请求，最后用角色状态确认是否到达。查询不需要交互权限；发起传送需要开启「允许游戏交互」。

## 查询已解锁的水晶

调用 `inspect_unlocked_travel`，例如：

```json
{"name_contains":"格里达尼亚","limit":10}
```

`name_contains` 按客户端语言的名称片段筛选；已知区域时也可传 `territory_id`。在 `destinations` 中核对 `name`、`aetheryteID`、`territoryID` 和 `gilCost`（客户端显示的费用）。有 `nextOffset` 时，可作为下一次的 `offset`。

## 提交传送请求

调用 `teleport_to_aetheryte`，将 `query` 设为查询结果中的完整名称，或将大水晶 ID 转成字符串：

```json
{"query":"<查询结果中的大水晶 ID>"}
```

大水晶查询没有唯一结果时，先核对实际目的地；若名称重复，可换用水晶 ID。城内以太网不在大水晶清单里，可按城内目的地查询提交，需要 Omni 的传送 IPC 和已启用的「更好的传送管理」。这一路径返回 `provider: "OmniToolbox"`。

## 验证到达

`requestAccepted=true` 表示请求已受理，此时 `resultObserved=false`。等切区或移动结束后，调用 `get_player_context`，用 `territoryID` 和 `position` 核对目的地。同一区域的以太网传送，单凭区域 ID 无法确认是否到达。

## 区域内导航

安装并启用 vnavmesh，确认当前区域导航网格就绪。先用 `inspect_navigation_path` 检查 `calculating`、`active` 和 `maximumRange`。确认角色在目标区域且没有正在使用的路线，再用 `route_to_coordinate`，传入世界坐标 `x`、`y`、`z` 及当前 `expected_territory_id`。工具会尝试将终点投到可用网格，且不会接管已有路线。

如果返回 `requested`，继续用 `inspect_navigation_path` 观察路径状态；最终用 `get_player_context` 核对角色位置。路径结束本身不能证明角色已经到达。
