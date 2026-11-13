using System.Collections;
using System.Reflection;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class RepositoryPackages
{
    public object Sources()
    {
        var api = bindings.Value;
        var manager = api.Manager();
        return new { catalogReady = api.Value(manager, "ReposReady") is true, sources = api.ReadSources(manager) };
    }

    public object StartRepository(ToolArguments input, Func<bool> isAllowed)
    {
        var command = input.Text("operation");
        if (command == "refresh")
        {
            if (input.Has("repository_url") || input.Has("enabled") || input.Has("expected_enabled"))
                throw new ArgumentException("刷新不接受仓库或状态参数。");
            return Enqueue(new(Guid.NewGuid(), command, null, null, null, "running", null, null), isAllowed);
        }

        if (command is not ("add" or "remove" or "set_enabled")) throw new ArgumentException("未知的仓库操作。");
        var url = RepositoryURL(input.Text("repository_url"));
        bool? expected = command == "add" ? null : input.Boolean("expected_enabled");
        bool? enabled = command == "remove" ? null : input.Boolean("enabled", command != "add");
        if (command == "set_enabled" && !input.Has("enabled") || command == "add" && input.Has("expected_enabled") ||
            command == "remove" && input.Has("enabled") || command != "add" && !input.Has("expected_enabled"))
            throw new ArgumentException("仓库状态参数与所选操作不符。");
        if (command == "add" && !IsPublicRepositoryHost(url))
            throw new ArgumentException("新增仓库须使用公网 DNS 主机名。");
        return Enqueue(new(Guid.NewGuid(), command, null, url, null, "running", null, null,
            ExpectedEnabled: expected, Enabled: enabled), isAllowed);
    }

    private async Task<object> ApplyRepositoryAsync(Bindings api, Operation operation, Func<bool> isAllowed, Action markStarted)
    {
        var changed = false;
        var refresh = await Tick(() =>
        {
            CheckAccess(isAllowed);
            var manager = api.Manager();
            api.RequireReady(manager);
            if (operation.Kind != "refresh")
                changed = api.EditSource(manager, operation, markStarted);
            if (!changed && operation.Kind != "refresh") return Task.CompletedTask;
            markStarted();
            return api.RefreshSources(manager);
        }).ConfigureAwait(false);
        await refresh.ConfigureAwait(false);
        return await Tick(() =>
        {
            var manager = api.Manager();
            var sources = api.ReadSources(manager);
            var target = operation.RepositoryUrl is { } url ? sources.Where(row => SameURL(row.URL, url)).ToArray() : sources;
            if (operation.Kind == "remove" && target.Any(row => row.ConfiguredEnabled.HasValue || row.RuntimeEnabled.HasValue) ||
                operation.Kind is ("add" or "set_enabled") &&
                (target is not [var entry] || entry.ConfiguredEnabled != operation.Enabled ||
                    operation.Enabled is true && entry.RuntimeEnabled is not true ||
                    operation.Enabled is false && entry.RuntimeEnabled is true))
                throw new InvalidOperationException("未确认配置与运行目录一致，请读取当前仓库状态。");
            return (object)new { configurationChanged = changed, saveQueued = changed, sources = target,
                refreshSucceeded = sources.All(item => !item.Ambiguous && (item.RuntimeEnabled != true || item.State == "Success")) };
        }).ConfigureAwait(false);
    }

    internal static bool IsPublicRepositoryHost(string url)
    {
        var uri = new Uri(url);
        var name = uri.IdnHost.TrimEnd('.');
        return uri.HostNameType == UriHostNameType.Dns && !uri.IsLoopback && name.Contains('.') &&
            !name.EndsWith(".local", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed partial class Bindings
    {
        private readonly ConfigurationMembers configuration;
        public bool TestingAllowed => configuration.Testing.GetValue(configuration.Get()) is true;
        public Task RefreshSources(object manager) =>
            (Task)(configuration.Refresh.Invoke(manager, [true]) ?? throw Unsupported("SetPluginReposFromConfigAsync"));

        public RepoSource[] ReadSources(object manager)
        {
            var config = configuration.Get();
            var configured = configuration.Entries(config).Cast<object>().Select(entry =>
                (URL: configuration.SourceURL(entry), Enabled: configuration.Enabled.GetValue(entry) is true)).ToArray();
            var runtime = RuntimeSources(manager);
            var result = new List<RepoSource>();
            foreach (var url in configured.Select(entry => entry.URL).Concat(runtime.Select(entry => entry.URL))
                         .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var cfg = configured.Where(entry => entry.URL == url).Take(2).ToArray();
                var run = runtime.Where(entry => entry.URL == url).Take(2).ToArray();
                var duplicate = cfg.Length > 1 || run.Length > 1;
                result.Add(new(VisibleRepositoryURL(url), cfg is [var setting] ? setting.Enabled : null,
                    run is [var source] ? source.Enabled : null, duplicate ? "ambiguous" : run is [var state] ? state.State : "not_active",
                    ProtectedSource(manager, url), duplicate));
            }
            return [.. result];
        }

        public bool EditSource(object manager, Operation operation, Action markStarted)
        {
            var url = operation.RepositoryUrl ?? throw Unsupported("URL");
            if (ProtectedSource(manager, url)) throw new InvalidOperationException("主仓库和预设仓库不可由 MCP 更改。");
            var config = configuration.Get();
            var entries = configuration.Entries(config);
            var match = entries.Cast<object>().Where(entry => SameURL(configuration.SourceURL(entry), url)).Take(2).ToArray();
            if (operation.Kind == "add")
            {
                if (match.Length != 0 || RuntimeSources(manager).Any(entry => SameURL(entry.URL, url)))
                    throw new InvalidOperationException("仓库已存在。");
                var entry = configuration.Create.Invoke(null);
                configuration.URL.SetValue(entry, url);
                configuration.Enabled.SetValue(entry, operation.Enabled == true);
                markStarted();
                entries.Add(entry);
            }
            else
            {
                if (match is not [var target] || !Equals(configuration.Enabled.GetValue(target), operation.ExpectedEnabled))
                    throw new InvalidOperationException("仓库不存在、来源重复或启用状态已变。");
                if (operation.Kind == "set_enabled" && operation.Enabled == operation.ExpectedEnabled) return false;
                markStarted();
                if (operation.Kind == "remove") entries.Remove(target);
                else configuration.Enabled.SetValue(target, operation.Enabled == true);
            }
            configuration.Save.Invoke(config, null);
            return true;
        }

        private (string URL, bool Enabled, string State)[] RuntimeSources(object manager)
        {
            var main = configuration.Main.GetValue(manager);
            var repos = Items(manager, "Repos", 512).Concat(main is null ? [] : [main]).Distinct(ReferenceEqualityComparer.Instance);
            return repos.Select(repo => (Canonical(Text(repo, "PluginMasterUrl")), Value(repo, "IsEnabled") is true, Text(repo, "State"))).ToArray();
        }

        private bool ProtectedSource(object manager, string url) =>
            configuration.Main.GetValue(manager) is { } main && SameURL(Text(main, "PluginMasterUrl"), url) ||
            configuration.Presets.GetValue(null) is IEnumerable<string> presets && presets.Any(value => SameURL(value, url));

        private static string Canonical(string url)
        {
            try { return RepositoryURL(url); }
            catch (ArgumentException) { return url; }
        }

        private sealed class ConfigurationMembers
        {
            private readonly MethodInfo getConfiguration;
            private readonly object noException;
            private readonly PropertyInfo entries;
            public PropertyInfo Testing { get; }
            public PropertyInfo URL { get; }
            public PropertyInfo Enabled { get; }
            public PropertyInfo Main { get; }
            public FieldInfo Presets { get; }
            public MethodInfo Refresh { get; }
            public MethodInfo Save { get; }
            public ConstructorInfo Create { get; }

            public ConfigurationMembers(Assembly sdk, Type manager, Type repo)
            {
                var config = sdk.GetType("Dalamud.Configuration.Internal.DalamudConfiguration") ?? throw Unsupported("DalamudConfiguration");
                var entry = sdk.GetType("Dalamud.Configuration.ThirdPartyRepoSettings") ?? throw Unsupported("ThirdPartyRepoSettings");
                var service = sdk.GetType("Dalamud.Service`1")?.MakeGenericType(config) ?? throw Unsupported("ConfigurationService");
                getConfiguration = service.GetMethod("GetNullable", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw Unsupported("GetNullable");
                if (getConfiguration.ReturnType != config || getConfiguration.GetParameters() is not [var parameter] ||
                    !parameter.ParameterType.IsEnum) throw Unsupported("Configuration.GetNullable");
                noException = Enum.Parse(parameter.ParameterType, "None");
                entries = Member(config, "ThirdRepoList", typeof(List<>).MakeGenericType(entry));
                Testing = Member(config, "DoPluginTest", typeof(bool));
                URL = Member(entry, "Url", typeof(string));
                Enabled = Member(entry, "IsEnabled", typeof(bool));
                if (URL.SetMethod is null || Enabled.SetMethod is null) throw Unsupported("Repository configuration setter");
                Main = Member(manager, "MainRepo", repo);
                Presets = repo.GetField("PresetRepos", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw Unsupported("PresetRepos");
                if (!typeof(IEnumerable<string>).IsAssignableFrom(Presets.FieldType)) throw Unsupported("PresetRepos");
                Refresh = Bind(manager, "SetPluginReposFromConfigAsync", typeof(Task), typeof(bool));
                Save = Bind(config, "QueueSave", typeof(void));
                Create = entry.GetConstructor(FLAGS, null, Type.EmptyTypes, null) ?? throw Unsupported("Repository constructor");
            }

            public object Get() => getConfiguration.Invoke(null, [noException]) ?? throw Unsupported("Configuration");
            public IList Entries(object config) => entries.GetValue(config) is IList { Count: <= 512 } items
                ? items : throw Unsupported("ThirdRepoList");
            public string SourceURL(object entry) => Canonical(URL.GetValue(entry) as string ?? string.Empty);
            private static PropertyInfo Member(Type type, string name, Type value) =>
                type.GetProperty(name, FLAGS) is { } property && property.PropertyType == value ? property : throw Unsupported(name);
        }

        internal sealed record RepoSource(string URL, bool? ConfiguredEnabled, bool? RuntimeEnabled, string State, bool Protected, bool Ambiguous);
    }
}
