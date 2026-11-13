using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DalamudMCP.Server;

internal sealed class NativeBridge : IAsyncDisposable
{
    private readonly SemaphoreSlim connectionLock = new(1, 1);
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
    private Connection? connection;
    private int port;
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

    public async Task ConnectAsync(int nativePort, bool force, CancellationToken cancellationToken)
    {
        await connectionLock.WaitAsync(cancellationToken);
        if (!force && nativePort == port && Environment.TickCount64 < nextProbe)
        {
            connectionLock.Release();
            return;
        }
        McpClient? pending = null;
        try
        {
            if (force || nativePort != port || Current is null) await DisconnectAsync();
            port = nativePort;
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
