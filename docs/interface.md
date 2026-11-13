# 界面与输入

[返回 README](../README.md)

## Addon 查询

Addon 是游戏原生窗口。用 `get_addon_list` 取得当前已加载的窗口，从 `visible=true` 和 `ready=true` 的条目中选出目标。用返回的 `addonName` 填写后续工具的 `addon_name`。

### 按条件定位节点

查单个控件时用 `get_addon_nodes`。下面演示在实际发现的窗口里查找标签精确匹配的可见按钮：

```json
{"addon_name":"<实际窗口名>","label":"<按钮全文>","component_type":"Button","visible_only":true,"max_results":10}
```

| 参数 | 用法 |
| --- | --- |
| `label`、`contains_match` | 默认按完整标签匹配，不区分大小写；开启 `contains_match` 后按子串匹配，仍须提供 `label`。 |
| `node_id`、`component_type` | 按节点编号或组件类型筛选，例如 `Button`、`CheckBox`；可与标签条件组合。 |
| `component_path` | 将返回的 `componentPath` 原样传回，按由外到内的组件 ID 限定搜索范围，最多 8 层。省略时搜索各组件；`[]` 将搜索限定在窗口顶层。 |
| `visible_only`、`max_results` | 默认只返回可见节点，最多返回 50 条；结果上限可设为 1 至 100。 |

`nodes` 包含 `nodeID`、`componentPath`、类型、标签、可见性和启用状态，复选框另有 `checked`。先看 `scopeStatus` 是否为 `resolved`，以及 `incomplete` 和 `truncated`：前者表示扫描未覆盖全部目标范围，后者表示只返回了部分匹配。遇到路径歧义或扫描不完整，不能据此认定节点不存在或唯一。

### 核对节点快照

把节点的 `nodeID` 和 `componentPath` 分别传为 `node_id`、`component_path`，调用 `inspect_addon_node`；也可用精确标签定位。它返回 `addonID`、节点位置与尺寸、变换、注册事件，以及可读取的动画和图像信息。后续查询可传 `expected_addon_id`；若返回 `addon_changed`，重新发现窗口和节点。想查看跨帧的位置或动画变化时，可用 `sample_addon_node_frames` 观察，节点换实例后采样停止。

如果需要查看整体层级，再用 `get_addon_tree`。可见性和启用状态有助于选定控件，实际交互还受工具权限、窗口状态和支持范围约束。

## 原生文字

只想知道窗口里有什么文字时，用 `get_addon_strings`，例如 `{"addon_name":"SelectString"}`。返回的 `strings` 中，`source=node` 的条目带 `nodeID`，`source=atkValue` 的条目带值的 `index`。读取结果可能含未显示的字符串；判断屏幕上是否可见时，用节点查询，或用 `capture_game_screenshot` 核对画面。

可用 `inspect_menu_choice` 核对已打开菜单的选项并消除歧义。需要操作时先确认交互权限和支持范围；派发后用下一窗口、控件状态或其他独立读数确认实际结果。

## ImGui 输入

这些工具适用于卫月和插件的 ImGui 窗口。先用 `get_window_snapshot` 读取窗口 ID、可见性、焦点和可用的控件 ID。若要等某窗口显示，可用 `wait_for_window_visibility`，参数为实际 `window_id` 或完整 `window_name`，并指定 `"condition":"visible"`。

取得授权且目标字段已聚焦时，`queue_imgui_text` 使用 `window_id`、`field_id` 和 `text` 排入一段文本；它不会提交表单。随后查看窗口或插件公开状态，确认文本是否已接受。任何无法确认目标的输入应先停止并重新取样。

## 截图与同帧裁剪

用 `capture_game_screenshot` 传 `{}` 获取完整渲染画面的 PNG。默认包含卫月界面，可用 `include_dalamud_ui:false` 只取绘制卫月界面前的画面。返回的 `width`、`height` 是 PNG 尺寸，`sourceWidth`、`sourceHeight` 是原始画面尺寸。

要放大同一张截图中的局部，用返回的 `screenshotID` 作为 `screenshot_id`，再传 `crop`（`x`、`y`、`width`、`height`）。裁剪矩形使用原始画面的像素坐标，截图缩放过时须先换算。缓存帧找不到时会返回 `screenshot_not_found`，此时重新截图；`crop_out_of_bounds` 表示需要重新核对矩形。缓存只保存在内存中。

## 副本列表

先在游戏内打开任务搜索器，再用 `inspect_duty_browser` 查看当前分组、已选副本和排队状态。用 `list_duty_entries` 读取当前分组中可辨识的副本行。`entriesComplete=false` 时，只能使用已返回的结果。

要选中条目，开启游戏交互，调用 `request_duty_selection`，使用刚取得的 `stateToken` 和 `territoryID` 填写 `state_token` 和 `expected_territory_id`。选副本传 `operation:"open_duty"` 及 `duty_id`；切分组传 `operation:"select_group"` 及 `group_id`。令牌仅短期有效，窗口或区域变化后应重新查询。随后用 `inspect_duty_browser` 核对选择结果；选中条目不会报名或确认进本。
