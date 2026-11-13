using System.Diagnostics;

namespace DalamudMCP.Plugin.Tools;

internal sealed class WindowFrameObserver : IDisposable
{
    internal const string SNAPSHOT_TOOL = "get_window_snapshot";
    internal const string WAIT_TOOL = "wait_for_window_visibility";
    internal const int LIST_LIMIT = 64;
    internal const int SCAN_LIMIT = 4096;

    private readonly object sync = new();
    private readonly List<Request> pending = [];
    private bool disposed;

    internal async Task<Result> ReadAsync(Selector selector, Condition? condition, int timeoutMs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        var request = new Request(selector, condition, deadline.Token);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (pending.Count(item => item.Condition.HasValue == condition.HasValue) >= (condition.HasValue ? 2 : 1))
                return new("busy");
            pending.Add(request);
        }

        try
        {
            var result = await request.Completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            return result with { ElapsedMs = clock.ElapsedMilliseconds };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            lock (sync)
                return request.Last is { } sample
                    ? sample with { Status = "timeout", SampleStatus = sample.Status, ElapsedMs = clock.ElapsedMilliseconds }
                    : new("no_frame", ElapsedMs: clock.ElapsedMilliseconds);
        }
        finally
        {
            lock (sync) pending.Remove(request);
        }
    }

    internal void Capture(Func<Selector[], Frame?> capture)
    {
        Request[] requests;
        lock (sync)
        {
            if (pending.Count == 0) return;
            requests = pending.Where(item => !item.Token.IsCancellationRequested && !item.Completion.Task.IsCompleted).ToArray();
        }
        if (requests.Length == 0) return;
        var frame = capture(requests.Select(item => item.Selector).ToArray());
        lock (sync)
        {
            if (disposed) return;
            foreach (var request in requests)
            {
                if (request.Token.IsCancellationRequested || request.Completion.Task.IsCompleted) continue;
                if (frame is not null && request.Last?.Frame == frame.Number) continue;
                var previous = request.Last;
                var sample = Inspect(frame, request.Selector);
                request.Last = sample;
                if (request.Condition is not { } condition || sample.Status == "ambiguous")
                {
                    request.Completion.TrySetResult(sample);
                    continue;
                }
                if (sample.Status != "ok" || sample.Windows is not [var window] || !Matches(window, condition))
                {
                    request.Streak = 0;
                    continue;
                }
                request.Streak = sample.Frame == previous?.Frame + 1 &&
                    previous?.Windows is [var lastWindow] && lastWindow.ID == window.ID ? request.Streak + 1 : 1;
                if (request.Streak >= 2)
                    request.Completion.TrySetResult(sample with { Status = "observed" });
            }
        }
    }

    private static Result Inspect(Frame? frame, Selector selector)
    {
        if (frame is null) return new("context_unavailable");
        if (!selector.IsExact)
            return new("ok", frame.Number, frame.Windows.Take(LIST_LIMIT).ToArray(), frame.ListTruncated || frame.ScanIncomplete);

        var matches = frame.Windows.Where(selector.Matches).Take(2).ToArray();
        if (matches.Length > 1) return new("ambiguous", frame.Number, Truncated: frame.ScanIncomplete);
        if (frame.ScanIncomplete) return new("scan_incomplete", frame.Number, Truncated: true);
        return new(matches.Length == 0 ? "not_found" : "ok", frame.Number, matches);
    }

    private static bool Matches(Window window, Condition condition) => condition switch
    {
        Condition.Visible => window.Visible,
        Condition.Collapsed => window.Active && !window.Hidden && window.Collapsed,
        Condition.NotVisible => !window.Visible,
        _ => false,
    };

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            foreach (var request in pending) request.Completion.TrySetCanceled();
            pending.Clear();
        }
    }

    internal enum Condition { Visible, Collapsed, NotVisible }
    internal readonly record struct Selector(string? Name = null, uint? ID = null)
    {
        public bool IsExact => Name is not null || ID.HasValue;
        public bool Matches(Window window) => Name is not null ? Name == window.Name : ID == window.ID;
    }
    internal sealed record Window(string Name, uint ID, bool Active, bool Hidden, bool Collapsed,
        bool Focused = false, uint? ActiveTextID = null, uint? HoveredControlID = null, uint? NavigationControlID = null,
        Bounds? Rectangle = null, ScrollState? Scroll = null)
    {
        public bool Visible => Active && !Hidden && !Collapsed;
    }
    internal sealed record Bounds(float X, float Y, float Width, float Height);
    internal sealed record ScrollState(float X, float Y, float MaxX, float MaxY);
    internal sealed record Frame(int Number, Window[] Windows, bool ScanIncomplete = false, bool ListTruncated = false);
    internal sealed record Result(string Status, int? Frame = null, Window[]? Windows = null, bool Truncated = false,
        string? SampleStatus = null, long ElapsedMs = 0);

    private sealed class Request(Selector selector, Condition? condition, CancellationToken token)
    {
        public Selector Selector { get; } = selector;
        public Condition? Condition { get; } = condition;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<Result> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Result? Last { get; set; }
        public int Streak { get; set; }
    }
}
