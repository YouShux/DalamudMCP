using System.Buffers.Binary;
using System.Globalization;

namespace DalamudMCP.Plugin.Tools;

internal sealed class RuntimeMemorySearch
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, Search> sessions = [];

    public object Call(ToolArguments input, Func<bool> allowed, CancellationToken token)
    {
        lock (sync)
        {
            foreach (var expired in sessions.Where(row => row.Value.Until <= Environment.TickCount64).Select(row => row.Key).ToArray()) sessions.Remove(expired);
            Check();
            var operation = input.Text("operation");
            if (operation == "start")
            {
                if (sessions.Count == 8) return new { status = "capacity_reached" };
                var address = checked((nint)input.ObjectID("address"));
                var length = input.Integer("length", 4096, 1, 16 * 1024 * 1024);
                if (address <= 0 || (nuint)address > (nuint)nint.MaxValue - (nuint)length) throw new ArgumentException("内存范围无效。");
                var type = input.Text("type");
                var size = Size(type);
                var alignment = input.Integer("alignment", size, 1, 16);
                var search = new Search(type, size);
                var value = input.Has("value") ? input.Text("value") : null;
                if (value is not null) ValidateValue(type, value);
                var id = Guid.NewGuid();
                sessions.Add(id, search);
                var until = Environment.TickCount64 + 1000;
                var buffer = new byte[4096 + size - 1];
                for (var block = 0; block < length && Environment.TickCount64 < until && search.Candidates.Count < 16384; block += 4096)
                {
                    Check();
                    var count = Math.Min(buffer.Length, length - block);
                    search.ScannedBytes = Math.Min(length, block + 4096);
                    if (!NativeMemoryReader.TryCopy(address + block, buffer.AsSpan(0, count))) { search.SkippedBytes += Math.Min(4096, count); continue; }
                    var first = (alignment - block % alignment) % alignment;
                    for (var offset = first; offset < Math.Min(4096, count - size + 1) && search.Candidates.Count < 16384; offset += alignment)
                    {
                        var data = buffer.AsSpan(offset, size);
                        if (value is null || Matches(type, data, value)) search.Candidates.Add(new(address + block + offset, data.ToArray()));
                    }
                }
                search.Complete = search.ScannedBytes == length && search.SkippedBytes == 0 && search.Candidates.Count < 16384;
                return Read(id, search, input);
            }
            var sessionID = Guid.ParseExact(input.Text("search_id"), "D");
            if (!sessions.TryGetValue(sessionID, out var current)) return new { status = "search_expired" };
            if (operation == "release") { sessions.Remove(sessionID); return new { status = "released" }; }
            if (operation == "refine")
            {
                var mode = input.Text("mode");
                if (mode is not ("equal" or "not_equal" or "changed" or "unchanged" or "increased" or "decreased")) throw new ArgumentException("未知收敛条件。");
                var expected = input.Has("value") ? input.Text("value") : null;
                if (expected is not null) ValidateValue(current.Type, expected);
                if ((mode is "equal" or "not_equal") != (expected is not null)) throw new ArgumentException("equal 和 not_equal 需要 value；其他模式不能传 value。");
                var result = new List<Candidate>();
                var until = Environment.TickCount64 + 1000;
                var processed = 0;
                var unreadable = 0;
                foreach (var candidate in current.Candidates)
                {
                    Check();
                    if (Environment.TickCount64 >= until) break;
                    processed++;
                    var bytes = new byte[current.Size];
                    if (!NativeMemoryReader.TryCopy(candidate.Address, bytes)) { unreadable++; continue; }
                    var comparison = Compare(current.Type, candidate.Bytes, bytes);
                    var matches = mode switch
                    {
                        "changed" => !candidate.Bytes.AsSpan().SequenceEqual(bytes),
                        "unchanged" => candidate.Bytes.AsSpan().SequenceEqual(bytes),
                        "increased" => comparison > 0,
                        "decreased" => comparison < 0,
                        "equal" => expected is not null && Matches(current.Type, bytes, expected),
                        _ => expected is not null && !Matches(current.Type, bytes, expected),
                    };
                    if (matches) result.Add(new(candidate.Address, bytes));
                }
                if (processed != current.Candidates.Count || unreadable != 0)
                    return new { status = "refinement_incomplete", searchID = sessionID, processed, unreadable, applied = false };
                current.Candidates = result;
                current.Until = Environment.TickCount64 + 60000;
            }
            else if (operation != "read") throw new ArgumentException("operation 必须为 start、read、refine 或 release。");
            return Read(sessionID, current, input);

            void Check()
            {
                token.ThrowIfCancellationRequested();
                if (!allowed()) throw new InvalidOperationException("内存搜索权限已关闭。");
            }
        }
    }

    private static object Read(Guid id, Search search, ToolArguments input)
    {
        var offset = input.Integer("offset", 0, 0, 16384);
        var limit = input.Integer("limit", 30, 1, 100);
        return new { status = "ok", searchID = id, type = search.Type, search.Complete, search.ScannedBytes, search.SkippedBytes,
            matched = search.Candidates.Count, expiresInMs = Math.Max(0, search.Until - Environment.TickCount64),
            candidates = search.Candidates.Skip(offset).Take(limit).Select(candidate => new
                { address = $"0x{candidate.Address:X}", hex = Convert.ToHexString(candidate.Bytes), value = Format(search.Type, candidate.Bytes) }).ToArray(),
            nextOffset = offset + limit < search.Candidates.Count ? (int?)offset + limit : null };
    }

    private static int Size(string type) => type switch
    {
        "u8" or "i8" => 1, "u16" or "i16" => 2, "u32" or "i32" or "f32" => 4, "u64" or "i64" or "f64" => 8,
        _ => throw new ArgumentException("仅支持整数和浮点标量类型。"),
    };
    private static void ValidateValue(string type, string value)
    {
        if (type is "f32" or "f64")
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || type == "f32" && !float.IsFinite((float)number))
                throw new ArgumentException("浮点筛选值必须是有限数。");
        }
        else _ = decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
    private static bool Matches(string type, ReadOnlySpan<byte> bytes, string value) => type is "f32" or "f64"
        ? Floating(type, bytes) == (type == "f32" ? (double)float.Parse(value, CultureInfo.InvariantCulture) : double.Parse(value, CultureInfo.InvariantCulture))
        : Number(type, bytes) == decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    private static int? Compare(string type, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        if (type is "f32" or "f64")
        {
            var first = Floating(type, before);
            var second = Floating(type, after);
            return double.IsFinite(first) && double.IsFinite(second) ? second.CompareTo(first) : null;
        }
        var a = Number(type, before);
        var b = Number(type, after);
        return a.HasValue && b.HasValue ? b.Value.CompareTo(a.Value) : null;
    }
    private static double Floating(string type, ReadOnlySpan<byte> bytes) => type == "f32"
        ? BinaryPrimitives.ReadSingleLittleEndian(bytes) : BinaryPrimitives.ReadDoubleLittleEndian(bytes);
    private static string? Format(string type, ReadOnlySpan<byte> bytes) => type is "f32" or "f64"
        ? Floating(type, bytes).ToString("R", CultureInfo.InvariantCulture) : Number(type, bytes)?.ToString(CultureInfo.InvariantCulture);
    private static decimal? Number(string type, ReadOnlySpan<byte> data)
    {
        try
        {
            return type switch
            {
                "u8" => data[0], "i8" => unchecked((sbyte)data[0]),
                "u16" => BinaryPrimitives.ReadUInt16LittleEndian(data), "i16" => BinaryPrimitives.ReadInt16LittleEndian(data),
                "u32" => BinaryPrimitives.ReadUInt32LittleEndian(data), "i32" => BinaryPrimitives.ReadInt32LittleEndian(data),
                "u64" => BinaryPrimitives.ReadUInt64LittleEndian(data), "i64" => BinaryPrimitives.ReadInt64LittleEndian(data),
                _ => null,
            };
        }
        catch (OverflowException) { return null; }
    }

    private sealed class Search(string type, int size)
    {
        public readonly string Type = type;
        public readonly int Size = size;
        public List<Candidate> Candidates = [];
        public long Until = Environment.TickCount64 + 60000;
        public int ScannedBytes;
        public int SkippedBytes;
        public bool Complete;
    }
    private sealed record Candidate(nint Address, byte[] Bytes);
}
