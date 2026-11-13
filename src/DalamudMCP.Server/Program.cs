using System.Diagnostics;
using System.Net;
using DalamudMCP.Server;
using DalamudMCP.Shared;

if (args is ["--licenses"])
{
    var assembly = typeof(Program).Assembly;
    var names = assembly.GetManifestResourceNames().Where(n => n.StartsWith("Notices.", StringComparison.Ordinal));
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var name in names.OrderBy(n => n == "Notices.Overview" ? "" : n, StringComparer.Ordinal))
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var notice = reader.ReadToEnd().TrimEnd();
        if (!seen.Add(notice.ReplaceLineEndings("\n")))
            continue;

        Console.WriteLine(notice);
        Console.WriteLine();
    }
    return;
}

var options = GatewayOptions.Parse(args);
if (options.Stdio)
{
    var builder = Host.CreateApplicationBuilder();
    ConfigureLogging(builder.Logging);
    ConfigureMCP(builder.Services, options).WithStdioServerTransport();
    using var host = builder.Build();
    using var parent = WatchParent(options.ParentID, host.Services.GetRequiredService<IHostApplicationLifetime>());
    await host.RunAsync();
    return;
}

if (LocalBearer.ReadToken() is not { } bearerToken)
{
    Console.Error.WriteLine($"请在 {LocalBearer.ENVIRONMENT_VARIABLE} 中设置有效的 MCP 令牌。");
    Environment.ExitCode = 1;
    return;
}

var tokenHash = LocalBearer.Hash(bearerToken);
var webBuilder = WebApplication.CreateBuilder();
webBuilder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, options.Port));
ConfigureLogging(webBuilder.Logging);
ConfigureMCP(webBuilder.Services, options).WithHttpTransport();
await using var app = webBuilder.Build();
app.Use(async (context, next) =>
{
    var host = context.Request.Host;
    var origin = context.Request.Headers.Origin;
    if (host.Port != options.Port || host.Host is not ("127.0.0.1" or "localhost")
        || (origin.Count != 0 && (origin.Count != 1 || !Uri.TryCreate(origin[0], UriKind.Absolute, out var uri)
            || uri.Scheme != "http" || uri.Port != options.Port || uri.Host is not ("127.0.0.1" or "localhost"))))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    var authorization = context.Request.Headers.Authorization;
    if (authorization.Count != 1 || !LocalBearer.Matches(authorization[0], tokenHash))
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next(context);
});
app.MapMcp("/mcp");
app.MapGet("/status", async (ToolGateway gateway, CancellationToken cancellationToken) =>
    await gateway.StatusAsync(false, cancellationToken));
app.MapPost("/refresh", async (ToolGateway gateway, CancellationToken cancellationToken) =>
    await gateway.StatusAsync(true, cancellationToken));
using var gameProcess = WatchParent(options.ParentID, app.Lifetime);
await app.RunAsync();

static IMcpServerBuilder ConfigureMCP(IServiceCollection services, GatewayOptions options)
{
    services.AddSingleton(options);
    services.AddSingleton<GameBridge>();
    services.AddSingleton<NativeBridge>();
    services.AddSingleton<ToolGateway>();
    var catalogChanges = new CatalogChanges();
    services.AddSingleton(catalogChanges);
    services.AddHostedService<ToolCatalogMonitor>();
    return services.AddMcpServer(server =>
    {
        server.ServerInfo = new() { Name = "DalamudMCP", Version = "1.0.0" };
        server.ToolCollection = catalogChanges;
        server.ServerInstructions =
            "游戏状态与 Dalamud 原生调试工具的统一入口。原生未连接时游戏工具仍可使用。收到工具目录变更通知后重新列出工具。" +
            "用 read_development_workflow 读取当前运行 DLL 的开发验收流程及 MVID。" +
            "验证开发插件时，先用 get_dev_build 核对注册路径。成功编译后读取本次产物的 MVID，调用 wait_for_dev_build。" +
            "新开发 DLL 尚未注册时，仅在用户授权且信任该 DLL 的情况下使用 load_dev_dll，传入本次产物的 MVID。" +
            "发现插件时用 list_plugins、list_plugin_commands、list_plugin_ipc 和 get_plugin_state。IPC 签名只说明调用形状；不要将函数名视为只读授权。" +
            "有授权时可用 show_plugin_window 调用插件公开的界面入口，再用 get_window_snapshot 或 wait_for_window_visibility 独立核对。回调返回不等于窗口已可见。" +
            "仅在 status=ready 且 buildVerified=true 时继续。先用 get_dalamud_log 查看运行日志，再用 get_addon_list、get_addon_strings 或截图核对实际结果。" +
            "请求受理或事件派发不代表游戏状态已改变。遵守工具权限；无明确授权时只观察。";
    }).WithListToolsHandler(async (context, cancellationToken) =>
        await (context.Services ?? throw new InvalidOperationException("服务容器不可用。")).GetRequiredService<ToolGateway>().ListAsync(cancellationToken))
      .WithCallToolHandler(async (context, cancellationToken) =>
        await (context.Services ?? throw new InvalidOperationException("服务容器不可用。")).GetRequiredService<ToolGateway>().CallAsync(context.Params ?? throw new ArgumentException("工具参数缺失。"), cancellationToken));
}

static void ConfigureLogging(ILoggingBuilder logging)
{
    logging.ClearProviders();
    logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
}

static CancellationTokenSource? WatchParent(int? parentID, IHostApplicationLifetime lifetime)
{
    if (parentID is not { } id)
    {
        return null;
    }

    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
    _ = WatchParentAsync(id, lifetime, cancellation.Token);
    return cancellation;
}

static async Task WatchParentAsync(int parentID, IHostApplicationLifetime lifetime, CancellationToken cancellationToken)
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
    try
    {
        do
        {
            using var parent = Process.GetProcessById(parentID);
        } while (await timer.WaitForNextTickAsync(cancellationToken));
    }
    catch (ArgumentException) { lifetime.StopApplication(); }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
}
