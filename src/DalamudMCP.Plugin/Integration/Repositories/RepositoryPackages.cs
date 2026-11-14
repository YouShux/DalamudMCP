using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using Dalamud.Plugin;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class RepositoryPackages(GameServices services, CancellationToken lifetime)
{
    private readonly Lazy<Bindings> bindings = new(() => new(typeof(IDalamudPluginInterface).Assembly));
    private readonly object sync = new();
    private readonly Queue<Operation> history = new();
    private bool running;

    public object Catalog(ToolArguments input)
    {
        var api = bindings.Value;
        var manager = api.Manager();
        var query = input.Text("query", string.Empty);
        if (query.Length > 128) throw new ArgumentException("query 最多 128 个字符。");
        var source = input.Has("repository_url") ? RepositoryURL(input.Text("repository_url")) : null;
        var offset = input.Integer("offset", 0, 0, 10_000);
        var limit = input.Integer("limit", 30, 1, 100);
        var installed = api.Installed(manager).Select(plugin => api.InternalName(plugin)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ready = api.ReadyRepositories(manager);
        var rows = api.Available(manager).Select(manifest => (Manifest: manifest, Repo: api.Repository(manifest)))
            .Where(item => api.Value(item.Repo, "IsThirdParty") is true).Select(item =>
        {
            var (manifest, repo) = item;
            var name = api.InternalName(manifest);
            var url = api.Text(repo, "PluginMasterUrl");
            var apiLevel = api.Value(manifest, "DalamudApiLevel");
            return new
            {
                internalName = name,
                name = api.Text(manifest, "Name"),
                repositoryUrl = VisibleRepositoryURL(url),
                version = api.Value(manifest, "AssemblyVersion")?.ToString(),
                apiLevel,
                testingVersion = api.Value(manifest, "TestingAssemblyVersion")?.ToString(),
                testingApiLevel = api.Value(manifest, "TestingDalamudApiLevel") ?? apiLevel,
                ready = ready.Contains(repo) && IsUsableRepositoryURL(url),
                stable = api.Value(manifest, "IsTestingExclusive") is false,
                installed = installed.Contains(name),
            };
        }).Where(row => (source is null || SameURL(row.repositoryUrl, source)) &&
            (row.internalName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             row.name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(row => row.internalName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.repositoryUrl, StringComparer.Ordinal).Skip(offset).Take(limit + 1).ToArray();
        return new { repositoryCatalogReady = api.Value(manager, "ReposReady") is true, testingAllowed = api.TestingAllowed,
            items = rows.Length > limit ? rows[..limit] : rows,
            nextOffset = rows.Length > limit ? offset + limit : (int?)null };
    }

    public object GetOperation(ToolArguments input)
    {
        var id = ParseID(input.Text("operation_id"));
        lock (sync)
            return history.FirstOrDefault(item => item.ID == id) is { } entry ? (object)entry : new { status = "not_found" };
    }

    public object Start(bool install, ToolArguments input, Func<bool> isAllowed)
    {
        var name = input.Text("plugin_name");
        if (name.Length > 128 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')) ||
            name is "." or ".." || name.Equals(services.PluginInterface.InternalName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("插件内部名无效或指向 MCP 自身。");
        var url = RepositoryURL(input.Text("repository_url"));
        if (!Version.TryParse(input.Text("expected_version"), out var version))
            throw new ArgumentException("expected_version 必须是目录中的精确版本。");
        var operation = new Operation(Guid.NewGuid(), install ? "install" : "uninstall", name, url, version.ToString(),
            "running", null, null, Testing: install && input.Boolean("testing"));
        return Enqueue(operation, isAllowed);
    }

    private object Enqueue(Operation operation, Func<bool> isAllowed)
    {
        CheckAccess(isAllowed);
        lock (sync)
        {
            if (running) return new { status = "busy", operationId = history.Last().ID };
            running = true;
            if (history.Count == 16) history.Dequeue();
            history.Enqueue(operation);
        }
        _ = RunAsync(operation, isAllowed);
        return operation;
    }

    private async Task RunAsync(Operation operation, Func<bool> isAllowed)
    {
        Operation completed;
        var started = false;
        try
        {
            var api = bindings.Value;
            var result = operation.Kind is "install" or "uninstall"
                ? await ApplyPackageAsync(api, operation, isAllowed, () => started = true).ConfigureAwait(false)
                : await ApplyRepositoryAsync(api, operation, isAllowed, () => started = true).ConfigureAwait(false);
            completed = operation with { Status = "completed", Result = result };
        }
        catch (Exception error)
        {
            var message = Regex.Replace(error.GetBaseException().Message, @"https?://\S+", "[仓库地址]", RegexOptions.IgnoreCase);
            completed = operation with { Status = "failed", Error = message[..Math.Min(message.Length, 512)], StateMayHaveChanged = started };
        }
        lock (sync)
        {
            var previous = history.ToArray();
            history.Clear();
            foreach (var item in previous) history.Enqueue(item.ID == completed.ID ? completed : item);
            running = false;
        }
    }

    private async Task<object> ApplyPackageAsync(Bindings api, Operation operation, Func<bool> isAllowed, Action markStarted)
    {
        var install = operation.Kind == "install";
        var name = operation.PluginName ?? throw new InvalidOperationException("插件任务缺少内部名。");
        var url = operation.RepositoryUrl ?? throw new InvalidOperationException("插件任务缺少来源。");
        var version = Version.Parse(operation.ExpectedVersion ?? throw new InvalidOperationException("插件任务缺少版本。"));
        object? target = null;
        var task = await Tick(() =>
        {
            CheckAccess(isAllowed);
            var manager = api.Manager();
            api.RequireReady(manager);
            var installed = api.Installed(manager).Where(p =>
                api.InternalName(p).Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (install)
            {
                if (installed.Length != 0) throw new InvalidOperationException("同名插件已安装或已注册。");
                var manifest = api.Available(manager).Where(m => api.InternalName(m) == name &&
                    SameURL(api.Text(api.Repository(m), "PluginMasterUrl"), url)).Take(2).ToArray();
                if (manifest is not [var candidate] || !api.ReadyRepositories(manager).Contains(api.Repository(candidate)) ||
                    !Equals(api.Value(candidate, operation.Testing ? "TestingAssemblyVersion" : "AssemblyVersion"), version) ||
                    !Equals((operation.Testing ? api.Value(candidate, "TestingDalamudApiLevel") : null) ??
                        api.Value(candidate, "DalamudApiLevel"), api.ApiLevel) ||
                    (operation.Testing ? !api.TestingAllowed : api.Value(candidate, "IsTestingExclusive") is not false))
                    throw new InvalidOperationException("仓库、渠道、版本或 API 不匹配，请重新查询目录。");
                _ = ValidateHTTPS(api.Text(candidate, operation.Testing ? "DownloadLinkTesting" : "DownloadLinkInstall"), true);
                CheckAccess(isAllowed);
                markStarted();
                return api.Install(manager, candidate, operation.Testing);
            }
            if (installed is not [var plugin] || api.InternalName(plugin) != name ||
                !api.IsInstalledTarget(plugin, version, url))
                throw new InvalidOperationException("找不到唯一的同源、同版本已安装插件，或目标为开发插件。");
            target = plugin;
            var state = api.Text(plugin, "State");
            if (state == "Unloaded") return Task.CompletedTask;
            if (state != "Loaded") throw new InvalidOperationException($"插件当前状态不允许卸载：{state}。");
            CheckAccess(isAllowed);
            markStarted();
            return api.Unload(plugin);
        }).ConfigureAwait(false);
        await task.ConfigureAwait(false);
        return await Tick<object>(() =>
        {
            var manager = api.Manager();
            var current = api.Installed(manager).Where(p =>
                api.InternalName(p).Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (install)
            {
                if (current is not [var plugin] || !api.IsInstalledTarget(plugin, version, url) ||
                    !Equals(api.Value(api.Value(plugin, "Manifest"), "Testing"), operation.Testing))
                    throw new InvalidOperationException("安装完成后未核对到预期的来源和版本。");
                return new { installed = true, isLoaded = api.Value(plugin, "IsLoaded"), state = api.Text(plugin, "State"), operation.Testing };
            }
            if (current is not [var pending] || !ReferenceEquals(pending, target) ||
                !api.IsInstalledTarget(pending, version, url) || api.Text(pending, "State") != "Unloaded")
                throw new InvalidOperationException("卸载后无法确认原插件身份，未安排文件清理。");
            CheckAccess(isAllowed);
            api.RequireReady(manager);
            markStarted();
            api.ScheduleDeletion(pending);
            if (api.Value(api.Value(pending, "Manifest"), "ScheduledForDeletion") is not true)
                throw new InvalidOperationException("卫月未确认文件清理计划。");
            api.Remove(manager, pending);
            if (api.Installed(manager).Any(p => api.InternalName(p).Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("文件清理已安排，插件仍在安装列表，请重新核对。");
            return new { removed = true, deletionScheduled = true, configurationPreserved = true };
        }).ConfigureAwait(false);
    }

    private async Task<T> Tick<T>(Func<T> action)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        deadline.CancelAfter(3000);
        var token = deadline.Token;
        return await services.Framework.RunOnTick(() =>
        {
            token.ThrowIfCancellationRequested();
            if (!services.Framework.IsInFrameworkUpdateThread || services.Framework.IsFrameworkUnloading)
                throw new InvalidOperationException("卫月框架线程不可用。");
            return action();
        }, cancellationToken: token).ConfigureAwait(false);
    }

    private void CheckAccess(Func<bool> isAllowed)
    {
        lifetime.ThrowIfCancellationRequested();
        if (!isAllowed()) throw new InvalidOperationException("插件管理权限已关闭。");
    }

    internal static string RepositoryURL(string text) => ValidateHTTPS(text, false);

    private static string ValidateHTTPS(string text, bool allowQuery) =>
        text.Length <= 2048 && Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(uri.Host) && uri.UserInfo.Length == 0 &&
        uri.Fragment.Length == 0 && (allowQuery || uri.Query.Length == 0) ? uri.AbsoluteUri :
        throw new ArgumentException("请提供不带凭据、查询参数或片段的 HTTPS 仓库地址。");

    private static bool IsUsableRepositoryURL(string text)
    {
        try { _ = RepositoryURL(text); return true; }
        catch (ArgumentException) { return false; }
    }

    private static string VisibleRepositoryURL(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 ? uri.AbsoluteUri : "[地址已隐藏]";

    private static bool SameURL(string text, string canonical)
    {
        try { return RepositoryURL(text) == canonical; }
        catch (ArgumentException) { return false; }
    }

    internal static Guid ParseID(string text) =>
        Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty ? id : throw new ArgumentException("ID 必须是非空的标准 GUID。");

    internal sealed record Operation(Guid ID, string Kind, string? PluginName, string? RepositoryUrl, string? ExpectedVersion,
        string Status, object? Result, string? Error, bool StateMayHaveChanged = false, bool Testing = false,
        bool? ExpectedEnabled = null, bool? Enabled = null);

    internal sealed partial class Bindings
    {
        private readonly MethodInfo getManager;
        private readonly object noException;
        private readonly MethodInfo install;
        private readonly MethodInfo unload;
        private readonly MethodInfo schedule;
        private readonly MethodInfo remove;
        private readonly PropertyInfo apiLevel;
        private readonly object waitBeforeDispose;
        private readonly Dictionary<(Type Type, string Name), PropertyInfo> properties = [];

        public Bindings(Assembly sdk)
        {
            Type GetType(string name) => sdk.GetType("Dalamud." + name) ?? throw Unsupported(name);
            var manager = GetType("Plugin.Internal.PluginManager");
            var local = GetType("Plugin.Internal.Types.LocalPlugin");
            var remote = GetType("Plugin.Internal.Types.Manifest.RemotePluginManifest");
            var repo = GetType("Plugin.Internal.Types.PluginRepository");
            var manifest = GetType("Plugin.Internal.Types.Manifest.ILocalPluginManifest");
            var mode = GetType("Plugin.Internal.Types.PluginLoaderDisposalMode");
            var service = GetType("Service`1").MakeGenericType(manager);
            getManager = service.GetMethod("GetNullable", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw Unsupported("GetNullable");
            if (getManager.ReturnType != manager || getManager.GetParameters() is not [var parameter] ||
                !parameter.ParameterType.IsEnum) throw Unsupported("GetNullable");
            noException = Enum.Parse(parameter.ParameterType, "None");
            waitBeforeDispose = Enum.Parse(mode, "WaitBeforeDispose");
            install = Bind(manager, "InstallPluginAsync", typeof(Task<>).MakeGenericType(local), remote, typeof(bool), typeof(PluginLoadReason));
            unload = Bind(local, "UnloadAsync", typeof(Task), mode);
            schedule = Bind(local, "ScheduleDeletion", typeof(void), typeof(bool));
            remove = Bind(manager, "RemovePlugin", typeof(void), local);
            apiLevel = manager.GetProperty("DalamudApiLevel", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw Unsupported("DalamudApiLevel");
            if (apiLevel.PropertyType != typeof(int)) throw Unsupported("DalamudApiLevel");
            Require(manager, "InstalledPlugins", "AvailablePlugins", "Repos", "ReposReady", "PluginsReady", "SafeMode");
            Require(local, "InternalName", "IsDev", "IsLoaded", "State", "EffectiveVersion", "Manifest");
            Require(remote, "InternalName", "Name", "SourceRepo", "AssemblyVersion", "DalamudApiLevel", "IsTestingExclusive",
                "DownloadLinkInstall", "DownloadLinkTesting", "TestingAssemblyVersion", "TestingDalamudApiLevel");
            Require(repo, "PluginMasterUrl", "IsEnabled", "IsThirdParty", "State");
            Require(manifest, "InstalledFromUrl", "ScheduledForDeletion");
            var localManifest = GetType("Plugin.Internal.Types.Manifest.LocalPluginManifest");
            Require(localManifest, "Testing");
            configuration = new(sdk, manager, repo);
        }

        public object Manager() => getManager.Invoke(null, [noException]) ?? throw new InvalidOperationException("卫月插件管理器不可用。");
        public int ApiLevel => apiLevel.GetValue(null) is int value ? value : throw Unsupported("DalamudApiLevel");
        public object[] Installed(object manager) => Items(manager, "InstalledPlugins", 4096);
        public object[] Available(object manager) => Items(manager, "AvailablePlugins", 10_000);
        public string InternalName(object item) => Text(item, "InternalName");
        public object Repository(object item) => Value(item, "SourceRepo") ?? throw Unsupported("SourceRepo");
        public HashSet<object> ReadyRepositories(object manager) => Items(manager, "Repos", 512)
            .Where(repo => Value(repo, "IsThirdParty") is true && Value(repo, "IsEnabled") is true &&
                Text(repo, "State") == "Success").ToHashSet(ReferenceEqualityComparer.Instance);
        public void RequireReady(object manager)
        {
            if (Value(manager, "SafeMode") is not false || Value(manager, "PluginsReady") is not true)
                throw new InvalidOperationException("卫月尚未就绪或处于安全模式。");
        }
        public bool IsInstalledTarget(object plugin, Version version, string url) => Value(plugin, "IsDev") is false &&
            Equals(Value(plugin, "EffectiveVersion"), version) && SameURL(Text(Value(plugin, "Manifest"), "InstalledFromUrl"), url);
        public Task Install(object manager, object manifest, bool testing) =>
            (Task)(install.Invoke(manager, [manifest, testing, PluginLoadReason.Installer]) ?? throw Unsupported("InstallPluginAsync"));
        public Task Unload(object plugin) => (Task)(unload.Invoke(plugin, [waitBeforeDispose]) ?? throw Unsupported("UnloadAsync"));
        public void ScheduleDeletion(object plugin) => schedule.Invoke(plugin, [true]);
        public void Remove(object manager, object plugin) => remove.Invoke(manager, [plugin]);
        public string Text(object? item, string name) => Value(item, name)?.ToString() ?? string.Empty;
        public object? Value(object? item, string name)
        {
            if (item is null) throw Unsupported(name);
            for (var type = item.GetType(); type is not null; type = type.BaseType)
                if (properties.TryGetValue((type, name), out var member)) return member.GetValue(item);
            foreach (var type in item.GetType().GetInterfaces())
                if (properties.TryGetValue((type, name), out var member)) return member.GetValue(item);
            throw Unsupported(name);
        }

        private object[] Items(object parent, string name, int limit)
        {
            if (Value(parent, name) is not IEnumerable source) throw Unsupported(name);
            var items = source.Cast<object>().Take(limit + 1).ToArray();
            return items.Length <= limit ? items : throw new InvalidOperationException($"卫月目录 {name} 超过安全扫描上限。");
        }

        private void Require(Type type, params string[] names)
        {
            foreach (var name in names)
                properties.Add((type, name), type.GetProperty(name, FLAGS) ?? throw Unsupported(name));
        }
        private static MethodInfo Bind(Type type, string name, Type result, params Type[] parameters)
        {
            var method = type.GetMethod(name, FLAGS, null, parameters, null);
            return method?.ReturnType == result ? method : throw Unsupported(name);
        }
        private static InvalidOperationException Unsupported(string member) => new($"当前卫月版本不支持仓库管理接口：{member}。");
        private const BindingFlags FLAGS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    }
}
