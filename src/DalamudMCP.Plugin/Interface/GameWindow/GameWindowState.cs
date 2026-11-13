using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameWindowState
{
    internal const string READ_TOOL = "inspect_game_window";
    internal const string SET_TOOL = "request_game_window_state";
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private int changing;

    public static object Read()
    {
        var window = CurrentWindow();
        return window == 0
            ? new { status = "unavailable" }
            : new { status = "ok", minimized = IsIconic(window), foreground = GetForegroundWindow() == window,
                inputWindow = ReadInputWindow() is { } identity ? new
                {
                    windowHandle = $"0x{identity.Handle:X}", clientWidth = identity.Width, clientHeight = identity.Height, identity.Dpi,
                } : null };
    }

    public async Task<object> SetAsync(ToolArguments input, Func<bool> isAllowed, CancellationToken token)
    {
        var expected = input.Boolean("expected_minimized");
        var desired = input.Boolean("minimized");
        if (Interlocked.CompareExchange(ref changing, 1, 0) != 0)
            return new { status = "busy", requestDispatched = false };
        try
        {
            var window = CurrentWindow();
            if (window == 0) return new { status = "unavailable", requestDispatched = false };
            if (IsIconic(window) != expected) return new { status = "state_changed", requestDispatched = false };
            if (desired == expected) return new { status = "already_matched", requestDispatched = false, resultObserved = true };
            token.ThrowIfCancellationRequested();
            if (!isAllowed()) throw new InvalidOperationException("窗口操作权限已关闭。");
            if (!ShowWindowAsync(window, desired ? SW_MINIMIZE : SW_RESTORE))
                return new { status = "not_accepted", requestDispatched = false };
            var deadline = Environment.TickCount64 + 1500;
            do
            {
                if (!IsOwnWindow(window)) return new { status = "window_changed", requestDispatched = true, resultObserved = false };
                if (IsIconic(window) == desired) return new { status = "observed", requestDispatched = true, resultObserved = true, minimized = desired };
                await Task.Delay(50, token).ConfigureAwait(false);
            } while (Environment.TickCount64 < deadline);
            return new { status = "timeout", requestDispatched = true, resultObserved = false };
        }
        finally { Volatile.Write(ref changing, 0); }
    }

    private static nint CurrentWindow()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        using var process = Process.GetCurrentProcess();
        var window = process.MainWindowHandle;
        return IsOwnWindow(window) ? window : 0;
    }

    private static bool IsOwnWindow(nint window) => window != 0 &&
        GetWindowThreadProcessId(window, out var owner) != 0 && owner == (uint)Environment.ProcessId;

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint window, int command);
}
