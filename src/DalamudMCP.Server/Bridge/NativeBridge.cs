using System.Text.Json;
using DalamudMCP.Shared;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DalamudMCP.Server;

internal sealed class NativeBridge : IAsyncDisposable
{
    private readonly SemaphoreSlim connectionLock = new(1, 1);
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
    private Connection? connection;
    private int port;
    private int gameProcessID;
    private long nextProbe;

    public Connection? Current
    {
        get
        {
            var active = Volatile.Read(ref connection);
            return active is { Client.Completion.IsCompleted: false } ? active : null;
        }
    }

    public string? Error { get; private set; }

    public async Task ConnectAsync(BridgeSettings settings, bool force, CancellationToken cancellationToken)
    {
        var nativePort = settings.NativePort;
        await connectionLock.WaitAsync(cancellationToken);
        var changed = nativePort != port || settings.GameProcessID != gameProcessID;
        if (!force && !changed && Environment.TickCount64 < nextProbe)
        {
            connectionLock.Release();
            return;
        }
        McpClient? pending = null;
        try
        {
            if (force || changed || Current is null) await DisconnectAsync();
            port = nativePort;
            gameProcessID = settings.GameProcessID;
            if (nativePort is < 1024 or > 65535)
            {
                Error = "卫月调试端口无效。";
                return;
            }
            if (gameProcessID <= 0)
            {
                Error = "无法确认当前游戏进程。";
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var client = Current?.Client;
            if (client is null)
            {
                var transport = new HttpClientTransport(new()
                {
                    Endpoint = new Uri($"http://127.0.0.1:{nativePort}/mcp/"),
                    Name = "Dalamud 原生 MCP",
                    TransportMode = HttpTransportMode.StreamableHttp,
                    ConnectionTimeout = TimeSpan.FromSeconds(3),
                }, http);
                pending = await McpClient.CreateAsync(transport, new()
                {
                    ClientInfo = new() { Name = "DalamudMCP", Version = typeof(NativeBridge).Assembly.GetName().Version?.ToString() ?? "0.0.0" },
                }, cancellationToken: timeout.Token);
                client = pending;
                if (client.ServerInfo.Name != "dalamud-mcp")
                    throw new InvalidOperationException("调试端口上的服务不是卫月原生 MCP。");
            }

            await VerifyProcessAsync(client, gameProcessID, timeout.Token);
            var discovered = await client.ListToolsAsync(cancellationToken: timeout.Token);
            var tools = discovered.Select(tool => tool.ProtocolTool)
                .GroupBy(tool => tool.Name, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .Select(group => group.First())
                .OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
            Volatile.Write(ref connection, new(client, client.ServerInfo.Version == "2.0.0", tools));
            pending = null;
            Error = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Error = $"无法连接卫月原生 MCP：{exception.Message}";
            await DisconnectAsync();
        }
        finally
        {
            try
            {
                if (pending is not null) await pending.DisposeAsync();
            }
            finally
            {
                nextProbe = Environment.TickCount64 + (Current is null ? 5_000 : 3_000);
                connectionLock.Release();
            }
        }
    }

    internal static async Task VerifyProcessAsync(McpClient client, int expected, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var result = await client.CallToolAsync("control", new Dictionary<string, object?> { ["action"] = "state" },
            cancellationToken: timeout.Token);
        var reported = ReadProcessID(result);
        if (reported != expected)
            throw new InvalidOperationException(reported is { } pid
                ? $"调试端口属于游戏进程 {pid}，当前游戏进程为 {expected}，已拒绝连接。"
                : $"卫月调试服务未报告游戏进程号，无法确认是否属于当前游戏（{expected}）。");
    }

    private static int? ReadProcessID(CallToolResult result)
    {
        if (result.IsError == true) return null;
        if (result.StructuredContent is { } structured) return Read(structured);
        foreach (var block in result.Content.OfType<TextContentBlock>())
        {
            try
            {
                using var json = JsonDocument.Parse(block.Text);
                if (Read(json.RootElement) is { } pid) return pid;
            }
            catch (JsonException) { }
        }
        return null;

        static int? Read(JsonElement data)
        {
            if (data.ValueKind != JsonValueKind.Object) return null;
            if (data.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String &&
                status.ValueEquals("completed") && data.TryGetProperty("result", out var completed))
                data = completed;
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var payload)) data = payload;
            if (data.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in data.EnumerateObject())
                if (property.Name.Equals("processId", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var pid) && pid > 0)
                    return pid;
            return null;
        }
    }

    private async Task DisconnectAsync()
    {
        var active = Interlocked.Exchange(ref connection, null);
        if (active is not null) await active.Client.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        http.Dispose();
        connectionLock.Dispose();
    }

    internal sealed record Connection(McpClient Client, bool Reviewed, Tool[] Tools);
}
