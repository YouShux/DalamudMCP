using System.Globalization;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;

namespace DalamudMCP.Plugin.Tools;

internal sealed class ChatHistory : IDisposable
{
    private const int CAPACITY = 1000;
    private readonly GameServices services;
    private readonly object gate = new();
    private readonly Entry?[] entries = new Entry?[CAPACITY];
    private long sequence;
    private int count;
    private int nextIndex;
    private bool disposed;
    internal long Sequence => Volatile.Read(ref sequence);

    internal bool HasBattleSystemText(long after, string exactText)
    {
        lock (gate)
        {
            var index = nextIndex;
            for (var i = 0; i < count; i++)
            {
                if (--index < 0) index = entries.Length - 1;
                if (entries[index] is not { } entry) break;
                if (entry.Sequence <= after) break;
                if (entry.Type is XivChatType.SystemMessage or XivChatType.Notice && entry.Sender.Length == 0 &&
                    entry.Message.Equals(exactText, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }

    public ChatHistory(GameServices services)
    {
        this.services = services;
        services.ChatGui.ChatMessage += OnChat;
    }

    public object Read(ToolArguments arguments)
    {
        HashSet<XivChatType>? channels = null;
        if (arguments.Has("channels"))
        {
            var value = arguments.Element("channels");
            var values = value.EnumerateArray();
            if (value.GetArrayLength() > 100)
                throw new ArgumentException("channels 最多支持 100 个频道。");
            foreach (var item in values)
            {
                if (!Enum.TryParse<XivChatType>(item.GetString(), true, out var type) || !Enum.IsDefined(type))
                    throw new ArgumentException("未知聊天频道。");
                (channels ??= []).Add(type);
            }
        }
        var since = arguments.Has("since") ? DateTimeOffset.Parse(arguments.Text("since"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : DateTimeOffset.MinValue;
        var max = arguments.Integer("max_count", 100, 1, CAPACITY);
        lock (gate)
        {
            List<object>? result = null;
            var index = nextIndex;
            for (var i = 0; i < count; i++)
            {
                if (--index < 0) index = entries.Length - 1;
                if (entries[index] is not { } entry) break;
                if (entry.Timestamp < since || channels is not null && !channels.Contains(entry.Type))
                    continue;
                (result ??= new(Math.Min(count, max))).Add(new { timestamp = entry.Timestamp, channel = entry.Type.ToString(),
                    channelID = (int)entry.Type, sender = entry.Sender, message = entry.Message });
                if (result.Count == max) break;
            }
            return (IReadOnlyList<object>?)result ?? Array.Empty<object>();
        }
    }

    public void Dispose()
    {
        services.ChatGui.ChatMessage -= OnChat;
        lock (gate)
        {
            disposed = true;
            Array.Clear(entries);
            count = 0;
        }
    }

    private void OnChat(IHandleableChatMessage message) =>
        Append(DateTimeOffset.UtcNow, message.LogKind, message.Sender?.TextValue ?? string.Empty, message.Message?.TextValue ?? string.Empty);

    private void Append(DateTimeOffset timestamp, XivChatType type, string sender, string message)
    {
        lock (gate)
        {
            if (disposed) return;
            var nextSequence = sequence + 1;
            entries[nextIndex] = new(nextSequence, timestamp, type, sender, message);
            if (++nextIndex == entries.Length) nextIndex = 0;
            if (count < entries.Length) count++;
            Volatile.Write(ref sequence, nextSequence);
        }
    }

    private sealed record Entry(long Sequence, DateTimeOffset Timestamp, XivChatType Type, string Sender, string Message);
}
