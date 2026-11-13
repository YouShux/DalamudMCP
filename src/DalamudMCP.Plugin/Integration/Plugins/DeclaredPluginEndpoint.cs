using System.Text;
using System.Text.Json;

namespace DalamudMCP.Plugin;

public sealed record DeclaredPluginEndpoint(string Alias, string PluginName, string Callgate,
    bool DevelopmentOnly, string[] ArgumentKinds, string ReturnKind)
{
    internal static readonly string[] Kinds = ["boolean", "integer", "number", "string", "nullable_boolean"];
    internal static readonly string[] ReturnKinds = [.. Kinds, "void"];
    internal const int MAX_ARGUMENTS = 4;

    internal static Type KindType(string kind) => kind switch
    {
        "boolean" => typeof(bool),
        "nullable_boolean" => typeof(bool?),
        "integer" => typeof(int),
        "number" => typeof(double),
        "string" => typeof(string),
        "void" => typeof(void),
        _ => throw new ArgumentException("不支持的 IPC 类型。"),
    };

    internal void Validate()
    {
        ValidateName(Alias, 48, "alias");
        ValidateName(PluginName, 128, "plugin_name");
        ValidateName(Callgate, 192, "callgate");
        if (ArgumentKinds is null || ArgumentKinds.Length > MAX_ARGUMENTS ||
            ArgumentKinds.Any(kind => !Kinds.Contains(kind, StringComparer.Ordinal)) ||
            !ReturnKinds.Contains(ReturnKind, StringComparer.Ordinal))
            throw new ArgumentException("IPC 签名无效。");
    }

    internal static void ValidateName(string value, int max, string field)
    {
        if (string.IsNullOrEmpty(value) || value.Length > max ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new ArgumentException($"{field} 只能使用字母、数字、点、连字符和下划线。");
    }

    internal static object? ParseArgument(string kind, JsonElement value) => kind switch
    {
        "boolean" or "nullable_boolean" when value.ValueKind is JsonValueKind.True or JsonValueKind.False => value.GetBoolean(),
        "nullable_boolean" when value.ValueKind == JsonValueKind.Null => null,
        "integer" when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var integer) => integer,
        "number" when value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) => number,
        "string" when value.ValueKind == JsonValueKind.String && value.GetString() is { } str &&
            str.Length <= 2048 && !str.Contains('\0') && Encoding.UTF8.GetByteCount(str) <= 4096 => str,
        _ => throw new ArgumentException($"IPC 实参与声明类型 {kind} 不匹配或超过限制。"),
    };
}
