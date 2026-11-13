namespace DalamudMCP.Plugin.Tools;

internal sealed class ZoneTrafficBuffer(string direction, int seconds, int capacity, ulong character, uint territory,
    nint dispatcher, Func<bool> allowed)
{
    private readonly object sync = new();
    private readonly long started = Environment.TickCount64;
    private readonly List<TrafficEvent> events = new(capacity);
    private volatile string status = "active";

    public readonly Guid ID = Guid.NewGuid();
    public readonly string Direction = direction;
    public readonly ulong Character = character;
    public readonly uint Territory = territory;
    public readonly nint Dispatcher = dispatcher;
    public readonly Func<bool> Allowed = allowed;
    public int RawByteLimit { get; init; }
    public nint ZoneClient { get; init; }
    public int ZoneClientOffset { get; init; }
    public HashSet<uint>? Opcodes { get; init; }
    public OpcodeCatalogCheck.Catalog? Catalog { get; init; }
    public int Errors;
    public int InvalidPackets;
    private int capturedPackets;
    private int contended;
    public string Status => status;
    public bool IsExpired => Environment.TickCount64 - started >= 1000L * seconds;
    public bool IsFull { get { lock (sync) return events.Count >= capacity; } }

    public bool Wants(string packetDirection) => status == "active" && !IsExpired && Allowed() &&
        packetDirection is "send" or "receive" && (Direction == "both" || Direction == packetDirection);

    public void Observe(string packetDirection, bool? accepted) => Add(packetDirection, accepted, default, default);

    public void ObservePacket(string packetDirection, bool? accepted, PacketInfo packet, ReadOnlySpan<byte> bytes)
    {
        if (RawByteLimit != 0 && (Opcodes is null || Opcodes.Contains(packet.Opcode))) Add(packetDirection, accepted, packet, bytes);
    }

    private void Add(string packetDirection, bool? accepted, PacketInfo? packet, ReadOnlySpan<byte> bytes)
    {
        if (!Wants(packetDirection)) return;
        if (!Monitor.TryEnter(sync))
        {
            Interlocked.Increment(ref contended);
            return;
        }
        try
        {
            if (status != "active" || IsExpired || events.Count >= capacity) return;
            var data = packet is null ? null : bytes.ToArray();
            events.Add(new(events.Count, Environment.TickCount64 - started, packetDirection, accepted, packet, data));
            if (data is { Length: > 0 }) capturedPackets++;
        }
        finally { Monitor.Exit(sync); }
    }

    public void Stop(string reason)
    {
        lock (sync)
            if (status == "active") status = reason;
    }

    public PacketSnapshot? GetPacket(int index)
    {
        lock (sync)
        {
            if (index < 0 || index >= events.Count || events[index] is not { Packet: { } packet, Bytes: { } bytes } entry) return null;
            return new(entry.Index, entry.Direction, packet, (byte[])bytes.Clone(), Names(entry.Direction, packet.Opcode));
        }
    }

    private string[] Names(string direction, uint opcode) => Catalog is null ? [] :
        (direction == "send" ? Catalog.Send : Catalog.Receive).Where(row => row.Opcode == opcode).Select(row => row.Name).Distinct().ToArray();

    internal sealed record PacketSnapshot(int Index, string Direction, PacketInfo Packet, byte[] Bytes, string[] OpcodeNames);

    public object ReadFiltered(int offset, int limit, bool includeBytes, string? direction, uint? opcode)
    {
        TrafficEvent[] entries;
        int buffered;
        int withBytes;
        string snapshotStatus;
        lock (sync)
        {
            if (includeBytes && RawByteLimit == 0) return new { status = "metadata_only", traceID = ID };
            entries = events.Where(entry => entry.Index >= offset && (direction is null || entry.Direction == direction) &&
                (opcode is null || entry.Packet?.Opcode == opcode)).Take(limit + 1).ToArray();
            buffered = events.Count;
            withBytes = capturedPackets;
            snapshotStatus = status == "active" && IsExpired ? "expired" : status;
        }
        var hasMore = entries.Length > limit;
        var rows = entries.Take(limit).Select(entry => new
        {
            entry.Index, entry.ElapsedMs, entry.Direction, entry.SendAccepted,
            entry.Packet, opcodeNames = entry.Packet is { } info ? Names(entry.Direction, info.Opcode) : [], capturedLength = entry.Bytes?.Length,
            truncated = entry.Packet is { } packet ? entry.Bytes?.Length < packet.DataLength : (bool?)null,
            hex = includeBytes && entry.Bytes is { } bytes ? Convert.ToHexString(bytes) : null,
        }).ToArray();
        return new
        {
            status = snapshotStatus,
            traceID = ID, direction = Direction, payloadCaptured = withBytes != 0, rawPayloadRequested = RawByteLimit != 0,
            clientRegion = RawByteLimit != 0 ? "CN" : null, channel = RawByteLimit != 0 ? "zone" : null,
            opcodeCatalog = Catalog is null ? null : new { Catalog.Version, Catalog.Sha256, compatibilityVerified = false },
            buffered, errors = Volatile.Read(ref Errors), contended = Volatile.Read(ref contended),
            invalidPackets = Volatile.Read(ref InvalidPackets), capturedPackets = withBytes,
            capacityReached = buffered >= capacity, events = rows,
            nextOffset = hasMore ? (int?)rows[^1].Index + 1 : null,
        };
    }

    internal readonly record struct PacketInfo(uint Opcode, int IpcLength, int DataLength, int DataOffset, string Layout);
    private sealed record TrafficEvent(int Index, long ElapsedMs, string Direction, bool? SendAccepted, PacketInfo? Packet, byte[]? Bytes);
}
