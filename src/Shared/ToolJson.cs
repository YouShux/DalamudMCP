using System.Text.Json;
using System.Text.Json.Serialization;

namespace DalamudMCP.Shared;

public static class ToolJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static JsonElement Success<T>(T value) => Element(new
    {
        content = new[] { new { type = "text", text = JsonSerializer.Serialize(value, Options) } },
        structuredContent = new { data = value },
        isError = false,
    });

    public static JsonElement Error(string message) => Element(new
    {
        content = new[] { new { type = "text", text = message } },
        isError = true,
    });
}
