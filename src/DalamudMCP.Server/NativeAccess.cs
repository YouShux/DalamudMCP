using System.Text.Json;
using DalamudMCP.Shared;
using ModelContextProtocol.Protocol;

namespace DalamudMCP.Server;

internal static class NativeAccess
{
    public static bool AllowsControlAction(string? action) => action is "threads" or "state" or "resume";

    public static ToolAccess GetAccess(Tool tool) => tool.Name switch
    {
        "input" => ToolAccess.Action,
        "ffcs_call" or "mem_write" or "control" or "observe" or "object_access" => ToolAccess.Unsafe,
        _ => tool.Annotations?.ReadOnlyHint == true ? ToolAccess.Read : ToolAccess.Unsafe,
    };

    public static ToolAccess GetAccess(Tool tool, IDictionary<string, JsonElement>? arguments)
    {
        if (arguments?.TryGetValue("action", out var value) == true && value.ValueKind == JsonValueKind.String)
        {
            var action = value.GetString();
            if (tool.Name == "object_access" && action is "read" or "list")
            {
                return ToolAccess.Read;
            }

            if (tool.Name == "observe" && action == "list")
            {
                return ToolAccess.Read;
            }

            if (tool.Name == "control" && action is "threads" or "state")
            {
                return ToolAccess.Read;
            }
        }

        return GetAccess(tool);
    }

    public static bool HasReadActions(Tool tool) => tool.Name is "object_access" or "observe" or "control";

    public static string GetReadActions(Tool tool) => tool.Name switch
    {
        "object_access" => "read、list",
        "observe" => "list",
        "control" => "threads、state",
        _ => string.Empty,
    };

    public static bool IsAllowed(ToolAccess access, BridgeSettings settings) => access switch
    {
        ToolAccess.Read => true,
        ToolAccess.Action => settings.AllowActions,
        ToolAccess.Unsafe => settings.AllowUnsafe,
        _ => false,
    };
}
