using System.Collections.Concurrent;
using System.Globalization;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;

namespace DalamudMCP.Plugin.Tools;

internal sealed class ChatHistory : IDisposable
{
    private readonly GameServices services;
    private readonly ConcurrentQueue<Entry> entries = new();

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
            var values = arguments.Element("channels").EnumerateArray().ToArray();
            if (values.Length > 100)
                throw new ArgumentException("channels 最多支持 100 个频道。");
            channels = values.Select(value => Enum.TryParse<XivChatType>(value.GetString(), true, out var type) && Enum.IsDefined(type) ? type : throw new ArgumentException("未知聊天频道。")).ToHashSet();
        }
        var since = arguments.Has("since") ? DateTimeOffset.Parse(arguments.Text("since"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : DateTimeOffset.MinValue;
        var max = arguments.Integer("max_count", 100, 1, 1000);
        return entries.ToArray().Reverse().Where(entry => entry.Timestamp >= since && (channels is not { Count: > 0 } || channels.Contains(entry.Type)))
            .Take(max).Select(entry => new { timestamp = entry.Timestamp, channel = entry.Type.ToString(), channelID = (int)entry.Type, sender = entry.Sender, message = entry.Message }).ToArray();
    }

    public void Dispose()
    {
        services.ChatGui.ChatMessage -= OnChat;
        entries.Clear();
    }

    private void OnChat(IHandleableChatMessage message)
    {
        entries.Enqueue(new(DateTimeOffset.UtcNow, message.LogKind, message.Sender?.TextValue ?? string.Empty, message.Message?.TextValue ?? string.Empty));
        while (entries.Count > 1000)
            entries.TryDequeue(out _);
    }

    private sealed record Entry(DateTimeOffset Timestamp, XivChatType Type, string Sender, string Message);
}
