using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace DalamudMCP.Plugin.Tools;

internal sealed class PluginIntegration(GameServices services) : IDisposable
{
    private static readonly Dictionary<int, MethodInfo> SubscriberMethods = typeof(IDalamudPluginInterface).GetMethods()
        .Where(method => method.Name == "GetIpcSubscriber" && method.IsGenericMethodDefinition)
        .ToDictionary(method => method.GetGenericArguments().Length);
    private readonly Dictionary<string, Subscription> subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    public object Call(string name, ToolArguments arguments)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return name switch
        {
            "unsafe_invoke_plugin_ipc" => Invoke(arguments, true),
            "invoke_plugin_ipc" => Invoke(arguments, false),
            "slash_command" => Command(arguments.Text("command")),
            "plugin_data_subscribe" => Subscribe(arguments.Text("plugin_name"), arguments.Text("channel")),
            "plugin_data_unsubscribe" => new { removed = Unsubscribe(arguments.Text("channel")) },
            "plugin_data_poll" => Poll(arguments.Text("channel"), arguments.Integer("max_items", 1000, 1, 1000)),
            _ => throw new ArgumentException("未知插件工具。"),
        };
    }

    public async Task<object> ReloadAsync(string pluginName, CancellationToken cancellationToken)
    {
        ValidateName(pluginName);
        if (pluginName.Equals(services.PluginInterface.InternalName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("不能通过本工具重载 DalamudMCP 自身。");
        object Reload()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var plugin = services.PluginInterface.InstalledPlugins.FirstOrDefault(candidate => candidate.InternalName.Equals(pluginName, StringComparison.OrdinalIgnoreCase));
            if (plugin is null || !plugin.IsDev)
                throw new InvalidOperationException("只允许重载已安装的开发插件。");
            var command = $"/xlreload {plugin.InternalName}";
            if (!services.CommandManager.ProcessCommand(command))
                throw new InvalidOperationException("Dalamud 未处理插件重载命令。");
            return new { initiated = true, pluginName = plugin.InternalName };
        }
        return services.Framework.IsInFrameworkUpdateThread ? Reload() : await services.Framework.RunOnFrameworkThread(Reload).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        disposed = true;
        foreach (var subscription in subscriptions.Values)
        {
            subscription.Provider.UnregisterAction();
            subscription.Queue.Writer.TryComplete();
        }
        subscriptions.Clear();
    }

    private object Invoke(ToolArguments arguments, bool arbitrary)
    {
        var callgate = arbitrary ? arguments.Text("callgate") : $"{arguments.Text("plugin_name")}.MCP.{arguments.Text("method")}";
        var json = arguments.Text("arguments_json", "[]");
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 8)
            throw new ArgumentException("arguments_json 必须为最多 8 个参数的 JSON 数组。");
        var elements = document.RootElement.EnumerateArray().ToArray();
        Type[] kinds;
        Type resultType;
        object?[] values;
        if (arbitrary)
        {
            kinds = arguments.Text("argument_kinds", string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(ParseType).ToArray();
            resultType = ParseType(arguments.Text("result_kind"));
            if (kinds.Length != elements.Length)
                throw new ArgumentException("argument_kinds 与 arguments_json 的参数数量不一致。");
            values = elements.Select((element, index) => JsonSerializer.Deserialize(element.GetRawText(), kinds[index])).ToArray();
        }
        else
        {
            var parsed = elements.Select(ParseValue).ToArray();
            values = parsed.Select(pair => pair.Value).ToArray();
            kinds = parsed.Select(pair => pair.Type).ToArray();
            resultType = typeof(object);
        }
        if (!SubscriberMethods.TryGetValue(kinds.Length + 1, out var method))
            throw new ArgumentException("当前 Dalamud 不支持该 IPC 参数数量。");
        var subscriber = method.MakeGenericMethod([.. kinds, resultType]).Invoke(services.PluginInterface, [callgate]) ?? throw new InvalidOperationException("IPC 订阅入口不可用。");
        var type = subscriber.GetType();
        if (type.GetProperty("HasFunction")?.GetValue(subscriber) is not true)
            throw new InvalidOperationException($"IPC {callgate} 未注册函数。");
        try
        {
            var invoke = type.GetMethod("InvokeFunc") ?? throw new InvalidOperationException("IPC 未暴露函数调用入口。");
            var result = invoke.Invoke(subscriber, values);
            return new { callgate, succeeded = true, result };
        }
        catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
        {
            throw new InvalidOperationException($"IPC {callgate} 执行失败：{inner.Message}", inner);
        }
    }

    private object Command(string command)
    {
        if (!command.StartsWith('/') || command.Length > 256 || command.Any(character => character is '\r' or '\n' or '\0'))
            throw new ArgumentException("command 必须是最多 256 个字符的单行斜杠命令。");
        return new { succeeded = services.CommandManager.ProcessCommand(command) };
    }

    private object Subscribe(string pluginName, string channel)
    {
        ValidateName(pluginName);
        ValidateName(channel);
        if (!services.PluginInterface.InstalledPlugins.Any(plugin => plugin.InternalName.Equals(pluginName, StringComparison.OrdinalIgnoreCase) && plugin.IsLoaded))
            throw new InvalidOperationException("目标插件未加载。");
        var fullName = $"{pluginName}.{channel}";
        var callgate = $"DalamudMCP.Relay.{fullName}";
        if (subscriptions.ContainsKey(fullName))
            return new { subscribed = false, fullChannelName = fullName, callgate };
        if (subscriptions.Count >= 32)
            throw new InvalidOperationException("最多允许 32 个数据订阅。");
        var queue = Channel.CreateBounded<string>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });
        var provider = services.PluginInterface.GetIpcProvider<string, object>(callgate);
        provider.RegisterAction(data =>
        {
            if (!disposed && data is { Length: <= 65536 })
                queue.Writer.TryWrite(data);
        });
        subscriptions.Add(fullName, new(queue, provider));
        return new { subscribed = true, fullChannelName = fullName, callgate };
    }

    private bool Unsubscribe(string channel)
    {
        if (!subscriptions.Remove(channel, out var subscription))
            return false;
        subscription.Provider.UnregisterAction();
        subscription.Queue.Writer.TryComplete();
        return true;
    }

    private object Poll(string channel, int max)
    {
        if (!subscriptions.TryGetValue(channel, out var subscription))
            throw new ArgumentException("没有订阅该通道。");
        var data = new List<string>();
        while (data.Count < max && subscription.Queue.Reader.TryRead(out var value))
            data.Add(value);
        return new { channel, data };
    }

    private static void ValidateName(string name)
    {
        if (name.Length > 128 || name.Any(character => !char.IsLetterOrDigit(character) && character is not '_' and not '-' and not '.'))
            throw new ArgumentException("插件名和通道名仅允许字母、数字、下划线、连字符及点，长度不超过 128。");
    }

    private static Type ParseType(string type) => type.ToLowerInvariant() switch
    {
        "bool" or "boolean" => typeof(bool),
        "int" or "int32" => typeof(int),
        "uint" or "uint32" => typeof(uint),
        "long" or "int64" => typeof(long),
        "ulong" or "uint64" => typeof(ulong),
        "float" or "single" => typeof(float),
        "double" => typeof(double),
        "string" => typeof(string),
        _ => throw new ArgumentException("不支持的 IPC 参数类型。"),
    };

    private static (object? Value, Type Type) ParseValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => (element.GetBoolean(), typeof(bool)),
        JsonValueKind.Number when element.TryGetInt32(out var value) => (value, typeof(int)),
        JsonValueKind.Number => (element.GetDouble(), typeof(double)),
        JsonValueKind.String => (element.GetString(), typeof(string)),
        JsonValueKind.Object or JsonValueKind.Array => (element.GetRawText(), typeof(string)),
        JsonValueKind.Null => (null, typeof(object)),
        _ => throw new ArgumentException("不支持的 JSON 参数。"),
    };

    private sealed record Subscription(Channel<string> Queue, ICallGateProvider<string, object> Provider);
}
