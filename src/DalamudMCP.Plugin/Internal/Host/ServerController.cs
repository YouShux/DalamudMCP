using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DalamudMCP.Shared;

namespace DalamudMCP.Plugin;

public sealed class ServerController : IDisposable
{
    private readonly SettingsStore settings;
    private readonly IFramework framework;
    private readonly string pipeName;
    private readonly string executable;
    private readonly IPluginLog log;
    private readonly CancellationTokenSource lifetime = new();
    private readonly HttpClient client = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };
    private readonly StringBuilder output = new();
    private Process? process;
    private int activePort;
    private string? activeToken;
    private Task? refresh;
    private Task? nativeRefresh;
    private long nextRefresh;
    private bool disposed;

    public ServerController(IDalamudPluginInterface pluginInterface, IFramework framework, SettingsStore settings, string pipeName, IPluginLog log)
    {
        this.framework = framework;
        this.settings = settings;
        this.pipeName = pipeName;
        this.log = log;
        executable = Path.Combine(pluginInterface.AssemblyLocation.DirectoryName
            ?? throw new InvalidOperationException("插件目录不存在。"), "server", "DalamudMCP.Server.exe");
    }

    public string? Endpoint => Volatile.Read(ref activePort) is > 0 and var port && IsRunning
                                   ? $"http://127.0.0.1:{port}/mcp" : null;
    public bool IsRunning => process is { HasExited: false };
    public bool IsRefreshing => refresh is { IsCompleted: false };
    public GatewayStatus? Status { get; private set; }
    public string? Error { get; private set; }

    public void Start()
    {
        if (disposed || IsRunning) return;
        Stop();
        if (LocalBearer.ReadToken(preferUserStore: true) is not { } token)
        {
            Error = $"请在用户环境变量 {LocalBearer.ENVIRONMENT_VARIABLE} 中设置令牌，再重启启动器和游戏。";
            return;
        }
        if (!File.Exists(executable))
        {
            Error = "未找到服务程序，请使用完整安装包。";
            return;
        }
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(executable),
        };
        foreach (var argument in new[] { "--pipe", pipeName, "--port", settings.Current.HTTPPort.ToString(), "--parent", Environment.ProcessId.ToString() })
            start.ArgumentList.Add(argument);
        start.Environment[LocalBearer.ENVIRONMENT_VARIABLE] = token;
        try
        {
            lock (output)
            {
                output.Clear();
                process = new Process { StartInfo = start };
            }
            process.ErrorDataReceived += OnOutput;
            process.OutputDataReceived += OnReady;
            if (!process.Start()) throw new InvalidOperationException("服务进程未启动。");
            activeToken = token;
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            Error = null;
            nextRefresh = 0;
            nativeRefresh = ResolveNativeEndpointAsync(process);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Stop();
            Error = exception.Message;
            log.Error(exception, "DalamudMCP 服务启动失败。");
        }
    }

    private void OnReady(object sender, DataReceivedEventArgs arguments)
    {
        const string READY_PREFIX = "DALAMUDMCP_READY:";
        if (arguments.Data is not { } line || !line.StartsWith(READY_PREFIX, StringComparison.Ordinal) ||
            !int.TryParse(line.AsSpan(READY_PREFIX.Length), out var port) || port is < 1024 or > 65535) return;
        lock (output)
            if (activePort == 0 && ReferenceEquals(sender, process)) Volatile.Write(ref activePort, port);
    }

    private void OnOutput(object sender, DataReceivedEventArgs arguments)
    {
        if (arguments.Data is not { } line) return;
        lock (output)
        {
            if (!ReferenceEquals(sender, process)) return;
            output.AppendLine(line);
            if (output.Length > 4096) output.Remove(0, output.Length - 4096);
        }
    }

    public void Refresh(bool reconnect = false)
    {
        if (disposed || IsRefreshing) return;
        if (!IsRunning)
        {
            Status = null;
            if (process is not null && process.ExitCode != 0)
            {
                var error = $"服务已退出（{process.ExitCode}），详细原因见卫月日志。";
                lock (output) log.Error($"DalamudMCP 服务异常退出：{output}");
                Stop();
                Error = error;
            }
            return;
        }
        var port = Volatile.Read(ref activePort);
        if (port == 0 || process is not { } current) return;
        if (!reconnect && Environment.TickCount64 < nextRefresh) return;
        nextRefresh = Environment.TickCount64 + 3000;
        if (reconnect && nativeRefresh is not { IsCompleted: false }) nativeRefresh = ResolveNativeEndpointAsync(current);
        refresh = RefreshAsync(current, port, reconnect);
    }

    private Task ResolveNativeEndpointAsync(Process current) => framework.RunOnTick(() =>
    {
        if (!ReferenceEquals(current, process) || lifetime.IsCancellationRequested) return;
        settings.SetResolvedNativePort(NativeEndpoint.Resolve(log));
    }, cancellationToken: lifetime.Token);

    private async Task RefreshAsync(Process current, int port, bool reconnect)
    {
        try
        {
            if (nativeRefresh is { } resolution) await resolution.ConfigureAwait(false);
            if (!ReferenceEquals(current, process)) return;
            var address = $"http://127.0.0.1:{port}/{(reconnect ? "refresh" : "status")}";
            using var request = new HttpRequestMessage(reconnect ? HttpMethod.Post : HttpMethod.Get, address);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", activeToken);
            using var response = await client.SendAsync(request, lifetime.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                lock (output)
                {
                    if (!ReferenceEquals(current, process)) return;
                    Status = null;
                    Error = "本地服务拒绝了令牌，请检查环境变量并重启启动器和游戏。";
                }
                return;
            }
            response.EnsureSuccessStatusCode();
            var status = await response.Content.ReadFromJsonAsync<GatewayStatus>(ToolJson.Options, lifetime.Token).ConfigureAwait(false)
                ?? throw new JsonException("服务状态为空。");
            lock (output)
            {
                if (!ReferenceEquals(current, process)) return;
                if (status.GameProcessID != Environment.ProcessId || status.InstanceID != settings.InstanceID)
                {
                    Status = null;
                    Error = "本地服务不属于当前游戏实例。";
                    return;
                }
                Status = status;
                Error = null;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            lock (output)
            {
                if (!ReferenceEquals(current, process)) return;
                Status = null;
                Error = "正在连接本地服务，请稍候。";
            }
        }
    }

    public void Stop()
    {
        Process? current;
        lock (output)
        {
            current = process;
            process = null;
            Volatile.Write(ref activePort, 0);
            Status = null;
            Error = null;
            activeToken = null;
        }
        if (current is not null)
        {
            try { if (!current.HasExited) current.Kill(true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                log.Debug(exception, "无法停止 DalamudMCP 服务进程。");
            }
            current.Dispose();
        }
    }

    public void Dispose()
    {
        disposed = true;
        lifetime.Cancel();
        Stop();
        client.Dispose();
        _ = Task.WhenAll(refresh ?? Task.CompletedTask, nativeRefresh ?? Task.CompletedTask)
            .ContinueWith(_ => lifetime.Dispose(), TaskScheduler.Default);
    }
}
