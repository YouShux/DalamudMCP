using System.Numerics;
using Dalamud.Game.ClientState.Conditions;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameState
{
    private NavigationRequest? navigationRequest;

    private void TickNavigation()
    {
        if (navigationRequest is not { } request || request.Status != "calculating") return;
        var position = services.ObjectTable.LocalPlayer?.Position;
        var contextMatches = position is { } from && FinitePoint(from) && Vector3.DistanceSquared(from, request.Start) <= 100 &&
            services.ClientState.IsLoggedIn && services.PlayerState.IsLoaded && services.PlayerState.ContentId == request.Character &&
            services.ClientState.TerritoryType == request.TerritoryID &&
            !services.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51);
        if (!contextMatches || !request.Allowed() || request.Cancellation.IsCancellationRequested)
        {
            var status = request.StopRequested ? "cancel_requested" : !contextMatches ? "context_changed" :
                !request.Allowed() ? "permission_changed" : "timeout";
            if (!request.Cancellation.IsCancellationRequested) request.Cancellation.Cancel();
            request.Status = status;
            return;
        }
        if (!request.Calculation.IsCompleted) return;
        if (!request.Calculation.IsCompletedSuccessfully)
        {
            request.Status = request.Calculation.IsCanceled ? "cancelled" : "pathfinding_failed";
            _ = request.Calculation.Exception;
            return;
        }
        var route = request.Calculation.Result;
        var ipc = services.PluginInterface;
        var running = ipc.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        var pending = ipc.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        var move = ipc.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
        if (!running.HasFunction || !pending.HasFunction || !move.HasAction || running.InvokeFunc() || pending.InvokeFunc())
        { request.Status = "path_in_use"; return; }
        if (route is null || route.Count is < 1 or > 20000 || route.Any(point => !FinitePoint(point)) ||
            Vector3.DistanceSquared(route[^1], request.Destination) > request.EndpointToleranceSquared)
        { request.Status = "destination_unavailable"; return; }
        if (!request.Allowed() || request.Cancellation.IsCancellationRequested)
        { request.Status = "cancelled"; return; }
        request.MovementEndpoint = route[^1];
        request.MoveDispatchMayHaveStarted = true;
        move.InvokeAction(route, request.Fly);
        request.Status = "movement_requested";
    }

    internal void MaintainNavigation()
    {
        try
        {
            TickNavigation();
            if (navigationRequest is { Status: "cancel_requested", Calculation.IsCompleted: true } stopped)
                stopped.Status = stopped.Calculation.IsCanceled ? "cancelled" : "cancelled_before_movement";
        }
        catch (Exception error)
        {
            if (navigationRequest is { } request) request.Status = "navigation_error";
            services.Log.Debug(error, "导航请求未能继续。");
        }
    }

    private object StartOwnedNavigation(Vector3 start, Vector3 destination, bool fly, uint territoryID, Func<bool> allowed,
        Func<CancellationToken, Task<List<Vector3>>> calculate, float endpointToleranceSquared = 36f)
    {
        if (navigationRequest is { Calculation.IsCompleted: false }) return new { status = "busy", requestDispatched = false };
        navigationRequest?.Cancellation.Dispose();
        navigationRequest = null;
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var calculation = calculate(cancellation.Token);
            navigationRequest = new(Guid.NewGuid(), territoryID, services.PlayerState.ContentId, start, destination, fly, allowed, cancellation, calculation, endpointToleranceSquared);
            _ = calculation.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return new { status = "requested", requestID = navigationRequest.ID, requestDispatched = true, requestAccepted = true,
                resultObserved = false, destination = Position(destination), territoryID, cancelSupported = true };
        }
        catch
        {
            cancellation.Dispose();
            throw;
        }
    }

    private void DisposeNavigation()
    {
        if (navigationRequest is not { } request) return;
        navigationRequest = null;
        request.Cancellation.Cancel();
        if (request.Calculation.IsCompleted) request.Cancellation.Dispose();
        else _ = request.Calculation.ContinueWith(_ => request.Cancellation.Dispose(), TaskScheduler.Default);
    }

    private sealed record NavigationRequest(Guid ID, uint TerritoryID, ulong Character, Vector3 Start, Vector3 Destination, bool Fly,
        Func<bool> Allowed, CancellationTokenSource Cancellation, Task<List<Vector3>> Calculation, float EndpointToleranceSquared)
    {
        public string Status { get; set; } = "calculating";
        public bool StopRequested { get; set; }
        public bool MoveDispatchMayHaveStarted { get; set; }
        public Vector3? MovementEndpoint { get; set; }
    }
}
