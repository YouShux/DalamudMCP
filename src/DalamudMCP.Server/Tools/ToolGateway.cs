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
        var tools = new List<Tool>();
        foreach (var definition in catalog.Tools)
        {
            if (settings.DisabledTools.Contains(definition.Name, StringComparer.Ordinal) ||
                !NativeAccess.IsAllowed(definition.Access, settings)) continue;
            tools.Add(GameTool(definition));
        }

        if (native.Current is not { } active) return new() { Tools = tools };
        foreach (var upstream in NativeTools(catalog, active))
        {
            var access = NativeAccess.GetAccess(upstream, active.Reviewed);
            var allowed = NativeAccess.IsAllowed(access, settings);
            var readActions = NativeAccess.ReadActions(upstream.Name, active.Reviewed);
            if (settings.DisabledTools.Contains(upstream.Name, StringComparer.Ordinal) || (!allowed && readActions.Length == 0))
                continue;

            var tool = ToolJson.Element(upstream).Deserialize<Tool>(ToolJson.Options)
                ?? throw new InvalidOperationException("原生工具定义无效。");
            tool.Description = $"[Dalamud 原生] {upstream.Description}";
            string[] actions = allowed ? [] : readActions;
            if (tool.Name == "control")
            {
                tool.Description = "查看卫月调试线程或状态。此入口不支持暂停线程。";
                actions = !active.Reviewed && settings.AllowUnsafe ? ["threads", "state", "resume"] : ["threads", "state"];
            }
            if (actions.Length != 0)
            {
                var schema = JsonNode.Parse(tool.InputSchema.GetRawText()) ?? throw new InvalidOperationException("原生工具参数无效。");
                if (schema["properties"]?["action"] is JsonObject actionSchema)
                    actionSchema["enum"] = new JsonArray(actions.Select(action => (JsonNode?)JsonValue.Create(action)).ToArray());
                tool.InputSchema = ToolJson.Element(schema);
                tool.Description += $" 可用操作：{string.Join("、", actions)}。";
            }

            tool.Annotations = new() { ReadOnlyHint = access == ToolAccess.Read || !allowed };
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
                return Error("此工具已禁用。");

            var gameTool = catalog.Tools.FirstOrDefault(tool => tool.Name == request.Name);
            if (gameTool is not null)
            {
                if (!NativeAccess.IsAllowed(gameTool.Access, settings))
                    return Error("此操作所需权限尚未开启，请在 DalamudMCP 设置中确认。");
                return await game.SendAsync<CallToolResult>(new("tools/call", gameTool.Name,
                    ToolJson.Element(request.Arguments ?? new Dictionary<string, JsonElement>())), cancellationToken);
            }

            await native.ConnectAsync(settings.NativePort, false, cancellationToken);
            if (native.Current is not { } active)
                return Error("卫月原生 MCP 未连接。");
            var tool = NativeTools(catalog, active).FirstOrDefault(item => item.Name == request.Name);
            if (tool is null) return Error($"工具不存在：{request.Name}。");

            if (request.Arguments?.Keys.Any(key => key != "action" && key.Equals("action", StringComparison.OrdinalIgnoreCase)) == true)
                return Error("原生工具的 action 参数须使用小写名称。");
            var action = NativeAccess.GetAction(request.Arguments);
            if (tool.Name == "control" && !NativeAccess.AllowsControlAction(action, active.Reviewed))
                return Error("不支持该线程操作；此入口禁止暂停线程。");
            var currentSettings = await game.SendAsync<BridgeSettings>(new("settings"), cancellationToken);
            if (currentSettings.NativePort != settings.NativePort || !ReferenceEquals(active.Client, native.Current?.Client))
                return Error("卫月原生 MCP 连接已变化，请刷新工具目录。");
            if (currentSettings.DisabledTools.Contains(request.Name, StringComparer.Ordinal))
                return Error("此工具已禁用。");
            if (!NativeAccess.IsAllowed(NativeAccess.GetAccess(tool, active.Reviewed, action), currentSettings))
                return Error("此操作所需权限尚未开启，请在 DalamudMCP 设置中确认。");

            return await active.Client.CallToolAsync(request, cancellationToken);
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
        if (native.Current is not { } active) return new(false, native.Error, catalog.Tools);

        var tools = catalog.Tools.Concat(NativeTools(catalog, active).Select(tool => new ToolDefinition(
            tool.Name, tool.Title ?? tool.Name, tool.Description ?? string.Empty,
            tool.InputSchema, NativeAccess.GetAccess(tool, active.Reviewed), "native"))).ToArray();
        return new(true, null, tools);
    }

    private static IEnumerable<Tool> NativeTools(ToolCatalog catalog, NativeBridge.Connection active)
    {
        var gameNames = catalog.Tools.Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
        return active.Tools.Where(tool => !gameNames.Contains(tool.Name));
    }

    private static Tool GameTool(ToolDefinition definition) => new()
    {
        Name = definition.Name,
        Title = definition.Title,
        Description = $"[游戏] {definition.Description}",
        InputSchema = definition.InputSchema,
        Annotations = new() { ReadOnlyHint = definition.Access == ToolAccess.Read },
        Meta = new JsonObject { ["dalamudmcp/source"] = "game" },
    };

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}
