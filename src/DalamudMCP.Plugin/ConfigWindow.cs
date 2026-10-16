using System.Numerics;
using Dalamud.Bindings.ImGui;
using DalamudMCP.Shared;

namespace DalamudMCP.Plugin;

public sealed class ConfigWindow(SettingsStore settings, ServerController server, IReadOnlyList<ToolDefinition> gameTools)
{
    private bool isOpen;
    private string search = string.Empty;
    private int source;
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
        ImGui.SetNextItemWidth(ImGui.CalcTextSize("全部工具      ").X + ImGui.GetFrameHeight());
        ImGui.Combo("##source", ref source, ["全部工具", "游戏工具", "卫月调试"], 3);
        var tools = server.Status?.Tools ?? [.. gameTools];
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
            if (source == 1 && tool.Source != "game" || source == 2 && tool.Source == "game") continue;
            if (search.Length != 0 && !tool.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !tool.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !tool.Description.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            ImGui.PushID(tool.Name);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var enabled = !settings.Current.DisabledTools.Contains(tool.Name);
            if (ImGui.Checkbox("##enabled", ref enabled))
            {
                if (enabled) settings.Current.DisabledTools.Remove(tool.Name);
                else settings.Current.DisabledTools.Add(tool.Name);
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
        var unsafeTools = config.AllowUnsafe;
        if (ImGui.Checkbox("允许底层调试", ref unsafeTools))
        {
            config.AllowUnsafe = unsafeTools;
            settings.Save();
        }
        Help("允许写入内存、调用函数、挂钩、控制线程和插件集成。错误调用可能导致游戏退出。");
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
