using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class DevPluginBuildTools(IFramework framework)
{
    private readonly Lazy<RuntimeReader> runtime = new(() => new());
    private int activeRequests;

    internal Assembly? GetLoadedPluginAssembly(IExposedPlugin plugin) => runtime.Value.GetLoadedAssembly(plugin);

    public async Task<Dictionary<string, object?>> CallAsync(bool waitForBuild, ToolArguments input, CancellationToken cancellationToken)
    {
        var path = NormalizeFilePath(input.Text("file_path"));
        Guid? expectedMVID = null;
        var timeoutMs = STATE_TIMEOUT_MS;
        if (waitForBuild)
        {
            if (!Guid.TryParseExact(input.Text("expected_build_mvid"), "D", out var expected) || expected == Guid.Empty)
                throw new ArgumentException("expected_build_mvid 必须是非空的标准 GUID。");
            expectedMVID = expected;
            timeoutMs = input.Integer("timeout_ms", DEFAULT_WAIT_MS, MIN_WAIT_MS, MAX_WAIT_MS);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = deadline.Token;
        var clock = Stopwatch.StartNew();
        Snapshot? last = null;
        if (Interlocked.Increment(ref activeRequests) > MAX_REQUESTS)
        {
            Interlocked.Decrement(ref activeRequests);
            return Result("busy");
        }

        deadline.CancelAfter(timeoutMs);
        var work = Task.Run(ObserveAsync);
        try
        {
            var status = await work.WaitAsync(token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Result(status);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _ = work.ContinueWith(static failed => { _ = failed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            cancellationToken.ThrowIfCancellationRequested();
            return Result("timeout");
        }

        async Task<string> ObserveAsync()
        {
            try
            {
                Snapshot? matching = null;
                long matchingSince = 0;
                long lastMatch = 0;
                var sawRegistration = false;

                while (clock.ElapsedMilliseconds < timeoutMs)
                {
                    token.ThrowIfCancellationRequested();
                    if (new DriveInfo(path[..3]).DriveType != DriveType.Fixed)
                        throw new ArgumentException("file_path 必须位于本机固定磁盘上。");
                    var snapshot = await framework.RunOnTick(ReadRuntime, cancellationToken: token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (snapshot.Instance is not null) snapshot = ReadDisk(path, snapshot);
                    token.ThrowIfCancellationRequested();
                    Volatile.Write(ref last, snapshot);
                    var now = clock.ElapsedMilliseconds;
                    if (now >= timeoutMs) break;

                    if (snapshot.Instance is null && !sawRegistration) return "unregistered";
                    if (!waitForBuild) return "snapshot";
                    sawRegistration |= snapshot.Instance is not null;
                    if (snapshot.FileMVID is Guid fileMVID && fileMVID != expectedMVID) return "build_changed";

                    if (snapshot.BuildMatches)
                    {
                        if (matching is null || !ReferenceEquals(matching.Instance, snapshot.Instance)
                            || !ReferenceEquals(matching.LoadedAssembly, snapshot.LoadedAssembly)
                            || now - lastMatch > MAX_SAMPLE_GAP_MS)
                            matchingSince = now;
                        matching = snapshot;
                        lastMatch = now;
                        if (now - matchingSince >= STABLE_MS) return "ready";
                    }
                    else
                    {
                        matching = null;
                    }

                    await Task.Delay(POLL_MS, token).ConfigureAwait(false);
                }

                return "timeout";
            }
            finally
            {
                Interlocked.Decrement(ref activeRequests);
            }
        }

        Snapshot ReadRuntime()
        {
            token.ThrowIfCancellationRequested();
            if (!framework.IsInFrameworkUpdateThread || framework.IsFrameworkUnloading)
                throw new InvalidOperationException("卫月框架线程当前不可用。");
            return runtime.Value.Read(path);
        }

        Dictionary<string, object?> Result(string status)
        {
            var snapshot = Volatile.Read(ref last);
            var result = new Dictionary<string, object?>
            {
                ["status"] = status,
                ["filePath"] = path,
                ["registered"] = snapshot is null ? null : snapshot.Instance is not null,
                ["internalName"] = snapshot?.InternalName,
                ["isLoaded"] = snapshot?.IsLoaded,
                ["autoReload"] = snapshot?.AutoReload,
                ["state"] = snapshot?.State,
                ["fileBuildMvid"] = snapshot?.FileMVID,
                ["loadedBuildMvid"] = snapshot?.LoadedMVID,
                ["buildMatches"] = snapshot?.BuildMatches ?? false,
                ["fileReadError"] = snapshot?.FileReadError,
            };
            if (waitForBuild)
            {
                result["expectedBuildMvid"] = expectedMVID;
                result["buildVerified"] = status == "ready";
                result["elapsedMs"] = clock.ElapsedMilliseconds;
            }
            return result;
        }
    }

    private static string NormalizeFilePath(string path)
    {
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')
            || path.AsSpan(2).Contains(':'))
            throw new ArgumentException("file_path 必须是本机磁盘上的 DLL 绝对路径。");

        var fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("file_path 必须指向 .dll 文件。");
        return fullPath;
    }

    private static Snapshot ReadDisk(string path, Snapshot snapshot)
    {
        try
        {
            DevPluginPackage.CheckPath(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return snapshot with { FileMVID = DevPluginPackage.ReadMVID(stream) };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return snapshot with { FileReadError = exception.Message };
        }

    }

    private sealed record Snapshot(object? Instance, string? InternalName = null, bool IsLoaded = false,
                                   bool? AutoReload = null, string? State = null, Assembly? LoadedAssembly = null,
                                   Guid? LoadedMVID = null, Guid? FileMVID = null, string? FileReadError = null)
    {
        public bool BuildMatches => IsLoaded && LoadedMVID.HasValue && LoadedMVID == FileMVID;
    }

    private sealed partial class RuntimeReader
    {
        private readonly MethodInfo getManager;
        private readonly object noExceptionPropagation;
        private readonly PropertyInfo installedPlugins;
        private readonly PropertyInfo dllFile;
        private readonly PropertyInfo internalName;
        private readonly PropertyInfo isDev;
        private readonly PropertyInfo isLoaded;
        private readonly PropertyInfo autoReload;
        private readonly PropertyInfo state;
        private readonly PropertyInfo loadedAssembly;
        private readonly Lazy<FieldInfo> exposedInstance;

        public RuntimeReader()
        {
            var assembly = typeof(IDalamudPluginInterface).Assembly;
            var managerType = RequireType(assembly, "Dalamud.Plugin.Internal.PluginManager");
            var localPlugin = RequireType(assembly, "Dalamud.Plugin.Internal.Types.LocalPlugin");
            var localDevPlugin = RequireType(assembly, "Dalamud.Plugin.Internal.Types.LocalDevPlugin");
            var service = RequireType(assembly, "Dalamud.Service`1").MakeGenericType(managerType);
            getManager = service.GetMethod("GetNullable", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw Unsupported("Service.GetNullable");
            if (getManager.GetParameters() is not [var parameter] || !parameter.ParameterType.IsEnum
                || !Enum.IsDefined(parameter.ParameterType, "None") || getManager.ReturnType != managerType)
                throw Unsupported("Service.GetNullable");
            noExceptionPropagation = Enum.Parse(parameter.ParameterType, "None");

            installedPlugins = GetProperty(managerType, "InstalledPlugins", typeof(IEnumerable));
            dllFile = GetProperty(localPlugin, "DllFile", typeof(FileInfo));
            internalName = GetProperty(localPlugin, "InternalName", typeof(string));
            isDev = GetProperty(localPlugin, "IsDev", typeof(bool));
            isLoaded = GetProperty(localPlugin, "IsLoaded", typeof(bool));
            autoReload = GetProperty(localDevPlugin, "AutomaticReload", typeof(bool));
            state = GetProperty(localPlugin, "State", typeof(Enum));
            loadedAssembly = GetProperty(localPlugin, "Assembly", typeof(Assembly));
            exposedInstance = new(() =>
            {
                var exposed = RequireType(assembly, "Dalamud.Plugin.ExposedPlugin");
                var fields = exposed.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Where(field => field.FieldType == localPlugin).ToArray();
                return fields is [var field] ? field : throw Unsupported("ExposedPlugin.LocalPlugin");
            });
        }

        private object Manager() => getManager.Invoke(null, [noExceptionPropagation])
            ?? throw new InvalidOperationException("卫月插件管理器尚未就绪。");

        public Assembly? GetLoadedAssembly(IExposedPlugin plugin)
        {
            var field = exposedInstance.Value;
            if (plugin.GetType() != field.DeclaringType || field.GetValue(plugin) is not { } instance)
                throw Unsupported("ExposedPlugin.LocalPlugin");
            return isLoaded.GetValue(instance) is true ? loadedAssembly.GetValue(instance) as Assembly : null;
        }

        public Snapshot Read(string path)
        {
            if (installedPlugins.GetValue(Manager()) is not IEnumerable plugins) throw Unsupported("InstalledPlugins");

            object? match = null;
            foreach (var plugin in plugins)
            {
                if (dllFile.GetValue(plugin) is not FileInfo file) throw Unsupported("DllFile");
                if (!string.Equals(file.FullName, path, StringComparison.OrdinalIgnoreCase)) continue;
                if (match is not null) throw new InvalidOperationException("该路径对应多个已注册插件，无法确认目标。");
                match = plugin;
            }

            if (match is null) return new(null);
            if (isDev.GetValue(match) is not true) throw new ArgumentException("仅支持已注册的开发插件 DLL。");

            var loaded = isLoaded.GetValue(match) is true;
            var assembly = loaded ? loadedAssembly.GetValue(match) as Assembly : null;
            return new(match, internalName.GetValue(match) as string, loaded, autoReload.GetValue(match) as bool?,
                state.GetValue(match)?.ToString(), assembly, assembly?.ManifestModule.ModuleVersionId);
        }

        private static Type RequireType(Assembly assembly, string name) => assembly.GetType(name) ?? throw Unsupported(name);

        private static PropertyInfo GetProperty(Type type, string name, Type valueType)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return property?.GetMethod is not null && property.GetIndexParameters().Length == 0
                && valueType.IsAssignableFrom(property.PropertyType)
                ? property : throw Unsupported($"{type.Name}.{name}");
        }

        private static InvalidOperationException Unsupported(string member) => new($"当前卫月版本不支持开发插件状态查询：{member}。");
    }

    public const string STATE_TOOL = "get_dev_build";
    public const string WAIT_TOOL = "wait_for_dev_build";
    public const int MIN_WAIT_MS = 1_000;
    public const int MAX_WAIT_MS = 20_000;
    public const int DEFAULT_WAIT_MS = 15_000;
    private const int STATE_TIMEOUT_MS = 3_000;
    private const int MAX_REQUESTS = 2;
    private const int POLL_MS = 100;
    private const int MAX_SAMPLE_GAP_MS = 3 * POLL_MS;
    private const int STABLE_MS = 750;
}
