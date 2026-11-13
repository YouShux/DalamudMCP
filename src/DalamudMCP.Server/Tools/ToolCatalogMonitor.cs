using System.Text.Json;
using DalamudMCP.Shared;
using ModelContextProtocol.Server;

namespace DalamudMCP.Server;

internal sealed class CatalogChanges : McpServerPrimitiveCollection<McpServerTool>
{
    public void Invalidate() => RaiseChanged();
}

internal sealed class ToolCatalogMonitor(ToolGateway gateway, CatalogChanges changes, ILogger<ToolCatalogMonitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        string? lastCatalog = null;
        do
        {
            string? catalog;
            try
            {
                var result = await gateway.ListAsync(stoppingToken);
                catalog = JsonSerializer.Serialize(result.Tools, ToolJson.Options);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogDebug(exception, "工具目录暂时不可用。");
                catalog = null;
            }

            if (catalog == lastCatalog) continue;
            lastCatalog = catalog;
            changes.Invalidate();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
