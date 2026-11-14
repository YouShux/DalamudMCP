using System.Reflection;
using Dalamud.Plugin;
using DalamudMCP.Shared;

namespace DalamudMCP.Plugin;

// Only reuse or start the native host in THIS game process. Do not save its
// temporary fallback port into the configuration shared by all game clients.
internal static class NativeEndpoint
{
    public static int Resolve(int preferred)
    {
        var sdk = typeof(IDalamudPluginInterface).Assembly;
        var runtime = sdk.GetType("Dalamud.MCPRuntime");
        var hostType = sdk.GetType("Dalamud.MCPHost");
        var configType = sdk.GetType("Dalamud.Configuration.Internal.DalamudConfiguration");
        var service = sdk.GetType("Dalamud.Service`1");
        if (runtime is null || hostType is null || configType is null || service is null) return preferred;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic;
        if (runtime.GetProperty("Trace", flags | BindingFlags.Static)?.GetValue(null) is { } trace)
            NativeTraceLog.Isolate(trace, Environment.ProcessId);
        var running = runtime.GetProperty("IsRunning", flags | BindingFlags.Static);
        var port = runtime.GetProperty("Port", flags | BindingFlags.Static);
        int? Current() => running?.GetValue(null) is true && port?.GetValue(null) is int value &&
            value is >= 1024 and <= 65535 ? value : null;
        if (Current() is { } active) return active;
        object? Get(Type type)
        {
            var method = service.MakeGenericType(type).GetMethods(flags | BindingFlags.Static)
                .SingleOrDefault(item => item.Name == "GetNullable" && item.GetParameters().All(parameter => parameter.HasDefaultValue));
            return method?.Invoke(null, method.GetParameters().Select(parameter => parameter.DefaultValue).ToArray());
        }
        var config = Get(configType);
        if (configType.GetProperty("DevMode", flags | BindingFlags.Instance)?.GetValue(config) is not true)
            return preferred;
        var host = Get(hostType);
        var start = hostType.GetMethod("Start", flags | BindingFlags.Instance, null, [typeof(int)], null);
        var server = hostType.GetField("server", flags | BindingFlags.Instance);
        if (host is null || start is null || server is null || server.GetValue(host) is not null) return preferred;
        foreach (var candidate in LocalInstances.CandidatePorts(preferred))
        {
            // MCPHost.Start handles bind errors and releases failed listeners.
            start.Invoke(host, [candidate]);
            if (Current() is { } selected) return selected;
            if (server.GetValue(host) is not null) break;
        }
        return preferred; // Gateway still verifies the remote PID before use.
    }
}
