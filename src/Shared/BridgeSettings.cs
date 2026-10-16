namespace DalamudMCP.Shared;

public sealed record BridgeSettings(
    int NativePort,
    bool AllowActions,
    bool AllowUnsafe,
    string[] DisabledTools);
