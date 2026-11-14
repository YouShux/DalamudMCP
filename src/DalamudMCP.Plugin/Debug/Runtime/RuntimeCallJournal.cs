using System.Diagnostics;

namespace DalamudMCP.Plugin.Tools;

internal sealed class RuntimeCallJournal
{
    private readonly object sync = new();
    private readonly Entry?[] entries = new Entry?[100];
    private readonly Guid instance = Guid.NewGuid();
    private long sequence;
    private int frameworkThread;

    public void ObserveFrameworkThread() => Volatile.Write(ref frameworkThread, Environment.CurrentManagedThreadId);

    public void Record(string tool, long started, string status)
    {
        lock (sync)
        {
            var index = (int)(sequence % entries.Length);
            entries[index] = new(++sequence, tool, DateTimeOffset.UtcNow, Environment.TickCount64 - started, status);
        }
    }

    public object Read(ToolArguments input)
    {
        var after = input.Has("after_sequence") ? input.Element("after_sequence").GetInt64() : 0;
        if (after < 0) throw new ArgumentException("序号不能为负数。");
        var limit = input.Integer("limit", 30, 1, 100);
        lock (sync)
        {
            if (input.Has("stream_id") && (!Guid.TryParse(input.Text("stream_id"), out var expected) || expected != instance))
                return new { status = "stream_changed", streamID = instance };
            var earliest = Math.Max(1, sequence - entries.Length + 1);
            var first = Math.Max(after, earliest - 1);
            var count = first < sequence ? (int)Math.Min(limit, sequence - first) : 0;
            var page = count == 0 ? [] : new Entry[count];
            if (count > 0)
            {
                var index = (int)(first % entries.Length);
                var tail = Math.Min(count, entries.Length - index);
                Array.Copy(entries, index, page, 0, tail);
                if (tail < count) Array.Copy(entries, 0, page, tail, count - tail);
            }
            return new { status = after > 0 && after < earliest - 1 ? "history_truncated" : "ok",
                streamID = instance, records = page, afterSequence = count > 0 ? first + count : after, latestSequence = sequence };
        }
    }

    public object Health()
    {
        using var process = Process.GetCurrentProcess();
        var threads = new List<int>();
        foreach (ProcessThread thread in process.Threads)
        {
            using (thread) threads.Add(thread.Id);
        }
        return new { status = "ok", instanceID = instance, processID = Environment.ProcessId,
            frameworkManagedThreadID = Volatile.Read(ref frameworkThread), currentManagedThreadID = Environment.CurrentManagedThreadId,
            osThreadIDs = threads.Take(512).ToArray(), threadsTruncated = threads.Count > 512, processSuspended = false };
    }

    private sealed record Entry(long Sequence, string Tool, DateTimeOffset CompletedAt, long ElapsedMs, string Status);
}
