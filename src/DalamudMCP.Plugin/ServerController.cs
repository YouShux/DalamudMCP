using System.Diagnostics;
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
    private readonly string pipeName;
    private readonly string executable;
    private readonly IPluginLog log;
    private readonly CancellationTokenSource lifetime = new();
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly StringBuilder output = new();
    private Process? process;
    private Task? refresh;
    private long nextRefresh;
    private bool disposed;

    public ServerController(IDalamudPluginInterface pluginInterface, SettingsStore settings, string pipeName, IPluginLog log)
    {
        this.settings = settings;
        this.pipeName = pipeName;
        this.log = log;
        executable = Path.Combine(pluginInterface.AssemblyLocation.DirectoryName
            ?? throw new InvalidOperationException("插件目录不存在。"), "server", "DalamudMCP.Server.exe");
    }

    public string Endpoint => $"http://127.0.0.1:{settings.Current.HTTPPort}/mcp";
    public bool IsRunning => process is { HasExited: false };
    public bool IsRefreshing => refresh is { IsCompleted: false };
    public GatewayStatus? Status { get; private set; }
    public string? Error { get; private set; }

    public void Start()
    {
        if (disposed || IsRunning) return;
        process?.Dispose();
        process = null;
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
        try
        {
            output.Clear();
            process = new Process { StartInfo = start };
            process.ErrorDataReceived += OnOutput;
            process.OutputDataReceived += OnOutput;
            if (!process.Start()) throw new InvalidOperationException("服务进程未启动。");
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            Error = null;
            nextRefresh = 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Error = exception.Message;
            log.Error(exception, "DalamudMCP 服务启动失败。");
            process?.Dispose();
            process = null;
        }
    }

    private void OnOutput(object sender, DataReceivedEventArgs arguments)
    {
        if (arguments.Data is not { } line) return;
        lock (output)
        {
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
                Error = $"服务已退出（{process.ExitCode}），详细原因见卫月日志。";
                lock (output) log.Error($"DalamudMCP 服务异常退出：{output}");
                process.Dispose();
                process = null;
            }
            return;
        }
        if (!reconnect && Environment.TickCount64 < nextRefresh) return;
        nextRefresh = Environment.TickCount64 + 3000;
        refresh = RefreshAsync(reconnect);
    }

    private async Task RefreshAsync(bool reconnect)
    {
        try
        {
            var address = $"http://127.0.0.1:{settings.Current.HTTPPort}/{(reconnect ? "refresh" : "status")}";
            using var request = new HttpRequestMessage(reconnect ? HttpMethod.Post : HttpMethod.Get, address);
            using var response = await client.SendAsync(request, lifetime.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var status = await response.Content.ReadFromJsonAsync<GatewayStatus>(ToolJson.Options, lifetime.Token).ConfigureAwait(false)
                ?? throw new JsonException("服务状态为空。");
            if (disposed) return;
            Status = status;
            Error = null;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            if (!disposed) Error = "正在连接本地服务，请稍候。";
        }
    }

    public void Stop()
    {
        if (process is { } current)
        {
            try { if (!current.HasExited) current.Kill(true); }
            catch (InvalidOperationException exception)
            {
                log.Debug(exception, "DalamudMCP 服务进程已结束。");
            }
            process = null;
            current.Dispose();
        }
        Status = null;
        Error = null;
    }

    public void Dispose()
    {
        disposed = true;
        lifetime.Cancel();
        Stop();
        client.Dispose();
        if (refresh is { } pending)
            _ = pending.ContinueWith(_ => lifetime.Dispose(), TaskScheduler.Default);
        else lifetime.Dispose();
    }
}
