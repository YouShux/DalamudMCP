using System.Diagnostics;
using System.Net;
using DalamudMCP.Server;

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
    return services.AddMcpServer(server =>
    {
        server.ServerInfo = new() { Name = "DalamudMCP", Version = "1.0.0" };
        server.ServerInstructions = "游戏状态与 Dalamud 原生调试工具的统一入口。遵守每项工具的权限与原生服务要求；原生未连接时游戏工具仍可使用。";
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
