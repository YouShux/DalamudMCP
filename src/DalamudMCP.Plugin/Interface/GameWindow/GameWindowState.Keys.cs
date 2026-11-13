using System.Runtime.InteropServices;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameWindowState
{
    internal const string KEY_TOOL = "request_foreground_game_key";
    private int keyInProgress;

    public async Task<object> RequestKeyAsync(ToolArguments input, Func<bool> isAllowed, Func<Task<bool>> contextMatches,
        CancellationToken token)
    {
        var keyName = input.Text("key");
        if (VirtualKey(keyName) is not { } key) throw new ArgumentException("不支持此游戏键名。");
        var hold = input.Integer("hold_ms", 60, 25, 1500);
        var repeat = input.Integer("repeat", 1, 1, 8);
        var gap = input.Integer("interval_ms", 100, 50, 500);
        if ((hold + gap) * repeat > 5000) throw new ArgumentException("整个按键请求不得超过 5 秒。");
        if (Interlocked.CompareExchange(ref keyInProgress, 1, 0) != 0) return new { status = "busy", pulses = 0 };
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(7000);
            var window = CurrentWindow();
            if (window == 0) return new { status = "unavailable", pulses = 0 };
            var scan = MapVirtualKeyW(key, 0);
            if (scan == 0) return new { status = "unsupported_key", pulses = 0 };
            var downBits = (nint)(1u | ((scan & 0xFF) << 16) | (key is >= 0x21 and <= 0x2E ? 1u << 24 : 0));
            var upBits = (nint)((uint)downBits | 0xC0000000);
            var sent = 0;
            var cleanup = true;
            for (; sent < repeat;)
            {
                token.ThrowIfCancellationRequested();
                if (!CanDispatch() || !await contextMatches().WaitAsync(deadline.Token).ConfigureAwait(false) || !CanDispatch())
                    return new { status = "context_changed", pulses = sent, cleanupConfirmed = cleanup, resultObserved = false };
                if (input.Boolean("dry_run")) return new { status = "ready", pulses = 0, resultObserved = false };
                if (!PostMessageW(window, 0x0100, key, downBits))
                    return new { status = "not_accepted", pulses = sent, cleanupConfirmed = cleanup, resultObserved = false };
                sent++;
                var stillSafe = true;
                try
                {
                    var until = Environment.TickCount64 + hold;
                    do
                    {
                        await Task.Delay((int)Math.Clamp(until - Environment.TickCount64, 1, 35), deadline.Token).ConfigureAwait(false);
                        if (!CanDispatch()) { stillSafe = false; break; }
                    } while (Environment.TickCount64 < until);
                }
                finally
                {
                    cleanup = IsOwnWindow(window) && PostMessageW(window, 0x0101, key, upBits);
                }
                if (!cleanup || !stillSafe)
                    return new { status = cleanup ? "interrupted" : "release_unconfirmed", pulses = sent,
                        cleanupConfirmed = cleanup, resultObserved = false };
                if (sent < repeat) await Task.Delay(gap, deadline.Token).ConfigureAwait(false);
            }
            return new { status = "requested", pulses = sent, cleanupConfirmed = cleanup, resultObserved = false };

            bool CanDispatch() => isAllowed() && IsOwnWindow(window) && GetForegroundWindow() == window && !IsIconic(window) &&
                (GetAsyncKeyState((int)key) & 0x8000) == 0 && (GetAsyncKeyState(0x10) & 0x8000) == 0 &&
                (GetAsyncKeyState(0x11) & 0x8000) == 0 && (GetAsyncKeyState(0x12) & 0x8000) == 0 &&
                (GetAsyncKeyState(0x5B) & 0x8000) == 0 && (GetAsyncKeyState(0x5C) & 0x8000) == 0;
        }
        finally { Volatile.Write(ref keyInProgress, 0); }
    }

    internal static uint? VirtualKey(string key)
    {
        if (key.Length == 1 && (key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) return key[0];
        if (key.Length is 2 or 3 && key[0] == 'F' && key[1] is >= '1' and <= '9' &&
            int.TryParse(key.AsSpan(1), out var function) && function is >= 1 and <= 12)
            return (uint)(0x6F + function);
        return key switch
        {
            "Backspace" => 0x08, "Tab" => 0x09, "Enter" => 0x0D, "Escape" => 0x1B, "Space" => 0x20,
            "PageUp" => 0x21, "PageDown" => 0x22, "End" => 0x23, "Home" => 0x24,
            "Left" => 0x25, "Up" => 0x26, "Right" => 0x27, "Down" => 0x28,
            "Insert" => 0x2D, "Delete" => 0x2E, _ => null,
        };
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint MapVirtualKeyW(uint code, uint mode);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
}
