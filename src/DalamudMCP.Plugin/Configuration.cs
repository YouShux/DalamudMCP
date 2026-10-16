using Dalamud.Configuration;

namespace DalamudMCP.Plugin;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public int HTTPPort { get; set; } = 61016;
    public int NativePort { get; set; } = 39217;
    public bool AutoStart { get; set; } = true;
    public bool AllowActions { get; set; }
    public bool AllowUnsafe { get; set; }
    public HashSet<string> DisabledTools { get; set; } = [];
}
