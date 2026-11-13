using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Application.Network;
using FFXIVClientStructs.FFXIV.Client.Network;
using NativeFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameDiagnostics
{
    internal const string ZONE_TRACE_STATUS_TOOL = "inspect_zone_traffic_support";
    internal const string ZONE_TRACE_CONTROL_TOOL = "set_zone_traffic_trace";
    internal const string ZONE_TRACE_READ_TOOL = "read_zone_traffic_trace";

    private Hook<ZoneClient.Delegates.SendPacket>? sendTrace;
    private Hook<PacketDispatcher.Delegates.OnReceivePacket>? receiveTrace;
    private ZoneTrafficBuffer? zoneTrace;
    private static readonly Guid CnSdkNovember13 = new("11763456-61fc-4b0e-9e27-7db51fd84797");
    internal static bool ReadoutLayoutKnown => SupportedClientBuild.Value;

    internal static bool CurrentCnLayoutKnown => CurrentCnBuild.Value;
    private static readonly Lazy<bool> SupportedClientBuild = new(() => NativeLayoutKnown || CurrentCnLayoutKnown);
    private static readonly Lazy<bool> CurrentCnBuild = new(() =>
    {
        if (typeof(ZoneClient).Module.ModuleVersionId != CnSdkNovember13) return false;
        try
        {
            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule;
            if (module is null || !Path.GetFileName(module.FileName).Equals("ffxiv_dx11.exe", StringComparison.OrdinalIgnoreCase))
                return false;
            using var stream = File.Open(module.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var executable = new PEReader(stream);
            var headers = executable.PEHeaders;
            return headers.CoffHeader.TimeDateStamp == 0x6AE8C849 && headers.PEHeader?.SizeOfImage == 0x0381D000;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException
            or System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    });

    public object ZoneTrafficSupport(RawZoneBuild? rawBuild)
    {
        var bindings = FindTrafficBindings();
        var callbacks = FindCallbackBindings();
        var raw = FindRawZoneBindings(rawBuild);
        var rawTraceActive = rawBuild is not null && zoneTrace is { RawByteLimit: > 0, Status: "active" };
        var rawSendAvailable = raw.Send != 0 || rawTraceActive && sendTrace is not null;
        var rawReceiveAvailable = raw.Receive != 0 || rawTraceActive && rawReceiveTrace is not null;
        return new
        {
            status = !SupportedClientBuild.Value ? "unsupported_sdk" : bindings.Send != 0 || bindings.Receive != 0 ? "available" : "unavailable",
            sdkMvid = typeof(ZoneClient).Module.ModuleVersionId,
            sendEvents = bindings.Send != 0, receiveDispatchEvents = bindings.Receive != 0,
            entryVerification = services.SignatureScanner?.IsCopy == true ? "sdk_search_image" : "live_image",
            rawZonePayloads = rawSendAvailable || rawReceiveAvailable, rawSendAvailable, rawReceiveAvailable,
            receiveStreamIdentification = rawBuild is not null, receiveChannel = rawBuild is null ? null : "zone",
            clientRegion = rawBuild is null ? null : "CN", receiveWorldVerified = false,
            sendBufferLengthVerified = rawBuild is not null, receiveBufferLengthVerified = rawBuild is not null,
            rawPayloadStatus = rawBuild is null ? "unverified_build" : rawTraceActive ? "active" :
                rawSendAvailable || rawReceiveAvailable ? "available" : "entry_unavailable",
            callbackReceiveEntryAvailable = callbacks.Receive != 0, callbackSendEntriesAvailable = callbacks.CanSend,
            callbackMetadataOnly = true,
        };
    }

    public unsafe object SetZoneTrafficTrace(ToolArguments input, Func<bool> allowed, RawZoneBuild? rawBuild, OpcodeCatalogCheck.Catalog? catalog = null)
    {
        if (!allowed()) throw new InvalidOperationException("连接采集权限已关闭。");
        if (!input.Boolean("enabled"))
        {
            if (input.Has("direction") || input.Has("duration_seconds") || input.Has("max_events") || input.Has("raw_payload") || input.Has("max_bytes") || input.Has("opcodes"))
                throw new ArgumentException("停止时不要传采集选项。");
            EndZoneTrafficTrace("stopped");
            return new { status = zoneTrace?.Status ?? "no_capture", traceID = zoneTrace?.ID };
        }

        if (sendTrace is not null || receiveTrace is not null || rawReceiveTrace is not null) return new { status = "busy" };
        if (!SupportedClientBuild.Value) return new { status = "unsupported_sdk" };
        if (!services.ClientState.IsLoggedIn || services.PlayerState.ContentId == 0) return new { status = "not_logged_in" };
        var direction = input.Text("direction", "both");
        if (direction is not ("send" or "receive" or "both")) throw new ArgumentException("方向必须为 send、receive 或 both。");
        var duration = input.Integer("duration_seconds", 10, 1, 60);
        var capacity = input.Integer("max_events", 128, 1, 512);
        var rawPayload = input.Boolean("raw_payload");
        if (!rawPayload && (input.Has("max_bytes") || input.Has("opcodes"))) throw new ArgumentException("max_bytes 需要 raw_payload=true。");
        var byteLimit = rawPayload ? input.Integer("max_bytes", 512, 32, 4096) : 0;
        if (rawPayload && rawBuild is null) return new { status = "unverified_build", payloadCaptured = false };
        HashSet<uint>? opcodes = null;
        if (input.Has("opcodes"))
        {
            var items = input.Element("opcodes");
            if (items.ValueKind != System.Text.Json.JsonValueKind.Array || items.GetArrayLength() is < 1 or > 128)
                throw new ArgumentException("opcodes 需要 1 至 128 项。");
            opcodes = [];
            foreach (var item in items.EnumerateArray())
                if (!item.TryGetUInt32(out var value) || value > ushort.MaxValue || !opcodes.Add(value))
                    throw new ArgumentException("操作码无效或重复。");
        }
        var needSend = direction != "receive";
        var needReceive = direction != "send";
        var bindings = rawPayload ? default : FindTrafficBindings();
        var raw = rawPayload ? FindRawZoneBindings(rawBuild) : default;
        var sendEntry = rawPayload ? raw.Send : bindings.Send;
        var receiveEntry = rawPayload ? raw.Receive : bindings.Receive;
        if (needSend && sendEntry == 0 || needReceive && receiveEntry == 0)
            return new { status = "entry_unavailable", sendEvents = sendEntry != 0, receiveDispatchEvents = !rawPayload && receiveEntry != 0,
                rawSendAvailable = raw.Send != 0, rawReceiveAvailable = raw.Receive != 0, rawPayloadRequested = rawPayload };

        var trace = new ZoneTrafficBuffer(direction, duration, capacity, services.PlayerState.ContentId,
            services.ClientState.TerritoryType, bindings.Dispatcher, allowed)
        {
            Opcodes = opcodes, Catalog = catalog, RawByteLimit = byteLimit, ZoneClient = raw.Client, ZoneClientOffset = rawBuild?.ZoneClientOffset ?? 0,
        };
        Hook<ZoneClient.Delegates.SendPacket>? send = null;
        Hook<PacketDispatcher.Delegates.OnReceivePacket>? receive = null;
        Hook<ZoneDequeue>? dequeue = null;
        zoneTrace = trace;
        try
        {
            if (needSend) sendTrace = send = services.Interop.HookFromAddress<ZoneClient.Delegates.SendPacket>(sendEntry, Send);
            if (needReceive && rawPayload) rawReceiveTrace = dequeue = services.Interop.HookFromAddress<ZoneDequeue>(receiveEntry, Dequeue);
            if (needReceive && !rawPayload) receiveTrace = receive = services.Interop.HookFromAddress<PacketDispatcher.Delegates.OnReceivePacket>(receiveEntry, Receive);
            receive?.Enable();
            dequeue?.Enable();
            send?.Enable();
        }
        catch
        {
            EndZoneTrafficTrace("enable_failed");
            throw;
        }
        return new { status = "active", traceID = trace.ID, direction, durationSeconds = duration,
            capacity, payloadCaptured = false, rawPayloadRequested = rawPayload, maxBytes = rawPayload ? (int?)byteLimit : null,
            clientRegion = rawPayload ? "CN" : null, channel = rawPayload ? "zone" : null };

        bool Send(ZoneClient* client, nint packet, uint first, uint second, bool flag)
        {
            if (send is null) return false;
            if (rawPayload && (nint)client == trace.ZoneClient)
            {
                Span<byte> bytes = stackalloc byte[byteLimit];
                PacketSample? sample = null;
                try
                {
                    if (trace.Wants("send"))
                    {
                        sample = ReadSendPacket(packet, bytes);
                        if (sample is null) Interlocked.Increment(ref trace.InvalidPackets);
                    }
                }
                catch (Exception) { Interlocked.Increment(ref trace.Errors); }
                var result = send.OriginalDisposeSafe(client, packet, first, second, flag);
                try { if (sample is { } observed) StorePacket("send", result, observed, bytes); }
                catch (Exception) { Interlocked.Increment(ref trace.Errors); }
                return result;
            }
            var accepted = send.OriginalDisposeSafe(client, packet, first, second, flag);
            try { if (!rawPayload) trace.Observe("send", accepted); }
            catch (Exception) { Interlocked.Increment(ref trace.Errors); }
            return accepted;
        }

        void Receive(PacketDispatcher* dispatcher, uint target, nint packet)
        {
            try
            {
                if ((nint)dispatcher == trace.Dispatcher) trace.Observe("receive", null);
            }
            catch (Exception) { Interlocked.Increment(ref trace.Errors); }
            finally { if (receive is not null) receive.OriginalDisposeSafe(dispatcher, target, packet); }
        }

        byte Dequeue(ZoneClient* client, nint result)
        {
            if (dequeue is null) return 0;
            var accepted = dequeue.OriginalDisposeSafe(client, result);
            try
            {
                if (accepted != 0 && (nint)client == trace.ZoneClient && trace.Wants("receive"))
                {
                    Span<byte> bytes = stackalloc byte[byteLimit];
                    if (ReadReceivedPacket(result, bytes) is { } observed) StorePacket("receive", null, observed, bytes);
                    else Interlocked.Increment(ref trace.InvalidPackets);
                }
            }
            catch (Exception) { Interlocked.Increment(ref trace.Errors); }
            return accepted;
        }

        void StorePacket(string flow, bool? accepted, PacketSample packet, ReadOnlySpan<byte> bytes) =>
            trace.ObservePacket(flow, accepted, new(packet.Opcode, packet.IpcLength, packet.DataLength, packet.DataOffset, packet.Layout), bytes[..packet.Count]);
    }

    public object ReadZoneTrafficTrace(ToolArguments input, bool includeBytes = false)
    {
        if (zoneTrace is not { } trace) return new { status = "no_capture" };
        if (input.Has("trace_id") && (!Guid.TryParseExact(input.Text("trace_id"), "D", out var expected) || expected != trace.ID))
            return new { status = "trace_changed" };
        var offset = input.Integer("offset", 0, 0, 512);
        var direction = input.Has("direction") ? input.Text("direction") : null;
        if (direction is not (null or "send" or "receive")) throw new ArgumentException("方向必须为 send 或 receive。");
        uint? opcode = input.Has("opcode") ? (uint)input.Integer("opcode", 0, 0, ushort.MaxValue) : null;
        return trace.ReadFiltered(offset, input.Integer("limit", includeBytes ? 8 : 40, 1, includeBytes ? 32 : 100), includeBytes, direction, opcode);
    }

    private void ObserveZoneTrafficTrace()
    {
        if (zoneTrace is not { } trace || sendTrace is null && receiveTrace is null && rawReceiveTrace is null) return;
        if (!trace.Allowed() || !services.ClientState.IsLoggedIn ||
            services.PlayerState.ContentId != trace.Character || services.ClientState.TerritoryType != trace.Territory ||
            (trace.RawByteLimit != 0 ? CurrentZoneClient(trace.ZoneClientOffset) != trace.ZoneClient :
                trace.Direction != "send" && !IsSameDispatcher(trace.Dispatcher)))
            EndZoneTrafficTrace("context_changed");
        else if (trace.IsExpired) EndZoneTrafficTrace("expired");
        else if (trace.IsFull) EndZoneTrafficTrace("capacity_reached");
    }

    public void EndZoneTrafficTrace(string reason)
    {
        zoneTrace?.Stop(reason);
        var send = sendTrace;
        var receive = receiveTrace;
        var dequeue = rawReceiveTrace;
        sendTrace = null;
        receiveTrace = null;
        rawReceiveTrace = null;
        try { send?.Dispose(); }
        finally
        {
            try { receive?.Dispose(); }
            finally { dequeue?.Dispose(); }
        }
    }

    private static unsafe TrafficBindings FindTrafficBindings()
    {
        if (!SupportedClientBuild.Value) return default;
        var framework = NativeFramework.Instance();
        var proxy = framework != null && framework->IsNetworkModuleInitialized ? framework->NetworkModuleProxy : null;
        if (proxy == null || proxy->NetworkModule == null) return default;
        var dispatcher = ResolveDispatcher(proxy);
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        if (module is null) return default;
        var send = ZoneClient.Addresses.SendPacket.Value;
        var receive = dispatcher == null || dispatcher->VirtualTable == null ? 0 : (nint)dispatcher->VirtualTable->OnReceivePacket;
        return new(IsGameCode(send, module) ? send : 0, IsGameCode(receive, module) ? receive : 0, (nint)dispatcher);
    }

    private static unsafe PacketDispatcher* ResolveDispatcher(NetworkModuleProxy* proxy)
    {
        if (proxy == null || proxy->NetworkModule == null) return null;
        var callback = proxy->ReceiverCallback;
        if (callback == null || callback != proxy->NetworkModule->PacketReceiverCallback) return null;
        var dispatcher = &callback->PacketDispatcher;
        return dispatcher->NetworkModuleProxy == proxy ? dispatcher : null;
    }

    private static unsafe bool IsSameDispatcher(nint expected)
    {
        var framework = NativeFramework.Instance();
        var proxy = framework != null && framework->IsNetworkModuleInitialized ? framework->NetworkModuleProxy : null;
        return expected != 0 && (nint)ResolveDispatcher(proxy) == expected;
    }

    private static bool IsGameCode(nint address, ProcessModule module)
    {
        if (address == 0 || (nuint)address - (nuint)module.BaseAddress >= (nuint)module.ModuleMemorySize) return false;
        return VirtualQuery(address, out var region, (nuint)Marshal.SizeOf<MemoryRegion>()) != 0 &&
            region.State == 0x1000 && (region.Protect & 0x100) == 0 &&
            (region.Protect & 0xFF) is 0x10 or 0x20 or 0x40 or 0x80;
    }

    internal static bool IsResolvedGameCode(nint address)
    {
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        return module is not null && IsGameCode(address, module);
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nuint VirtualQuery(nint address, out MemoryRegion info, nuint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryRegion
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionID;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    private readonly record struct TrafficBindings(nint Send, nint Receive, nint Dispatcher);
}
