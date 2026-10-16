using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using DalamudMCP.Plugin.Tools;
using DalamudMCP.Shared;

namespace DalamudMCP.Plugin;

public sealed class PipeServer : IDisposable
{
    private readonly GameTools tools;
    private readonly SettingsStore settings;
    private readonly IPluginLog log;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim slots = new(8, 8);
    private readonly Task listener;

    public PipeServer(GameTools tools, SettingsStore settings, IPluginLog log)
    {
        this.tools = tools;
        this.settings = settings;
        this.log = log;
        Name = $"DalamudMCP.{Environment.ProcessId}.{Guid.NewGuid():N}";
        listener = ListenAsync();
    }

    public string Name { get; }

    private async Task ListenAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await slots.WaitAsync(lifetime.Token).ConfigureAwait(false);
                var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 8,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false); }
                catch { pipe.Dispose(); slots.Release(); throw; }
                _ = HandleAsync(pipe);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            log.Error(exception, "DalamudMCP 管道监听失败。");
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                var line = new StringBuilder();
                var buffer = new char[4096];
                while (true)
                {
                    var count = await reader.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
                    if (count == 0) throw new IOException("请求未完成。");
                    var newline = Array.IndexOf(buffer, '\n', 0, count);
                    line.Append(buffer, 0, newline < 0 ? count : newline);
                    if (line.Length > 1024 * 1024) throw new InvalidDataException("请求过大。");
                    if (newline >= 0) break;
                }
                PipeResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<PipeRequest>(line.ToString(), ToolJson.Options)
                        ?? throw new JsonException("请求为空。");
                    response = new(await DispatchAsync(request, timeout.Token).ConfigureAwait(false));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    response = new(Error: exception.Message);
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, ToolJson.Options).AsMemory(), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            {
                if (exception is not OperationCanceledException && !lifetime.IsCancellationRequested)
                    log.Debug(exception, "DalamudMCP 请求连接结束。");
            }
            finally { slots.Release(); }
        }
    }

    private async ValueTask<JsonElement> DispatchAsync(PipeRequest request, CancellationToken cancellationToken)
    {
        if (request.Method == "settings") return ToolJson.Element(settings.Snapshot);
        if (request.Method == "tools/list") return ToolJson.Element(new ToolCatalog([.. tools.Tools], settings.Snapshot));
        if (request.Method != "tools/call") throw new InvalidOperationException("未知管道方法。");
        var tool = tools.Tools.FirstOrDefault(tool => tool.Name == request.Name)
            ?? throw new InvalidOperationException("工具不存在。");
        var access = settings.Snapshot;
        if (access.DisabledTools.Contains(tool.Name, StringComparer.Ordinal)
            || tool.Access == ToolAccess.Action && !access.AllowActions
            || tool.Access == ToolAccess.Unsafe && !access.AllowUnsafe)
            return ToolJson.Error("工具未启用，请在 DalamudMCP 设置中调整权限。");
        cancellationToken.ThrowIfCancellationRequested();
        return await tools.CallAsync(tool.Name, request.Arguments, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lifetime.Cancel();
        _ = listener.ContinueWith(_ => lifetime.Dispose(), TaskScheduler.Default);
    }
}
