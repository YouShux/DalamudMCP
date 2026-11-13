<div align="center">
  <h1>DalamudMCP</h1>
  <p><img src="images/icon.png" alt="DalamudMCP 插件图标" width="160"></p>
  <p>
    <a href="https://github.com/YouShux/DalamudMCP/releases/latest"><img src="https://img.shields.io/github/v/release/YouShux/DalamudMCP?label=%E6%9C%80%E6%96%B0%E7%89%88%E6%9C%AC&amp;style=for-the-badge" alt="最新版本"></a>
    <a href="https://github.com/YouShux/DalamudMCP/releases"><img src="https://img.shields.io/github/downloads/YouShux/DalamudMCP/total?label=%E7%B4%AF%E8%AE%A1%E4%B8%8B%E8%BD%BD&amp;style=for-the-badge" alt="累计下载"></a>
    <a href="#安装"><img src="https://img.shields.io/badge/安装方式-卫月插件库-5865F2?style=for-the-badge" alt="查看安装方式"></a>
  </p>
  <p>
    <img src="https://img.shields.io/badge/Windows-x64-0A66C2?style=flat-square" alt="Windows x64">
    <img src="https://img.shields.io/badge/.NET-10-512BD4?style=flat-square" alt=".NET 10">
    <img src="https://img.shields.io/badge/Dalamud-API%2015-0078D4?style=flat-square" alt="Dalamud API 15">
    <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-2F855A?style=flat-square" alt="MIT 许可证"></a>
  </p>
  <p><strong>把许愿式编程带进《最终幻想 XIV》</strong></p>
</div>

DalamudMCP 是面向《最终幻想 XIV》国服的卫月插件。它把游戏状态、运行日志和战斗归档提供给同一台电脑上的 MCP 客户端，也可按权限执行传送和界面操作。

## 安装

### 从卫月在线仓库安装

在卫月设置的「自定义插件仓库」添加并保存下方地址，再打开插件安装器，搜索并安装 `DalamudMCP`。

```text
https://raw.githubusercontent.com/YouShux/DalamudMCP/main/Pluginmaster.json
```

### 从本地安装

按[构建说明](scripts/README.md)生成本地插件，在卫月的开发插件设置中添加输出目录里的 `DalamudMCP.dll`，并保留同目录的 `server` 文件夹。两种方式任选其一；切换安装方式时，先停用原先加载的副本。

## 配置与连接

1. **启动服务。** 进入游戏后输入 `/dalamudmcp`，确认「服务运行中」并复制地址。若插件提示需要令牌，先在 Windows 用户环境变量 `DALAMUD_MCP_BEARER_TOKEN` 中设置随机令牌，再重启启动器、游戏和 MCP 客户端。令牌须为 32 至 256 个字符，仅使用英文字母、数字、`-` 或 `_`；建议使用 32 字节随机数据的十六进制字符串。
2. **添加 MCP 服务。** 在同一台电脑上的 MCP 客户端添加名为 `DalamudMCP` 的 **Streamable HTTP** 服务，默认地址为 `http://127.0.0.1:61016/mcp`。如果服务启用了认证，通过客户端的密钥配置发送同一令牌（`Authorization: Bearer <令牌>`）。需要修改端口时，先在插件窗口停止服务，改完重新启动，再更新客户端地址。
3. **验证连接。** 让客户端调用 `get_session_status`。若 `loggedIn` 和 `localPlayerAvailable` 均为 `true`，再用 `get_player_context` 读取职业、等级和坐标。首次查询无需开启游戏交互。

服务仅监听本机。游戏交互和底层调试默认关闭，在「设置」页按需开启。「工具」页支持按用途筛选、整组启停和单项开关；关闭的工具不会出现在客户端的新工具目录中。变更后刷新客户端的工具目录，必要时重新连接。需要连接卫月内置调试时，再开启卫月开发者模式，对齐调试端口（默认 `39217`），点击「重新连接卫月调试」。

## 文档导航

| 功能 | 用途 |
| --- | --- |
| [Agent 开发指引](docs/agent.md) | 获取工具目录和输入约束，读取开发验收指引。 |
| [界面、截图与副本列表](docs/interface.md) | 查询原生窗口、文字和 ImGui 控件，裁剪同一帧，读取副本选择与排队状态。 |
| [角色、队伍与任务](docs/game-state.md) | 查看角色属性、队伍状态、任务目标、配装、货币和技能可用性。 |
| [传送与区域内导航](docs/travel.md) | 查询水晶和费用，提交传送或导航请求并确认到达。 |
| [日志、战斗与诊断](docs/diagnostics.md) | 筛选日志，查询插件命令、物品格位、战斗归档和 ACR 决策，也可查运行时及协议数据。 |
| [危险区与安全区计算](docs/mechanics.md) | 根据读条、网格规则或归档事件计算危险范围和站位候选。 |

其余功能见[工具参考](docs/tools.md)。

## 常见问题

| 现象 | 先检查什么 |
| --- | --- |
| 无法连接或返回 401 | 在 `/dalamudmcp` 确认服务正在运行，再核对端口和 Bearer 令牌。服务启动失败时检查端口是否被占用、`server` 目录是否完整。 |
| 已连接但读不到角色 | 用 `get_session_status` 检查登录状态；登录或切区时等角色就绪再查询。 |
| 工具列表不完整 | 检查工具开关、权限及卫月调试连接，再刷新客户端的工具目录。 |
| 没有战斗归档或 ACR 决策 | 检查是否启用了记录、战斗是否已归档，以及 ACR 是否主动上报。 |
| 传送已受理，位置却没变 | 等切区或移动结束，再核对区域和坐标。 |
| 编译后游戏里还是旧行为 | 比较新 DLL 和运行构建的 MVID，再检查实际效果。 |
