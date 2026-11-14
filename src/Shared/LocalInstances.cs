using System.Diagnostics;
using System.Text.Json;

namespace DalamudMCP.Shared;

public sealed record LocalInstance(string InstanceID, int GameProcessID, int GatewayProcessID,
    long GameStartedUtcTicks, long GatewayStartedUtcTicks, int Port)
{
    public string Endpoint => $"http://127.0.0.1:{Port}/mcp";
}

public static class LocalInstances
{
    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DalamudMCP", "instances");

    public static IEnumerable<int> CandidatePorts(int preferred)
    {
        if (preferred is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(preferred));
        for (var offset = 0; offset < 64; offset++) yield return 1024 + (preferred - 1024 + offset) % (65536 - 1024);
    }

    public static string Register(BridgeSettings settings, int port)
    {
        if (!Guid.TryParseExact(settings.InstanceID, "N", out _) || settings.GameProcessID <= 0)
            throw new InvalidOperationException("游戏实例身份无效。");
        using var game = Process.GetProcessById(settings.GameProcessID);
        using var gateway = Process.GetCurrentProcess();
        var instance = new LocalInstance(settings.InstanceID, game.Id, gateway.Id,
            game.StartTime.ToUniversalTime().Ticks, gateway.StartTime.ToUniversalTime().Ticks, port);
        Directory.CreateDirectory(DirectoryPath);
        var file = Path.Combine(DirectoryPath, settings.InstanceID + ".json");
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(instance, ToolJson.Options));
        File.Move(temporary, file, overwrite: true);
        return file;
    }

    public static LocalInstance[] List()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        var result = new List<LocalInstance>();
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json").Take(256))
        {
            try
            {
                if (new FileInfo(file).Length > 4096) continue;
                var item = JsonSerializer.Deserialize<LocalInstance>(File.ReadAllText(file), ToolJson.Options);
                if (item is null || !Guid.TryParseExact(item.InstanceID, "N", out _) || item.Port is < 1024 or > 65535)
                    continue;
                using var game = Process.GetProcessById(item.GameProcessID);
                using var gateway = Process.GetProcessById(item.GatewayProcessID);
                if (game.StartTime.ToUniversalTime().Ticks == item.GameStartedUtcTicks &&
                    gateway.StartTime.ToUniversalTime().Ticks == item.GatewayStartedUtcTicks &&
                    !game.HasExited && !gateway.HasExited) result.Add(item);
            }
            catch (Exception error) when (error is IOException or JsonException or ArgumentException or
                InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return result.OrderBy(item => item.Port).ToArray();
    }
}
