using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class WindowInputScheduler
{
    internal const string QUEUE_SEQUENCE_TOOL = "queue_input_sequence";
    internal const string READ_SEQUENCE_TOOL = "get_input_sequence";
    internal const string CANCEL_SEQUENCE_TOOL = "cancel_input_sequence";
    private readonly Queue<InputSequence> sequences = new();
    private readonly Dictionary<Guid, InputSequence> recentSequences = new();
    private InputSequence? activeSequence;
    private bool legacyGameKey;
    private bool sequencePumpRunning;

    internal bool BeginLegacyGameKey()
    {
        lock (sync)
        {
            if (disposed || legacyGameKey || active is not null || queue.Count != 0 || activeSequence is not null || sequences.Count != 0) return false;
            return legacyGameKey = true;
        }
    }

    internal void EndLegacyGameKey()
    {
        lock (sync) legacyGameKey = false;
    }

    internal object QueueSequence(SequencePlan plan, Func<bool> allowed, Func<CancellationToken, Task<bool>> contextMatches, Func<CancellationToken, Task<bool>>? observe,
        CancellationToken lifetime, long queuedAt)
    {
        lock (sync)
        {
            if (disposed) return new { status = "unavailable" };
            if (sequences.Count >= 8) return new { status = "busy" };
            var job = new InputSequence(plan, allowed, contextMatches, observe, lifetime, queuedAt);
            recentSequences.Add(job.ID, job);
            sequences.Enqueue(job);
            if (!sequencePumpRunning)
            {
                sequencePumpRunning = true;
                _ = Task.Run(RunSequencesAsync);
            }
            return SequenceResult(job);
        }
    }

    internal object SequenceState(Guid id, bool cancel, Func<bool> allowed)
    {
        lock (sync)
        {
            if (!recentSequences.TryGetValue(id, out var job)) return new { status = "not_found" };
            if (cancel && job.Status is ("queued" or "running"))
            {
                if (!allowed()) return new { status = "permission_changed" };
                job.Cancellation.Cancel();
                if (job.Status == "queued")
                {
                    var count = sequences.Count;
                    while (count-- > 0)
                    {
                        var queued = sequences.Dequeue();
                        if (queued != job) sequences.Enqueue(queued);
                    }
                    FinishSequence(job, "cancelled", true);
                }
            }
            return SequenceResult(job);
        }
    }

    private async Task RunSequencesAsync()
    {
        while (true)
        {
            InputSequence? job = null;
            lock (sync)
            {
                while (sequences.TryPeek(out var pending))
                {
                    if (pending.Status != "queued") { sequences.Dequeue(); continue; }
                    if (pending.Token.IsCancellationRequested)
                    {
                        sequences.Dequeue();
                        FinishSequence(pending, pending.Cancellation.IsCancellationRequested ? "cancelled" : "timeout", true);
                        continue;
                    }
                    if (legacyGameKey || active is not null || queue.Count != 0) break;
                    job = sequences.Dequeue();
                    activeSequence = job;
                    job.Status = "running";
                    break;
                }
                if (disposed || job is null && sequences.Count == 0)
                {
                    sequencePumpRunning = false;
                    return;
                }
            }
            if (job is null) { await Task.Delay(20).ConfigureAwait(false); continue; }
            await RunSequenceAsync(job).ConfigureAwait(false);
        }
    }

    private async Task RunSequenceAsync(InputSequence job)
    {
        var status = "completed";
        var cleanup = true;
        var stamp = GameWindowState.PhysicalInputStamp();
        try
        {
            if (job.Plan.GameWindow is { } identity)
            {
                lock (sync)
                {
                    job.Token.ThrowIfCancellationRequested();
                    job.GameInput = new GameWindowState.InputSession(identity);
                }
            }
            for (var index = 0; index < job.Plan.Steps.Length; index++)
            {
                var step = job.Plan.Steps[index];
                if ((await CheckAsync().ConfigureAwait(false)) is { } rejected) { status = rejected; break; }
                if (step.Kind == "wait")
                {
                    if ((await Pause(step.HoldMs).ConfigureAwait(false)) is { } interrupted) { status = interrupted; break; }
                }
                else if (job.GameInput is { } game)
                {
                    var count = step.Kind == "double_click" ? 2 : 1;
                    for (var click = 0; click < count; click++)
                    {
                        if ((await CheckAsync().ConfigureAwait(false)) is { } interrupted) { status = interrupted; break; }
                        if (!game.Send(step.Kind, step.Point, step.Button ?? 0, (int)step.Wheel, step.GameKey, click == 1))
                        { status = "not_accepted"; break; }
                        MarkDispatched(index);
                        if (step.Kind is "click" or "double_click" or "drag" or "key")
                        {
                            var started = Environment.TickCount64;
                            try
                            {
                                if (step.End is { } end && step.Point is { } start)
                                {
                                    do
                                    {
                                        if ((await Pause(20).ConfigureAwait(false)) is { } aborted) { status = aborted; break; }
                                        var fraction = Math.Clamp((Environment.TickCount64 - started) / (float)step.HoldMs, 0, 1);
                                        if (!game.Send("move", Vector2.Lerp(start, end, fraction), step.Button ?? 0, 0, 0))
                                        { status = "not_accepted"; break; }
                                    } while (Environment.TickCount64 - started < step.HoldMs);
                                }
                                else if ((await Pause(step.HoldMs).ConfigureAwait(false)) is { } aborted) status = aborted;
                            }
                            finally { cleanup &= game.Release(); }
                            if (!cleanup) status = "release_unconfirmed";
                        }
                        if (status != "completed") break;
                        if (count == 2 && click == 0 && (await Pause(35).ConfigureAwait(false)) is { } gap) { status = gap; break; }
                    }
                    if (status != "completed") break;
                }
                else
                {
                    var count = step.Kind == "double_click" ? 2 : 1;
                    for (var click = 0; click < count; click++)
                    {
                        if ((await CheckAsync().ConfigureAwait(false)) is { } interrupted) { status = interrupted; break; }
                        var task = new Job(job.Plan.WindowID, step.ControlID, step.Text, step.Button, step.Wheel, step.Key,
                            step.Modifiers, step.Point, step.End, step.HoldMs, job.Allowed, job.Token)
                        {
                            SequenceStep = index, PhysicalStamp = stamp, Screenshot = job.Plan.Screenshot,
                        };
                        var result = DalamudMCP.Shared.ToolJson.Element(await SubmitAsync(task, job.ID).ConfigureAwait(false));
                        if (task.Dispatched) MarkDispatched(index);
                        cleanup &= result.GetProperty("cleanupConfirmed").GetBoolean();
                        var outcome = result.GetProperty("status").GetString();
                        if (outcome is not ("completed" or "observed")) { status = outcome ?? "input_error"; break; }
                    }
                    if (status != "completed") break;
                }
                lock (sync)
                {
                    job.Completed++;
                    job.LastPluginJob = null;
                }
            }
            if (status == "completed" && job.Observe is { } observe)
            {
                if ((await CheckAsync().ConfigureAwait(false)) is { } rejected) status = rejected;
                else
                {
                    var observed = await observe(job.Token).WaitAsync(job.Token).ConfigureAwait(false);
                    lock (sync) job.Observed = observed;
                }
            }
        }
        catch (OperationCanceledException)
        {
            status = job.Cancellation.IsCancellationRequested || job.Lifetime.IsCancellationRequested ? "cancelled" : "timeout";
        }
        catch (Exception error) { status = "input_error:" + error.GetType().Name; }
        finally
        {
            lock (sync)
            {
                job.GameInput?.Dispose();
                cleanup &= job.GameInput?.CleanupConfirmed ?? true;
                if (job.LastPluginJob is { } pluginJob)
                {
                    if (pluginJob.Dispatched && !job.Dispatched.Contains(pluginJob.SequenceStep)) job.Dispatched.Add(pluginJob.SequenceStep);
                    if (ReferenceEquals(active, pluginJob)) cleanup = false;
                }
                if (activeSequence == job) activeSequence = null;
                FinishSequence(job, cleanup ? status : status == "completed" ? "release_unconfirmed" : status, cleanup);
            }
        }

        void MarkDispatched(int index)
        {
            lock (sync) if (!job.Dispatched.Contains(index)) job.Dispatched.Add(index);
        }

        async Task<string?> CheckAsync()
        {
            job.Token.ThrowIfCancellationRequested();
            if (!job.Allowed()) return "permission_changed";
            if (stamp is null || GameWindowState.PhysicalInputStamp() != stamp || GameWindowState.PhysicalKeyDown()) return "human_input";
            if (job.Plan.GameWindow is { } window && GameWindowState.ReadInputWindow() != window) return "context_changed";
            if (!await job.ContextMatches(job.Token).WaitAsync(job.Token).ConfigureAwait(false)) return "context_changed";
            return !job.Allowed() ? "permission_changed" : GameWindowState.PhysicalInputStamp() != stamp ||
                GameWindowState.PhysicalKeyDown() ? "human_input" : null;
        }

        async Task<string?> Pause(int duration)
        {
            var until = Environment.TickCount64 + duration;
            do
            {
                await Task.Delay((int)Math.Clamp(until - Environment.TickCount64, 1, 25), job.Token).ConfigureAwait(false);
                if ((await CheckAsync().ConfigureAwait(false)) is { } reason) return reason;
            } while (Environment.TickCount64 < until);
            return null;
        }
    }

    private void FinishSequence(InputSequence job, string status, bool cleanup)
    {
        job.Status = status;
        job.FinishedAt = Environment.TickCount64;
        job.CleanupConfirmed = cleanup;
        job.Cancellation.Dispose();
        job.Deadline.Dispose();
        foreach (var old in recentSequences.Values.Where(item => item.FinishedAt != 0)
            .OrderByDescending(item => item.FinishedAt).Skip(32).ToArray()) recentSequences.Remove(old.ID);
    }

    private static object SequenceResult(InputSequence job) => new
    {
        sequenceID = job.ID, job.Status, completedSteps = job.Completed, dispatchedSteps = job.Dispatched.ToArray(),
        requestDispatched = job.Dispatched.Count != 0, resultObserved = job.Observed,
        failedStep = job.FinishedAt != 0 && job.Completed < job.Plan.Steps.Length ? (int?)job.Completed : null,
        elapsedMs = (job.FinishedAt == 0 ? Environment.TickCount64 : job.FinishedAt) - job.QueuedAt,
        cleanupConfirmed = job.CleanupConfirmed,
    };

    private sealed class InputSequence
    {
        public readonly Guid ID = Guid.NewGuid();
        public readonly SequencePlan Plan;
        public readonly Func<bool> Allowed;
        public readonly Func<CancellationToken, Task<bool>> ContextMatches;
        public readonly Func<CancellationToken, Task<bool>>? Observe;
        public readonly CancellationToken Lifetime;
        public readonly CancellationTokenSource Cancellation = new();
        public readonly CancellationTokenSource Deadline;
        public readonly CancellationToken Token;
        public readonly long QueuedAt;
        public readonly List<int> Dispatched = new();
        public GameWindowState.InputSession? GameInput;
        public Job? LastPluginJob;
        public string Status = "queued";
        public int Completed;
        public long FinishedAt;
        public bool Observed;
        public bool? CleanupConfirmed;

        public InputSequence(SequencePlan plan, Func<bool> allowed, Func<CancellationToken, Task<bool>> contextMatches, Func<CancellationToken, Task<bool>>? observe,
            CancellationToken lifetime, long queuedAt)
        {
            Plan = plan;
            Allowed = allowed;
            ContextMatches = contextMatches;
            Observe = observe;
            Lifetime = lifetime;
            QueuedAt = queuedAt;
            Deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime, Cancellation.Token);
            Deadline.CancelAfter((int)Math.Max(1, plan.TimeoutMs - (Environment.TickCount64 - queuedAt)));
            Token = Deadline.Token;
        }
    }

    internal sealed record SequencePlan(uint WindowID, GameWindowState.WindowIdentity? GameWindow, SequenceStep[] Steps, int TimeoutMs, ScreenshotInputFrame? Screenshot = null);
    internal sealed record SequenceStep(string Kind, uint ControlID, string? Text, int? Button, float Wheel, ImGuiKey Key,
        uint GameKey, ImGuiKey[] Modifiers, Vector2? Point, Vector2? End, int HoldMs);
}
