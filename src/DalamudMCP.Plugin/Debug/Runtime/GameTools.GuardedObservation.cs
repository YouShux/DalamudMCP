using System.Diagnostics;
using System.Text.Json;
using DalamudMCP.Shared;

namespace DalamudMCP.Plugin.Tools;

public sealed partial class GameTools
{
    private int runtimeObservers;
    private int guardedEvent;

    private async Task<object> WatchRuntimeAsync(ToolArguments input, Func<bool> allowed, CancellationToken token)
    {
        var timeout = input.Integer("duration_ms", 2000, 100, 5000);
        var interval = input.Integer("interval_ms", 100, 50, 1000);
        if (Interlocked.Increment(ref runtimeObservers) > 2)
        {
            Interlocked.Decrement(ref runtimeObservers);
            return new { status = "busy" };
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var clock = Stopwatch.StartNew();
        var changes = new List<object>();
        JsonElement? previous = null;
        try
        {
            while (true)
            {
                var value = await services.Framework.RunOnTick(() =>
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!allowed()) throw new InvalidOperationException("运行时观察权限已关闭。");
                    return ToolJson.Element(runtime.ReadScalar(input));
                }, cancellationToken: deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                if (previous is null || !JsonElement.DeepEquals(previous.Value, value))
                {
                    changes.Add(new { elapsedMs = clock.ElapsedMilliseconds, value });
                    previous = value;
                }
                if (input.Has("expected_value") && JsonElement.DeepEquals(value, input.Element("expected_value")))
                    return new { status = "observed", changes, elapsedMs = clock.ElapsedMilliseconds };
                if (changes.Count >= 64) return new { status = "capacity_reached", changes, elapsedMs = clock.ElapsedMilliseconds };
                await Task.Delay(interval, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new { status = input.Has("expected_value") ? "timeout" : "complete", changes, elapsedMs = clock.ElapsedMilliseconds }; }
        finally { Interlocked.Decrement(ref runtimeObservers); }
    }

    private async Task<object> DispatchWhenReadyAsync(ToolArguments input, Func<bool> allowed, CancellationToken token)
    {
        if (input.Has("geometry")) return await DispatchForGeometryAsync(input, allowed, token).ConfigureAwait(false);
        if (input.Has("expected_territory_id") || input.Has("dry_run"))
            throw new ArgumentException("区域核验和预览参数用于几何触发。");
        var timeout = input.Integer("timeout_ms", 2000, 100, 5000);
        var minWidth = input.Integer("min_width", 0, 0, ushort.MaxValue);
        var maxWidth = input.Integer("max_width", ushort.MaxValue, minWidth, ushort.MaxValue);
        var minHeight = input.Integer("min_height", 0, 0, ushort.MaxValue);
        var maxHeight = input.Integer("max_height", ushort.MaxValue, minHeight, ushort.MaxValue);
        if (Interlocked.CompareExchange(ref guardedEvent, 1, 0) != 0) return new { status = "busy" };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var dispatched = false;
        (nint Window, nint Node) identity = default;
        try
        {
            while (true)
            {
                (bool Stop, object Value) result = await services.Framework.RunOnTick(() =>
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!allowed()) throw new InvalidOperationException("窗口操作权限已关闭。");
                    var snapshot = addons.NodeSnapshot(input.Text("addon_name"), input, out var current);
                    if (current == default) return (true, (object)new { status = "target_unavailable", snapshot });
                    if (identity != default && identity != current) return (true, (object)new { status = "target_changed" });
                    identity = current;
                    var json = ToolJson.Element(snapshot);
                    var node = json.GetProperty("node");
                    var size = json.GetProperty("size");
                    var width = size.GetProperty("width").GetInt32();
                    var height = size.GetProperty("height").GetInt32();
                    if (!node.GetProperty("visible").GetBoolean() || !node.GetProperty("enabled").GetBoolean() ||
                        width < minWidth || width > maxWidth || height < minHeight || height > maxHeight)
                        return (false, (object)new { status = "waiting" });
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!allowed()) throw new InvalidOperationException("窗口操作权限已关闭。");
                    dispatched = true;
                    var action = addons.Call("send_addon_event", input);
                    return (true, (object)new { status = "requested", action, resultObserved = false });
                }, delayTicks: 1, cancellationToken: deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                if (result.Stop) return result.Value;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new { status = "timeout", dispatchMayHaveStarted = dispatched, resultObserved = false }; }
        finally { Interlocked.Exchange(ref guardedEvent, 0); }
    }
}
