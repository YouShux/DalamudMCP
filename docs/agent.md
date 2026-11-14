# Agent 开发指引

[返回 README](../README.md)

连接方式和权限设置见 README。给 Agent 描述任务时，写清要检查的目标、期望结果及是否允许交互。

示例：「先检查任务窗口和里面的文字，告诉我当前任务标题；本次只读。」

## 按需使用工具

已知工具名称和参数时直接调用。例如，只查区域、职业和世界坐标时，调用 `get_player_context` 并传入 `{"compact":true}`。不知道工具名时，用客户端的工具搜索限定到 DalamudMCP，按任务找相关工具，只加载候选工具的参数定义。例如查背包里的某件物品，搜索「背包物品」，选择 `locate_inventory_item`。

如果客户端把全部定义传给模型，插件无法替客户端按需加载。可在游戏内按用途[关闭不使用的工具](tools.md#按需启用工具)。手动禁用的工具不会参与搜索，Agent 也不能自动启用；游戏交互和底层调试权限仍由用户控制。

自行实现 MCP 客户端时，在客户端内部用 `tools/list` 获取工具名称和输入 schema；如果返回 `nextCursor`，继续取下一页。收到工具目录变更通知后刷新内部目录，只给模型提供任务相关的定义。

同一步骤需要区域、职业或坐标时，复用一次角色查询结果。发生移动、传送或切换职业后再读取新快照。只监视登录、区域、职业等变化时，用 `poll_client_changes` 的游标按需续读；坐标仍用 `get_player_context` 获取。支持结构化结果的客户端可在 MCP URL 加 `?result=structured`，由网关自动省去重复的文本副本。

## 读取开发指引

`read_development_workflow` 返回当前运行构建所附的验收说明及修订信息。请让 Agent 连接插件后直接读取。

开发验收见[脚本与开发](../scripts/README.md)中的「核对运行构建」。

查找已加载插件、命令、IPC 或运行状态时，分别用 `list_plugins`、`list_plugin_commands`、`list_plugin_ipc` 或 `get_plugin_state`。IPC 签名只说明调用方式，不能据此判断调用是否只读。经授权用 `show_plugin_window` 打开插件窗口后，用 `get_window_snapshot` 或 `wait_for_window_visibility` 核对窗口是否可见。
