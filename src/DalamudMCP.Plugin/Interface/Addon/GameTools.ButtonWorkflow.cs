using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using DalamudMCP.Shared;

namespace DalamudMCP.Plugin.Tools;

public sealed partial class GameTools
{
    private const string BUTTON_WORKFLOW_TOOL = "request_addon_button";
    private const string ARRIVAL_TOOL = "wait_game_arrival";
    private int arrivalWaiters;
    private int buttonWorkflowActive;

    private async Task<object> RequestAddonButtonAsync(ToolArguments input, Func<bool> allowed, CancellationToken token)
    {
        var source = input.Text("addon_name");
        var addonID = input.Integer("expected_addon_id", 0, 1, ushort.MaxValue);
        var sourceTerritory = input.Element("expected_territory_id").GetUInt32();
        var query = AddonNodeQuery.Parse(input, false) with { ComponentType = "Button" };
        if (!query.NodeID.HasValue && query.Label is null) throw new ArgumentException("请指定按钮节点 ID 或完整标签。");
        var prompt = input.Has("expected_prompt") ? input.Text("expected_prompt") : null;
        if (source is { Length: < 1 or > 64 } || prompt is { Length: < 1 or > 1024 }) throw new ArgumentException("窗口名或提示长度无效。");
        if ((source == "SelectYesno") != (prompt is not null)) throw new ArgumentException("SelectYesno 必须核对完整提示。");
        var outcome = input.Text("outcome");
        var targetName = input.Has("result_addon") ? input.Text("result_addon") : null;
        var targetText = input.Has("result_text") ? input.Text("result_text") : null;
        var destination = input.Has("destination") ? ParseArrival(input.Element("destination")) : null;
        if (sourceTerritory == 0 || targetName is { Length: < 1 or > 64 } || targetText is { Length: < 1 or > 256 } || (outcome switch
            {
                "addon_open" => targetName is null || targetName == source || destination is not null,
                "source_closed" => targetName is not null || targetText is not null || destination is not null,
                "arrival" => destination is null || targetName is not null || targetText is not null ||
                    destination.TerritoryID == sourceTerritory && destination.Position is null,
                _ => true,
            })) throw new ArgumentException("请选择一种明确的回读结果；同区域到达必须指定坐标。");
        var timeout = input.Integer("timeout_ms", 4000, 250, 20000);
        if (Interlocked.CompareExchange(ref buttonWorkflowActive, 1, 0) != 0)
            return new { status = "busy", requestDispatched = false, resultObserved = false };

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var current = deadline.Token;
        var started = 0;
        var sent = false;
        ulong character = 0;
        long generation = 0;
        Vector3 origin = default;
        ButtonObservation? previous = null;
        try
        {
            var status = await services.Framework.RunOnTick(() =>
            {
                if (!GameDiagnostics.CurrentCnLayoutKnown) return "unsupported_sdk";
                if (!SessionReady() || services.ClientState.TerritoryType != sourceTerritory ||
                    services.ObjectTable.LocalPlayer is not { } player) return "context_changed";
                character = services.PlayerState.ContentId;
                generation = state.TerritoryGeneration;
                origin = player.Position;
                if ((targetName is not null && addons.AddonVisible(targetName)) || ArrivalMatches(origin, sourceTerritory))
                    return "already_matched";
                return addons.DispatchRegisteredButton(source, (ushort)addonID, query, prompt, input.Boolean("dry_run"), () =>
                {
                    current.ThrowIfCancellationRequested();
                    return allowed() && Interlocked.CompareExchange(ref started, 1, 0) == 0;
                });
            }, cancellationToken: current).WaitAsync(current).ConfigureAwait(false);
            if (status != "requested") return Result(status);
            sent = true;
            while (true)
            {
                var sample = await services.Framework.RunOnTick(() =>
                {
                    current.ThrowIfCancellationRequested();
                    if (!services.ClientState.IsLoggedIn || services.PlayerState.IsLoaded && services.PlayerState.ContentId != character)
                        return new ButtonObservation("context_changed");
                    if (destination is null && (services.ClientState.TerritoryType != sourceTerritory || state.TerritoryGeneration != generation))
                        return new ButtonObservation("context_changed");
                    if (!SessionReady() || services.ObjectTable.LocalPlayer is not { } player) return new ButtonObservation("waiting");
                    var territory = services.ClientState.TerritoryType;
                    return outcome switch
                    {
                        "arrival" when ArrivalMatches(player.Position, territory) &&
                            (territory != sourceTerritory || Vector3.DistanceSquared(player.Position, origin) > 1) =>
                            new ButtonObservation("matched", TerritoryID: territory, Position: player.Position),
                        "source_closed" when !addons.AddonVisible(source) => new ButtonObservation("matched"),
                        "addon_open" when targetName is not null && addons.ReadNPCDialogue(targetName, targetText) is { Matches: true } surface =>
                            new ButtonObservation("matched", surface.AddonID, surface.Text, AddonAddress: surface.Address),
                        _ => new ButtonObservation("waiting"),
                    };
                }, delayTicks: 1, cancellationToken: current).WaitAsync(current).ConfigureAwait(false);
                if (sample.Status == "context_changed") return Result(sample.Status);
                if (sample.Status == "matched" && previous is { Status: "matched" } && previous.AddonID == sample.AddonID &&
                    previous.TerritoryID == sample.TerritoryID && previous.AddonAddress == sample.AddonAddress && previous.Text == sample.Text)
                    return Result("observed", sample);
                previous = sample;
                await Task.Delay(70, current).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Interlocked.CompareExchange(ref started, 2, 0);
            return Result(token.IsCancellationRequested ? "cancelled" : "timeout");
        }
        finally { Interlocked.Exchange(ref buttonWorkflowActive, 0); }

        bool SessionReady() => !services.Framework.IsFrameworkUnloading && services.ClientState.IsLoggedIn &&
            services.PlayerState.IsLoaded && services.PlayerState.ContentId != 0 &&
            !services.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51);

        bool ArrivalMatches(Vector3 position, uint territory) => destination?.Matches(position, territory) == true;

        object Result(string status, ButtonObservation? sample = null) => new
        {
            status, source, outcome, requestDispatched = sent ? true : Volatile.Read(ref started) == 1 ? null : (bool?)false,
            resultObserved = status == "observed", resultAddon = targetName, resultAddonID = sample?.AddonID,
            observedText = sample?.Text, territoryID = sample?.TerritoryID,
            position = sample?.Position is { } point ? new { point.X, point.Y, point.Z } : null,
        };
    }

    private async Task<object> WaitGameArrivalAsync(ToolArguments input, CancellationToken token)
    {
        var origin = ParseArrival(input.Element("origin"));
        var destination = ParseArrival(input.Element("destination"));
        var timeout = input.Integer("timeout_ms", 5000, 150, 20000);
        if (origin.Position is null || origin.TerritoryID == destination.TerritoryID && destination.Position is null)
            throw new ArgumentException("起点需提供坐标；同区域到达也需提供目的坐标。");
        if (origin.Position is { } point && destination.Matches(point, origin.TerritoryID))
            return new { status = "origin_already_matches", resultObserved = false };
        if (Interlocked.Increment(ref arrivalWaiters) > 2)
        {
            Interlocked.Decrement(ref arrivalWaiters);
            return new { status = "busy", resultObserved = false };
        }
        try
        {
            var end = Environment.TickCount64 + timeout;
            ulong character = 0;
            uint? territoryID = null;
            Vector3? position = null;
            var consecutive = 0;
            while (true)
            {
                var status = await services.Framework.RunOnTick(() =>
                {
                    if (!services.ClientState.IsLoggedIn) return "context_changed";
                    if (!services.PlayerState.IsLoaded || services.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51) ||
                        services.ObjectTable.LocalPlayer is not { } player)
                    {
                        consecutive = 0;
                        return "waiting";
                    }
                    var id = services.PlayerState.ContentId;
                    if (id == 0 || character != 0 && character != id) return "context_changed";
                    character = id;
                    territoryID = services.ClientState.TerritoryType;
                    position = player.Position;
                    if (territoryID != origin.TerritoryID && territoryID != destination.TerritoryID) return "context_changed";
                    consecutive = destination.Matches(player.Position, territoryID.Value) ? consecutive + 1 : 0;
                    return consecutive == 2 ? "observed" : "waiting";
                }, cancellationToken: token).WaitAsync(token).ConfigureAwait(false);
                var remaining = end - Environment.TickCount64;
                if (status != "waiting" || remaining <= 0)
                    return new { status = status == "waiting" ? "timeout" : status, resultObserved = status == "observed", territoryID,
                        position = position is { } last ? new { last.X, last.Y, last.Z } : null };
                await Task.Delay((int)Math.Min(80, remaining), token).ConfigureAwait(false);
            }
        }
        finally { Interlocked.Decrement(ref arrivalWaiters); }
    }

    private static Arrival ParseArrival(System.Text.Json.JsonElement value)
    {
        ValidateFields(value, "territory_id", "x", "y", "z", "radius");
        var input = new ToolArguments(value);
        var territory = input.Element("territory_id").GetUInt32();
        var hasCoordinates = input.Has("x") || input.Has("y") || input.Has("z");
        if (territory == 0 || hasCoordinates && !(input.Has("x") && input.Has("y") && input.Has("z")) ||
            !hasCoordinates && input.Has("radius")) throw new ArgumentException("到达条件需要有效区域及可选的完整坐标。");
        return new(territory, hasCoordinates ? new Vector3((float)input.Number("x", 0, -10000, 10000),
            (float)input.Number("y", 0, -10000, 10000), (float)input.Number("z", 0, -10000, 10000)) : null,
            (float)input.Number("radius", 3, 1, 15));
    }

    private sealed record Arrival(uint TerritoryID, Vector3? Position, float Radius)
    {
        internal bool Matches(Vector3 position, uint territory) => territory == TerritoryID &&
            float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z) &&
            (Position is not { } point || Vector3.DistanceSquared(position, point) <= Radius * Radius);
    }
    private sealed record ButtonObservation(string Status, ushort? AddonID = null, string? Text = null,
        uint? TerritoryID = null, Vector3? Position = null, nint AddonAddress = 0);

    private static object IntegerRange(int min, int max) => new { type = "integer", minimum = min, maximum = max };

    private static ToolDefinition ButtonWorkflowTool() => Define(BUTTON_WORKFLOW_TOOL, "单次按钮操作并核对结果",
        "核对当前窗口、按钮及其注册的原生事件，派发一次，再核对窗口变化或到达位置。确认框需给出完整提示。", new()
        {
            ["addon_name"] = new { type = "string", minLength = 1, maxLength = 64 },
            ["expected_addon_id"] = IntegerRange(1, ushort.MaxValue),
            ["expected_territory_id"] = new { type = "integer", minimum = 1, maximum = uint.MaxValue },
            ["node_id"] = new { type = "integer", minimum = 1, maximum = uint.MaxValue },
            ["component_path"] = new { type = "array", maxItems = AddonNodeQuery.MAX_COMPONENT_DEPTH, items = new { type = "integer", minimum = 1 } },
            ["label"] = new { type = "string", minLength = 1, maxLength = 256 },
            ["expected_prompt"] = new { type = "string", minLength = 1, maxLength = 1024 },
            ["outcome"] = new { type = "string", @enum = new[] { "addon_open", "source_closed", "arrival" } },
            ["result_addon"] = new { type = "string", minLength = 1, maxLength = 64 },
            ["result_text"] = new { type = "string", minLength = 1, maxLength = 256 },
            ["destination"] = ArrivalSchema(),
            ["timeout_ms"] = IntegerRange(250, 20000), ["dry_run"] = new { type = "boolean" },
        }, ["addon_name", "expected_addon_id", "expected_territory_id", "outcome"], ToolAccess.Action);

    private static ToolDefinition ArrivalTool() => Define(ARRIVAL_TOOL, "独立核对到达区域和位置",
        "根据移动前记录的起点与目标，等角色连续两次位于目标区域或指定坐标内。不会派发移动。", new()
        {
            ["origin"] = ArrivalSchema(), ["destination"] = ArrivalSchema(), ["timeout_ms"] = IntegerRange(150, 20000),
        }, ["origin", "destination"], ToolAccess.Read);

    private static object ArrivalSchema() => new { type = "object", required = new[] { "territory_id" }, additionalProperties = false, properties = new
        {
            territory_id = new { type = "integer", minimum = 1, maximum = uint.MaxValue },
            x = new { type = "number", minimum = -10000, maximum = 10000 }, y = new { type = "number", minimum = -10000, maximum = 10000 },
            z = new { type = "number", minimum = -10000, maximum = 10000 }, radius = new { type = "number", minimum = 1, maximum = 15 },
        } };
}
