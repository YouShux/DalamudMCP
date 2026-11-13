using System.Text;
using System.Text.Json;

namespace DalamudMCP.Plugin.Tools;

internal sealed class PluginStateSnapshots(TimeProvider clock)
{
    private readonly object sync = new();
    private readonly Dictionary<(Guid PluginID, string Name), Entry> entries = [];
    private bool closed;

    public bool Publish(Guid pluginID, string? name, string? json, Func<bool> stillLoaded)
    {
        if (pluginID == Guid.Empty || name is null || !IsValidName(name) || json is null || json.Length > MAX_JSON_BYTES ||
            Encoding.UTF8.GetByteCount(json) > MAX_JSON_BYTES)
            return false;
        JsonElement value;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
            value = document.RootElement.Clone();
        }
        catch (JsonException) { return false; }

        var key = (pluginID, name);
        lock (sync)
        {
            if (closed || !stillLoaded()) return false;
            var now = clock.GetTimestamp();
            RemoveExpired(now);
            if (!entries.ContainsKey(key) && (entries.Count >= MAX_ENTRIES ||
                entries.Keys.Count(candidate => candidate.PluginID == pluginID) >= MAX_PER_PLUGIN))
                return false;
            entries[key] = new(name, clock.GetUtcNow(), value, now);
            return true;
        }
    }

    public Entry[] Read(Guid pluginID)
    {
        lock (sync)
        {
            RemoveExpired(clock.GetTimestamp());
            return entries.Where(pair => pair.Key.PluginID == pluginID).Select(pair => pair.Value)
                .OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
        }
    }

    public void Clear(Guid pluginID)
    {
        lock (sync)
        {
            foreach (var key in entries.Keys.Where(key => key.PluginID == pluginID).ToArray())
                entries.Remove(key);
        }
    }

    public void Close()
    {
        lock (sync)
        {
            closed = true;
            entries.Clear();
        }
    }

    public static bool IsValidName(string? name) => name is { Length: > 0 and <= 64 } &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private void RemoveExpired(long now)
    {
        foreach (var key in entries.Where(pair => clock.GetElapsedTime(pair.Value.Timestamp, now) >= Lifetime)
                     .Select(pair => pair.Key).ToArray())
            entries.Remove(key);
    }

    internal sealed record Entry(string Name, DateTimeOffset PublishedAt, JsonElement Value, long Timestamp);

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private const int MAX_JSON_BYTES = 16 * 1024;
    private const int MAX_PER_PLUGIN = 8;
    private const int MAX_ENTRIES = 128;
}
