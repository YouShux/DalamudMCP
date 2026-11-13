using System.Diagnostics;

namespace DalamudMCP.Plugin.Tools;

public sealed partial class GameTools
{
    private AddonFrameSample? addonSampling;

    private async Task<object> SampleAddonNodeAsync(ToolArguments input, CancellationToken token)
    {
        var request = new AddonFrameSample(input, input.Text("addon_name"), input.Integer("frames", 12, 2, 60),
            input.Integer("interval_frames", 1, 1, 10), input.Integer("timeout_ms", 3000, 250, 5000), token);
        if (Interlocked.CompareExchange(ref addonSampling, request, null) is not null) return new { status = "busy" };
        try
        {
            return await request.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(request.TimeoutMs), token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (request.Sync)
                return request.Completion.Task.IsCompletedSuccessfully ? request.Completion.Task.Result : request.Result("timeout");
        }
        finally { Interlocked.CompareExchange(ref addonSampling, null, request); }
    }

    private void ObserveAddonNodeFrame()
    {
        if (Volatile.Read(ref addonSampling) is not { } request || request.Token.IsCancellationRequested) return;
        lock (request.Sync)
        {
            if (request.Completion.Task.IsCompleted || request.Token.IsCancellationRequested) return;
            if (request.Clock.ElapsedMilliseconds >= request.TimeoutMs)
            {
                request.Completion.TrySetResult(request.Result("timeout"));
                return;
            }

            var frame = request.NextFrame++;
            if (frame % request.Interval != 0) return;
            try
            {
                var snapshot = addons.NodeSnapshot(request.Name, request.Input, out var identity);
                if (identity == default || request.Identity != default && identity != request.Identity)
                {
                    var status = identity != default ? "target_changed" : request.Samples.Count == 0 ? "target_unavailable" : "target_lost";
                    request.Completion.TrySetResult(request.Result(status, identity == default ? snapshot : null));
                    return;
                }
                request.Identity = identity;
                request.Samples.Add(new { frameOffset = frame, elapsedMs = request.Clock.ElapsedMilliseconds, snapshot });
                if (request.Samples.Count == request.Count) request.Completion.TrySetResult(request.Result("complete"));
            }
            catch (Exception error) { request.Completion.TrySetException(error); }
        }
    }

    private sealed class AddonFrameSample(ToolArguments input, string name, int count, int interval, int timeoutMs, CancellationToken token)
    {
        public readonly ToolArguments Input = input;
        public readonly string Name = name;
        public readonly int Count = count;
        public readonly int Interval = interval;
        public readonly int TimeoutMs = timeoutMs;
        public readonly CancellationToken Token = token;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public readonly TaskCompletionSource<object> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly object Sync = new();
        public readonly List<object> Samples = new(count);
        public (nint Window, nint Node) Identity;
        public int NextFrame;

        public object Result(string status, object? terminal = null) =>
            new { status, requestedFrames = Count, intervalFrames = Interval, samples = Samples.ToArray(), terminal };
    }
}
