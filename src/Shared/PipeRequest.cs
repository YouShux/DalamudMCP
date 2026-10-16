using System.Text.Json;
using System.Text.Json.Serialization;

namespace DalamudMCP.Shared;

public sealed record PipeRequest(string Method, string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Arguments = default);
