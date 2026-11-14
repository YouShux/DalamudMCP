namespace DalamudMCP.Shared;

public sealed record GatewayStatus(bool NativeConnected, string? NativeError, ToolDefinition[] Tools,
    int GameProcessID, Guid InstanceID, int? NativePort = null);
