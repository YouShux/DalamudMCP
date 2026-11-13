using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;

namespace DalamudMCP.Plugin.Tools;

internal sealed class RuntimeObservations(IGameInteropProvider interop, RuntimeInspector runtime) : IDisposable
{
    private readonly Dictionary<Guid, Observation> observations = [];
    private static readonly ConcurrentDictionary<MethodBase, Observation> Managed = new();

    public object Start(ToolArguments input, Func<bool> allowed)
    {
        if (!allowed()) throw new InvalidOperationException("运行时观察权限已关闭。");
        if (observations.Count >= 16) return new { status = "capacity_reached" };
        var kind = input.Text("kind");
        var duration = input.Integer("duration_seconds", 10, 1, 300);
        var capacity = input.Integer("max_records", 256, 1, 4096);
        var observation = new Observation(kind, duration, capacity, allowed);
        try
        {
            if (kind == "memory")
            {
                observation.Address = checked((nint)input.ObjectID("address"));
                observation.Length = input.Integer("length", 32, 1, 256);
                observation.Interval = input.Integer("interval_ms", 100, 50, 1000);
                if (observation.Address == 0) throw new ArgumentException("地址不能为空。");
            }
            else if (kind == "event")
            {
                var (target, member) = runtime.ReferencedMember(input);
                if (member is not EventInfo evt || evt.EventHandlerType is not { } handlerType || evt.AddMethod?.IsPublic != true || evt.RemoveMethod?.IsPublic != true)
                    throw new ArgumentException("需要可公开订阅和退订的事件。");
                var invoke = handlerType.GetMethod("Invoke") ?? throw new ArgumentException("事件委托不可用。");
                var parameters = invoke.GetParameters();
                if (invoke.ReturnType != typeof(void) || parameters.Length > 16 || parameters.Any(parameter => parameter.ParameterType.IsByRef || parameter.ParameterType.IsPointer || parameter.ParameterType.IsByRefLike))
                    throw new ArgumentException("事件参数不支持被动观察。");
                var arguments = parameters.Select(parameter => Expression.Parameter(parameter.ParameterType)).ToArray();
                var body = Expression.Call(Expression.Constant(observation), typeof(Observation).GetMethod(nameof(Observation.Capture)) ?? throw new MissingMethodException(),
                    Expression.NewArrayInit(typeof(object), arguments.Select(argument => Expression.Convert(argument, typeof(object)))));
                var handler = Expression.Lambda(handlerType, body, arguments).Compile();
                observation.Cleanup = () => evt.RemoveEventHandler(target, handler);
                evt.AddEventHandler(target, handler);
            }
            else if (kind == "native") InstallNative(input, observation);
            else if (kind == "managed") InstallManaged(input, observation);
            else throw new ArgumentException("kind 必须为 memory、event、native 或 managed。");
            if (!allowed()) throw new InvalidOperationException("运行时观察权限已关闭。");
            observations.Add(observation.ID, observation);
            return new { status = "active", observationID = observation.ID, kind, durationSeconds = duration, maxRecords = capacity };
        }
        catch
        {
            observation.Stop("start_failed");
            if (!observation.CleanupSucceeded) observations[observation.ID] = observation;
            throw;
        }
    }

    public object Read(ToolArguments input, bool stop = false, bool remove = false)
    {
        Tick();
        if (!input.Has("observation_id")) return new { status = "ok", observations = observations.Values.Select(item => item.Read(0, 0)).ToArray() };
        var id = Guid.ParseExact(input.Text("observation_id"), "D");
        if (!observations.TryGetValue(id, out var observation)) return new { status = "not_found" };
        if (stop || remove) observation.Stop(remove ? "removed" : "stopped");
        var result = observation.Read(input.Integer("offset", 0, 0, 4096), input.Integer("limit", 50, 1, 100));
        if (remove && observation.CleanupSucceeded) observations.Remove(id);
        return result;
    }

    public void Tick()
    {
        foreach (var observation in observations.Values)
        {
            if (!observation.Active) continue;
            try
            {
                if (!observation.Allowed()) { observation.Stop("permission_revoked"); continue; }
                if (Environment.TickCount64 >= observation.Until) { observation.Stop("expired"); continue; }
                if (observation.Full) { observation.Stop("capacity_reached"); continue; }
                if (observation.Kind != "memory" || Environment.TickCount64 < observation.NextSample) continue;
                observation.NextSample = Environment.TickCount64 + observation.Interval;
                var bytes = new byte[observation.Length];
                if (!NativeMemoryReader.TryCopy(observation.Address, bytes)) { observation.Stop("unreadable"); continue; }
                if (observation.LastBytes is { } previous && previous.AsSpan().SequenceEqual(bytes)) continue;
                observation.LastBytes = bytes;
                observation.Capture([Convert.ToHexString(bytes)]);
            }
            catch (Exception error) { observation.Stop("observation_error:" + error.GetType().Name); }
        }
    }

    private void InstallNative(ToolArguments input, Observation observation)
    {
        var (type, method, address) = RuntimeInspector.ResolveSdkEntry(input);
        if (!method.GetCustomAttributesData().Any(attribute => attribute.AttributeType.Name == "MemberFunctionAttribute"))
            throw new ArgumentException("原生观察仅支持有明确 SDK 函数声明的入口。");
        var delegateType = type.GetNestedType("Delegates", BindingFlags.Public | BindingFlags.NonPublic)?.GetNestedType(method.Name, BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SDK 未提供该函数的委托。");
        var invoke = delegateType.GetMethod("Invoke") ?? throw new MissingMethodException();
        var types = invoke.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        var expectedTypes = method.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        if (!method.IsStatic) expectedTypes = [type.MakePointerType(), .. expectedTypes];
        if (!types.SequenceEqual(expectedTypes) || invoke.ReturnType != method.ReturnType)
            throw new InvalidOperationException("SDK 委托与包装函数签名不匹配。");
        var convention = delegateType.GetCustomAttribute<UnmanagedFunctionPointerAttribute>()?.CallingConvention ?? CallingConvention.Winapi;
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || types.Length > 16 || types.Any(type => !NativeScalar(type)) || invoke.ReturnType != typeof(void) && !NativeScalar(invoke.ReturnType) ||
            convention is not (CallingConvention.Winapi or CallingConvention.Cdecl))
            throw new InvalidOperationException("原生委托 ABI 不在支持范围内。");
        var thunk = new DynamicMethod("ObserveSdkEntry", invoke.ReturnType, [typeof(Observation), .. types], typeof(RuntimeObservations).Module, true);
        var il = thunk.GetILGenerator();
        var forward = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, typeof(Observation).GetMethod(nameof(Observation.Wants)) ?? throw new MissingMethodException());
        il.Emit(OpCodes.Brfalse, forward);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, types.Length);
        il.Emit(OpCodes.Newarr, typeof(object));
        for (var index = 0; index < types.Length; index++)
        {
            il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldc_I4, index); il.Emit(OpCodes.Ldarg, index + 1);
            if (types[index].IsPointer) { il.Emit(OpCodes.Conv_I); il.Emit(OpCodes.Box, typeof(nint)); }
            else il.Emit(OpCodes.Box, types[index]);
            il.Emit(OpCodes.Stelem_Ref);
        }
        il.Emit(OpCodes.Callvirt, typeof(Observation).GetMethod(nameof(Observation.Capture)) ?? throw new MissingMethodException());
        il.MarkLabel(forward);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, typeof(Observation).GetMethod(nameof(Observation.Original)) ?? throw new MissingMethodException());
        il.Emit(OpCodes.Castclass, delegateType);
        for (var index = 0; index < types.Length; index++) il.Emit(OpCodes.Ldarg, index + 1);
        il.Emit(OpCodes.Callvirt, invoke);
        il.Emit(OpCodes.Ret);
        var callback = thunk.CreateDelegate(delegateType, observation);
        var factory = typeof(IGameInteropProvider).GetMethods().Single(method => method.Name == "HookFromAddress" && method.IsGenericMethodDefinition &&
            method.GetParameters()[0].ParameterType == typeof(nint));
        var parameters = factory.GetParameters();
        var args = new object?[parameters.Length];
        args[0] = address;
        args[1] = callback;
        for (var index = 2; index < args.Length; index++)
            args[index] = parameters[index].HasDefaultValue ? parameters[index].DefaultValue : throw new InvalidOperationException("SDK Hook 接口不匹配。");
        var hook = factory.MakeGenericMethod(delegateType).Invoke(interop, args) ?? throw new InvalidOperationException("无法创建原生观察。");
        observation.NativeHook = hook;
        observation.Cleanup = () => ((IDisposable)hook).Dispose();
        observation.OriginalProperty = hook.GetType().GetProperty("OriginalDisposeSafe") ?? throw new MissingMemberException("OriginalDisposeSafe");
        (hook.GetType().GetMethod("Enable") ?? throw new MissingMethodException("Enable")).Invoke(hook, null);
    }

    private void InstallManaged(ToolArguments input, Observation observation)
    {
        var (target, member) = runtime.ReferencedMember(input);
        if (member is not MethodInfo method || method.IsAbstract || method.IsGenericMethod || method.GetMethodBody() is null ||
            method.GetParameters().Any(parameter => parameter.ParameterType.IsByRef || parameter.ParameterType.IsByRefLike || parameter.ParameterType.IsPointer))
            throw new ArgumentException("此托管方法不支持前置观察。");
        var candidates = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("HarmonyLib.Harmony", false)).OfType<Type>().ToArray();
        if (candidates.Length != 1) throw new InvalidOperationException("宿主未提供唯一的 Harmony 观察后端。");
        var harmony = candidates[0];
        var wrapperType = harmony.Assembly.GetType("HarmonyLib.HarmonyMethod") ?? throw new InvalidOperationException("Harmony 接口不匹配。");
        var patches = harmony.GetMethods().Where(candidate => candidate.Name == "Patch" && candidate.GetParameters().Length >= 2 &&
            candidate.GetParameters()[0].ParameterType == typeof(MethodBase) && candidate.GetParameters().Skip(1).All(parameter => parameter.ParameterType == wrapperType)).ToArray();
        var unpatch = harmony.GetMethod("Unpatch", [typeof(MethodBase), typeof(MethodInfo)]);
        if (patches.Length != 1 || unpatch is null) throw new InvalidOperationException("Harmony 接口不匹配。");
        var instance = Activator.CreateInstance(harmony, "DalamudMCP.observer." + observation.ID.ToString("N")) ?? throw new InvalidOperationException("无法创建观察后端。");
        var prefix = typeof(RuntimeObservations).GetMethod(nameof(ObserveManaged), BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingMethodException();
        var wrapper = Activator.CreateInstance(wrapperType, prefix) ?? throw new InvalidOperationException("无法创建前置观察。");
        observation.Target = target;
        if (!Managed.TryAdd(method, observation)) throw new InvalidOperationException("该方法正在被观察。");
        observation.Cleanup = () =>
        {
            Managed.TryRemove(method, out _);
            unpatch.Invoke(instance, [method, prefix]);
        };
        var args = new object?[patches[0].GetParameters().Length];
        args[0] = method; args[1] = wrapper;
        patches[0].Invoke(instance, args);
    }

    private static void ObserveManaged(MethodBase __originalMethod, object? __instance, object?[] __args)
    {
        if (Managed.TryGetValue(__originalMethod, out var observation) && ReferenceEquals(__instance, observation.Target)) observation.Capture(__args);
    }

    private static bool NativeScalar(Type type) => type.IsPointer || type.IsEnum && NativeScalar(Enum.GetUnderlyingType(type)) ||
        type.IsPrimitive && type != typeof(bool) && type != typeof(char) && type != typeof(void);

    public void Dispose()
    {
        foreach (var observation in observations.Values) observation.Stop("unloaded");
        if (observations.Values.Any(item => !item.CleanupSucceeded)) throw new InvalidOperationException("部分运行时观察未能解除。");
        observations.Clear();
    }

    private sealed class Observation(string kind, int duration, int capacity, Func<bool> allowed)
    {
        private readonly object sync = new();
        private readonly long started = Environment.TickCount64;
        private readonly List<object> records = [];
        private volatile string status = "active";
        private int dropped;
        public readonly Guid ID = Guid.NewGuid();
        public readonly string Kind = kind;
        public readonly Func<bool> Allowed = allowed;
        public readonly long Until = Environment.TickCount64 + duration * 1000L;
        public Action? Cleanup;
        public object? NativeHook;
        public PropertyInfo? OriginalProperty;
        public object? Target;
        public nint Address;
        public int Length;
        public int Interval;
        public long NextSample;
        public byte[]? LastBytes;
        public bool Active => status == "active";
        public bool CleanupSucceeded => !status.StartsWith("cleanup_failed", StringComparison.Ordinal);
        public bool Full { get { lock (sync) return records.Count >= capacity; } }

        public object Original() => OriginalProperty?.GetValue(NativeHook) ?? throw new InvalidOperationException("原生观察缺少原函数。");

        public bool Wants()
        {
            try { return Active && Environment.TickCount64 < Until && Allowed() && !Full; }
            catch (Exception) { return false; }
        }

        public void Capture(object?[] arguments)
        {
            try
            {
                if (!Active || Environment.TickCount64 >= Until || !Allowed()) return;
                if (!Monitor.TryEnter(sync)) { Interlocked.Increment(ref dropped); return; }
                try
                {
                    if (!Active || records.Count >= capacity) return;
                    records.Add(new { index = records.Count, elapsedMs = Environment.TickCount64 - started,
                        arguments = arguments.Take(16).Select(Scalar).ToArray(), truncated = arguments.Length > 16 });
                }
                finally { Monitor.Exit(sync); }
            }
            catch (Exception) { Interlocked.Increment(ref dropped); }
        }

        public void Stop(string reason)
        {
            if (!Active && CleanupSucceeded) return;
            status = reason;
            try { Cleanup?.Invoke(); }
            catch (Exception error) { status = "cleanup_failed:" + error.GetType().Name; }
        }

        public object Read(int offset, int limit)
        {
            lock (sync) return new { observationID = ID, kind = Kind, status, buffered = records.Count, dropped = Volatile.Read(ref dropped),
                records = Allowed() ? records.Skip(offset).Take(limit).ToArray() : [],
                nextOffset = limit > 0 && offset + limit < records.Count ? (int?)offset + limit : null };
        }

        private static object? Scalar(object? value) => value switch
        {
            null => null,
            string text => new { text = text[..Math.Min(128, text.Length)], truncated = text.Length > 128 },
            nint pointer => $"0x{pointer:X}",
            nuint pointer => $"0x{pointer:X}",
            long or ulong => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            float single when !float.IsFinite(single) => single.ToString(System.Globalization.CultureInfo.InvariantCulture),
            double number when !double.IsFinite(number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ when value.GetType().IsPrimitive || value.GetType().IsEnum => value,
            _ => new { type = value.GetType().FullName },
        };
    }
}
