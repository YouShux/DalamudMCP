using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using DalamudMCP.Shared;

namespace DalamudMCP.Plugin.Tools;

public sealed partial class GameTools
{
    private object QueueSequence(ToolArguments input, Func<bool> allowed)
    {
        var queuedAt = Environment.TickCount64;
        var target = input.Text("target");
        var game = target == "game";
        if (!game && target != "plugin") throw new ArgumentException("target 必须为 plugin 或 game。");
        var timeout = input.Integer("timeout_ms", 3000, 250, 5000);
        var actions = input.Element("actions");
        if (actions.ValueKind != JsonValueKind.Array || actions.GetArrayLength() is < 1 or > 32)
            throw new ArgumentException("actions 需要 1 至 32 步。");
        var windowID = 0u;
        uint territory = 0;
        GameWindowState.WindowIdentity? window = null;
        if (game)
        {
            if (input.Has("window_id")) throw new ArgumentException("game 使用 window_handle。");
            var hwnd = checked((nint)input.ObjectID("window_handle"));
            var width = input.Integer("client_width", 0, 1, short.MaxValue);
            var height = input.Integer("client_height", 0, 1, short.MaxValue);
            var dpi = input.Integer("dpi", 0, 1, 10000);
            territory = input.Element("expected_territory_id").GetUInt32();
            if (territory == 0 || hwnd <= 0) throw new ArgumentException("游戏窗口和预期区域必须为非零值。");
            window = GameWindowState.ReadInputWindow();
            if (window is not { Foreground: true } actual || actual.Handle != hwnd ||
                actual.Width != width || actual.Height != height || actual.Dpi != dpi)
                return new { status = "context_changed", reason = "game_window_changed" };
        }
        else
        {
            if (input.Has("window_handle") || input.Has("client_width") || input.Has("client_height") || input.Has("dpi") ||
                input.Has("expected_territory_id")) throw new ArgumentException("plugin 使用 window_id 和 ImGui 绘制坐标。");
            windowID = input.Element("window_id").GetUInt32();
            if (windowID == 0) throw new ArgumentException("window_id 必须大于零。");
        }

        ScreenshotInputFrame? frame = null;
        if (input.Has("screenshot_id"))
        {
            var screenshotID = RepositoryPackages.ParseID(input.Text("screenshot_id"));
            frame = GameScreenshot.GetInputFrame(screenshotID);
            if (frame is null || !frame.IsCurrent(ReadScreenshotViewport()) || game && window != frame.Viewport.Window)
                return new { status = "context_changed", reason = "screenshot_unavailable_or_changed", requestDispatched = false };
        }
        var steps = actions.EnumerateArray().Select(ParseStep).ToArray();
        if (frame is not null && !steps.Any(step => step.Point.HasValue))
            throw new ArgumentException("只有坐标输入可引用截图。");
        if (steps.Sum(step => step.Kind == "double_click" ? step.HoldMs * 2 + 35 : step.HoldMs) >= timeout)
            throw new ArgumentException("步骤等待时间必须小于总期限。");
        Func<CancellationToken, Task<bool>>? readback = null;
        if (input.Has("observe"))
        {
            var fields = input.Element("observe");
            var observation = new ToolArguments(fields);
            var kind = observation.Text("kind");
            var addon = observation.Text("addon_name");
            if (addon.Length > 128) throw new ArgumentException("addon_name 最多 128 个字符。");
            if (kind == "addon_visible")
            {
                ValidateFields(fields, "kind", "addon_name", "visible");
                var visible = observation.Element("visible").GetBoolean();
                readback = checkToken => services.Framework.RunOnTick(() => !lifetimeToken.IsCancellationRequested &&
                    services.GameGui.GetAddonByName(addon, 1) is { } view && (!view.IsNull && view.IsReady && view.IsVisible) == visible,
                    cancellationToken: checkToken);
            }
            else if (kind == "control_value")
            {
                ValidateFields(fields, "kind", "addon_name", "expected_addon_id", "node_id", "component_path", "value");
                var addonID = observation.Element("expected_addon_id").GetUInt32();
                if (addonID is 0 or > ushort.MaxValue) throw new ArgumentException("expected_addon_id 无效。");
                var selector = AddonNodeQuery.Parse(observation, false);
                if (!selector.NodeID.HasValue) throw new ArgumentException("控件回读需要 node_id。");
                var value = observation.Element("value");
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.String or JsonValueKind.Number))
                    throw new ArgumentException("控件回读值必须为文本、数字或布尔值。");
                readback = checkToken => services.Framework.RunOnTick(() => !lifetimeToken.IsCancellationRequested &&
                    addons.MatchInputReadback(addon, addonID, selector, value), cancellationToken: checkToken);
            }
            else throw new ArgumentException("未知的只读条件。");
        }

        if (!allowed()) return new { status = "permission_changed" };
        return windowInput.QueueSequence(new(windowID, window, steps, timeout, frame), allowed, checkToken =>
        {
            if (frame is not null && !frame.IsCurrent(ReadScreenshotViewport()))
                return Task.FromResult(false);
            return !game ? Task.FromResult(true) : services.Framework.RunOnTick(() =>
                !services.Framework.IsFrameworkUnloading && services.ClientState.IsLoggedIn && !services.ClientState.IsPvP &&
                services.ClientState.TerritoryType == territory && !services.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51),
                cancellationToken: checkToken);
        },
            readback, lifetimeToken, queuedAt);

        WindowInputScheduler.SequenceStep ParseStep(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) throw new ArgumentException("步骤必须为对象。");
            var step = new ToolArguments(element);
            var kind = step.Text("kind");
            var pointer = kind is "move" or "click" or "double_click" or "drag" or "scroll";
            if (!pointer && kind is not ("key" or "text" or "wait")) throw new ArgumentException("未知的输入步骤。");
            var fields = new List<string> { "kind" };
            if (pointer) fields.AddRange(["x", "y"]);
            if (kind is "click" or "double_click" or "drag") fields.Add("button");
            if (kind is "drag" or "wait" or "key") fields.Add("hold_ms");
            if (kind == "drag") fields.AddRange(["end_x", "end_y"]);
            if (kind == "scroll") fields.Add("wheel");
            if (kind == "key") fields.Add("key");
            if (kind == "text" && !game) fields.Add("text");
            if (!game && kind is not ("move" or "wait")) fields.Add("control_id");
            if (!game && kind == "key") fields.AddRange(["ctrl", "alt", "shift"]);
            ValidateFields(element, [.. fields]);
            if (game && kind == "text") throw new ArgumentException("游戏窗口不支持文字注入。");
            var control = !game && kind is not ("move" or "wait") ? step.Element("control_id").GetUInt32() : 0;
            if (!game && kind is not ("move" or "wait") && control == 0) throw new ArgumentException("插件步骤需要非零 control_id。");
            var modifiers = new List<ImGuiKey>();
            if (!game && kind == "key")
            {
                if (step.Boolean("ctrl")) modifiers.Add(ImGuiKey.ModCtrl);
                if (step.Boolean("alt")) modifiers.Add(ImGuiKey.ModAlt);
                if (step.Boolean("shift")) modifiers.Add(ImGuiKey.ModShift);
            }
            var key = kind == "key" && !game ? ParseWindowKey(step.Text("key")) : ImGuiKey.None;
            if (key != ImGuiKey.None && !WindowInputScheduler.SupportsKey(key)) throw new ArgumentException("插件输入路径不支持此按键。");
            var gameKey = kind == "key" && game ? GameWindowState.VirtualKey(step.Text("key")) ??
                throw new ArgumentException("不支持此游戏键名。") : 0;
            var text = kind == "text" ? AddonTools.PlainText(step.Element("text")) : null;
            if (text is not null && (text.Length == 0 || text.Any(char.IsSurrogate)))
                throw new ArgumentException("请输入非空的基本多文种平面文本。");
            int? button = kind is "click" or "double_click" or "drag" ? step.Integer("button", 0, 0, 2) : null;
            var wheel = kind == "scroll" ? step.Integer("wheel", 0, -10, 10) : 0;
            if (kind == "scroll" && wheel == 0) throw new ArgumentException("wheel 不能为零。");
            var hold = kind switch
            {
                "drag" => step.Integer("hold_ms", 300, 50, 2000), "key" => step.Integer("hold_ms", 60, 25, 2000),
                "wait" => step.Integer("hold_ms", 100, 1, 2000), "click" or "double_click" when game => 35, _ => 0,
            };
            return new(kind, control, text, button, wheel, key, gameKey, [.. modifiers],
                pointer ? Point("x", "y") : null, kind == "drag" ? Point("end_x", "end_y") : null, hold);

            Vector2 Point(string x, string y) => frame is { } source
                ? source.Map(step.Element(x).GetInt32(), step.Element(y).GetInt32(), game)
                : new(step.Integer(x, int.MinValue, game ? 0 : -10000, window?.Width - 1 ?? 10000),
                    step.Integer(y, int.MinValue, game ? 0 : -10000, window?.Height - 1 ?? 10000));
        }
    }

    private static void ValidateFields(JsonElement input, params string[] fields)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("参数必须为对象。");
        var allowed = fields.Select(ToolArguments.Normalize).ToHashSet(StringComparer.Ordinal);
        foreach (var item in input.EnumerateObject())
            if (!allowed.Remove(ToolArguments.Normalize(item.Name))) throw new ArgumentException($"参数未知或重复：{item.Name}。");
    }

    private static IEnumerable<ToolDefinition> InputSequenceTools()
    {
        var text = new { type = "string" };
        var id = new { type = "integer", minimum = 1, maximum = uint.MaxValue };
        var flag = new { type = "boolean" };
        var position = Range(-10000, short.MaxValue);
        var sequence = new Dictionary<string, object> { ["sequence_id"] = new { type = "string", format = "uuid" } };
        yield return Define(WindowInputScheduler.QUEUE_SEQUENCE_TOOL, "排队执行输入序列", "向已核对的前台游戏窗口或插件控件派发有界输入。人工输入会停止剩余步骤。", new()
        {
            ["target"] = new { type = "string", @enum = new[] { "plugin", "game" } }, ["window_id"] = id, ["window_handle"] = text,
            ["screenshot_id"] = new { type = "string", format = "uuid", description = "截图返回的 inputFrameID；坐标使用该图像的像素。" },
            ["client_width"] = Range(1, short.MaxValue), ["client_height"] = Range(1, short.MaxValue), ["dpi"] = Range(1, 10000),
            ["expected_territory_id"] = id, ["timeout_ms"] = Range(250, 5000),
            ["actions"] = new
            {
                type = "array", minItems = 1, maxItems = 32, items = new
                {
                    type = "object", additionalProperties = false, required = new[] { "kind" },
                    properties = new Dictionary<string, object>
                    {
                        ["kind"] = new { type = "string", @enum = new[] { "move", "click", "double_click", "drag", "scroll", "key", "text", "wait" } },
                        ["control_id"] = id, ["x"] = position, ["y"] = position, ["end_x"] = position, ["end_y"] = position,
                        ["button"] = Range(0, 2), ["wheel"] = Range(-10, 10), ["key"] = text, ["text"] = text,
                        ["hold_ms"] = Range(1, 2000), ["ctrl"] = flag, ["shift"] = flag, ["alt"] = flag,
                    },
                },
            },
            ["observe"] = new
            {
                type = "object", additionalProperties = false, required = new[] { "kind", "addon_name" },
                properties = new Dictionary<string, object>
                {
                    ["kind"] = new { type = "string", @enum = new[] { "addon_visible", "control_value" } }, ["addon_name"] = text,
                    ["visible"] = flag, ["expected_addon_id"] = Range(1, ushort.MaxValue), ["node_id"] = id,
                    ["component_path"] = new { type = "array", maxItems = 8, items = Range(0, int.MaxValue) }, ["value"] = new { },
                },
            },
        }, ["target", "actions"], ToolAccess.Action);
        yield return Define(WindowInputScheduler.READ_SEQUENCE_TOOL, "查询输入序列状态", "查询派发步骤、业务回读和输入释放状态。", sequence, ["sequence_id"]);
        yield return Define(WindowInputScheduler.CANCEL_SEQUENCE_TOOL, "取消剩余输入步骤", "停止序列并释放工具持有的输入。", sequence, ["sequence_id"], ToolAccess.Action);
    }
}
