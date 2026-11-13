using System.Numerics;
using Dalamud.Bindings.ImGui;
using DalamudMCP.Shared;

namespace DalamudMCP.Plugin;

public sealed class ConfigWindow(SettingsStore settings, ServerController server, IReadOnlyList<ToolDefinition> gameTools)
{
    private bool isOpen;
    private string search = string.Empty;
    private int source;
    private int purpose;
    private int httpPort = settings.Current.HTTPPort;
    private int nativePort = settings.Current.NativePort;
    private string? portError;

    public void Open() => isOpen = true;

    public void Draw()
    {
        if (!isOpen) return;
        server.Refresh();
        ImGui.SetNextWindowSize(new Vector2(780, 560), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("DalamudMCP###DalamudMCP.Config", ref isOpen, ImGuiWindowFlags.NoCollapse))
        {
            DrawConnection();
            ImGui.Separator();
            if (ImGui.BeginTabBar("sections"))
            {
                if (ImGui.BeginTabItem("工具"))
                {
                    DrawTools();
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("设置"))
                {
                    DrawSettings();
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }
        ImGui.End();
    }

    private void DrawConnection()
    {
        ImGui.AlignTextToFramePadding();
        var inlineButtons = ImGui.GetContentRegionAvail().X >= ImGui.CalcTextSize(server.Endpoint).X
            + ImGui.CalcTextSize("复制地址    启动").X + ImGui.GetStyle().FramePadding.X * 4 + ImGui.GetStyle().ItemSpacing.X * 2;
        ImGui.TextWrapped(server.Endpoint);
        if (inlineButtons) ImGui.SameLine();
        if (ImGui.Button("复制地址")) ImGui.SetClipboardText(server.Endpoint);
        ImGui.SameLine();
        if (ImGui.Button(server.IsRunning ? "停止" : "启动"))
        {
            if (server.IsRunning) server.Stop();
            else server.Start();
        }
        ImGui.TextColored(server.IsRunning ? SuccessColor : MutedColor, server.IsRunning ? "服务运行中" : "服务已停止");
        ImGui.SameLine();
        var nativeConnected = server.Status?.NativeConnected == true;
        ImGui.TextColored(nativeConnected ? SuccessColor : MutedColor, nativeConnected ? "卫月调试已连接" : "卫月调试未连接");
        if (server.Error is { } error) ImGui.TextWrapped(error);
    }

    private void DrawTools()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "搜索工具名称或用途", ref search, 256);

        var comboPadding = ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("来源");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.CalcTextSize("全部来源").X + comboPadding);
        ImGui.Combo("##source", ref source, ["全部来源", "游戏工具", "卫月调试"], 3);
        ImGui.SameLine();
        ImGui.TextUnformatted("用途");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.CalcTextSize("战斗与机制").X + comboPadding);
        ImGui.Combo("##purpose", ref purpose, ToolPurposes.Labels, ToolPurposes.Labels.Length);

        IReadOnlyList<ToolDefinition> tools = server.Status?.Tools ?? gameTools;
        DrawToolGroupActions(tools);

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY
            | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("tools", 4, flags, new Vector2(0, 0))) return;
        ImGui.TableSetupColumn("启用", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("启用").X + ImGui.GetStyle().FramePadding.X * 2);
        ImGui.TableSetupColumn("工具", ImGuiTableColumnFlags.WidthStretch, 4);
        ImGui.TableSetupColumn("来源", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn("权限", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
        foreach (var tool in tools)
        {
            if (!MatchesToolFilters(tool)) continue;
            if (search.Length != 0 && !tool.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !tool.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !tool.Description.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            ImGui.PushID(tool.Name);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var enabled = IsToolEnabled(tool);
            if (ImGui.Checkbox("##enabled", ref enabled))
            {
                SetToolEnabled(tool, enabled);
                settings.Save();
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(tool.Title);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 34);
                ImGui.TextUnformatted(tool.Name);
                ImGui.Separator();
                ImGui.TextWrapped(tool.Description);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(tool.Source == "game" ? "游戏" : "卫月");
            ImGui.TableNextColumn();
            var allowed = tool.Access switch
            {
                ToolAccess.Action => settings.Current.AllowActions,
                ToolAccess.Unsafe => settings.Current.AllowUnsafe,
                _ => true,
            };
            var label = tool.Access switch { ToolAccess.Action => "交互", ToolAccess.Unsafe => "底层调试", _ => "只读" };
            ImGui.TextColored(allowed ? MutedColor : WarningColor, allowed ? label : $"{label}未启用");
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawToolGroupActions(IReadOnlyList<ToolDefinition> tools)
    {
        var count = 0;
        var enabledCount = 0;
        foreach (var tool in tools)
        {
            if (!MatchesToolFilters(tool)) continue;
            ++count;
            if (IsToolEnabled(tool)) ++enabledCount;
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(enabledCount == count);
        var enable = ImGui.Button("全部开启");
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(enabledCount == 0);
        var disable = ImGui.Button("全部关闭");
        ImGui.EndDisabled();

        if (enable || disable)
        {
            foreach (var tool in tools)
                if (MatchesToolFilters(tool)) SetToolEnabled(tool, enable);
            settings.Save();
            enabledCount = enable ? count : 0;
        }

        ImGui.SameLine();
        ImGui.TextUnformatted($"已勾选{enabledCount}/{count}");
    }

    private bool MatchesToolFilters(ToolDefinition tool) =>
        (source == 0 || (source == 1) == (tool.Source == "game")) &&
        (purpose == (int)ToolPurposes.Purpose.All || purpose == (int)ToolPurposes.Get(tool));

    private bool IsToolEnabled(ToolDefinition tool) =>
        !settings.Current.DisabledTools.Contains(tool.Name);

    private void SetToolEnabled(ToolDefinition tool, bool enabled)
    {
        var disabledTools = settings.Current.DisabledTools;
        if (enabled) disabledTools.Remove(tool.Name);
        else disabledTools.Add(tool.Name);
    }

    private void DrawSettings()
    {
        var config = settings.Current;
        ImGui.Separator();
        ImGui.TextUnformatted("工具权限");
        var actions = config.AllowActions;
        if (ImGui.Checkbox("允许游戏交互", ref actions))
        {
            config.AllowActions = actions;
            settings.Save();
        }
        Help("允许移动、传送、目标交互和界面操作。每项工具仍可单独关闭。");
        var extendedEditing = config.AllowExtendedControlEditing;
        if (ImGui.Checkbox("允许编辑更多原生控件", ref extendedEditing))
        {
            config.AllowExtendedControlEditing = extendedEditing;
            settings.Save();
        }
        Help("需要游戏交互权限。仅编辑能确认类型和原值的控件，且不会提交表单。");
        var unsafeTools = config.AllowUnsafe;
        if (ImGui.Checkbox("允许底层调试", ref unsafeTools))
        {
            config.AllowUnsafe = unsafeTools;
            settings.Save();
        }
        Help("允许写入内存、调用函数、挂钩、控制线程和插件集成。错误调用可能导致游戏退出。");
        ImGui.Separator();
        ImGui.TextUnformatted("诊断");
        var opcodeCheck = config.CheckOpcodesOnLoad;
        if (ImGui.Checkbox("加载插件时检查操作码表更新", ref opcodeCheck))
        {
            config.CheckOpcodesOnLoad = opcodeCheck;
            settings.Save();
        }
        Help("下次加载时检查一次公开表；已有采集继续使用启动时固定的版本。公开表不代表客户端兼容。");
        var packetCheck = config.CheckPacketSourcesOnLoad;
        if (ImGui.Checkbox("加载插件时检查协议定义来源", ref packetCheck))
        {
            config.CheckPacketSourcesOnLoad = packetCheck;
            settings.Save();
        }
        Help("后台检查发布包、源码和名称目录的变化，只报告结果，不自动替换定义。");
        var recording = config.RecordCombatContinuously;
        if (ImGui.Checkbox("持续记录战斗", ref recording))
        {
            config.RecordCombatContinuously = recording;
            settings.Save();
        }
        Help("需要底层调试权限。登录后被动记录，脱战自动保存，权限关闭时暂停。");
        DrawBounded("战斗归档数量", config.CombatArchiveLimit, 1, 1000, value => config.CombatArchiveLimit = value);
        DrawBounded("战斗归档容量（MiB）", config.CombatArchiveMiB, 1, 100, value => config.CombatArchiveMiB = value);
        DrawBounded("最大导航距离（游戏码）", config.NavigationRange, 250, 2000, value => config.NavigationRange = value);
        ImGui.Separator();
        ImGui.TextUnformatted("连接");
        var autoStart = config.AutoStart;
        if (ImGui.Checkbox("加载插件时启动服务", ref autoStart))
        {
            config.AutoStart = autoStart;
            settings.Save();
        }
        ImGui.BeginDisabled(server.IsRunning);
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 9);
        ImGui.InputInt("MCP 端口", ref httpPort);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            if (httpPort is >= 1024 and <= 65535 && httpPort != config.NativePort)
            {
                config.HTTPPort = httpPort;
                settings.Save();
                portError = null;
            }
            else
            {
                httpPort = config.HTTPPort;
                portError = "端口须为 1024～65535，且两个服务不能使用同一端口。";
            }
        }
        ImGui.EndDisabled();
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 9);
        ImGui.InputInt("卫月调试端口", ref nativePort);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            if (nativePort is >= 1024 and <= 65535 && nativePort != config.HTTPPort)
            {
                config.NativePort = nativePort;
                settings.Save();
                server.Refresh(true);
                portError = null;
            }
            else
            {
                nativePort = config.NativePort;
                portError = "端口须为 1024～65535，且两个服务不能使用同一端口。";
            }
        }
        if (portError is { } errorText) ImGui.TextColored(WarningColor, errorText);
        ImGui.BeginDisabled(!server.IsRunning || server.IsRefreshing);
        if (ImGui.Button("重新连接卫月调试")) server.Refresh(true);
        ImGui.EndDisabled();
        if (server.Status?.NativeError is { } error)
            ImGui.TextWrapped($"{error}。请在卫月设置中开启开发者模式，并核对调试端口。");
    }

    private void DrawBounded(string label, int saved, int min, int max, Action<int> update)
    {
        var value = Math.Clamp(saved, min, max);
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 9);
        if (ImGui.InputInt(label, ref value) && value >= min && value <= max)
        {
            update(value);
            settings.Save();
        }
        if (saved < min || saved > max) ImGui.TextUnformatted($"配置值超出范围，当前按 {Math.Clamp(saved, min, max)} 使用。");
    }

    private static void Help(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (!ImGui.IsItemHovered()) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
        ImGui.TextWrapped(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static readonly Vector4 SuccessColor = new(0.40f, 0.80f, 0.55f, 1);
    private static readonly Vector4 MutedColor = new(0.65f, 0.68f, 0.73f, 1);
    private static readonly Vector4 WarningColor = new(0.95f, 0.70f, 0.35f, 1);
}
