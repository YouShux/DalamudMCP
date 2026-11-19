using System.Net;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace DalamudMCP.Plugin;

internal static class NativeEndpoint
{
    private const BindingFlags STATIC = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags INSTANCE = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const int URL_ALREADY_REGISTERED = 183;

    public static int? Resolve(IPluginLog log)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var sdk = typeof(IDalamudPluginInterface).Assembly;
        var runtime = sdk.GetType("Dalamud.MCPRuntime");
        var isRunning = runtime?.GetProperty("IsRunning", STATIC);
        var port = runtime?.GetProperty("Port", STATIC);
        if (runtime is null || isRunning?.PropertyType != typeof(bool) || port?.PropertyType != typeof(int)) return null;

        int? ReadPort() => isRunning.GetValue(null) is true && port.GetValue(null) is int value and >= 1024 and <= 65535
                                ? value : null;

        try
        {
            if (ReadPort() is { } active)
            {
                IsolateDefaultTrace(runtime);
                return active;
            }

            var lastError = runtime.GetProperty("LastError", STATIC);
            var host = sdk.GetType("Dalamud.MCPHost");
            var config = sdk.GetType("Dalamud.Configuration.Internal.DalamudConfiguration");
            var service = sdk.GetType("Dalamud.Service`1");
            var start = host?.GetMethod("Start", INSTANCE, null, [typeof(int)], null);
            var listener = host?.GetField("server", INSTANCE);
            if (lastError is null || host is null || config is null || service is null ||
                start?.ReturnType != typeof(void) || listener?.FieldType.FullName != "Dalamud.MCPHttpServer") return null;

            var configuration = GetService(service, config);
            if (configuration is null || config.GetProperty("DevMode", INSTANCE)?.GetValue(configuration) is not true ||
                config.GetProperty("MCPPort", INSTANCE)?.GetValue(configuration) is not int preferred ||
                preferred is < 1024 or > 65535 || !FailedToBind(lastError) || !IsRegistered(preferred)) return null;

            var ownHost = GetService(service, host);
            if (ownHost is null) return null;
            if (!IsolateDefaultTrace(runtime))
            {
                log.Warning("当前卫月版本无法隔离原生调试日志，未尝试启动备用监听。");
                return null;
            }
            for (var offset = 1; offset <= 8 && preferred + offset <= 65535; offset++)
            {
                if (ReadPort() is { } running) return running;
                if (listener.GetValue(ownHost) is not null) return null;

                var candidate = preferred + offset;
                start.Invoke(ownHost, [candidate]);
                if (ReadPort() is { } selected)
                {
                    log.Information("卫月原生 MCP 端口 {PreferredPort} 已占用。游戏 PID {ProcessID} 使用临时端口 {Port}。",
                        preferred, Environment.ProcessId, selected);
                    return selected;
                }

                if (!FailedToBind(lastError) || !IsRegistered(candidate)) break;
            }
        }
        catch (Exception exception) when (exception is TargetInvocationException or MemberAccessException or
                                          TargetException or ArgumentException or InvalidOperationException or HttpListenerException)
        {
            log.Warning(exception, "无法自动确定游戏 PID {ProcessID} 的卫月调试端口。", Environment.ProcessId);
            return ReadPort();
        }

        return null;
    }

    private static object? GetService(Type service, Type type)
    {
        var getter = service.MakeGenericType(type).GetMethods(STATIC)
            .FirstOrDefault(method => method.Name == "GetNullable" && method.GetParameters() is [{ HasDefaultValue: true }]);
        return getter?.Invoke(null, [getter.GetParameters()[0].DefaultValue]);
    }

    private static bool FailedToBind(PropertyInfo error) =>
        error.GetValue(null) is string message && message.StartsWith("启动监听失败: HttpListenerException:", StringComparison.Ordinal);

    private static bool IsolateDefaultTrace(Type runtime)
    {
        var directory = TraceLogs.GetDirectory();
        if (directory is null || runtime.GetProperty("Trace", STATIC)?.GetValue(null) is not { } trace) return false;
        var type = trace.GetType();
        var path = type.GetProperty("FilePath", INSTANCE);
        if (path?.GetValue(trace) is not string current) return false;

        var defaultPath = Path.Combine(directory, "trace.jsonl");
        if (!string.Equals(current, defaultPath, StringComparison.OrdinalIgnoreCase)) return true;

        var backingField = type.GetField("<FilePath>k__BackingField", INSTANCE);
        if (backingField?.FieldType != typeof(string) || type.GetField("writeLock", INSTANCE)?.GetValue(trace) is not Lock writeLock)
            return false;

        // Use the host's own lock so a write or rotation cannot span the path change.
        lock (writeLock)
        {
            if (path.GetValue(trace) is string activePath && string.Equals(activePath, defaultPath, StringComparison.OrdinalIgnoreCase))
                backingField.SetValue(trace, Path.Combine(directory, $"trace-{Environment.ProcessId}.jsonl"));
        }
        return true;
    }

    private static bool IsRegistered(int port)
    {
        // The host reports text only. Ask HTTP.sys for the exact prefix error before retrying.
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/mcp/");
        try { listener.Start(); }
        catch (HttpListenerException exception) when (exception.NativeErrorCode == URL_ALREADY_REGISTERED) { return true; }
        return false;
    }
}
