using System.Text.Json;
using System.Text.Json.Nodes;
using DalamudMCP.Shared;
using ModelContextProtocol.Protocol;

namespace DalamudMCP.Server;

internal sealed class ToolGateway(GameBridge game, NativeBridge native)
{
    public async Task<ListToolsResult> ListAsync(CancellationToken cancellationToken)
    {
        var catalog = await game.SendAsync<ToolCatalog>(new("tools/list"), cancellationToken);
        await native.ConnectAsync(catalog.Settings.NativePort, false, cancellationToken);
        var settings = catalog.Settings;
        var tools = catalog.Tools
            .Where(tool => !settings.DisabledTools.Contains(tool.Name, StringComparer.Ordinal) && NativeAccess.IsAllowed(tool.Access, settings))
            .Select(tool => new Tool
            {
                Name = tool.Name,
                Title = tool.Title,
                Description = $"[游戏] {tool.Description}",
                InputSchema = tool.InputSchema,
                Annotations = new() { ReadOnlyHint = tool.Access == ToolAccess.Read },
                Meta = new JsonObject { ["dalamudmcp/source"] = "game" },
            }).ToList();
        foreach (var upstream in native.Tools)
        {
            if (settings.DisabledTools.Contains(upstream.Name, StringComparer.Ordinal)
                || (!NativeAccess.HasReadActions(upstream) && !NativeAccess.IsAllowed(NativeAccess.GetAccess(upstream), settings)))
            {
                continue;
            }

            var tool = JsonSerializer.SerializeToElement(upstream).Deserialize<Tool>() ?? throw new InvalidOperationException("原生工具定义无效。");
            tool.Description = $"[Dalamud 原生] {upstream.Description}";
            if (tool.Name == "control")
            {
                tool.Description = "查看线程或暂停状态，以及恢复已暂停的线程。本入口禁用线程暂停，避免游戏失去响应。";
                var schema = JsonNode.Parse(tool.InputSchema.GetRawText())
                    ?? throw new InvalidOperationException("线程工具参数无效。");
                if (schema["properties"]?["action"] is JsonObject actionSchema)
                    actionSchema["enum"] = new JsonArray("threads", "state", "resume");
                tool.InputSchema = JsonSerializer.SerializeToElement(schema);
            }
            if (NativeAccess.HasReadActions(upstream))
            {
                tool.Description += $"\n底层调试权限关闭时，仅允许 action：{NativeAccess.GetReadActions(upstream)}；其它操作须在插件设置中启用底层调试权限。";
            }

            if (!string.IsNullOrWhiteSpace(native.Instructions))
            {
                tool.Description += $"\n原生服务要求：{native.Instructions}";
            }

            tool.Meta ??= new JsonObject();
            tool.Meta["dalamudmcp/source"] = "native";
            tools.Add(tool);
        }

        return new() { Tools = tools };
    }

    public async Task<CallToolResult> CallAsync(CallToolRequestParams request, CancellationToken cancellationToken)
    {
        try
        {
            var catalog = await game.SendAsync<ToolCatalog>(new("tools/list"), cancellationToken);
            var settings = catalog.Settings;
            if (settings.DisabledTools.Contains(request.Name, StringComparer.Ordinal))
            {
                return Error("此工具已禁用。");
            }

            var gameTool = catalog.Tools.FirstOrDefault(item => item.Name == request.Name);
            if (gameTool is not null)
            {
                if (!NativeAccess.IsAllowed(gameTool.Access, settings))
                {
                    return Error("此操作所需权限尚未开启，请在 DalamudMCP 设置中确认。");
                }

                return await game.SendAsync<CallToolResult>(new("tools/call", request.Name, ToolJson.Element(request.Arguments ?? new Dictionary<string, JsonElement>())), cancellationToken);
            }

            await native.ConnectAsync(settings.NativePort, false, cancellationToken);
            var tool = native.Tools.FirstOrDefault(item => item.Name == request.Name);
            if (tool is not null)
            {
                if (tool.Name == "control")
                {
                    var action = request.Arguments?.TryGetValue("action", out var value) == true
                        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                    if (!NativeAccess.AllowsControlAction(action))
                        return Error("线程暂停功能已禁用；仅支持查看线程、查看状态和恢复线程。");
                }
                if (!NativeAccess.IsAllowed(NativeAccess.GetAccess(tool, request.Arguments), settings))
                {
                    return Error("此操作所需权限尚未开启，请在 DalamudMCP 设置中确认。");
                }

                return await native.CallAsync(request, cancellationToken);
            }

            return Error($"工具不存在或原生 MCP 未连接：{request.Name}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Error(exception.Message);
        }
    }

    public async Task<GatewayStatus> StatusAsync(bool refresh, CancellationToken cancellationToken)
    {
        var catalog = await game.SendAsync<ToolCatalog>(new("tools/list"), cancellationToken);
        await native.ConnectAsync(catalog.Settings.NativePort, refresh, cancellationToken);
        var tools = catalog.Tools.Concat(native.Tools.Select(tool => new ToolDefinition(
            tool.Name, tool.Title ?? tool.Name, tool.Description ?? string.Empty,
            tool.InputSchema, NativeAccess.GetAccess(tool), "native"))).ToArray();
        return new(native.IsConnected, native.Error, tools);
    }

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}
