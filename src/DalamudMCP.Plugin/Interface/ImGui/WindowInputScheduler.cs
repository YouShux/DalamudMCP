using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class WindowInputScheduler : IDisposable
{
    private readonly object sync = new();
    private readonly Queue<Job> queue = new();
    private readonly HashSet<ImGuiKey> ownedKeys = [];
    private readonly ImGuiContextHookCallback before;
    private readonly ImGuiContextHookCallback after;
    private ImGuiContextPtr context;
    private uint beforeID;
    private uint afterID;
    private Job? active;
    private int? ownedButton;
    private string? outcome;
    private object? lastResult;
    private bool disposed;
    private bool releasePending;
    private string? setupError;

    public unsafe WindowInputScheduler()
    {
        before = BeforeFrame;
        after = AfterFrame;
    }

    public unsafe void Attach()
    {
        lock (sync)
        {
            if (disposed || beforeID != 0 || setupError is not null) return;
            var current = ImGui.GetCurrentContext();
            if (current.IsNull) return;
            context = current;
            try
            {
                var hook = new ImGuiContextHook { Type = ImGuiContextHookType.NewFramePre, Callback = (void*)Marshal.GetFunctionPointerForDelegate(before) };
                beforeID = ImGuiP.AddContextHook(current, ref hook);
                hook = new() { Type = ImGuiContextHookType.NewFramePost, Callback = (void*)Marshal.GetFunctionPointerForDelegate(after) };
                afterID = ImGuiP.AddContextHook(current, ref hook);
                if (beforeID == 0 || afterID == 0) throw new InvalidOperationException("无法注册 ImGui 帧回调。");
            }
            catch (Exception error)
            {
                setupError = error.GetType().Name;
                if (beforeID != 0) ImGuiP.RemoveContextHook(current, beforeID);
                if (afterID != 0) ImGuiP.RemoveContextHook(current, afterID);
                beforeID = afterID = 0;
            }
        }
    }

    public object State()
    {
        lock (sync) return new { ready = beforeID != 0 && afterID != 0, setupError, queued = queue.Count,
            activeRequest = active?.ID, ownedKeys = ownedKeys.Select(key => key.ToString()).ToArray(), ownedButton, releasePending, lastOutcome = outcome, lastResult };
    }

    public object Cancel()
    {
        lock (sync)
        {
            foreach (var job in queue) job.Completion.TrySetResult(Result(job, "cancelled", true));
            queue.Clear();
            if (active is { } current) current.Stop = "cancelled";
            return new { status = "cancellation_requested", cleanupConfirmed = active is null && !releasePending };
        }
    }

    public async Task<object> SubmitAsync(Job job, Guid? sequence = null)
    {
        lock (sync)
        {
            if (disposed || setupError is not null) return Result(job, "unavailable", true);
            if (sequence.HasValue && activeSequence?.ID != sequence || !sequence.HasValue && (legacyGameKey || activeSequence is not null || sequences.Count != 0))
                return Result(job, "busy", true);
            if (sequence.HasValue && activeSequence is { } owner) owner.LastPluginJob = job;
            if (job.Key != ImGuiKey.None && VirtualKey(job.Key) == 0) return Result(job, "unsupported_key", true);
            if (queue.Count >= 8) return Result(job, "busy", true);
            job.SequenceID = sequence;
            queue.Enqueue(job);
        }
        try { return await job.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5), job.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            lock (sync)
            {
                job.Stop = error is TimeoutException ? "timeout" : "cancelled";
                return Result(job, job.Stop, !ReferenceEquals(active, job));
            }
        }
    }

    private unsafe void BeforeFrame(ImGuiContext* pointer, ImGuiContextHook* hook)
    {
        lock (sync)
        {
            try
            {
                if (disposed || (nint)pointer != (nint)context.Handle) return;
                var io = ImGui.GetIO();
                if (active is null)
                {
                    while (queue.TryDequeue(out var next))
                    {
                        if (next.Stop is not null || next.Token.IsCancellationRequested || Environment.TickCount64 >= next.Deadline)
                        { next.Completion.TrySetResult(Result(next, next.Stop ?? "expired", true)); continue; }
                        active = next;
                        next.Physical = PhysicalState();
                        next.OriginalPointer = io.MousePos;
                        if (next.Point.HasValue && (!float.IsFinite(io.MousePos.X) || !float.IsFinite(io.MousePos.Y))) next.Stop = "pointer_unavailable";
                        break;
                    }
                }
                if (active is not { } job) return;
                var physical = PhysicalState();
                if (job.Stop is null)
                {
                    if (job.Token.IsCancellationRequested) job.Stop = "cancelled";
                    else if (Environment.TickCount64 >= job.Deadline) job.Stop = "timeout";
                    else if (!job.Allowed()) job.Stop = "permission_revoked";
                    else if (job.Screenshot is { } frame && !frame.IsCurrent(ScreenshotViewport.Read())) job.Stop = "context_changed";
                    else if (!physical.Valid || physical != job.Physical || physical.AnyDown || UserEvents() ||
                        job.PhysicalStamp is { } stamp && GameWindowState.PhysicalInputStamp() != stamp) job.Stop = "human_input";
                    else if (io.AppFocusLost || !io.AppAcceptingEvents || io.WantSetMousePos) job.Stop = "focus_changed";
                }
                if (job.Stop is not null || job.Phase == 4)
                {
                    Release(io);
                    if (ownedKeys.Count != 0 || ownedButton.HasValue) { releasePending = true; return; }
                    if (job.Point.HasValue && physical.Valid && physical == job.Physical)
                    {
                        io.AddMousePosEvent(job.OriginalPointer.X, job.OriginalPointer.Y);
                        if (!io.AppAcceptingEvents) { io.MousePos = job.OriginalPointer; io.MouseDelta = Vector2.Zero; }
                    }
                    job.Phase = 5;
                    releasePending = true;
                    return;
                }
                var window = FindWindow(job.WindowID);
                if (window.IsNull || window.Hidden || window.Collapsed || !(window.Active || window.WasActive))
                { job.Stop = "window_changed"; return; }
                if (job.Point is { } point)
                {
                    if (job.End is { } end && job.Phase == 3)
                    {
                        var progress = Math.Clamp((Environment.TickCount64 - job.PressedAt) / (float)job.HoldMs, 0, 1);
                        point = Vector2.Lerp(point, end, progress);
                    }
                    if (point.X < window.Pos.X || point.Y < window.Pos.Y || point.X >= window.Pos.X + window.Size.X || point.Y >= window.Pos.Y + window.Size.Y)
                    { job.Stop = "outside_window"; return; }
                    FilterBackendEvents(pointerEvents: true);
                    io.AddMousePosEvent(point.X, point.Y);
                    if (job.Phase == 0) { job.Phase = 1; return; }
                }
                if (job.Phase <= 1)
                {
                    var pointerInput = job.Point.HasValue || job.Button.HasValue || job.Wheel != 0;
                    var valid = job.Text is not null
                        ? io.WantTextInput && context.ActiveId == job.ControlID && context.InputTextState.ID == job.ControlID &&
                            !context.ActiveIdWindow.IsNull && context.ActiveIdWindow.ID == job.WindowID &&
                            (context.InputTextState.Flags & (ImGuiInputTextFlags.Password | ImGuiInputTextFlags.ReadOnly)) == 0
                        : pointerInput
                            ? !context.HoveredWindow.IsNull && context.HoveredWindow.ID == job.WindowID &&
                                (job.ControlID == 0 || context.HoveredId == job.ControlID) && context.ActiveId == 0 && !io.MouseDown.Contains(true)
                            : !context.NavWindow.IsNull && context.NavWindow.ID == job.WindowID && context.NavId == job.ControlID && context.ActiveId == 0 && !io.WantTextInput;
                    if (!valid) { job.Stop = "target_changed"; return; }
                    job.HoveredControlID = pointerInput ? context.HoveredId : null;
                    if (job.Text is { } text)
                        foreach (var character in text) io.AddInputCharacter(character);
                    else if (job.Wheel != 0) io.AddMouseWheelEvent(0, job.Wheel);
                    else if (job.Button is { } button) { ownedButton = button; io.AddMouseButtonEvent(button, true); }
                    else if (job.Key != ImGuiKey.None)
                    {
                        foreach (var modifier in job.Modifiers) { ownedKeys.Add(modifier); io.AddKeyEvent(modifier, true); }
                        ownedKeys.Add(job.Key);
                        io.AddKeyEvent(job.Key, true);
                    }
                    job.PressedAt = Environment.TickCount64;
                    job.Dispatched = true;
                    job.Phase = job.HoldMs == 0 ? 4 : 3;
                    return;
                }
                if (job.Phase == 3)
                {
                    if (job.Key != ImGuiKey.None && (context.NavWindow.IsNull || context.NavWindow.ID != job.WindowID ||
                        context.NavId != job.ControlID && context.ActiveId != job.ControlID) ||
                        job.End.HasValue && context.ActiveId != job.ControlID)
                    { job.Stop = "target_changed"; return; }
                    FilterBackendEvents(pointerEvents: false);
                    foreach (var key in ownedKeys) io.AddKeyEvent(key, true);
                    if (ownedButton is { } button) io.AddMouseButtonEvent(button, true);
                    if (Environment.TickCount64 - job.PressedAt >= job.HoldMs) job.Phase = 4;
                }
            }
            catch (Exception error)
            {
                if (active is { } job) { job.Stop = "input_error:" + error.GetType().Name; releasePending = true; }
            }
        }
    }

    private unsafe void AfterFrame(ImGuiContext* pointer, ImGuiContextHook* hook)
    {
        lock (sync)
        {
            if (active is not { Phase: 5 } job) return;
            outcome = job.Stop ?? (job.Point.HasValue && job.End is null && job.Button is null && job.Key == ImGuiKey.None && job.Text is null && job.Wheel == 0 ? "observed" : "completed");
            releasePending = false;
            lastResult = Result(job, outcome, true);
            job.Completion.TrySetResult(lastResult);
            active = null;
            if (job.SequenceID is { } sequence && recentSequences.TryGetValue(sequence, out var owner) && owner.FinishedAt != 0)
            {
                owner.CleanupConfirmed = true;
                if (job.Dispatched && !owner.Dispatched.Contains(job.SequenceStep)) owner.Dispatched.Add(job.SequenceStep);
            }
        }
    }

    private object Result(Job job, string status, bool cleanup) => new
    {
        status, requestID = job.ID, requestQueued = job.Dispatched, resultObserved = status == "observed", cleanupConfirmed = cleanup,
        hoveredControlID = job.HoveredControlID,
        systemCursorMoved = false,
    };

    private bool UserEvents()
    {
        foreach (var item in context.InputEventsQueue)
        {
            if (item.Type is ImGuiInputEventType.Text or ImGuiInputEventType.MouseWheel or ImGuiInputEventType.Focus) return true;
            if (item.Type == ImGuiInputEventType.Key && item.Union.Key.Down != 0) return true;
            if (item.Type == ImGuiInputEventType.MouseButton && item.Union.MouseButton.Down != 0) return true;
        }
        return false;
    }

    private void FilterBackendEvents(bool pointerEvents)
    {
        var index = 0;
        for (var position = 0; position < context.InputEventsQueue.Size; position++)
        {
            var item = context.InputEventsQueue[position];
            var skip = pointerEvents && item.Type is ImGuiInputEventType.MousePos or ImGuiInputEventType.MouseViewport;
            skip |= item.Type == ImGuiInputEventType.Key && item.Union.Key.Down == 0 && ownedKeys.Contains(item.Union.Key.Key);
            skip |= item.Type == ImGuiInputEventType.MouseButton && item.Union.MouseButton.Down == 0 && ownedButton == item.Union.MouseButton.Button;
            if (!skip) context.InputEventsQueue[index++] = item;
        }
        context.InputEventsQueue.Resize(index);
    }

    private void Release(ImGuiIOPtr io)
    {
        foreach (var key in ownedKeys)
        {
            var physical = VirtualKey(key);
            if (physical != 0 && (GetAsyncKeyState(physical) & 0x8000) != 0) continue;
            io.AddKeyEvent(key, false);
            if (!io.AppAcceptingEvents)
            {
                var state = ImGuiP.GetKeyData(key);
                state.Down = false;
                state.AnalogValue = 0;
                state.DownDuration = state.DownDurationPrev = -1;
                if (key == ImGuiKey.ModCtrl) io.KeyCtrl = false;
                if (key == ImGuiKey.ModShift) io.KeyShift = false;
                if (key == ImGuiKey.ModAlt) io.KeyAlt = false;
                if (key == ImGuiKey.ModSuper) io.KeySuper = false;
                if (key is ImGuiKey.ModCtrl or ImGuiKey.ModShift or ImGuiKey.ModAlt or ImGuiKey.ModSuper) io.KeyMods &= ~(key switch { ImGuiKey.ModCtrl => ImGuiModFlags.Ctrl, ImGuiKey.ModShift => ImGuiModFlags.Shift, ImGuiKey.ModAlt => ImGuiModFlags.Alt, _ => ImGuiModFlags.Super });
            }
        }
        ownedKeys.Clear();
        if (ownedButton is { } button && (GetAsyncKeyState(button == 0 ? 1 : button == 1 ? 2 : 4) & 0x8000) == 0)
        {
            if (active is { Stop: not null } current && context.ActiveId == current.ControlID && context.ActiveIdSource == ImGuiInputSource.Mouse)
                ImGuiP.ClearActiveID();
            io.AddMouseButtonEvent(button, false);
            if (!io.AppAcceptingEvents)
            {
                io.MouseDown[button] = false;
                io.MouseDownDuration[button] = io.MouseDownDurationPrev[button] = -1;
            }
        }
        ownedButton = null;
    }

    private ImGuiWindowPtr FindWindow(uint id)
    {
        for (var index = 0; index < Math.Min(context.Windows.Size, 512); index++)
            if (!context.Windows[index].IsNull && context.Windows[index].ID == id) return context.Windows[index];
        return default;
    }

    internal static bool SupportsKey(ImGuiKey key) => VirtualKey(key) != 0;

    private static int VirtualKey(ImGuiKey key) => key switch
    {
        ImGuiKey.ModCtrl => 0x11, ImGuiKey.ModShift => 0x10, ImGuiKey.ModAlt => 0x12, ImGuiKey.ModSuper => 0x5B,
        >= ImGuiKey.A and <= ImGuiKey.Z => 0x41 + key - ImGuiKey.A,
        >= ImGuiKey.F1 and <= ImGuiKey.F12 => 0x70 + key - ImGuiKey.F1,
        ImGuiKey.Apostrophe => 0xDE, ImGuiKey.Comma => 0xBC, ImGuiKey.Minus => 0xBD, ImGuiKey.Period => 0xBE,
        ImGuiKey.Slash => 0xBF, ImGuiKey.Semicolon => 0xBA, ImGuiKey.Equal => 0xBB, ImGuiKey.LeftBracket => 0xDB,
        ImGuiKey.Backslash => 0xDC, ImGuiKey.RightBracket => 0xDD, ImGuiKey.GraveAccent => 0xC0,
        ImGuiKey.CapsLock => 0x14, ImGuiKey.ScrollLock => 0x91, ImGuiKey.NumLock => 0x90, ImGuiKey.PrintScreen => 0x2C, ImGuiKey.Pause => 0x13,
        >= ImGuiKey.Keypad0 and <= ImGuiKey.Keypad9 => 0x60 + key - ImGuiKey.Keypad0,
        ImGuiKey.KeypadDecimal => 0x6E, ImGuiKey.KeypadDivide => 0x6F, ImGuiKey.KeypadMultiply => 0x6A,
        ImGuiKey.KeypadSubtract => 0x6D, ImGuiKey.KeypadAdd => 0x6B, ImGuiKey.KeypadEnter => 0x0D,
        _ => NamedKey(key.ToString()),
    };

    private static int NamedKey(string name)
    {
        if (name.Length > 1 && char.IsAsciiDigit(name[^1]) && (name.StartsWith("Alpha", StringComparison.Ordinal) ||
            name.StartsWith("Key", StringComparison.Ordinal) || name[0] == '_')) return name[^1];
        return (int)(GameWindowState.VirtualKey(name switch { "UpArrow" => "Up", "DownArrow" => "Down", "LeftArrow" => "Left", "RightArrow" => "Right", _ => name }) ?? 0);
    }

    private static Physical PhysicalState()
    {
        var window = GetForegroundWindow();
        var valid = GetCursorPos(out var point) && GetWindowThreadProcessId(window, out var process) != 0 && process == Environment.ProcessId;
        var down = false;
        for (var key = 1; key < 256; key++) if ((GetAsyncKeyState(key) & 0x8000) != 0) { down = true; break; }
        return new(valid, point.X, point.Y, window, down);
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            foreach (var sequence in sequences)
                if (sequence.Status == "queued") FinishSequence(sequence, "unloaded", true);
            sequences.Clear();
            if (activeSequence is { } sequenceJob)
            {
                sequenceJob.Cancellation.Cancel();
                sequenceJob.GameInput?.Dispose();
            }
            foreach (var job in queue) job.Completion.TrySetResult(Result(job, "unloaded", true));
            queue.Clear();
            if (!context.IsNull)
            {
                var previous = ImGui.GetCurrentContext();
                ImGui.SetCurrentContext(context);
                try
                {
                    Release(ImGui.GetIO());
                    if (beforeID != 0) ImGuiP.RemoveContextHook(context, beforeID);
                    if (afterID != 0) ImGuiP.RemoveContextHook(context, afterID);
                }
                finally { ImGui.SetCurrentContext(previous); }
            }
            active?.Completion.TrySetResult(Result(active, "unloaded", false));
            active = null;
        }
    }

    internal sealed class Job(uint windowID, uint controlID, string? text, int? button, float wheel, ImGuiKey key,
        ImGuiKey[] modifiers, Vector2? point, Vector2? end, int holdMs, Func<bool> allowed, CancellationToken token)
    {
        public readonly Guid ID = Guid.NewGuid();
        public readonly uint WindowID = windowID;
        public readonly uint ControlID = controlID;
        public readonly string? Text = text;
        public readonly int? Button = button;
        public readonly float Wheel = wheel;
        public readonly ImGuiKey Key = key;
        public readonly ImGuiKey[] Modifiers = modifiers;
        public readonly Vector2? Point = point;
        public readonly Vector2? End = end;
        public readonly int HoldMs = holdMs;
        public readonly Func<bool> Allowed = allowed;
        public readonly CancellationToken Token = token;
        public readonly long Deadline = Environment.TickCount64 + 5000;
        public readonly TaskCompletionSource<object> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid? SequenceID;
        public int SequenceStep;
        public ScreenshotInputFrame? Screenshot { get; init; }
        public uint? PhysicalStamp;
        public Physical Physical;
        public Vector2 OriginalPointer;
        public int Phase;
        public long PressedAt;
        public bool Dispatched;
        public string? Stop;
        public uint? HoveredControlID;
    }

    internal readonly record struct Physical(bool Valid, int X, int Y, nint Window, bool AnyDown);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
