using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace DalamudMCP.Server;

internal sealed class NativeBridge : IAsyncDisposable
{
    private readonly SemaphoreSlim connectionLock = new(1, 1);
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
    private Connection? connection;
    private int port;
    private int processID;
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

    public async Task ConnectAsync(int nativePort, int gameProcessID, bool force, CancellationToken cancellationToken)
    {
        await connectionLock.WaitAsync(cancellationToken);
        if (!force && nativePort == port && gameProcessID == processID && Environment.TickCount64 < nextProbe)
        {
            connectionLock.Release();
            return;
        }
        McpClient? pending = null;
        try
        {
            if (force || nativePort != port || gameProcessID != processID || Current is null) await DisconnectAsync();
            port = nativePort;
            processID = gameProcessID;
            if (nativePort is < 1024 or > 65535)
            {
                Error = "卫月调试端口无效。";
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
                    ClientInfo = new() { Name = "DalamudMCP", Version = "1.0.0" },
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
            Volatile.Write(ref connection, new(client, client.ServerInfo.Version == "2.0.0", tools, gameProcessID));
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

    public static async Task VerifyProcessAsync(McpClient client, int expected, CancellationToken cancellationToken)
    {
        if (expected <= 0) throw new InvalidOperationException("游戏进程身份缺失，拒绝连接卫月调试。");
        var result = await client.CallToolAsync("control", new Dictionary<string, object?> { ["action"] = "state" },
            cancellationToken: cancellationToken);
        if (ReadProcessID(result) != expected)
            throw new InvalidOperationException($"卫月调试服务不属于当前游戏进程（期望 PID {expected}），已拒绝跨客户端连接。");
    }

    internal static int? ReadProcessID(CallToolResult result)
    {
        if (result.IsError == true) return null;
        if (result.StructuredContent is { } structured && Read(structured) is { } id) return id;
        foreach (var text in result.Content.OfType<TextContentBlock>())
        {
            try { using var document = JsonDocument.Parse(text.Text); if (Read(document.RootElement) is { } found) return found; }
            catch (JsonException) { }
        }
        return null;

        static int? Read(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals("processId", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt32(out var value) && value > 0) return value;
            if (element.TryGetProperty("result", out var result) &&
                element.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String &&
                status.GetString() == "completed") return Read(result);
            return element.TryGetProperty("data", out var data) ? Read(data) : null;
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

    internal sealed record Connection(McpClient Client, bool Reviewed, Tool[] Tools, int GameProcessID);
}
