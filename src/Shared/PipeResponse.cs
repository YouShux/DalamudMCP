using System.Text.Json;

namespace DalamudMCP.Shared;

public sealed record PipeResponse(JsonElement? Result = null, string? Error = null);
