namespace DalamudMCP.Shared;

public sealed record GatewayStatus(bool NativeConnected, string? NativeError, ToolDefinition[] Tools,
    int GameProcessID = 0, string InstanceID = "");
