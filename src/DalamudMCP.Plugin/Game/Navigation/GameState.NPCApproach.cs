using System.Numerics;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameState
{
    internal sealed record NPCApproach(Guid RequestID);

    internal NPCApproach? StartNPCApproach(Vector3 from, Vector3 target, uint territoryID, Func<bool> allowed)
    {
        if (!FinitePoint(from) || !FinitePoint(target) || navigationRequest is { Calculation.IsCompleted: false }) return null;
        var ipc = services.PluginInterface;
        var ready = ipc.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        var running = ipc.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        var pending = ipc.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        var calculate = ipc.GetIpcSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>>("vnavmesh.Nav.PathfindCancelable");
        var move = ipc.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
        var nearest = ipc.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
        var stop = ipc.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        var points = ipc.GetIpcSubscriber<List<Vector3>>("vnavmesh.Path.ListWaypoints");
        if (!ready.HasFunction || !running.HasFunction || !pending.HasFunction || !calculate.HasFunction || !move.HasAction ||
            !nearest.HasFunction || !stop.HasAction || !points.HasFunction || !ready.InvokeFunc() || running.InvokeFunc() ||
            pending.InvokeFunc() || !allowed()) return null;
        var direction = new Vector3(from.X - target.X, 0, from.Z - target.Z);
        if (direction.LengthSquared() < .01f) return null;
        var near = target + Vector3.Normalize(direction) * 2.5f;
        if (nearest.InvokeFunc(near, 2f, 2f) is not { } destination || !FinitePoint(destination) ||
            Vector3.DistanceSquared(destination, target) > 14.44f) return null;
        StartOwnedNavigation(from, destination, false, territoryID, allowed,
            cancellation => calculate.InvokeFunc(from, destination, false, cancellation), 1f);
        return navigationRequest is { } request ? new(request.ID) : null;
    }

    internal string PollNPCApproach(NPCApproach approach, bool inRange)
    {
        if (navigationRequest is not { } request || request.ID != approach.RequestID || !request.Allowed()) return "context_changed";
        if (inRange) return StopNPCApproach(approach) ? "in_range" : "navigation_changed";
        if (request.Status == "calculating") return "approaching";
        if (request.Status != "movement_requested") return request.Status;
        var running = services.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        return running.HasFunction && running.InvokeFunc() && NPCApproachOwnsPath(request) ? "approaching" : "out_of_range";
    }

    internal bool StopNPCApproach(NPCApproach approach)
    {
        if (navigationRequest is not { } request || request.ID != approach.RequestID) return false;
        var ipc = services.PluginInterface;
        var running = ipc.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        var pending = ipc.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        if (!running.HasFunction || !pending.HasFunction || pending.InvokeFunc()) return false;
        if (request.Status == "calculating")
        {
            request.StopRequested = true;
            request.Status = "cancel_requested";
            request.Cancellation.Cancel();
        }
        if (!running.InvokeFunc()) return true;
        var stop = ipc.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        if (!stop.HasAction || !NPCApproachOwnsPath(request) || request.Character != services.PlayerState.ContentId ||
            request.TerritoryID != services.ClientState.TerritoryType) return false;
        stop.InvokeAction();
        return !running.InvokeFunc();
    }

    private bool NPCApproachOwnsPath(NavigationRequest request)
    {
        var points = services.PluginInterface.GetIpcSubscriber<List<Vector3>>("vnavmesh.Path.ListWaypoints");
        return request.MovementEndpoint is { } endpoint && points.HasFunction &&
            points.InvokeFunc() is { Count: > 0 } waypoints && Vector3.DistanceSquared(waypoints[^1], endpoint) <= .01f;
    }
}
