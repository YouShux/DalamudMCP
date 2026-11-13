namespace DalamudMCP.Plugin.Tools;

internal sealed class ClientStateJournal(int capacity = 256)
{
    internal const string TOOL = "poll_client_changes";
    internal static readonly string[] Kinds = ["login", "territory", "combat", "job", "target", "hp", "mp", "gp", "position", "focus", "target_hp", "level", "duty", "fates", "cast", "statuses", "dead"];
    private readonly Guid streamID = Guid.NewGuid();
    private readonly object gate = new();
    private readonly Queue<Change> changes = new(capacity);
    private TaskCompletionSource updated = NewSignal();
    private long sequence;
    private int waiting;
    private Frame? previous;

    internal void Observe(Frame frame)
    {
        if (previous is not { } before)
        {
            previous = frame;
            return;
        }
        if (before == frame) return;
        previous = frame;

        lock (gate)
        {
            var start = sequence;
            var now = DateTimeOffset.UtcNow;
            Append("login", before.LoggedIn, frame.LoggedIn);
            if (before.LoggedIn && frame.LoggedIn)
            {
                Append("territory", before.TerritoryID, frame.TerritoryID);
                Append("combat", before.InCombat, frame.InCombat);
                Append("job", before.JobID, frame.JobID);
                if (before.TargetID != frame.TargetID)
                    Append("target", DisplayID(before.TargetID), DisplayID(frame.TargetID));
                Append("hp", before.HP, frame.HP);
                Append("mp", before.MP, frame.MP);
                Append("gp", before.GP, frame.GP);
                Append("position", before.Position, frame.Position);
                Append("focus", DisplayID(before.FocusID), DisplayID(frame.FocusID));
                Append("target_hp", before.TargetHP, frame.TargetHP);
                Append("level", before.Level, frame.Level);
                Append("duty", before.DutyStarted, frame.DutyStarted);
                Append("fates", before.Fates, frame.Fates);
                Append("cast", before.CastActionID, frame.CastActionID);
                Append("statuses", before.Statuses, frame.Statuses);
                Append("dead", before.HP == 0, frame.HP == 0);
            }
            if (sequence == start) return;
            var signal = updated;
            updated = NewSignal();
            signal.SetResult();

            void Append<T>(string kind, T oldValue, T newValue)
            {
                if (EqualityComparer<T>.Default.Equals(oldValue, newValue)) return;
                if (changes.Count == capacity) changes.Dequeue();
                changes.Enqueue(new(++sequence, now, kind, oldValue, newValue));
            }
        }
    }

    internal async Task<object> ReadAsync(ToolArguments input, CancellationToken token)
    {
        var wait = input.Integer("wait_ms", 0, 0, 10000);
        var limit = input.Integer("limit", 50, 1, 100);
        var kinds = input.Has("kinds") ? ReadKinds(input) : null;
        var hasCursor = input.Has("stream_id") && input.Has("after_sequence");
        if (input.Has("stream_id") != input.Has("after_sequence") || (!hasCursor && wait != 0))
            throw new ArgumentException("续读或等待须同时提供 stream_id 和 after_sequence。");
        Guid? requestedStream = hasCursor
            ? Guid.ParseExact(input.Text("stream_id"), "D")
            : null;
        var after = hasCursor ? input.Element("after_sequence").GetInt64() : 0;
        if (after < 0) throw new ArgumentOutOfRangeException("after_sequence");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (wait > 0) deadline.CancelAfter(wait);
        var waitingSlot = false;
        var timedOut = false;

        try
        {
            while (true)
            {
                Task signal;
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    var earliest = changes.TryPeek(out var first) ? first.Sequence : sequence + 1;
                    if (requestedStream.HasValue && requestedStream != streamID)
                        return Result("stream_changed", sequence, earliest, []);
                    if (after > sequence) throw new ArgumentOutOfRangeException("after_sequence");
                    var missed = hasCursor && after < earliest - 1;
                    var matches = changes.Where(change => change.Sequence > after && (kinds is null || kinds.Contains(change.Kind)))
                        .Take(limit + 1).ToArray();
                    var more = matches.Length > limit;
                    var page = more ? matches[..limit] : matches;
                    var last = more ? page[^1].Sequence : sequence;
                    if (page.Length > 0 || missed || wait == 0 || timedOut)
                        return Result(missed ? "history_truncated" : timedOut && page.Length == 0 ? "timeout" : "ok", last, earliest, page, more);
                    if (!waitingSlot)
                    {
                        if (waiting == 4) return Result("busy", after, earliest, []);
                        waiting++;
                        waitingSlot = true;
                    }
                    signal = updated.Task;
                }
                try { await signal.WaitAsync(deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
                {
                    timedOut = true;
                }
            }
        }
        finally
        {
            if (waitingSlot)
                lock (gate) waiting--;
        }

        object Result(string status, long next, long earliest, Change[] items, bool hasMore = false) => new
        {
            status, streamID, afterSequence = next, earliestSequence = earliest, hasMore, items,
        };
    }

    private static HashSet<string> ReadKinds(ToolArguments input)
    {
        var values = input.Element("kinds");
        if (values.ValueKind != System.Text.Json.JsonValueKind.Array || values.GetArrayLength() < 1 || values.GetArrayLength() > Kinds.Length)
            throw new ArgumentException("kinds 须为非空的事件类型数组。");
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values.EnumerateArray())
        {
            var kind = value.GetString();
            if (kind is null || !Kinds.Contains(kind, StringComparer.Ordinal) || !result.Add(kind))
                throw new ArgumentException("kinds 有未知或重复的事件类型。");
        }
        return result;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string? DisplayID(ulong? id) => id is ulong value ? $"0x{value:X}" : null;

    internal readonly record struct Frame(bool LoggedIn, uint TerritoryID, bool InCombat, uint JobID, ulong? TargetID,
        uint? HP = null, uint? MP = null, uint? GP = null, string? Position = null, ulong? FocusID = null,
        uint? TargetHP = null, byte? Level = null, bool DutyStarted = false, string? Fates = null, uint? CastActionID = null, string? Statuses = null);
    private sealed record Change(long Sequence, DateTimeOffset Timestamp, string Kind, object? Before, object? After);
}
