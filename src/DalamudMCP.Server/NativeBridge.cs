using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DalamudMCP.Server;

internal sealed class NativeBridge : IAsyncDisposable
{
    private readonly SemaphoreSlim connectionLock = new(1, 1);
    private McpClient? client;
    private Tool[] tools = [];
    private int port;
    private DateTimeOffset nextAttempt;

    public bool IsConnected => client is { Completion.IsCompleted: false };
    public string? Error { get; private set; }
    public string? Instructions => client?.ServerInstructions;
    public Tool[] Tools => IsConnected ? tools : [];

    public async Task ConnectAsync(int nativePort, bool force, CancellationToken cancellationToken)
    {
        if (!force && nativePort == port && (IsConnected || DateTimeOffset.UtcNow < nextAttempt))
        {
            return;
        }

        await connectionLock.WaitAsync(cancellationToken);
        McpClient? pending = null;
        try
        {
            if (!force && nativePort == port && (IsConnected || DateTimeOffset.UtcNow < nextAttempt))
            {
                return;
            }

            if (client is not null)
            {
                await client.DisposeAsync();
                client = null;
            }

            tools = [];
            port = nativePort;
            nextAttempt = DateTimeOffset.UtcNow.AddSeconds(10);
            if (nativePort is < 1024 or > 65535)
            {
                Error = "原生 MCP 端口无效。";
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var transport = new HttpClientTransport(new()
            {
                Endpoint = new Uri($"http://127.0.0.1:{nativePort}/mcp/"),
                Name = "Dalamud 原生 MCP",
                ConnectionTimeout = TimeSpan.FromSeconds(3),
            });
            pending = await McpClient.CreateAsync(transport, new()
            {
                ClientInfo = new() { Name = "DalamudMCP", Version = "1.0.0" },
            }, cancellationToken: timeout.Token);
            var discovered = await pending.ListToolsAsync(cancellationToken: timeout.Token);
            tools = discovered.Select(tool => tool.ProtocolTool).ToArray();
            client = pending;
            pending = null;
            Error = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Error = $"无法连接原生 MCP：{exception.Message}";
            if (client is not null)
            {
                await client.DisposeAsync();
                client = null;
            }
        }
        finally
        {
            if (pending is not null)
            {
                await pending.DisposeAsync();
            }

            connectionLock.Release();
        }
    }

    public async Task<CallToolResult> CallAsync(CallToolRequestParams request, CancellationToken cancellationToken)
    {
        var active = client;
        if (active is null || active.Completion.IsCompleted)
        {
            throw new IOException(Error ?? "原生 MCP 未连接。");
        }

        return await active.CallToolAsync(request, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (client is not null)
        {
            await client.DisposeAsync();
        }

        connectionLock.Dispose();
    }
}
