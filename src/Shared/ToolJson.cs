using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace DalamudMCP.Shared;

public static class ToolJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ResultOptions = new(Options) { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static JsonElement Success<T>(T value, bool structuredOnly)
    {
        var data = JsonSerializer.SerializeToElement(value, ResultOptions);
        return Element(new
        {
            content = structuredOnly ? [] : new[] { new { type = "text", text = data.GetRawText() } },
            structuredContent = new { data },
            isError = false,
        });
    }

    public static JsonElement Error(string message) => Element(new
    {
        content = new[] { new { type = "text", text = message } },
        isError = true,
    });
}
