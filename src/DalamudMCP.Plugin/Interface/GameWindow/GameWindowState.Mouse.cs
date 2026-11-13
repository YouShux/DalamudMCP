using System.Numerics;
using System.Runtime.InteropServices;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameWindowState
{
    internal static WindowIdentity? ReadInputWindow()
    {
        var hwnd = CurrentWindow();
        if (hwnd == 0 || !GetClientRect(hwnd, out var rect)) return null;
        var dpi = GetDpiForWindow(hwnd);
        var thread = GetWindowThreadProcessId(hwnd, out _);
        return rect.Right <= 0 || rect.Bottom <= 0 || dpi == 0 || thread == 0 ? null :
            new(hwnd, thread, rect.Right, rect.Bottom, dpi, GetForegroundWindow() == hwnd && !IsIconic(hwnd));
    }

    internal static uint? PhysicalInputStamp()
    {
        var state = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref state) ? state.Time : null;
    }

    internal static bool PhysicalKeyDown()
    {
        for (var key = 1; key < 256; key++)
            if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
        return false;
    }

    internal sealed class InputSession(WindowIdentity expected, Func<bool, bool>? windowMatches = null,
        Func<nint, uint, nuint, nint, bool>? post = null) : IDisposable
    {
        private readonly object sync = new();
        private bool closed;
        private uint? releaseMessage;
        private nuint releaseWParam;
        private nint releaseLParam;
        public bool CleanupConfirmed { get; private set; } = true;

        public bool Send(string kind, Vector2? position, int button, int wheel, uint key, bool doubleClick = false)
        {
            lock (sync)
            {
                if (closed || !Matches(true)) return false;
                if (kind == "key")
                {
                    var scan = MapVirtualKeyW(key, 0);
                    if (scan == 0 || releaseMessage.HasValue) return false;
                    var bits = 1u | ((scan & 0xff) << 16) | (key is >= 0x21 and <= 0x2e ? 1u << 24 : 0);
                    return Press(0x100, key, (nint)bits, 0x101, key, (nint)(bits | 0xC0000000));
                }
                if (position is not { } point || point.X < 0 || point.Y < 0 || point.X >= expected.Width || point.Y >= expected.Height) return false;
                var bitsXY = Pack((int)point.X, (int)point.Y);
                if (kind == "move")
                {
                    if (releaseMessage.HasValue) releaseLParam = bitsXY;
                    var held = releaseMessage switch { 0x202 => 1u, 0x205 => 2u, 0x208 => 16u, _ => 0u };
                    return Post(0x200, held, bitsXY);
                }
                if (kind == "scroll")
                {
                    var screen = new NativePoint { X = (int)point.X, Y = (int)point.Y };
                    if (!ClientToScreen(expected.Handle, ref screen) || screen.X is < short.MinValue or > short.MaxValue ||
                        screen.Y is < short.MinValue or > short.MaxValue) return false;
                    return Post(0x20A, (nuint)(uint)((wheel * 120) << 16), Pack(screen.X, screen.Y));
                }
                if (releaseMessage.HasValue) return false;
                var (down, up, mask) = button switch
                {
                    0 => (0x201u, 0x202u, 1u), 1 => (0x204u, 0x205u, 2u), _ => (0x207u, 0x208u, 16u),
                };
                return Press(doubleClick ? down + 2 : down, mask, bitsXY, up, 0, bitsXY);
            }
        }

        public bool Release()
        {
            lock (sync)
            {
                if (releaseMessage is not { } up) return CleanupConfirmed;
                releaseMessage = null;
                return CleanupConfirmed = Matches(false) && Post(up, releaseWParam, releaseLParam);
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                closed = true;
                Release();
            }
        }

        private bool Press(uint down, nuint wParam, nint lParam, uint up, nuint upWParam, nint upLParam)
        {
            if (!Post(down, wParam, lParam)) return false;
            releaseMessage = up;
            releaseWParam = upWParam;
            releaseLParam = upLParam;
            return true;
        }

        private bool Matches(bool requireFocus) => windowMatches?.Invoke(requireFocus) ??
            (IsOwnWindow(expected.Handle) && GetWindowThreadProcessId(expected.Handle, out _) == expected.Thread &&
                (!requireFocus || ReadInputWindow() == expected));

        private bool Post(uint message, nuint wParam, nint lParam) =>
            (post ?? PostMessageW)(expected.Handle, message, wParam, lParam);

        private static nint Pack(int x, int y) => (nint)(uint)((ushort)x | (uint)(ushort)y << 16);
    }

    internal readonly record struct WindowIdentity(nint Handle, uint Thread, int Width, int Height, uint Dpi, bool Foreground);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Time; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint window, ref NativePoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLastInputInfo(ref LastInput input);
}
