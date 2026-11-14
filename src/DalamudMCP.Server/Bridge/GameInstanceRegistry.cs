using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using DalamudMCP.Shared;

namespace DalamudMCP.Server;

internal static class GameInstanceRegistry
{
    public const string LIST_TOOL = "list_game_instances";
    public static ToolDefinition ListTool { get; } = new(LIST_TOOL, "本机游戏实例",
        "列出本机实例的角色、所属服务器、进程号和 MCP 地址。不会切换当前连接。",
        ToolJson.Element(new { type = "object", additionalProperties = false }), ToolAccess.Read);

    private static string DirectoryPath
    {
        get
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!Path.IsPathFullyQualified(profile)) throw new IOException("未找到当前用户的本地应用数据目录。");
            return Path.Combine(profile, "DalamudMCP", "instances");
        }
    }

    public static async Task<string?> RegisterAsync(GameBridge bridge, GatewayOptions options, int port,
        ILogger log, CancellationToken cancellationToken)
    {
        string? temporary = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            var settings = await bridge.SendAsync<BridgeSettings>(new("settings"), deadline.Token);
            if (settings.GameProcessID <= 0 || settings.InstanceID == Guid.Empty ||
                options.Pipe != $"DalamudMCP.{settings.GameProcessID}.{settings.InstanceID:N}" ||
                options.ParentID is { } parent && settings.GameProcessID != parent)
                throw new InvalidOperationException("游戏身份与网关配置不符。");

            using var game = Process.GetProcessById(settings.GameProcessID);
            using var gateway = Process.GetCurrentProcess();
            var entry = new Entry(settings.InstanceID, game.Id, gateway.Id, Started(game), Started(gateway), port);
            var directory = DirectoryPath;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName(entry));
            temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(entry, ToolJson.Options), deadline.Token);
            File.Move(temporary, path);
            temporary = null;
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                          InvalidOperationException or ArgumentException or Win32Exception or TimeoutException or OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested) log.LogWarning(exception, "无法登记游戏实例，可继续按 MCP 地址连接。");
            return null;
        }
        finally { if (temporary is not null) Remove(temporary, log); }
    }

    public static async Task<object> ListAsync(ILogger log, CancellationToken cancellationToken)
    {
        var directory = DirectoryPath;
        if (!Directory.Exists(directory)) return new { instances = Array.Empty<object>() };
        var entries = new List<Entry>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(path).Length > 4096) continue;
                var entry = JsonSerializer.Deserialize<Entry>(await File.ReadAllTextAsync(path, cancellationToken), ToolJson.Options);
                if (entry is null || entry.InstanceID == Guid.Empty || entry.GameProcessID <= 0 || entry.GatewayProcessID <= 0 ||
                    entry.GameStarted <= 0 || entry.GatewayStarted <= 0 || entry.Port is < 1024 or > 65535 ||
                    Path.GetFileName(path) != FileName(entry)) continue;
                if (IsAlive(entry.GameProcessID, entry.GameStarted) && IsAlive(entry.GatewayProcessID, entry.GatewayStarted))
                    entries.Add(entry);
                else
                    Remove(path, log);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or Win32Exception)
            {
                log.LogDebug(exception, "无法读取实例登记 {File}。", path);
            }
        }

        var instances = await Task.WhenAll(entries.OrderBy(entry => entry.Port)
            .Select(async entry =>
            {
                var character = await ReadCharacterAsync(entry, log, cancellationToken);
                return new
                {
                    entry.InstanceID,
                    processId = entry.GameProcessID,
                    entry.Port,
                    endpoint = $"http://127.0.0.1:{entry.Port}/mcp",
                    characterAvailable = character?.CharacterName is not null,
                    characterName = character?.CharacterName,
                    homeWorld = character?.HomeWorld,
                };
            }));
        return new { instances };
    }

    private static async Task<Character?> ReadCharacterAsync(Entry entry, ILogger log, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var pipeName = $"DalamudMCP.{entry.GameProcessID}.{entry.InstanceID:N}";
            var settings = await GameBridge.SendAsync<BridgeSettings>(pipeName, new("settings"), deadline.Token);
            if (settings.GameProcessID != entry.GameProcessID || settings.InstanceID != entry.InstanceID) return null;
            var result = await GameBridge.SendAsync<JsonElement>(pipeName,
                new("tools/call", "get_player_context", ToolJson.Element(new { }), StructuredOnly: true), deadline.Token);
            return result.TryGetProperty("structuredContent", out var content) && content.ValueKind == JsonValueKind.Object &&
                   content.TryGetProperty("data", out var data)
                       ? data.Deserialize<Character>(ToolJson.Options) : null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                                          exception is IOException or UnauthorizedAccessException or TimeoutException or
                                              InvalidOperationException or JsonException or OperationCanceledException)
        {
            log.LogDebug(exception, "暂时无法确认游戏进程 {ProcessId} 的角色。", entry.GameProcessID);
            return null;
        }
    }

    public static void Remove(string path, ILogger log)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(exception, "无法移除实例登记 {File}。", path);
        }
    }

    private static bool IsAlive(int id, long started)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited && Started(process) == started;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { return false; }
    }

    private static long Started(Process process) => process.StartTime.ToUniversalTime().Ticks;

    private static string FileName(Entry entry) => $"{entry.GatewayProcessID}-{entry.GatewayStarted}.json";

    private sealed record Entry(Guid InstanceID, int GameProcessID, int GatewayProcessID, long GameStarted, long GatewayStarted, int Port);
    private sealed record Character(string? CharacterName, string? HomeWorld);
}
