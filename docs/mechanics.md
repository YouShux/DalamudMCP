# 危险区与安全区计算

[返回 README](../README.md) · [日志、战斗与诊断](diagnostics.md)

安全候选是指按当前输入或已观测事件计算、未落入已建模危险区的点或格子。计算不执行移动，也不覆盖未识别、未观测或未提供规则的机制。

## 检查当前读条和站位

直接调用 `inspect_combat_mechanics`，传入 `{}` 即可查看当前识别到的危险区。要验证站位，提供场地边界和待检查的游戏世界坐标。下面的圆场地仅用于说明参数，请按实际场地替换数值：

```json
{
  "arena": {"shape":"circle","x":100,"z":100,"radius":20},
  "points": [[100,0,100],[110,0,100]],
  "grid_step": 4
}
```

- `arena` 支持圆形、矩形和多边形。矩形传 `x`、`z`、`half_length`、`half_width`；多边形传 `vertices`，每个顶点为 `[x,z]`。
- `points` 接受最多 32 个 `[x,y,z]` 点；危险区计算使用水平面的 X、Z 坐标。未指定场地时，仍可判断点是否落入已建模危险区，但不能判断是否在场内。
- `grid_step` 是可选的采样间距，需要已知场地边界。若超出 1024 个采样点，增大间距。

| 返回字段 | 判读 |
| --- | --- |
| `hazards` | 已识别的技能危险区，包括作用范围和预计生效时间。 |
| `samples[].result` | `modeled_danger` 表示命中已建模危险区；`outside_arena` 表示在场外；`outside_modeled_hazards` 表示在场内且不落入已建模危险区；`unknown` 表示信息不足。 |
| `grid.candidates` | 在场内且不落入已建模危险区的采样格中心。只代表所列采样点。 |
| `status`、`unmodeledActionIDs`、`truncated` | 分别用于检查当前环境是否可用、哪些技能没有建模、扫描是否截断。没有识别到危险区、存在未建模技能或扫描截断时，不输出栅格候选点。 |

## 根据已知规则计算多轮网格

`solve_mechanic_grid` 根据输入的危险行、列、格以及生效时间计算，不依赖当前副本或角色状态。下例在一个 3×3 场地中，先封锁中间一列，再封锁第一行；左下角始终不可站。

```json
{
  "rows": 3,
  "columns": 3,
  "duration_ms": 12000,
  "hazards": [
    {"from_ms":2000,"until_ms":5000,"columns":[1]},
    {"from_ms":5000,"until_ms":8000,"rows":[0]}
  ],
  "excluded_cells": [[2,0]],
  "at_ms": 5500,
  "limit": 9
}
```

索引从 0 开始，`cells` 和 `excluded_cells` 中的格子按 `[行,列]` 填写；每个时段包含起点、不包含终点。若要返回格中心的世界坐标，用 `layout` 指定网格原点 `x`、`z` 和格宽 `cell_size`。无旋转时，列号沿 X 增加，行号沿 Z 增加；第 0 行第 0 列的中心在原点两个方向上各偏移半格。

`phases` 列出各时段的危险格数与候选格数；`cells` 给出每格的 `danger` 和 `candidates` 时间段。传入 `at_ms` 后，单格还会返回该时刻的状态：上例在 5500 毫秒时，第 0 行是 `danger`，左下格是 `excluded`，其余 5 格是 `candidate`。用 `nextOffset` 读取后续格子。候选只依据输入的规则，未提供的危险不会被自动补全。

## 从战斗归档回看

- **查看已保存的计算快照**：向 `inspect_combat_mechanics` 传 `archive_id`。默认取最后保存的快照，传 `at_ms` 则取该时刻或更早的最近快照。`no_local_snapshot` 表示未取得快照；要在录制时保存快照，可在实时调用中传当前 `combat_id` 和 `record=true`，并检查 `recorded`。
- **用已知规则重放**：先用 `read_combat_mechanics` 查该场记录到了哪些事件，再向 `replay_combat_mechanics` 传 `archive_id` 与 `rules`。每条规则需要 `id`、`event`、`event_id` 和 `duration_ms`，还需指定危险形状或格子。结果中的 `rulesWithoutObservation` 列出没有匹配观测的规则。若提供了网格布局和行列数，可把返回的 `gridInput` 传给 `solve_mechanic_grid`；若 `gridInputIncomplete=true`，候选结果只覆盖已提供的证据。
