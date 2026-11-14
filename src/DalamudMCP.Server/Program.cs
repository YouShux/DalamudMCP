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
webBuilder.Services.AddHttpContextAccessor();
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
            "已知工具和参数时直接调用；否则按用途只查相关工具。目录变更后由客户端刷新，不把完整目录交给模型。" +
            "原生未连接时仍可查游戏状态。" +
            "开发验收按 read_development_workflow 执行。" +
            "无明确授权时只观察，IPC 名称和签名不代表只读。请求受理后独立回读状态。";
    }).WithListToolsHandler(async (context, cancellationToken) =>
        await (context.Services ?? throw new InvalidOperationException("服务容器不可用。")).GetRequiredService<ToolGateway>().ListAsync(cancellationToken))
      .WithCallToolHandler(async (context, cancellationToken) =>
      {
          var services = context.Services ?? throw new InvalidOperationException("服务容器不可用。");
          var request = services.GetService<IHttpContextAccessor>()?.HttpContext?.Request;
          var structuredOnly = request is not null && request.Query["result"] is { Count: 1 } mode && mode[0] == "structured";
          var result = await services.GetRequiredService<ToolGateway>().CallAsync(
              context.Params ?? throw new ArgumentException("工具参数缺失。"), structuredOnly, cancellationToken);
          if (structuredOnly)
              ToolGateway.RemoveDuplicateText(result);
          return result;
      });
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
