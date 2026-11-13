using System.Reflection;
using Dalamud.Plugin;
using Serilog.Events;

namespace DalamudMCP.Plugin.Tools;

internal sealed class DalamudLog : IDisposable
{
    private readonly object gate = new();
    private readonly Queue<Entry> entries = new();
    private readonly string streamID = Guid.NewGuid().ToString("N");
    private readonly object? sink;
    private readonly EventInfo? logLine;
    private readonly string? unavailable;
    private long lastID;
    private bool disposed;

    public DalamudLog()
    {
        try
        {
            var type = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Logging.Internal.SerilogEventSink");
            var target = type?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var signal = type?.GetEvent("LogLine");
            if (target is null || signal?.EventHandlerType != typeof(EventHandler<(string, LogEvent)>))
            {
                unavailable = "当前卫月版本不支持读取运行日志。";
                return;
            }

            signal.AddEventHandler(target, (EventHandler<(string, LogEvent)>)OnLogLine);
            sink = target;
            logLine = signal;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or MemberAccessException or TargetInvocationException)
        {
            unavailable = "无法订阅卫月运行日志。";
        }
    }

    public object Read(ToolArguments arguments)
    {
        if (unavailable is not null) throw new InvalidOperationException(unavailable);
        var count = arguments.Integer("max_count", 50, 1, 100);
        var afterID = arguments.Has("after_id") ? arguments.Element("after_id").GetInt64() : 0;
        if (afterID < 0) throw new ArgumentException("after_id 必须为非负整数。");
        if (afterID != 0 && !arguments.Has("stream_id"))
            throw new ArgumentException("继续读取日志时请提供上次返回的 streamId。");
        var expectedStream = arguments.Has("stream_id") ? arguments.Text("stream_id") : null;
        var source = arguments.Has("source") ? arguments.Text("source") : null;
        var contains = arguments.Has("contains") ? arguments.Text("contains") : null;
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(DalamudLog));
            if (expectedStream is not null && expectedStream != streamID)
                return new { status = "stream_changed", streamId = streamID, nextAfterId = 0L, entries = Array.Empty<Entry>() };
            if (afterID > lastID) throw new ArgumentException("after_id 超过当前日志游标。");
            var gap = afterID != 0 && entries.TryPeek(out var first) && first.Id > afterID + 1;
            var matches = entries.Where(entry => entry.Id > afterID
                && (source is null || entry.Source.Contains(source, StringComparison.OrdinalIgnoreCase))
                && (contains is null || entry.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)));
            var page = (afterID == 0 ? matches.TakeLast(count) : matches.Take(count + 1)).ToArray();
            var hasMore = page.Length > count;
            if (hasMore) page = page[..count];
            return new
            {
                status = "snapshot",
                streamId = streamID,
                nextAfterId = hasMore ? page[^1].Id : lastID,
                hasMore,
                gap,
                entries = page,
            };
        }
    }

    private void OnLogLine(object? sender, (string Line, LogEvent Event) item)
    {
        var source = item.Event.Properties.TryGetValue("SourceContext", out var value)
            && value is ScalarValue { Value: string context } ? context : string.Empty;
        lock (gate)
        {
            if (disposed) return;
            if (entries.Count == CAPACITY) entries.Dequeue();
            entries.Enqueue(new(++lastID, item.Event.Timestamp, item.Event.Level.ToString(),
                source.Length > 256 ? source[..256] : source,
                item.Line.Length > MAX_MESSAGE_LENGTH ? item.Line[..MAX_MESSAGE_LENGTH] : item.Line,
                item.Line.Length > MAX_MESSAGE_LENGTH));
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            entries.Clear();
        }
        logLine?.RemoveEventHandler(sink, (EventHandler<(string, LogEvent)>)OnLogLine);
    }

    private sealed record Entry(long Id, DateTimeOffset Timestamp, string Level, string Source, string Message, bool Truncated);

    private const int CAPACITY = 256;
    private const int MAX_MESSAGE_LENGTH = 2_048;
}
