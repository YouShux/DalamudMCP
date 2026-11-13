using System.Text.Json;
using DalamudMCP.Shared;
using ModelContextProtocol.Protocol;

namespace DalamudMCP.Server;

internal static class NativeAccess
{
    public static bool AllowsControlAction(string? action, bool reviewed)
        => action is "threads" or "state" || !reviewed && action == "resume";

    public static string? GetAction(IDictionary<string, JsonElement>? arguments)
        => arguments?.TryGetValue("action", out var value) == true && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    public static ToolAccess GetAccess(Tool tool, bool reviewed, string? action = null)
    {
        if (!reviewed) return ToolAccess.Unsafe;
        if (tool.Name == "input") return ToolAccess.Action;
        if (tool.Name is "service_list" or "ffcs_index" or "mem_read" or "data" or "capture" or "trace")
            return tool.Annotations?.ReadOnlyHint == true ? ToolAccess.Read : ToolAccess.Unsafe;
        return tool.Name switch
        {
            "object_access" when action is "types" or "describe" => ToolAccess.Read,
            "observe" when action == "list" => ToolAccess.Read,
            "control" when action is "threads" or "state" => ToolAccess.Read,
            _ => ToolAccess.Unsafe,
        };
    }

    public static string[] ReadActions(string name, bool reviewed) => !reviewed ? [] : name switch
    {
        "object_access" => ["types", "describe"],
        "observe" => ["list"],
        "control" => ["threads", "state"],
        _ => [],
    };

    public static bool IsAllowed(ToolAccess access, BridgeSettings settings) => access switch
    {
        ToolAccess.Read => true,
        ToolAccess.Action => settings.AllowActions,
        ToolAccess.Unsafe => settings.AllowUnsafe,
        _ => false,
    };
}
