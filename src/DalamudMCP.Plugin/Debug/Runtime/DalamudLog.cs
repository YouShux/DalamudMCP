using System.Reflection;
using Dalamud.Plugin;
using Serilog.Events;

namespace DalamudMCP.Plugin.Tools;

internal sealed class DalamudLog : IDisposable
{
    private readonly object gate = new();
    private readonly Entry?[] entries = new Entry?[CAPACITY];
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
            var firstID = Math.Max(1, lastID - CAPACITY + 1);
            var gap = afterID != 0 && afterID < firstID - 1;
            var recent = afterID == 0;
            var start = recent ? lastID : Math.Max(firstID, afterID + 1);
            var step = recent ? -1 : 1;
            List<Entry>? page = null;
            var hasMore = false;
            for (var id = start; id >= firstID && id <= lastID; id += step)
            {
                var entry = entries[(int)(id % CAPACITY)]!;
                if ((source is not null && !entry.Source.Contains(source, StringComparison.OrdinalIgnoreCase)) ||
                    (contains is not null && !entry.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (page?.Count == count)
                {
                    hasMore = true;
                    break;
                }
                (page ??= new(Math.Min(count, (int)(recent ? lastID - firstID + 1 : lastID - start + 1)))).Add(entry);
                if (recent && page.Count == count) break;
            }
            if (recent) page?.Reverse();
            return new
            {
                status = "snapshot",
                streamId = streamID,
                nextAfterId = hasMore ? page![^1].Id : lastID,
                hasMore,
                gap,
                entries = (IReadOnlyList<Entry>?)page ?? Array.Empty<Entry>(),
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
            var id = ++lastID;
            entries[(int)(id % CAPACITY)] = new(id, item.Event.Timestamp, item.Event.Level.ToString(),
                source.Length > 256 ? source[..256] : source,
                item.Line.Length > MAX_MESSAGE_LENGTH ? item.Line[..MAX_MESSAGE_LENGTH] : item.Line,
                item.Line.Length > MAX_MESSAGE_LENGTH);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            Array.Clear(entries);
        }
        logLine?.RemoveEventHandler(sink, (EventHandler<(string, LogEvent)>)OnLogLine);
    }

    private sealed record Entry(long Id, DateTimeOffset Timestamp, string Level, string Source, string Message, bool Truncated);

    private const int CAPACITY = 256;
    private const int MAX_MESSAGE_LENGTH = 2_048;
}
