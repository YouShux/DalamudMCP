using System.Text.Json;

namespace DalamudMCP.Shared;

public sealed record GameToolRoute(JsonElement? Result = null, BridgeSettings? NativeSettings = null);
