using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DalamudMCP.Shared;

namespace DalamudMCP.Plugin.Tools;

internal static class GameScreenshot
{
    private static readonly SemaphoreSlim CaptureLock = new(1, 1);

    public static JsonElement Capture(string area)
    {
        if (area is not "client" and not "window")
            throw new ArgumentException("capture_area 必须为 client 或 window。");
        if (!CaptureLock.Wait(0))
            throw new InvalidOperationException("已有截图正在执行。");
        try
        {
            using var process = Process.GetCurrentProcess();
            var window = process.MainWindowHandle;
            if (window == 0 || IsIconic(window))
                throw new InvalidOperationException("游戏窗口不可用或已最小化。");
            var client = area == "client";
            var success = client ? GetClientRect(window, out var bounds) : GetWindowRect(window, out bounds);
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            if (!success || width <= 0 || height <= 0 || (long)width * height > 20000000)
                throw new InvalidOperationException("游戏窗口尺寸无效或截图超过 2000 万像素。");
            var source = client ? GetDC(window) : GetWindowDC(window);
            if (source == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            nint memory = 0;
            nint bitmap = 0;
            nint previous = 0;
            try
            {
                memory = CreateCompatibleDC(source);
                var info = new BitmapInfo
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfo>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                };
                bitmap = CreateDIBSection(source, ref info, 0, out var pixels, 0, 0);
                if (memory == 0 || bitmap == 0 || pixels == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                previous = SelectObject(memory, bitmap);
                if (previous == 0 || previous == -1)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!PrintWindow(window, memory, client ? 3u : 2u) && !BitBlt(memory, 0, 0, width, height, source, 0, 0, 0x40CC0020))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var raw = new byte[checked(width * height * 4)];
                Marshal.Copy(pixels, raw, 0, raw.Length);
                var png = EncodePNG(raw, width, height);
                return ToolJson.Element(new
                {
                    content = new object[]
                    {
                        new { type = "image", data = Convert.ToBase64String(png), mimeType = "image/png" },
                    },
                    structuredContent = new { width, height, captureArea = area },
                    isError = false,
                });
            }
            finally
            {
                if (memory != 0 && previous != 0 && previous != -1)
                    SelectObject(memory, previous);
                if (bitmap != 0)
                    DeleteObject(bitmap);
                if (memory != 0)
                    DeleteDC(memory);
                ReleaseDC(window, source);
            }
        }
        finally
        {
            CaptureLock.Release();
        }
    }

    private static byte[] EncodePNG(byte[] bgra, int width, int height)
    {
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        WriteChunk(png, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true))
        {
            var line = new byte[checked(width * 3 + 1)];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var source = (y * width + x) * 4;
                    var destination = x * 3 + 1;
                    line[destination] = bgra[source + 2];
                    line[destination + 1] = bgra[source + 1];
                    line[destination + 2] = bgra[source];
                }
                zlib.Write(line);
            }
        }
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string name, byte[] data)
    {
        Span<byte> integer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(integer, data.Length);
        stream.Write(integer);
        var type = Encoding.ASCII.GetBytes(name);
        stream.Write(type);
        stream.Write(data);
        var crc = uint.MaxValue;
        foreach (var value in type.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc);
        stream.Write(integer);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetDC(nint window);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetWindowDC(nint window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out Rect rectangle);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect rectangle);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint pixels, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint ImageSize;
        public int XPixelsPerMeter;
        public int YPixelsPerMeter;
        public uint ColorsUsed;
        public uint ImportantColors;
    }
}
