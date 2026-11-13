using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DalamudMCP.Plugin.Tools;

internal static unsafe class ProcessTcpSummary
{
    internal static object Read()
    {
        if (!OperatingSystem.IsWindows()) return new { status = "unsupported_platform" };
        var pid = (uint)Environment.ProcessId;
        var ipv4 = ReadFamily(false, pid);
        var ipv6 = ReadFamily(true, pid);
        return new { status = ipv4.Counts is null || ipv6.Counts is null ? "partial" : "ok", ipv4, ipv6 };
    }

    private static Family ReadFamily(bool v6, uint pid)
    {
        uint size = 0;
        var family = v6 ? AF_INET6 : AF_INET;
        var result = GetExtendedTcpTable(null, ref size, false, family, OWNER_PID_CONNECTIONS, 0);
        for (var attempt = 0; attempt < 3 && result == INSUFFICIENT_BUFFER; attempt++)
        {
            if (size is < 4 or > MAX_BUFFER) return new(null, "buffer_limit", result);
            var bytes = ArrayPool<byte>.Shared.Rent((int)size);
            try
            {
                var capacity = size;
                fixed (byte* buffer = bytes)
                    result = GetExtendedTcpTable(buffer, ref size, false, family, OWNER_PID_CONNECTIONS, 0);
                if (result == 0)
                {
                    if (size > capacity || !TrySummarize(bytes.AsSpan(0, (int)size), v6, pid, out var counts))
                        return new(null, "invalid_table", null);
                    return new(counts, "ok", null);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(bytes, clearArray: true); }
        }
        return new(null, "unavailable", result);
    }

    internal static bool TrySummarize(ReadOnlySpan<byte> table, bool v6, uint pid, out Counts counts)
    {
        counts = default;
        if (table.Length < 4) return false;
        var rowSize = v6 ? 56 : 24;
        var stateOffset = v6 ? 48 : 0;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        if (count > (table.Length - 4) / rowSize) return false;
        var established = 0;
        var opening = 0;
        var closing = 0;
        var other = 0;
        for (var index = 0; index < count; index++)
        {
            var row = table.Slice(4 + index * rowSize, rowSize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(row[^4..]) != pid) continue;
            switch (BinaryPrimitives.ReadUInt32LittleEndian(row[stateOffset..]))
            {
                case 5: established++; break;
                case 3 or 4: opening++; break;
                case >= 6 and <= 11: closing++; break;
                default: other++; break;
            }
        }
        counts = new(established, opening, closing, other);
        return true;
    }

    internal readonly record struct Counts(int Established, int Opening, int Closing, int Other);
    private sealed record Family(Counts? Counts, string Status, uint? Error);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(byte* table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool sort,
        uint addressFamily, int tableClass, uint reserved);

    private const uint AF_INET = 2;
    private const uint AF_INET6 = 23;
    private const int OWNER_PID_CONNECTIONS = 4;
    private const uint INSUFFICIENT_BUFFER = 122;
    private const int MAX_BUFFER = 4 * 1024 * 1024;
}
