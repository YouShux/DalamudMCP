using System.Collections;
using System.Reflection;
using Dalamud.Plugin;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class DevPluginBuildTools
{
    private int registrationActive;

    public async Task<Dictionary<string, object?>> LoadAsync(ToolArguments input, string hostName,
        Func<bool> isAllowed, CancellationToken cancellationToken)
    {
        var path = NormalizeFilePath(input.Text("file_path"));
        if (!Guid.TryParseExact(input.Text("expected_build_mvid"), "D", out var expected) || expected == Guid.Empty)
            throw new ArgumentException("expected_build_mvid 必须是非空的标准 GUID。");
        var timeoutMs = input.Integer("timeout_ms", DEFAULT_WAIT_MS, MIN_WAIT_MS, MAX_WAIT_MS);
        bool? requestedAutoReload = input.Has("auto_reload") ? input.Boolean("auto_reload") : null;
        var changed = 0;
        if (Interlocked.Exchange(ref registrationActive, 1) != 0) return Result("busy");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        var token = deadline.Token;
        var work = Task.Run(LoadCoreAsync);
        try
        {
            return await work.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _ = work.ContinueWith(static failed => { _ = failed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            cancellationToken.ThrowIfCancellationRequested();
            return Result("timeout");
        }

        async Task<Dictionary<string, object?>> LoadCoreAsync()
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var manifestPath = Path.ChangeExtension(path, ".json");
                DevPluginPackage.CheckPath(path);
                DevPluginPackage.CheckPath(manifestPath);
                using var dll = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var manifest = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (DevPluginPackage.ReadMVID(dll) != expected) return Result("build_changed");
                var reader = runtime.Value;
                var name = DevPluginPackage.ValidateManifest(manifest, Path.GetFileNameWithoutExtension(path), hostName, reader.APIVersion);
                var parsedManifest = reader.ReadManifest(manifestPath, name);
                var (initial, register) = await framework.RunOnTick(() =>
                {
                    CheckFramework();
                    var snapshot = reader.ReadRegistration(path, name);
                    if (snapshot.Instance is not null) return (snapshot, (Task?)null);
                    CheckFramework();
                    Volatile.Write(ref changed, 1);
                    return (snapshot, reader.Register(path, parsedManifest));
                }, cancellationToken: token).ConfigureAwait(false);
                if (register is not null) await register.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                var load = await framework.RunOnTick<Task?>(() =>
                {
                    CheckFramework();
                    var plugin = reader.ReadRegistration(path, name);
                    if (plugin.Instance is null) throw new InvalidOperationException("卫月未注册指定的开发插件。");
                    if (plugin.IsLoaded || plugin.State is "Loading" or "Unloading") return null;
                    CheckFramework();
                    Volatile.Write(ref changed, 1);
                    return reader.Load(plugin.Instance, token);
                }, cancellationToken: token).ConfigureAwait(false);
                if (load is not null) await load.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                var verified = await CallAsync(true, input, token).ConfigureAwait(false);
                verified["stateMayHaveChanged"] = Volatile.Read(ref changed) != 0;
                if (verified["buildVerified"] is not true) return verified;

                var settings = await framework.RunOnTick(() =>
                {
                    CheckFramework();
                    var snapshot = reader.ReadRegistration(path, name);
                    if (!snapshot.IsLoaded || snapshot.LoadedMVID != expected || snapshot.Instance is null)
                        throw new InvalidOperationException("保存开发位置前，运行构建已变化。");
                    var targetAutoReload = requestedAutoReload ?? (initial.Instance is null ? true : (bool?)null);
                    return reader.SaveLocation(path, snapshot.Instance, targetAutoReload, () =>
                    {
                        CheckFramework();
                        Volatile.Write(ref changed, 1);
                    });
                }, cancellationToken: token).ConfigureAwait(false);
                verified["stateMayHaveChanged"] = Volatile.Read(ref changed) != 0;
                verified["autoReload"] = settings.AutoReload;
                verified["saveQueued"] = settings.Saved;
                return verified;
            }
            catch (Exception exception) when (exception is not OperationCanceledException && Volatile.Read(ref changed) != 0)
            {
                return Result("load_failed", (exception as TargetInvocationException)?.InnerException?.Message ?? exception.Message);
            }
            finally { Volatile.Write(ref registrationActive, 0); }
        }

        void CheckFramework()
        {
            token.ThrowIfCancellationRequested();
            if (!framework.IsInFrameworkUpdateThread || framework.IsFrameworkUnloading)
                throw new InvalidOperationException("卫月框架线程当前不可用。");
            if (!isAllowed()) throw new InvalidOperationException("开发插件加载权限已关闭。");
        }

        Dictionary<string, object?> Result(string status, string? error = null) => new()
        {
            ["status"] = status,
            ["filePath"] = path,
            ["expectedBuildMvid"] = expected,
            ["buildVerified"] = false,
            ["stateMayHaveChanged"] = Volatile.Read(ref changed) != 0,
            ["error"] = error,
        };
    }

    private sealed partial class RuntimeReader
    {
        private readonly Lazy<RegistrationMembers> registration = new(() => new());

        public int APIVersion => registration.Value.API.GetValue(null) is int level && level > 0
            ? level : throw Unsupported("DalamudApiLevel");

        public object ReadManifest(string path, string name)
        {
            var members = registration.Value;
            var manifest = members.ReadManifest.Invoke(null, [new FileInfo(path)]);
            if (manifest is null || members.ManifestName.GetValue(manifest) as string != name ||
                members.ManifestAPI.GetValue(manifest) is not int api || api != APIVersion)
                throw new ArgumentException("卫月解析的清单与预检结果不一致。");
            return manifest;
        }

        public Snapshot ReadRegistration(string path, string name)
        {
            if (installedPlugins.GetValue(Manager()) is not IEnumerable plugins) throw Unsupported("InstalledPlugins");
            var count = 0;
            foreach (var plugin in plugins)
            {
                if (++count > 4096) throw new InvalidOperationException("已注册插件过多，无法排除冲突。");
                if (dllFile.GetValue(plugin) is not FileInfo file || internalName.GetValue(plugin) is not string installedName)
                    throw Unsupported("InstalledPlugins");
                if (installedName.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    !file.FullName.Equals(path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("已有同名插件注册在其他路径。");
            }
            var snapshot = Read(path);
            if (snapshot.Instance is not null && !string.Equals(snapshot.InternalName, name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("已注册路径的插件名称与清单不匹配。");
            return snapshot;
        }

        public Task Register(string path, object manifest)
        {
            var members = registration.Value;
            var (_, _, existing) = members.FindLocation(path);
            var location = existing ?? members.NewLocation(path);
            return members.Register.Invoke(Manager(), [new FileInfo(path), manifest, PluginLoadReason.Installer, location, false, true, null]) as Task
                ?? throw Unsupported("LoadPluginAsync");
        }

        public Task Load(object plugin, CancellationToken token) =>
            registration.Value.Load.Invoke(plugin, [PluginLoadReason.Installer, false, token]) as Task ?? throw Unsupported("LoadAsync");

        public (bool AutoReload, bool Saved) SaveLocation(string path, object plugin, bool? requested, Action markChanged)
        {
            var members = registration.Value;
            var (config, locations, existing) = members.FindLocation(path);
            var previous = autoReload.GetValue(plugin) is bool enabled ? enabled : throw Unsupported("AutomaticReload");
            var add = existing is null;
            var location = existing ?? members.NewLocation(path);
            var enable = members.LocationEnabled.GetValue(location) is false;
            var next = requested ?? previous;
            if (!add && !enable && previous == next) return (previous, false);
            markChanged();
            if (previous != next) autoReload.SetValue(plugin, next);
            if (enable) members.LocationEnabled.SetValue(location, true);
            if (add) locations.Add(location);
            members.QueueSave.Invoke(config, null);
            return (next, true);
        }

        private sealed class RegistrationMembers
        {
            private readonly Type locationType;
            private readonly MethodInfo getConfig;
            private readonly PropertyInfo configLocations;
            private readonly PropertyInfo locationPath;

            public RegistrationMembers()
            {
                var assembly = typeof(IDalamudPluginInterface).Assembly;
                var manager = RequireType(assembly, "Dalamud.Plugin.Internal.PluginManager");
                var localPlugin = RequireType(assembly, "Dalamud.Plugin.Internal.Types.LocalPlugin");
                var config = RequireType(assembly, "Dalamud.Configuration.Internal.DalamudConfiguration");
                var manifest = RequireType(assembly, "Dalamud.Plugin.Internal.Types.Manifest.LocalPluginManifest");
                locationType = RequireType(assembly, "Dalamud.Configuration.DevPluginLocationSettings");
                var service = RequireType(assembly, "Dalamud.Service`1").MakeGenericType(config);
                getConfig = Bind(service, "Get", config, true, []);
                API = manager.GetProperty("DalamudApiLevel", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw Unsupported("DalamudApiLevel");
                ReadManifest = Bind(manifest, "Load", manifest, true, [typeof(FileInfo)]);
                ManifestName = GetProperty(manifest, "InternalName", typeof(string));
                ManifestAPI = GetProperty(manifest, "DalamudApiLevel", typeof(int));
                Register = Bind(manager, "LoadPluginAsync", typeof(Task<>).MakeGenericType(localPlugin), false,
                    [typeof(FileInfo), manifest, typeof(PluginLoadReason), locationType, typeof(bool), typeof(bool), localPlugin]);
                Load = Bind(localPlugin, "LoadAsync", typeof(Task), false, [typeof(PluginLoadReason), typeof(bool), typeof(CancellationToken)]);
                QueueSave = Bind(config, "QueueSave", typeof(void), false, []);
                configLocations = GetProperty(config, "DevPluginLoadLocations", typeof(IList));
                locationPath = GetProperty(locationType, "Path", typeof(string));
                LocationEnabled = GetProperty(locationType, "IsEnabled", typeof(bool));
                if (locationPath.SetMethod is null || LocationEnabled.SetMethod is null)
                    throw Unsupported("DevPluginLocationSettings");
            }

            public PropertyInfo API { get; }
            public MethodInfo ReadManifest { get; }
            public PropertyInfo ManifestName { get; }
            public PropertyInfo ManifestAPI { get; }
            public MethodInfo Register { get; }
            public MethodInfo Load { get; }
            public MethodInfo QueueSave { get; }
            public PropertyInfo LocationEnabled { get; }

            public (object Config, IList Locations, object? Existing) FindLocation(string path)
            {
                var config = getConfig.Invoke(null, null) ?? throw Unsupported("DalamudConfiguration");
                if (configLocations.GetValue(config) is not IList locations || locations.Count > 4096)
                    throw Unsupported("DevPluginLoadLocations");
                object? existing = null;
                foreach (var location in locations)
                {
                    if (location is null || locationPath.GetValue(location) is not string candidate) continue;
                    if (!candidate.Equals(path, StringComparison.OrdinalIgnoreCase)) continue;
                    if (existing is not null) throw new InvalidOperationException("该 DLL 对应多个开发插件位置。");
                    existing = location;
                }
                return (config, locations, existing);
            }

            public object NewLocation(string path)
            {
                var location = Activator.CreateInstance(locationType, nonPublic: true) ?? throw Unsupported("DevPluginLocationSettings");
                locationPath.SetValue(location, path);
                LocationEnabled.SetValue(location, true);
                return location;
            }

            private static MethodInfo Bind(Type type, string name, Type result, bool isStatic, Type[] parameters)
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
                var method = type.GetMethod(name, flags, null, parameters, null);
                return method?.ReturnType == result ? method : throw Unsupported($"{type.Name}.{name}");
            }
        }
    }

    public const string LOAD_TOOL = "load_dev_dll";
}
