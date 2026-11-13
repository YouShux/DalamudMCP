using System.Text.Json;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DalamudMCP.Plugin.Tools;

public sealed partial class GameTools
{
    private async Task<object> DispatchForGeometryAsync(ToolArguments input, Func<bool> allowed, CancellationToken token)
    {
        var name = input.Text("addon_name");
        if (name is "SelectYesno" or "ContentsFinderConfirm") throw new ArgumentException("确认框须使用核对完整提示的按钮工具。");
        var id = input.Element("expected_addon_id").GetUInt16();
        var territory = input.Element("expected_territory_id").GetUInt32();
        var click = AddonNodeQuery.Parse(input, false) with { ComponentType = "Button" };
        if (input.Has("min_width") || input.Has("max_width") || input.Has("min_height") || input.Has("max_height"))
            throw new ArgumentException("尺寸条件与几何条件须分别使用。");
        if (id == 0 || territory == 0 || click.NodeID is null && click.Label is null)
            throw new ArgumentException("指定按钮及有效的窗口和区域 ID。");
        if (!Enum.TryParse<AtkEventType>(input.Text("event_type"), true, out var type) ||
            type is not (AtkEventType.ButtonClick or AtkEventType.ButtonPress))
            throw new ArgumentException("几何触发仅支持已注册的 ButtonClick 或 ButtonPress。");

        var condition = input.Element("geometry");
        var specification = new ToolArguments(condition);
        var kind = specification.Text("kind");
        var observed = ReadSelector(specification.Element("observed"));
        AddonNodeQuery? reference = null;
        double first, second, tolerance = 0;
        if (kind == "rotation")
        {
            ValidateFields(condition, "kind", "observed", "min", "max");
            first = specification.Number("min", double.NaN, -Math.PI, Math.PI);
            second = specification.Number("max", double.NaN, -Math.PI, Math.PI);
            if (!double.IsFinite(first) || !double.IsFinite(second) || first > second) throw new ArgumentException("角度区间无效。");
        }
        else if (kind is "align_x" or "align_y")
        {
            ValidateFields(condition, "kind", "observed", "reference", "observed_anchor", "reference_anchor", "tolerance_px");
            reference = ReadSelector(specification.Element("reference"));
            first = specification.Number("observed_anchor", .5, 0, 1);
            second = specification.Number("reference_anchor", .5, 0, 1);
            tolerance = specification.Number("tolerance_px", 2, 0, 100);
        }
        else throw new ArgumentException("geometry.kind 必须为 align_x、align_y 或 rotation。");
        var timeout = input.Integer("timeout_ms", 2000, 100, 5000);
        var preview = input.Boolean("dry_run");
        if (!GameDiagnostics.CurrentCnLayoutKnown) return Result("unsupported_sdk");
        if (Interlocked.CompareExchange(ref guardedEvent, 1, 0) != 0) return Result("busy");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var started = 0;
        var invalidated = 0;
        var registered = false;
        ((nint, nint), (nint, nint), (nint, nint)?) identities = default;
        long generation = -1;
        ulong character = 0;
        double? lastMeasurement = null;
        try
        {
            for (var frames = 0; frames < 600; frames++)
            {
                var (status, value) = await services.Framework.RunOnTick<(string, double?)>(() =>
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!allowed() || !services.ClientState.IsLoggedIn || !services.PlayerState.IsLoaded ||
                        services.PlayerState.ContentId == 0 || services.ClientState.TerritoryType != territory ||
                        services.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51) ||
                        Volatile.Read(ref invalidated) != 0) return ("context_changed", null);
                    if (!registered)
                    {
                        registered = true;
                        foreach (var evt in new[] { AddonEvent.PreSetup, AddonEvent.PreHide, AddonEvent.PreClose, AddonEvent.PreFinalize })
                            services.AddonLifecycle.RegisterListener(evt, name, OnRebuilt);
                        generation = state.TerritoryGeneration;
                        character = services.PlayerState.ContentId;
                    }
                    if (generation != state.TerritoryGeneration || character != services.PlayerState.ContentId)
                        return ("context_changed", null);
                    var button = addons.ReadNodeGeometry(name, id, click);
                    var sample = addons.ReadNodeGeometry(name, id, observed);
                    var marker = reference is null ? null : addons.ReadNodeGeometry(name, id, reference);
                    if (button is not { Enabled: true } ready || sample is not { } reading || reference is not null && marker is null)
                        return ("target_unavailable", null);
                    var key = (ready.Identity, reading.Identity, marker?.Identity);
                    if (identities != default && key != identities) return ("target_changed", null);
                    identities = key;
                    double? measured = kind == "rotation"
                        ? float.IsFinite(reading.Rotation) ? reading.Rotation : null
                        : reading.Anchor(kind == "align_x", first) - marker?.Anchor(kind == "align_x", second);
                    if (measured is null) return ("geometry_unavailable", null);
                    var matches = kind == "rotation" ? measured >= first && measured <= second : Math.Abs(measured.Value) <= tolerance;
                    if (!matches && !preview) return ("waiting", measured);
                    var result = addons.DispatchRegisteredButton(name, id, click, null, preview, () =>
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        return allowed() && Volatile.Read(ref invalidated) == 0 && Interlocked.CompareExchange(ref started, 1, 0) == 0;
                    }, type);
                    if (result == "requested") Volatile.Write(ref started, 3);
                    return (result == "ready" && !matches ? "condition_not_matched" : result, measured);
                }, delayTicks: 1, cancellationToken: deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                if (status != "waiting") return Result(status, status == "requested", value);
                lastMeasurement = value;
            }
            return Result("frame_limit", measured: lastMeasurement);
        }
        catch (OperationCanceledException)
        {
            var dispatch = Interlocked.CompareExchange(ref started, 2, 0);
            return Result(token.IsCancellationRequested ? "cancelled" : "timeout",
                dispatch == 3 ? true : dispatch == 1 ? null : false, lastMeasurement);
        }
        finally
        {
            try
            {
                if (registered) services.AddonLifecycle.UnregisterListener(OnRebuilt);
            }
            finally { Interlocked.Exchange(ref guardedEvent, 0); }
        }

        void OnRebuilt(AddonEvent _, AddonArgs __) => Interlocked.Exchange(ref invalidated, 1);

        object Result(string status, bool? dispatched = false, double? measured = null) =>
            new { status, kind, measured, requestDispatched = dispatched, resultObserved = false };
    }

    private static object GeometrySchema()
    {
        var selector = new { type = "object", additionalProperties = false, properties = new
            {
                node_id = new { type = "integer", minimum = 0, maximum = uint.MaxValue },
                component_path = new { type = "array", maxItems = 8, items = IntegerRange(0, int.MaxValue) },
                label = new { type = "string" }, component_type = new { type = "string" },
            } };
        return new { type = "object", additionalProperties = false, required = new[] { "kind", "observed" }, properties = new
            {
                kind = new { type = "string", @enum = new[] { "align_x", "align_y", "rotation" } }, observed = selector, reference = selector,
                min = new { type = "number", minimum = -Math.PI, maximum = Math.PI },
                max = new { type = "number", minimum = -Math.PI, maximum = Math.PI },
                observed_anchor = new { type = "number", minimum = 0, maximum = 1 },
                reference_anchor = new { type = "number", minimum = 0, maximum = 1 },
                tolerance_px = new { type = "number", minimum = 0, maximum = 100 },
            } };
    }

    private static AddonNodeQuery ReadSelector(JsonElement value)
    {
        ValidateFields(value, "node_id", "component_path", "label", "component_type");
        var query = AddonNodeQuery.Parse(new(value), false);
        if (query.NodeID is null && query.Label is null) throw new ArgumentException("几何节点需要 node_id 或 label。");
        return query;
    }
}
