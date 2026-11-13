using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Ipc;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class PluginIntegration
{
    internal const string MESSAGES_TOOL = "read_plugin_messages";
    private readonly PluginMessageHistory messages = new();
    private ICallGateProvider<string, string, string, bool>? messagePublisher;

    internal void StartMessages()
    {
        messagePublisher = services.PluginInterface.GetIpcProvider<string, string, string, bool>("DalamudMCP.Messages.Publish");
        messagePublisher.RegisterFunc(messages.Publish);
    }

    internal object ReadMessages(ToolArguments input) => messages.Read(input);
}

internal sealed class PluginMessageHistory(int capacity = 2000, int byteBudget = 8 * 1024 * 1024)
{
    private readonly object sync = new();
    private readonly Queue<Message> history = new();
    private readonly Guid streamID = Guid.NewGuid();
    private long sequence;
    private long dropped;
    private int bytes;
    private bool closed;

    internal bool Publish(string source, string key, string payload)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 128 || string.IsNullOrWhiteSpace(key) || key.Length > 128 ||
            payload is null or { Length: > 16384 }) return false;
        var size = Encoding.UTF8.GetByteCount(payload) + Encoding.UTF8.GetByteCount(source) + Encoding.UTF8.GetByteCount(key);
        if (size > 16384 || size > byteBudget) return false;
        JsonElement value;
        try
        {
            using var json = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
            value = json.RootElement.Clone();
        }
        catch (JsonException) { return false; }
        lock (sync)
        {
            if (closed) return false;
            while (history.Count >= capacity || bytes + size > byteBudget)
            {
                bytes -= history.Dequeue().Size;
                dropped++;
            }
            history.Enqueue(new(++sequence, DateTimeOffset.UtcNow, Environment.TickCount64, source, key, value, size));
            bytes += size;
            return true;
        }
    }

    internal object Read(ToolArguments input)
    {
        Guid? requested = input.Has("stream_id") ? RepositoryPackages.ParseID(input.Text("stream_id")) : null;
        var after = input.Has("after_sequence") ? input.Element("after_sequence").GetInt64() : 0;
        if (after < 0 || after > 0 && requested is null) throw new ArgumentException("after_sequence 需要有效的 stream_id。");
        var source = input.Text("source", "");
        var key = input.Text("key", "");
        if (source.Length > 128 || key.Length > 128) throw new ArgumentException("source 和 key 最多 128 个字符。");
        var limit = input.Integer("limit", 50, 1, 100);
        lock (sync)
        {
            var first = history.TryPeek(out var oldest) ? oldest.Sequence : sequence + 1;
            var invalid = requested is { } id && id != streamID || after > sequence;
            if (invalid || closed)
                return new { status = closed ? "unavailable" : "cursor_reset", streamID, firstAvailableSequence = first, nextSequence = first - 1,
                    droppedBeforeCursor = 0L, dropped, publisherVerified = false, messages = Array.Empty<object>() };
            var page = history.Where(row => row.Sequence > after && (source.Length == 0 || row.Source == source) &&
                (key.Length == 0 || row.Key == key)).Take(limit + 1).ToArray();
            return new
            {
                status = "ok", streamID, firstAvailableSequence = first,
                nextSequence = page.Length > limit ? page[limit - 1].Sequence : sequence,
                droppedBeforeCursor = Math.Max(0, first - 1 - after), dropped, publisherVerified = false,
                messages = page.Take(limit).Select(row => new
                {
                    row.Sequence, row.ReceivedUtc, row.MonotonicMs, row.Source, row.Key, row.Payload, publisherVerified = false,
                }).ToArray(),
            };
        }
    }

    internal void Close()
    {
        lock (sync)
        {
            closed = true;
            history.Clear();
            bytes = 0;
        }
    }

    private sealed record Message(long Sequence, DateTimeOffset ReceivedUtc, long MonotonicMs, string Source, string Key, JsonElement Payload, int Size);
}
