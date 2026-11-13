using System.Diagnostics;

namespace DalamudMCP.Plugin.Tools;

internal sealed class RuntimeCallJournal
{
    private readonly object sync = new();
    private readonly Queue<Entry> entries = new();
    private readonly Guid instance = Guid.NewGuid();
    private long sequence;
    private int frameworkThread;

    public void ObserveFrameworkThread() => Volatile.Write(ref frameworkThread, Environment.CurrentManagedThreadId);

    public void Record(string tool, long started, string status)
    {
        lock (sync)
        {
            if (entries.Count == 100) entries.Dequeue();
            entries.Enqueue(new(++sequence, tool, DateTimeOffset.UtcNow, Environment.TickCount64 - started, status));
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
            var page = entries.Where(entry => entry.Sequence > after).Take(limit).ToArray();
            return new { status = after > 0 && entries.TryPeek(out var first) && after < first.Sequence - 1 ? "history_truncated" : "ok",
                streamID = instance, records = page, afterSequence = page.LastOrDefault()?.Sequence ?? after, latestSequence = sequence };
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
