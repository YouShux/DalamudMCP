using System.Numerics;
using Dalamud.Game.ClientState.Conditions;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameState
{
    internal const string ROUTE_TOOL = "route_to_coordinate";
    internal const string NAVIGATION_TOOL = "inspect_navigation_path";
    internal const string STOP_TOOL = "stop_navigation_path";
    internal const string FOCUS_TOOL = "set_focus_actor";

    public object SetFocus(ToolArguments input, Func<bool> isAllowed)
    {
        if (!services.ClientState.IsLoggedIn || services.ObjectTable.LocalPlayer is null)
            return new { status = "unavailable", reason = "player_not_ready" };
        var wanted = input.ObjectID("game_object_id");
        if (wanted == 0) throw new ArgumentException("game_object_id 必须为当前区域的对象。");
        var before = services.TargetManager.FocusTarget?.GameObjectId ?? 0;
        if (before != input.ObjectID("expected_focus_id")) return new { status = "focus_changed", requestDispatched = false };
        if (before == wanted) return new { status = "already_matched", requestDispatched = false, resultObserved = true };
        var actor = FindObject(wanted);
        if (!actor.IsTargetable || actor.Address == 0)
            return new { status = "target_unavailable", requestDispatched = false };
        if (!isAllowed()) throw new InvalidOperationException("焦点目标权限已关闭。");
        services.TargetManager.FocusTarget = actor;
        var after = services.TargetManager.FocusTarget?.GameObjectId;
        return new { status = after == wanted ? "observed" : "not_observed", requestDispatched = true,
            resultObserved = after == wanted, focusID = after is { } actual ? $"0x{actual:X}" : null };
    }

    public object Navigation(string name, ToolArguments input, Func<bool> isAllowed)
    {
        if (!services.ClientState.IsLoggedIn || services.ObjectTable.LocalPlayer is not { } player)
            return new { status = "unavailable", reason = "player_not_ready" };
        var ipc = services.PluginInterface;
        var running = ipc.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        var pending = ipc.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        var waypoints = ipc.GetIpcSubscriber<List<Vector3>>("vnavmesh.Path.ListWaypoints");
        if (!running.HasFunction || !pending.HasFunction || !waypoints.HasFunction)
            return new { status = "unavailable", reason = "navigation_ipc" };

        var territoryID = services.ClientState.TerritoryType;
        var externalCalculation = pending.InvokeFunc();
        var owned = navigationRequest is { } request && request.TerritoryID == territoryID &&
            services.PlayerState.IsLoaded && request.Character == services.PlayerState.ContentId ? request : null;
        var calculating = externalCalculation || owned is { Calculation.IsCompleted: false } || owned?.Status == "calculating";
        var active = running.InvokeFunc();
        var points = active ? waypoints.InvokeFunc() : null;
        var endpoint = points is { Count: > 0 } && FinitePoint(points[^1]) ? (Vector3?)points[^1] : null;
        if (name == NAVIGATION_TOOL)
            return new { status = "ok", territoryID, calculating, active, waypointCount = points?.Count ?? 0,
                endpoint = endpoint is { } value ? Position(value) : null,
                requestID = owned?.ID, requestStatus = owned?.Status, movementDispatchMayHaveStarted = owned?.MoveDispatchMayHaveStarted,
                maximumRange = Math.Clamp(configuration.NavigationRange, 250, 2000) };

        if (input.Element("expected_territory_id").GetUInt32() != territoryID)
            return new { status = "territory_changed", requestDispatched = false };
        var position = new Vector3((float)input.Number("x", double.NaN, -10000, 10000),
            (float)input.Number("y", double.NaN, -10000, 10000),
            (float)input.Number("z", double.NaN, -10000, 10000));
        if (name == STOP_TOOL)
        {
            var requested = input.Has("request_id") ? RepositoryPackages.ParseID(input.Text("request_id")) : (Guid?)null;
            if (calculating)
            {
                if (owned is null || owned.Status is not ("calculating" or "cancel_requested"))
                    return new { status = "cancel_unsupported", requestDispatched = false, resultObserved = false };
                if (requested != owned.ID || Vector3.DistanceSquared(owned.Destination, position) > 36f)
                    return new { status = "context_changed", reason = "request_identity_mismatch", requestDispatched = false, resultObserved = false };
                if (!isAllowed()) return new { status = "permission_changed", requestDispatched = false, resultObserved = false };
                if (!owned.StopRequested)
                {
                    owned.StopRequested = true;
                    owned.Cancellation.Cancel();
                    owned.Status = "cancel_requested";
                }
                return new { status = "cancel_requested", requestID = owned.ID, requestDispatched = true, resultObserved = false };
            }
            if (requested.HasValue && owned is { StopRequested: true } && requested == owned.ID && !owned.MoveDispatchMayHaveStarted)
                return new { status = owned.Status, requestID = owned.ID, requestDispatched = false, resultObserved = owned.Calculation.IsCompleted };
            if (requested.HasValue && (owned is null || requested != owned.ID || !owned.MoveDispatchMayHaveStarted))
                return new { status = "context_changed", requestDispatched = false, resultObserved = false };
            if (!active) return new { status = "already_stopped", requestDispatched = false, resultObserved = true };
            if (endpoint is not { } end || Vector3.DistanceSquared(end, position) > 0.01f)
                return new { status = "path_changed", requestDispatched = false };
            var stop = ipc.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
            if (!stop.HasAction) return new { status = "unavailable", reason = "stop_ipc" };
            if (!isAllowed()) throw new InvalidOperationException("导航权限已关闭。");
            stop.InvokeAction();
            var stopped = !running.InvokeFunc() && !pending.InvokeFunc();
            return new { status = stopped ? "observed" : "not_observed", requestDispatched = true, resultObserved = stopped };
        }

        if (calculating) return new { status = "path_calculating", requestDispatched = false };
        if (active) return new { status = "path_in_use", requestDispatched = false };
        var maxDistance = Math.Clamp(configuration.NavigationRange, 250, 2000);
        if (!services.PlayerState.IsLoaded || services.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51) ||
            !FinitePoint(player.Position) || Vector3.DistanceSquared(player.Position, position) > (float)maxDistance * maxDistance)
            return new { status = "destination_unavailable", requestDispatched = false };

        var ready = ipc.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        var route = ipc.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        var cancellable = ipc.GetIpcSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>>("vnavmesh.Nav.PathfindCancelable");
        var move = ipc.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
        var canCancel = cancellable.HasFunction && move.HasAction;
        if (!ready.HasFunction || (!route.HasFunction && !canCancel) || !ready.InvokeFunc())
            return new { status = "unavailable", reason = "navmesh_not_ready" };
        var fly = input.Boolean("allow_flight");
        Vector3? projected;
        if (fly)
        {
            var nearest = ipc.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
            projected = nearest.HasFunction ? nearest.InvokeFunc(position, 4f, 6f) : null;
        }
        else
        {
            var floor = ipc.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
            projected = floor.HasFunction ? floor.InvokeFunc(position, false, 4f) : null;
        }
        if (projected is not { } destination || !FinitePoint(destination) || Vector3.DistanceSquared(destination, position) > 36f)
            return new { status = "no_nearby_navmesh", requestDispatched = false };
        if (!isAllowed()) throw new InvalidOperationException("导航权限已关闭。");
        if (canCancel)
            return StartOwnedNavigation(player.Position, destination, fly, territoryID, isAllowed,
                cancellation => cancellable.InvokeFunc(player.Position, destination, fly, cancellation));
        var accepted = route.InvokeFunc(destination, fly);
        return new { status = accepted ? "requested" : "not_accepted", requestDispatched = true,
            requestAccepted = accepted, resultObserved = false, destination = Position(destination), territoryID, cancelSupported = false };
    }

    private static bool FinitePoint(Vector3 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
}
