# 脚本与开发

安装方式、端口和认证见 [README](../README.md)。以下命令从仓库根目录执行。

## 构建与安装

准备 PowerShell 7、.NET 10 SDK 和 Dalamud API 15。将 `-DalamudHome` 改为包含 `Dalamud.dll` 的目录，输出使用独立的插件目录。

```powershell
pwsh -NoProfile -File ./build.ps1 -DotNet (Get-Command dotnet).Source -DalamudHome 'D:\Path\To\Dalamud' -OutputDirectory 'D:\Plugins\DalamudMCP'
```

[build.ps1](../build.ps1) 默认构建 Release，同时发布网关。可用 `-Configuration Debug` 生成调试构建。省略 `-OutputDirectory` 时，使用本机配置中的输出目录；没有配置则输出到仓库内的 `artifacts/DalamudMCP`。发布后，在卫月开发插件设置中加载 `DalamudMCP.dll`，保持旁边的 `server/DalamudMCP.Server.exe` 路径不变。

如需将 Rider 或命令行编译的插件 DLL 输出到固定目录，在仓库根目录创建不纳入 Git 的 `Directory.Build.local.props`，将示例路径替换为自己的路径：

```xml
<Project>
  <PropertyGroup>
    <DalamudMCPOutputRoot>D:\Plugins\DalamudMCP</DalamudMCPOutputRoot>
  </PropertyGroup>
</Project>
```

本机配置只改变插件的普通编译输出。网关仍在项目的编译目录生成；要更新插件旁的单文件网关，请用 `build.ps1`。没有本机配置时，普通编译仍使用各项目的默认输出目录。

## 选择游戏实例

脚本仅依赖 Python 标准库，Windows 下自动读取当前用户的 MCP 令牌，不需要把令牌写进命令。

```powershell
python scripts/mcp_client.py instances
python scripts/mcp_client.py --pid 12345 call get_session_status
python scripts/mcp_client.py --instance <实例ID> call get_player_context
python scripts/mcp_client.py --pid 12345 list --filter log --schemas
```

把 `12345` 或实例 ID 替换为 `instances` 输出的目标值。多实例时必须明确选择；不存在的目标、身份变化和已退出的实例都会报错。`--instance` 固定本次插件加载，`--pid` 固定游戏进程。显式 `--url` 适合手动连接，不能与实例选择参数混用。`call` 支持 `--arguments-file <JSON文件>`；使用 `--server exd` 可连接本机 EXDViewer。

## 多开回归检查

在 Windows 上从仓库根目录执行以下命令。C# 检查会启动模拟游戏管道、原生端点及三个并发网关，不需要运行游戏；Python 检查模拟发现与调用结果。

```powershell
dotnet run --project tests/DalamudMCP.Tests/DalamudMCP.Tests.csproj -c Release -- "$PWD/src/DalamudMCP.Server/bin/Release/net10.0/win-x64/DalamudMCP.Server.dll"
python tests/test_client_selection.py
```

覆盖端口占用与并发启动、实例发现、目标退出、原生 PID 校验、默认日志路径隔离，以及多实例选择歧义和端点身份变化。卫月内部宿主与日志适配仍须在实际游戏中验证。

## 核对运行构建

1. 连接后，调用 `read_development_workflow` 查看当前运行构建附带的验收流程，再用 `get_dev_build` 核对目标插件登记的 DLL 路径。
2. 编译后读取新 DLL 的 MVID（程序集模块标识），用 `wait_for_dev_build` 确认已登记的插件加载了同一构建。仅在 `status=ready` 且 `buildVerified=true` 时继续；首次注册或手动重载须经授权。
3. 用 `get_dalamud_log` 检查日志，再按功能回读窗口或游戏状态。网关重载后需重新连接并刷新工具列表。

## 发行

[Pluginmaster.json](../Pluginmaster.json) 提供插件安装器所需的版本、安装包地址和图标地址。准备发行时，使用 Release 产物，保持 `DalamudMCP.dll`、插件清单、依赖清单、`images` 和 `server` 的相对位置；让清单中的版本和安装、更新地址与实际发行附件一致。开发安装仅需 [build.ps1](../build.ps1) 的输出目录。

打包时联网获取固定版本的依赖声明并嵌入网关，可通过 `server/DalamudMCP.Server.exe --licenses` 查看。

## 数据维护

用 [Import-PacketLayouts.py](Import-PacketLayouts.py) 和 [Export-PacketMetadata.mjs](Export-PacketMetadata.mjs) 维护内置协议定义，输入版本见[数据锁定清单](packet-sources.lock.json)。
