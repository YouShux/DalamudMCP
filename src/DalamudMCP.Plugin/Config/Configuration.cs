using Dalamud.Configuration;

namespace DalamudMCP.Plugin;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public int HTTPPort { get; set; } = 61016;
    public int NativePort { get; set; } = 39217;
    public bool AutoStart { get; set; } = true;
    public bool CheckOpcodesOnLoad { get; set; } = true;
    public bool CheckPacketSourcesOnLoad { get; set; } = true;
    public bool RecordCombatContinuously { get; set; }
    public string CombatRecordingScope { get; set; } = "nearby";
    public int CombatEventLimit { get; set; } = 20000;
    public bool AllowActions { get; set; }
    public bool AllowExtendedControlEditing { get; set; }
    public int CombatArchiveLimit { get; set; } = 96;
    public int CombatArchiveMiB { get; set; } = 24;
    public int NavigationRange { get; set; } = 250;
    public bool AllowUnsafe { get; set; }
    public HashSet<string> DisabledTools { get; set; } = [];
    public List<DeclaredPluginEndpoint> SavedEndpoints { get; set; } = [];
}
