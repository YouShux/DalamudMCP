# Agent 开发指引

[返回 README](../README.md)

连接方式和权限设置见 README。给 Agent 描述任务时，写清要检查的目标、期望结果及是否允许交互。

示例：「先检查任务窗口和里面的文字，告诉我当前任务标题；本次只读。」

## 获取工具目录

自行实现 MCP 客户端时，用 `tools/list` 获取工具名称和输入 schema；如果返回 `nextCursor`，继续取下一页。收到工具目录变更通知后重新查询目录。

## 读取开发指引

`read_development_workflow` 返回当前运行构建所附的验收说明及修订信息。请让 Agent 连接插件后直接读取。

开发验收见[脚本与开发](../scripts/README.md)中的「核对运行构建」。
