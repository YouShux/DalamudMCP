namespace DalamudMCP.Shared;

public sealed record GatewayStatus(bool NativeConnected, string? NativeError, ToolDefinition[] Tools);
