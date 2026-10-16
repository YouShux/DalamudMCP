using System.Globalization;
using System.Text.Json;

namespace DalamudMCP.Plugin.Tools;

internal readonly struct ToolArguments(JsonElement value)
{
    public bool Has(string name) => Find(name, out _);

    public string Text(string name, string? fallback = null)
    {
        if (!Find(name, out var item))
            return fallback ?? throw new ArgumentException($"缺少参数 {name}。");
        var text = item.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096)
            throw new ArgumentException($"{name} 必须是 1 至 4096 个字符。");
        return text.Trim();
    }

    public int Integer(string name, int fallback, int min, int max)
    {
        var number = Find(name, out var item) ? item.GetInt32() : fallback;
        return number >= min && number <= max ? number : throw new ArgumentOutOfRangeException(name, $"{name} 范围为 {min} 至 {max}。");
    }

    public double Number(string name, double fallback, double min, double max)
    {
        var number = Find(name, out var item) ? item.GetDouble() : fallback;
        return double.IsFinite(number) && number >= min && number <= max ? number : throw new ArgumentOutOfRangeException(name);
    }

    public bool Boolean(string name, bool fallback = false) => Find(name, out var item) ? item.GetBoolean() : fallback;

    public JsonElement Element(string name) => Find(name, out var item) ? item : throw new ArgumentException($"缺少参数 {name}。");

    public ulong ObjectID(string name)
    {
        var text = Text(name);
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ulong.Parse(text, CultureInfo.InvariantCulture);
    }

    public static string Normalize(string value)
    {
        var normalized = value.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
        return normalized == "addon" ? "addonname" : normalized;
    }

    private bool Find(string name, out JsonElement result)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (Normalize(property.Name) != Normalize(name))
                continue;
            result = property.Value;
            return true;
        }
        result = default;
        return false;
    }
}
