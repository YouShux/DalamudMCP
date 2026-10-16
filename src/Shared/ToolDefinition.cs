using System.Text.Json;

namespace DalamudMCP.Shared;

public sealed record ToolDefinition(
    string Name,
    string Title,
    string Description,
    JsonElement InputSchema,
    ToolAccess Access,
    string Source = "game");
