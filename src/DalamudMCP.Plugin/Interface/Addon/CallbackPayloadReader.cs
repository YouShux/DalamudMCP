using System.Runtime.InteropServices;

namespace DalamudMCP.Plugin.Tools;

internal static class CallbackPayloadReader
{
    internal const int MaxArgumentCount = 64;
    internal const int MaxPayloadLength = 1_048_576;

    internal static bool TryCopyPayload(nint source, nuint length, Span<byte> destination, out int copied)
    {
        copied = 0;
        return length <= MaxPayloadLength && TryCopy(source, (int)length, destination, out copied);
    }

    internal static bool TryCopyArguments(nint source, nuint count, Span<byte> destination, out int copied)
    {
        copied = 0;
        return count <= MaxArgumentCount && TryCopy(source, (int)count * sizeof(int),
            destination[..(destination.Length / sizeof(int) * sizeof(int))], out copied);
    }

    private static unsafe bool TryCopy(nint source, int length, Span<byte> destination, out int copied)
    {
        copied = 0;
        if (length == 0) return true;
        var size = Math.Min(length, destination.Length);
        if (size == 0 || !NativeMemoryReader.TryCopy(source, destination[..size])) return false;
        copied = size;
        return true;
    }
}

internal static class NativeMemoryReader
{
    private static readonly int PageSize = Environment.SystemPageSize;

    internal static int ReadCString(nint source, Span<byte> buffer)
    {
        if (source == 0 || buffer.IsEmpty || (nuint)source > nuint.MaxValue - (nuint)buffer.Length) return -1;
        for (var offset = 0; offset < buffer.Length;)
        {
            var address = source + offset;
            var size = Math.Min(buffer.Length - offset, PageSize - (int)((nuint)address % (nuint)PageSize));
            var chunk = buffer.Slice(offset, size);
            if (!TryCopy(address, chunk)) return -1;
            var end = chunk.IndexOf((byte)0);
            if (end >= 0) return offset + end;
            offset += size;
        }
        return -1;
    }

    internal static unsafe bool TryRead<T>(T* address, out T value) where T : unmanaged
    {
        T buffer = default;
        var success = TryCopy((nint)address, new Span<byte>(&buffer, sizeof(T)));
        value = success ? buffer : default;
        return success;
    }

    internal static unsafe bool TryCopy(nint source, Span<byte> destination)
    {
        var size = destination.Length;
        if (source == 0 || size == 0 || (nuint)source > nuint.MaxValue - (nuint)(size - 1)) return false;
        fixed (byte* target = destination)
            return ReadProcessMemory(GetCurrentProcess(), source, target, (nuint)size, out var read) && read == (nuint)size;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool ReadProcessMemory(nint process, nint address, byte* buffer, nuint size, out nuint read);
}
