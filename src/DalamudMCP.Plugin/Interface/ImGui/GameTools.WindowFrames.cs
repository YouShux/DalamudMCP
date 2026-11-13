using System.Numerics;
using System.Text;
using System.Text.Json;
using Dalamud.Bindings.ImGui;

namespace DalamudMCP.Plugin.Tools;

public sealed partial class GameTools
{
    private readonly WindowFrameObserver windowFrames = new();
    private ScreenshotViewport? renderViewport;
    private readonly WindowInputScheduler windowInput = new();

    private async Task<object> ProbeImGuiPointsAsync(ToolArguments input, Func<bool> isAllowed, CancellationToken token)
    {
        var windowID = input.Element("window_id").GetUInt32();
        var points = input.Element("points");
        if (windowID == 0 || points.ValueKind != JsonValueKind.Array || points.GetArrayLength() is < 1 or > 12)
            throw new ArgumentException("需要有效窗口 ID 和 1 至 12 个探测点。");
        var validated = new List<(int X, int Y)>();
        foreach (var element in points.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 2 ||
                !element[0].TryGetInt32(out var x) || !element[1].TryGetInt32(out var y) ||
                x is < -10000 or > 10000 || y is < -10000 or > 10000)
                throw new ArgumentException("坐标必须是两个范围为 -10000 至 10000 的整数。");
            validated.Add((x, y));
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(5000);
        var observations = new List<object>(validated.Count);
        try
        {
            foreach (var (x, y) in validated)
            {
                var arguments = new ToolArguments(JsonSerializer.SerializeToElement(new { window_id = windowID, x, y }));
                var observation = await QueueImGuiInputAsync(arguments, "probe_imgui_point", isAllowed, deadline.Token).ConfigureAwait(false);
                observations.Add(new { x, y, observation });
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        return new { status = observations.Count == validated.Count ? "complete" : "timeout", observations };
    }

    internal void CaptureWindowFrame()
    {
        Volatile.Write(ref renderViewport, ScreenshotViewport.Read(renderViewport, includeWindow: false));
        windowFrames.Capture(ReadWindowFrame);
        windowInput.Attach();
    }

    private ScreenshotViewport? ReadScreenshotViewport() =>
        Volatile.Read(ref renderViewport) is { } viewport ? viewport with { Window = GameWindowState.ReadInputWindow() } : null;

    private async Task<object> QueueImGuiInputAsync(ToolArguments input, string tool, Func<bool> isAllowed, CancellationToken token)
    {
        var windowID = input.Element("window_id").GetUInt32();
        var hover = tool == "probe_imgui_point";
        var chord = tool is "queue_window_chord" or "hold_window_key";
        var drag = tool == "drag_window_control";
        var textInput = tool == "queue_imgui_text";
        var controlID = hover ? 0 : input.Element(textInput ? "field_id" : "control_id").GetUInt32();
        var text = textInput ? AddonTools.PlainText(input.Element("text")) : null;
        var action = tool == "queue_imgui_gesture" ? input.Text("gesture") : null;
        var coordinates = input.Has("x") || input.Has("y");
        if (coordinates && (!input.Has("x") || !input.Has("y") || !hover && !drag &&
            action is not ("click" or "right_click" or "middle_click" or "wheel_up" or "wheel_down")))
            throw new ArgumentException("坐标须成对传入，且只用于鼠标输入。");
        Vector2? point = hover || drag || coordinates ? new Vector2(input.Integer("x", int.MinValue, -10000, 10000),
            input.Integer("y", int.MinValue, -10000, 10000)) : null;
        var modifiers = new List<ImGuiKey>(3);
        var key = action switch
        {
            "up" => ImGuiKey.UpArrow,
            "down" => ImGuiKey.DownArrow,
            "left" => ImGuiKey.LeftArrow,
            "right" => ImGuiKey.RightArrow,
            "enter" => ImGuiKey.Enter,
            "escape" => ImGuiKey.Escape,
            "click" or "right_click" or "middle_click" or "wheel_up" or "wheel_down" or null => ImGuiKey.None,
            _ => throw new ArgumentException("不支持该 ImGui 手势。"),
        };
        if (chord)
        {
            key = ParseWindowKey(input.Text("key"));
            if (input.Boolean("ctrl")) modifiers.Add(ImGuiKey.ModCtrl);
            if (input.Boolean("shift")) modifiers.Add(ImGuiKey.ModShift);
            if (input.Boolean("alt")) modifiers.Add(ImGuiKey.ModAlt);
        }
        if (windowID == 0 || !hover && controlID == 0 || text == string.Empty)
            throw new ArgumentException("需要非零的窗口及控件 ID；文本不能为空。");
        if (text is not null && text.Any(char.IsSurrogate)) throw new ArgumentException("当前 ImGui 输入仅接受基本多文种平面的字符。");
        Vector2? end = drag ? new Vector2(input.Integer("end_x", int.MinValue, -10000, 10000), input.Integer("end_y", int.MinValue, -10000, 10000)) : null;
        var hold = drag || tool == "hold_window_key" ? input.Integer("hold_ms", 300, 50, 2000) : 0;
        var wheel = action == "wheel_up" ? 1f : action == "wheel_down" ? -1f : 0f;
        int? button = drag ? 0 : action switch { "click" => 0, "right_click" => 1, "middle_click" => 2, _ => null };
        return await windowInput.SubmitAsync(new(windowID, controlID, text, button, wheel, key,
            [.. modifiers], point, end, hold, isAllowed, token)).ConfigureAwait(false);
    }

    private async Task<WindowFrameObserver.Result> ObserveWindowAsync(bool wait, ToolArguments input, CancellationToken token)
    {
        string? name = null;
        uint? id = null;
        if (input.Has("window_name"))
        {
            name = input.Element("window_name").GetString();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 512 || name.Contains('\0'))
                throw new ArgumentException("window_name 必须是完整窗口名，最多 512 个字符。");
        }
        if (input.Has("window_id"))
        {
            if (!input.Element("window_id").TryGetUInt32(out var value) || value == 0)
                throw new ArgumentException("window_id 必须是非零的无符号整数。");
            id = value;
        }
        if (name is not null && id.HasValue || wait && name is null && !id.HasValue)
            throw new ArgumentException("请选择一个窗口名称或 ID。");
        WindowFrameObserver.Condition? condition = wait ? input.Text("condition") switch
        {
            "visible" => WindowFrameObserver.Condition.Visible,
            "collapsed" => WindowFrameObserver.Condition.Collapsed,
            "not_visible" => WindowFrameObserver.Condition.NotVisible,
            _ => throw new ArgumentException("condition 必须是 visible、collapsed 或 not_visible。"),
        } : null;
        var timeoutMs = wait ? input.Integer("timeout_ms", 2000, 250, 5000) : 2000;
        return await windowFrames.ReadAsync(new(name, id), condition, timeoutMs, token).ConfigureAwait(false);
    }

    private static unsafe WindowFrameObserver.Frame? ReadWindowFrame(WindowFrameObserver.Selector[] selectors)
    {
        var context = ImGui.GetCurrentContext();
        if (context.IsNull || !context.WithinFrameScope) return null;
        var frame = context.FrameCount;
        var windows = context.Windows;
        var list = selectors.Any(selector => !selector.IsExact);
        var names = selectors.Where(selector => selector.Name is not null)
            .Select(selector => Encoding.UTF8.GetBytes(selector.Name!)).ToArray();
        var ids = selectors.Where(selector => selector.ID.HasValue).Select(selector => selector.ID.GetValueOrDefault()).ToArray();
        var output = new List<WindowFrameObserver.Window>();
        var listed = 0;
        var incomplete = windows.Size < 0 || windows.Size > WindowFrameObserver.SCAN_LIMIT ||
                         windows.Size > 0 && windows.Data == null;
        if (windows.Data != null)
        {
            for (var i = 0; i < Math.Min(windows.Size, WindowFrameObserver.SCAN_LIMIT); i++)
            {
                var window = windows[i];
                if (window.IsNull || window.Name == null)
                {
                    incomplete = true;
                    continue;
                }
                var id = window.ID;
                var include = list && ++listed <= WindowFrameObserver.LIST_LIMIT;
                var idMatch = Array.IndexOf(ids, id) >= 0;
                if (!include && !idMatch && names.Length == 0) continue;
                var length = 0;
                while (length < 2048 && window.Name[length] != 0) length++;
                if (length == 2048)
                {
                    incomplete = true;
                    continue;
                }
                var bytes = new ReadOnlySpan<byte>(window.Name, length);
                if (!include && !idMatch)
                {
                    var match = false;
                    foreach (var name in names)
                        if (bytes.SequenceEqual(name)) { match = true; break; }
                    if (!match) continue;
                }
                var active = window.Active && window.LastFrameActive == frame ||
                             window.WasActive && window.LastFrameActive == frame - 1;
                var focused = !context.NavWindow.IsNull && context.NavWindow.ID == id;
                uint? fieldID = !context.ActiveIdWindow.IsNull && context.ActiveIdWindow.ID == id &&
                    context.ActiveId != 0 && context.ActiveId == context.InputTextState.ID ? context.ActiveId : null;
                uint? hoveredID = !context.HoveredWindow.IsNull && context.HoveredWindow.ID == id && context.HoveredId != 0
                    ? context.HoveredId : null;
                uint? navID = focused && context.NavId != 0 ? context.NavId : null;
                var position = window.Pos;
                var size = window.Size;
                var rectangle = float.IsFinite(position.X) && float.IsFinite(position.Y) &&
                    float.IsFinite(size.X) && float.IsFinite(size.Y)
                    ? new WindowFrameObserver.Bounds(position.X, position.Y, size.X, size.Y) : null;
                var scroll = window.Scroll;
                var maximum = window.ScrollMax;
                var scrollState = float.IsFinite(scroll.X) && float.IsFinite(scroll.Y) &&
                    float.IsFinite(maximum.X) && float.IsFinite(maximum.Y)
                    ? new WindowFrameObserver.ScrollState(scroll.X, scroll.Y, maximum.X, maximum.Y) : null;
                output.Add(new(Encoding.UTF8.GetString(bytes), id, active, window.Hidden, window.Collapsed, focused, fieldID,
                    hoveredID, navID, rectangle, scrollState));
            }
        }
        return new(frame, [.. output], incomplete, listed > WindowFrameObserver.LIST_LIMIT);
    }

    internal static ImGuiKey ParseWindowKey(string name)
    {
        if (name.Length is < 1 or > 24 || char.IsAsciiDigit(name[0]) || !Enum.TryParse<ImGuiKey>(name, out var key) ||
            key.ToString() != name || key is < ImGuiKey.Tab or > ImGuiKey.KeypadEqual ||
            key is >= ImGuiKey.LeftCtrl and <= ImGuiKey.RightSuper)
            throw new ArgumentException("需要准确的 ImGui 键名，修饰键用独立参数传入。");
        return key;
    }

}
