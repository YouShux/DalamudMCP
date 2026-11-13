using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DalamudMCP.Plugin.Tools;

public sealed partial class GameTools
{
    private readonly object callbackSync = new();
    private CallbackObservation? callbackObservation;

    private async Task<object> ObserveAddonCallbacksAsync(ToolArguments input, Func<bool> allowed, CancellationToken token, bool nodeListener, bool allWindows = false)
    {
        var name = allWindows ? "*" : input.Text("addon_name");
        if (!allWindows && (name.Length > 32 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException("addon_name 必须是明确的原生窗口名。");
        var addonID = allWindows ? 0 : input.Integer("expected_addon_id", 0, 1, ushort.MaxValue);
        var duration = input.Integer("duration_ms", 3000, 250, 10000);
        var limit = input.Integer("limit", 24, 1, 64);
        var query = nodeListener ? AddonNodeQuery.Parse(input, false) : null;
        if (query is not null && query.NodeID is null && query.Label is null) throw new ArgumentException("需要节点 ID 或完整标签。");
        var eventType = input.Has("event_type") ? (byte?)input.Integer("event_type", 0, 0, byte.MaxValue) : null;
        var parameter = input.Has("event_param") ? input.Element("event_param").GetUInt32() : (uint?)null;
        var observation = new CallbackObservation(name, (ushort)addonID, duration, limit, input.Boolean("include_text"), allowed, token) { IncludeRawEvent = input.Boolean("include_raw_event") };
        try
        {
            var status = await services.Framework.RunOnFrameworkThread(() => StartCallbackObservation(observation, token, query, eventType, parameter))
                .WaitAsync(token).ConfigureAwait(false);
            if (status != "active") return new { status, addonName = name, addonID };
            await Task.WhenAny(Task.Delay(duration, token), observation.Finalized.Task).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return observation.Snapshot();
        }
        finally { StopCallbackObservation(observation); }
    }

    private unsafe string StartCallbackObservation(CallbackObservation observation, CancellationToken token, AddonNodeQuery? query,
        byte? eventType, uint? parameter)
    {
        lock (callbackSync)
        {
            token.ThrowIfCancellationRequested();
            if (callbackObservation is not null) return "busy";
            if (!observation.Allowed() || services.Framework.IsFrameworkUnloading) return "unavailable";
            if (!GameDiagnostics.CurrentCnLayoutKnown) return "unsupported_sdk";
            var addon = services.GameGui.GetAddonByName(observation.Name, 1);
            if (!observation.AllWindows && (addon.IsNull || !addon.IsReady || !addon.IsVisible || ((AtkUnitBase*)addon.Address)->Id != observation.AddonID))
                return "target_changed";
            observation.Address = addon.Address;
            if (query is not null) return StartNodeListener((AtkUnitBase*)addon.Address, observation, query, eventType, parameter);
            var address = AtkUnitBase.Addresses.FireCallback.Value;
            if (!GameDiagnostics.IsResolvedGameCode(address)) return "entry_unavailable";
            Hook<AtkUnitBase.Delegates.FireCallback>? hook = null;
            hook = services.Interop.HookFromAddress<AtkUnitBase.Delegates.FireCallback>(address, Observe);
            EnableObservation(observation, hook, hook.Enable);
            return "active";

            bool Observe(AtkUnitBase* target, uint count, AtkValue* values, bool close)
            {
                if (hook is null) return false;
                CallbackEvent? entry = null;
                try { entry = observation.Copy(target, count, values, close); }
                catch (Exception) { Interlocked.Increment(ref observation.Dropped); }
                var accepted = hook.OriginalDisposeSafe(target, count, values, close);
                try { if (entry is not null) observation.Add(entry with { Accepted = accepted }); }
                catch (Exception) { Interlocked.Increment(ref observation.Dropped); }
                return accepted;
            }
        }
    }

    private void EnableObservation(CallbackObservation observation, IDisposable hook, Action enable)
    {
        observation.Hook = hook;
        callbackObservation = observation;
        try
        {
            if (!observation.AllWindows)
            {
                services.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, observation.Name, observation.OnFinalize);
                observation.Subscribed = true;
            }
            observation.Clock.Start();
            enable();
        }
        catch
        {
            StopCallbackObservation(observation);
            throw;
        }
    }

    private unsafe string StartNodeListener(AtkUnitBase* addon, CallbackObservation observation, AddonNodeQuery query,
        byte? eventType, uint? parameter)
    {
        var scan = AddonNodeScanner.Scan(addon, query, true, true, 1);
        if (scan.Incomplete) return "scan_incomplete";
        if (scan.ScopeStatus == "ambiguous" || scan.MatchCount > 1) return "ambiguous_node";
        if (scan.MatchCount == 0 || !NativeMemoryReader.TryRead((AtkResNode*)scan.Matches[0].Address, out var node)) return "node_not_found";
        var nodeAddress = scan.Matches[0].Address;
        var seen = new HashSet<nint>();
        nint registered = 0;
        nint listenerAddress = 0;
        AtkEventType registeredType = default;
        uint registeredParam = 0;
        for (var next = node.AtkEventManager.Event; next != null;)
        {
            if (seen.Count == 64 || !seen.Add((nint)next) || !NativeMemoryReader.TryRead(next, out var entry)) return "scan_incomplete";
            if (entry.Listener != null && (!eventType.HasValue || eventType.Value == (byte)entry.State.EventType) &&
                (!parameter.HasValue || parameter.Value == entry.Param))
            {
                if (registered != 0) return "ambiguous_listener";
                registered = (nint)next;
                listenerAddress = (nint)entry.Listener;
                registeredType = entry.State.EventType;
                registeredParam = entry.Param;
            }
            next = entry.NextEvent;
        }
        if (registered == 0) return "no_listener";
        if (!NativeMemoryReader.TryRead((AtkEventListener*)listenerAddress, out var listener) || listener.VirtualTable == null ||
            !NativeMemoryReader.TryRead((nint*)&listener.VirtualTable->ReceiveEvent, out var address) || !GameDiagnostics.IsResolvedGameCode(address))
            return "entry_unavailable";
        observation.NodeID = node.NodeId;
        Hook<AtkEventListener.Delegates.ReceiveEvent>? hook = null;
        hook = services.Interop.HookFromAddress<AtkEventListener.Delegates.ReceiveEvent>(address, Observe);
        EnableObservation(observation, hook, hook.Enable);
        return "active";

        void Observe(AtkEventListener* target, AtkEventType type, int eventParam, AtkEvent* ev, AtkEventData* data)
        {
            try
            {
                if ((nint)target != listenerAddress || type != registeredType || unchecked((uint)eventParam) != registeredParam ||
                    !observation.CanObserve() || !NativeMemoryReader.TryRead(ev, out var live) ||
                    (nint)live.Listener != listenerAddress || ((nint)ev != registered && (nint)live.Node != nodeAddress && (nint)live.Target != nodeAddress) ||
                    !NativeMemoryReader.TryRead(&((AtkResNode*)nodeAddress)->NodeId, out var currentNode) || currentNode != node.NodeId) return;
                string? eventHex = null;
                string? dataHex = null;
                if (observation.IncludeRawEvent)
                {
                    Span<byte> eventBytes = stackalloc byte[sizeof(AtkEvent)];
                    Span<byte> dataBytes = stackalloc byte[sizeof(AtkEventData)];
                    if (NativeMemoryReader.TryCopy((nint)ev, eventBytes)) eventHex = Convert.ToHexString(eventBytes);
                    if (NativeMemoryReader.TryCopy((nint)data, dataBytes)) dataHex = Convert.ToHexString(dataBytes);
                }
                observation.Add(new ListenerEvent(observation.Clock.ElapsedMilliseconds, node.NodeId, (int)type, type.ToString(), eventParam, eventHex, dataHex));
            }
            catch (Exception) { Interlocked.Increment(ref observation.Dropped); }
            finally { if (hook is not null) hook.OriginalDisposeSafe(target, type, eventParam, ev, data); }
        }
    }

    private void StopCallbackObservation(CallbackObservation? expected = null)
    {
        lock (callbackSync)
        {
            if (callbackObservation is not { } observation || expected is not null && !ReferenceEquals(observation, expected)) return;
            callbackObservation = null;
            observation.Finalized.TrySetResult();
            try
            {
                if (observation.Subscribed)
                    services.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, observation.Name, observation.OnFinalize);
            }
            finally { observation.Hook?.Dispose(); }
        }
    }

    private sealed class CallbackObservation(string name, ushort addonID, int duration, int limit, bool includeText,
        Func<bool> allowed, CancellationToken token)
    {
        private readonly object sync = new();
        private readonly List<object> events = new(limit);
        private volatile bool full;

        public string Name => name;
        public bool AllWindows => name == "*";
        public bool IncludeRawEvent;
        public ushort AddonID => addonID;
        public Func<bool> Allowed => allowed;
        public nint Address;
        public bool Subscribed;
        public IDisposable? Hook;
        public uint? NodeID;
        public int Dropped;
        public readonly Stopwatch Clock = new();
        public readonly TaskCompletionSource Finalized = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnFinalize(AddonEvent type, AddonArgs args)
        {
            if (type == AddonEvent.PreFinalize && args.Addon.Address == Address) Finalized.TrySetResult();
        }

        public bool CanObserve() => !Finalized.Task.IsCompleted && !token.IsCancellationRequested &&
            allowed() && Clock.ElapsedMilliseconds < duration;

        public unsafe CallbackEvent? Copy(AtkUnitBase* target, uint count, AtkValue* values, bool close)
        {
            if ((!AllWindows && (nint)target != Address) || !CanObserve() || !NativeMemoryReader.TryRead(target, out var actual) ||
                (!AllWindows && actual.Id != addonID)) return null;
            if (full) { Interlocked.Increment(ref Dropped); return null; }
            var length = (int)Math.Min(count, 16);
            Span<AtkValue> args = stackalloc AtkValue[length];
            var readable = length == 0 || NativeMemoryReader.TryCopy((nint)values, MemoryMarshal.AsBytes(args));
            var output = new CallbackValue[readable ? length : 0];
            Span<byte> text = stackalloc byte[192];
            for (var i = 0; i < output.Length; i++)
            {
                var value = args[i];
                object? scalar = value.Type switch
                {
                    AtkValueType.Bool => value.Bool,
                    AtkValueType.Int => value.Int,
                    AtkValueType.UInt => value.UInt,
                    AtkValueType.Int64 => value.Int64,
                    AtkValueType.UInt64 => value.UInt64,
                    AtkValueType.Float when float.IsFinite(value.Float) => value.Float,
                    AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString when includeText =>
                        NativeMemoryReader.ReadCString((nint)value.String.Value, text) is var size && size >= 0 ? Encoding.UTF8.GetString(text[..size]) : null,
                    _ => null,
                };
                output[i] = new(value.Type.ToString(), scalar);
            }
            return new(Clock.ElapsedMilliseconds, count, close, false, output, !readable || count > (uint)length, actual.Id, actual.NameString);
        }

        public void Add(object entry)
        {
            if (!Monitor.TryEnter(sync)) { Interlocked.Increment(ref Dropped); return; }
            try
            {
                if (events.Count == limit) { Interlocked.Increment(ref Dropped); return; }
                events.Add(entry);
                full = events.Count == limit;
            }
            finally { Monitor.Exit(sync); }
        }

        public object Snapshot()
        {
            lock (sync)
            {
                var permitted = allowed();
                return new
                {
                    status = !permitted ? "permission_revoked" : Finalized.Task.IsCompleted ? "target_closed" : events.Count == 0 ? "no_events" : "observed",
                    addonName = name, addonID, durationMs = duration, nodeID = NodeID,
                    source = NodeID.HasValue ? "registered_node_listener" : "addon_fire_callback",
                    events = permitted ? events.ToArray() : [], dropped = Volatile.Read(ref Dropped),
                };
            }
        }
    }

    private sealed record CallbackEvent(long ElapsedMs, uint ArgumentCount, bool Close, bool Accepted, CallbackValue[] Arguments, bool ArgumentsTruncated, ushort AddonID, string AddonName);
    private sealed record CallbackValue(string Type, object? Value);
    private sealed record ListenerEvent(long ElapsedMs, uint NodeID, int EventType, string EventName, int EventParam, string? EventHex, string? DataHex);
}
