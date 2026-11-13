using System.Collections;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace DalamudMCP.Plugin.Tools;

internal sealed class PluginDiscovery : IDisposable
{
    private readonly GameServices services;
    private readonly Func<IExposedPlugin, Assembly?> getLoadedAssembly;
    private readonly Lazy<IPCCatalog> ipcCatalog = new(() => new());
    private readonly PluginStateSnapshots states = new(TimeProvider.System);
    private readonly ICallGateProvider<string, string, bool> statePublisher;

    public PluginDiscovery(GameServices services, Func<IExposedPlugin, Assembly?> getLoadedAssembly)
    {
        this.services = services;
        this.getLoadedAssembly = getLoadedAssembly;
        statePublisher = services.PluginInterface.GetIpcProvider<string, string, bool>(STATE_PUBLISHER);
        statePublisher.RegisterFunc(Publish);
        services.PluginInterface.ActivePluginsChanged += OnPluginsChanged;
    }

    public object Call(string name, ToolArguments input) => name switch
    {
        LIST_TOOL => ListPlugins(input),
        COMMANDS_TOOL => ListCommands(input),
        IPC_TOOL => ListIPC(input),
        STATE_TOOL => GetState(input),
        _ => throw new ArgumentException("未知插件发现工具。"),
    };

    public void Dispose()
    {
        services.PluginInterface.ActivePluginsChanged -= OnPluginsChanged;
        states.Close();
        statePublisher.UnregisterFunc();
    }

    public async Task<object> ShowWindowAsync(ToolArguments input, Func<bool> isAllowed, CancellationToken cancellationToken)
    {
        var name = Filter(input, "plugin_name");
        var surfaceName = input.Text("surface");
        var surface = surfaceName switch
        {
            "main" => PluginWindowRequest.Surface.Main,
            "settings" => PluginWindowRequest.Surface.Settings,
            _ => throw new ArgumentException("surface 必须是 main 或 settings。"),
        };
        var developmentOnly = input.Boolean("development_only");
        var result = await PluginWindowRequest.RunAsync(name, surface, developmentOnly, PluginWindowRequest.DISPATCH_TIMEOUT_MS,
            (request, token) => services.Framework.RunOnTick(() =>
            {
                if (!services.Framework.IsInFrameworkUpdateThread || services.Framework.IsFrameworkUnloading)
                    throw new InvalidOperationException("卫月框架线程当前不可用。");
                return request();
            }, cancellationToken: token), () => services.PluginInterface.InstalledPlugins,
            plugin => (plugin.InternalName, plugin.IsDev, plugin.IsLoaded), GetCallback, isAllowed, cancellationToken)
            .ConfigureAwait(false);
        return new { pluginName = name, surface = surfaceName, developmentOnly, result.Status, result.Requested };

        static Action? GetCallback(IExposedPlugin plugin, PluginWindowRequest.Surface surface)
        {
            if (surface == PluginWindowRequest.Surface.Settings) return plugin.HasConfigUi ? plugin.OpenConfigUi : null;
            return plugin.HasMainUi ? plugin.OpenMainUi : null;
        }
    }

    private bool Publish(string name, string json)
    {
        var source = statePublisher.GetContext()?.SourcePlugin;
        return source is not null && states.Publish(source.Manifest.WorkingPluginId, name, json, () => source.IsLoaded);
    }

    private void OnPluginsChanged(IActivePluginsChangedEventArgs args)
    {
        foreach (var plugin in args.AffectedPlugins) states.Clear(plugin.WorkingPluginId);
    }

    private object ListPlugins(ToolArguments input)
    {
        var query = Filter(input, "query");
        var loadedOnly = input.Boolean("loaded_only");
        var snapshot = services.PluginInterface.InstalledPlugins.Take(MAX_SCAN + 1).ToArray();
        var items = snapshot.Take(MAX_SCAN)
            .Where(plugin => (!loadedOnly || plugin.IsLoaded) &&
                (Matches(plugin.InternalName, query) || Matches(plugin.Name, query)))
            .OrderBy(plugin => plugin.InternalName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plugin => plugin.Version)
            .Select(PluginInfo);
        return Page(items, input, snapshot.Length > MAX_SCAN);
    }

    private object ListCommands(ToolArguments input)
    {
        var query = Filter(input, "query");
        var plugin = OptionalPlugin(input);
        if (plugin is { IsLoaded: false }) return Page(Array.Empty<object>(), input, false, plugin.InternalName, "not_loaded");
        var assembly = plugin is null ? null : RequireAssembly(plugin);
        var commands = services.CommandManager.Commands.Take(MAX_SCAN + 1).ToArray();
        var items = commands.Take(MAX_SCAN)
            .Where(pair => pair.Value.ShowInHelp &&
                (assembly is null || ReferenceEquals(HandlerAssembly(pair.Value.Handler), assembly)))
            .Where(pair => Matches(pair.Key, query) || Matches(pair.Value.HelpMessage, query))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new
            {
                command = pair.Key,
                help = Clip(pair.Value.HelpMessage, 512),
                helpTruncated = pair.Value.HelpMessage is { Length: > 512 },
                handlerAssembly = HandlerAssembly(pair.Value.Handler)?.GetName().Name,
            });
        return Page(items, input, commands.Length > MAX_SCAN, plugin?.InternalName);
    }

    private object ListIPC(ToolArguments input)
    {
        var query = Filter(input, "query");
        var plugin = OptionalPlugin(input);
        if (plugin is { IsLoaded: false }) return Page(Array.Empty<object>(), input, false, plugin.InternalName, "not_loaded");
        var assembly = plugin is null ? null : RequireAssembly(plugin);
        var items = new List<(string Name, string? MatchBasis, Delegate? Function, Delegate? Action)>();
        var scanned = 0;
        foreach (var gate in ipcCatalog.Value.Read())
        {
            if (++scanned > MAX_SCAN) break;
            if (!Matches(gate.Name, query) || gate.Action is null && gate.Function is null) continue;
            var exact = assembly is not null &&
                (ReferenceEquals(HandlerAssembly(gate.Action), assembly) || ReferenceEquals(HandlerAssembly(gate.Function), assembly));
            var byName = plugin is not null && gate.Name.StartsWith(plugin.InternalName + ".", StringComparison.OrdinalIgnoreCase);
            if (plugin is not null && !exact && !byName) continue;
            items.Add((gate.Name, exact ? "handler_assembly" : byName ? "name_prefix" : null, gate.Function, gate.Action));
        }
        var ordered = items.OrderBy(item => item.Name, StringComparer.Ordinal);
        return Page(ordered, input, scanned > MAX_SCAN, plugin?.InternalName,
            map: item => new IPCInfo(item.Name, item.MatchBasis, Signature(item.Function), Signature(item.Action)));
    }

    private object GetState(ToolArguments input)
    {
        var plugin = FindPlugin(Filter(input, "plugin_name"), input.Boolean("development_only"));
        var name = input.Has("state_name") ? input.Text("state_name") : null;
        if (name is not null && !PluginStateSnapshots.IsValidName(name)) throw new ArgumentException("state_name 无效。");
        var isLoaded = plugin.IsLoaded;
        var entries = isLoaded ? states.Read(plugin.Manifest.WorkingPluginId) : [];
        var selected = name is null ? null : entries.SingleOrDefault(entry => entry.Name == name);
        return new
        {
            status = !isLoaded ? "not_loaded" : name is not null && selected is null ? "not_published" : "ok",
            plugin = PluginInfo(plugin),
            publishedStates = entries.Select(entry => new { entry.Name, entry.PublishedAt }),
            state = selected is null ? null : new { selected.Name, selected.PublishedAt, selected.Value },
        };
    }

    private IExposedPlugin? OptionalPlugin(ToolArguments input) =>
        input.Has("plugin_name") ? FindPlugin(Filter(input, "plugin_name")) : null;

    private IExposedPlugin FindPlugin(string name, bool developmentOnly = false)
    {
        var snapshot = services.PluginInterface.InstalledPlugins.Take(MAX_SCAN + 1).ToArray();
        if (snapshot.Length > MAX_SCAN) throw new InvalidOperationException("已安装插件过多，无法确认唯一匹配。");
        var matches = snapshot.Where(plugin => (!developmentOnly || plugin.IsDev) &&
            plugin.InternalName.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] :
            throw new ArgumentException(matches.Length == 0 ? "未找到此插件内部名。" : "插件内部名不唯一。");
    }

    private Assembly RequireAssembly(IExposedPlugin plugin) =>
        getLoadedAssembly(plugin) ?? throw new InvalidOperationException("已加载插件的程序集无法确认，请重新查询。");

    internal void VerifyDeclaredEndpoint(DeclaredPluginEndpoint endpoint, Type[] argumentTypes, Type result) =>
        VerifiedEndpoint(endpoint.PluginName, endpoint.Callgate, argumentTypes, result, endpoint.DevelopmentOnly);

    internal Delegate VerifiedEndpoint(string pluginName, string callgate, Type[] argumentTypes, Type result, bool developmentOnly = false)
    {
        var plugin = FindPlugin(pluginName, developmentOnly);
        if (!plugin.IsLoaded) throw new InvalidOperationException("声明的 IPC 所属插件未加载。");
        var assembly = RequireAssembly(plugin);
        var scanned = 0;
        foreach (var gate in ipcCatalog.Value.Read())
        {
            if (++scanned > MAX_SCAN) break;
            if (!gate.Name.Equals(callgate, StringComparison.Ordinal)) continue;
            var handler = result == typeof(void) ? gate.Action : gate.Function;
            if (handler is null || handler.GetInvocationList().Length != 1 || !ReferenceEquals(HandlerAssembly(handler), assembly))
                break;
            var signature = handler.GetType().GetMethod("Invoke");
            if (signature is not null && signature.ReturnType == result &&
                signature.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(argumentTypes))
                return handler;
            break;
        }
        throw new InvalidOperationException("IPC 已断开，或处理函数的所属程序集与声明签名不匹配。");
    }

    private static object PluginInfo(IExposedPlugin plugin) => new
    {
        plugin.InternalName,
        name = Clip(plugin.Name, 256),
        version = plugin.Version?.ToString(),
        plugin.IsLoaded,
        plugin.IsDev,
        plugin.IsOutdated,
        plugin.IsTesting,
        plugin.HasMainUi,
        plugin.HasConfigUi,
    };

    private static object Page<T>(IEnumerable<T> source, ToolArguments input, bool incomplete, string? pluginName = null,
        string status = "ok", Func<T, object>? map = null)
    {
        var offset = input.Integer("offset", 0, 0, MAX_SCAN);
        var limit = input.Integer("limit", 30, 1, 100);
        var values = source.Skip(offset).Take(limit + 1).ToArray();
        return new
        {
            status, pluginName, scanIncomplete = incomplete, offset,
            nextOffset = values.Length > limit ? offset + limit : (int?)null,
            items = map is null ? values.Take(limit).Cast<object>().ToArray() : values.Take(limit).Select(map).ToArray(),
        };
    }

    private static string Filter(ToolArguments input, string name)
    {
        var value = input.Text(name, string.Empty);
        return value.Length <= 128 ? value : throw new ArgumentException($"{name} 不能超过 128 个字符。");
    }

    private static bool Matches(string? text, string query) =>
        query.Length == 0 || text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private static string? Clip(string? text, int max) => text is { } value && value.Length > max ? value[..max] : text;
    private static Assembly? HandlerAssembly(Delegate? handler) => handler?.Method.DeclaringType?.Assembly;

    private static IPCSignature? Signature(Delegate? handler)
    {
        if (handler is null) return null;
        var invoke = handler.GetType().GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("IPC 委托签名不可用。");
        return new(invoke.GetParameters().Select(parameter => TypeName(parameter.ParameterType)).ToArray(),
            TypeName(invoke.ReturnType), HandlerAssembly(handler)?.GetName().Name);
    }

    private static string TypeName(Type type) => Clip(type.FullName ?? type.Name, 256) ?? type.Name;

    private sealed record IPCSignature(string[] ArgumentTypes, string ReturnType, string? HandlerAssembly);
    private sealed record IPCInfo(string Name, string? MatchBasis, IPCSignature? Function, IPCSignature? Action);

    private sealed class IPCCatalog
    {
        private readonly MethodInfo getService;
        private readonly PropertyInfo gates;
        private readonly PropertyInfo values;
        private readonly PropertyInfo name;
        private readonly PropertyInfo function;
        private readonly PropertyInfo action;

        public IPCCatalog()
        {
            var sdk = typeof(IDalamudPluginInterface).Assembly;
            var serviceType = sdk.GetType("Dalamud.Plugin.Ipc.Internal.CallGate") ?? throw Unsupported();
            var channelType = sdk.GetType("Dalamud.Plugin.Ipc.Internal.CallGateChannel") ?? throw Unsupported();
            var service = sdk.GetType("Dalamud.Service`1")?.MakeGenericType(serviceType) ?? throw Unsupported();
            getService = service.GetMethod("Get", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, Type.EmptyTypes, null) ?? throw Unsupported();
            if (getService.ReturnType != serviceType) throw Unsupported();
            gates = Property(serviceType, "Gates", typeof(IEnumerable));
            values = Property(gates.PropertyType, "Values", typeof(IEnumerable));
            name = Property(channelType, "Name", typeof(string));
            function = Property(channelType, "Func", typeof(Delegate));
            action = Property(channelType, "Action", typeof(Delegate));
        }

        public IEnumerable<(string Name, Delegate? Function, Delegate? Action)> Read()
        {
            var service = getService.Invoke(null, null) ?? throw Unsupported();
            var dictionary = gates.GetValue(service) ?? throw Unsupported();
            if (values.GetValue(dictionary) is not IEnumerable channels) throw Unsupported();
            foreach (var channel in channels)
            {
                if (channel is null || name.GetValue(channel) is not string key) throw Unsupported();
                yield return (key, function.GetValue(channel) as Delegate, action.GetValue(channel) as Delegate);
            }
        }

        private static PropertyInfo Property(Type type, string propertyName, Type valueType)
        {
            var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return property?.GetMethod is not null && property.GetIndexParameters().Length == 0 &&
                valueType.IsAssignableFrom(property.PropertyType) ? property : throw Unsupported();
        }

        private static InvalidOperationException Unsupported() => new("当前卫月版本不支持读取 IPC 注册目录。");
    }

    public const string LIST_TOOL = "list_plugins";
    public const string COMMANDS_TOOL = "list_plugin_commands";
    public const string IPC_TOOL = "list_plugin_ipc";
    public const string STATE_TOOL = "get_plugin_state";
    private const string STATE_PUBLISHER = "DalamudMCP.MCP.PublishState";
    private const int MAX_SCAN = 4096;
}
