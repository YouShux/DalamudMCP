using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DalamudMCP.Shared;

namespace DalamudMCP.Plugin;

public sealed class SettingsStore
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly object sync = new();
    private BridgeSettings snapshot;

    public SettingsStore(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        Current = Load(pluginInterface, log);
        snapshot = CreateSnapshot();
    }

    public Configuration Current { get; }

    public BridgeSettings Snapshot
    {
        get { lock (sync) return snapshot; }
    }

    public void Save()
    {
        pluginInterface.SavePluginConfig(Current);
        lock (sync) snapshot = CreateSnapshot();
    }

    private BridgeSettings CreateSnapshot() => new(Current.NativePort, Current.AllowActions,
        Current.AllowUnsafe, [.. Current.DisabledTools]);

    private static Configuration Load(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        if (!pluginInterface.ConfigFile.Exists) return new();
        try
        {
            var json = File.ReadAllText(pluginInterface.ConfigFile.FullName);
            var config = JsonSerializer.Deserialize<Configuration>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? throw new JsonException("配置为空。");
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty(nameof(Configuration.HTTPPort), out _))
            {
                config.AllowActions = root.TryGetProperty("EnableActionOperations", out var actions) && actions.GetBoolean();
                config.AllowUnsafe = root.TryGetProperty("EnableUnsafeOperations", out var unsafeTools) && unsafeTools.GetBoolean();
                if (root.TryGetProperty("AutoStartHttpServerOnLoad", out var autoStart))
                    config.AutoStart = autoStart.GetBoolean();
            }
            if (config.HTTPPort is < 1024 or > 65535 || config.NativePort is < 1024 or > 65535 || config.HTTPPort == config.NativePort)
                throw new JsonException("MCP 端口无效或冲突。");
            if (config.DisabledTools is null) throw new JsonException("工具设置无效。");
            return config;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            log.Error(exception, "DalamudMCP 配置读取失败，原文件已保留。");
            throw;
        }
    }
}
