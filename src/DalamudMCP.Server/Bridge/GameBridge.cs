using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DalamudMCP.Shared;

namespace DalamudMCP.Server;

internal sealed class GameBridge(GatewayOptions options)
{
    public async Task<T> SendAsync<T>(PipeRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var pipe = new NamedPipeClientStream(".", options.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000, timeout.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, ToolJson.Options).AsMemory(), timeout.Token);
        var line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("游戏连接已断开。");
        var response = JsonSerializer.Deserialize<PipeResponse>(line, ToolJson.Options) ?? throw new IOException("游戏返回了空响应。");
        if (response.Error is { } error)
        {
            throw new InvalidOperationException(error);
        }

        return response.Result is { } result
                   ? result.Deserialize<T>(ToolJson.Options) ?? throw new IOException("游戏返回了空结果。")
                   : throw new IOException("游戏未返回结果。");
    }
}
