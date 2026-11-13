using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameDiagnostics
{
    internal const string ZONE_CAPTURE_TOOL = "set_zone_callback_capture";
    internal const string ZONE_READ_TOOL = "read_zone_callback_capture";
    private Hook<ServerRequestCallbackManager.Delegates.ProcessPacket>? zoneCallback;
    private Hook<ServerRequestCallbackManager.Delegates.Request>? zoneRequest;
    private Hook<ServerRequestCallbackManager.Delegates.RequestSimple>? zoneSimpleRequest;
    private ZoneCapture? zoneCapture;
    [ThreadStatic] private static int RequestDepth;

    public unsafe object SetZoneCapture(ToolArguments input, Func<bool> isAllowed)
    {
        if (!isAllowed()) throw new InvalidOperationException("连接采集权限已关闭。");
        if (!input.Boolean("enabled"))
        {
            if (input.Has("command_ids") || input.Has("metadata_only") || input.Has("duration_seconds") || input.Has("max_records") ||
                input.Has("max_bytes") || input.Has("direction"))
                throw new ArgumentException("停止时不要传采集选项。");
            EndZoneCapture("stopped");
            return new { status = zoneCapture?.Status ?? "no_capture", captureID = zoneCapture?.ID };
        }

        if (zoneCallback is not null || zoneRequest is not null || zoneSimpleRequest is not null) return new { status = "busy" };
        if (!SupportedClientBuild.Value) return new { status = "unsupported_sdk" };
        if (!services.ClientState.IsLoggedIn || services.PlayerState.ContentId == 0)
            return new { status = "not_logged_in" };
        var duration = input.Integer("duration_seconds", 10, 1, 60);
        var direction = input.Text("direction", "receive");
        if (direction is not ("send" or "receive" or "both")) throw new ArgumentException("方向必须为 send、receive 或 both。");
        HashSet<int>? ids = null;
        if (input.Boolean("metadata_only"))
        {
            if (direction != "receive" || input.Has("command_ids") || input.Has("max_bytes"))
                throw new ArgumentException("仅观察元数据时只支持入站，请省略 command_ids 和 max_bytes。");
        }
        else
        {
            var values = input.Element("command_ids");
            if (values.ValueKind != System.Text.Json.JsonValueKind.Array || values.GetArrayLength() is < 1 or > 32)
                throw new ArgumentException("command_ids 必须含 1 至 32 个命令 ID。");
            ids = new HashSet<int>();
            foreach (var value in values.EnumerateArray())
                if (!value.TryGetInt32(out var id) || id < 0 || !ids.Add(id)) throw new ArgumentException("命令 ID 无效或重复。");
        }
        var needReceive = direction != "send";
        var needSend = direction != "receive";
        var bindings = FindCallbackBindings();
        if (needReceive && bindings.Receive == 0 || needSend && !bindings.CanSend)
            return new { status = "entry_unavailable", callbackReceiveEntryAvailable = bindings.Receive != 0, callbackSendEntriesAvailable = bindings.CanSend };
        var capture = new ZoneCapture(ids, Environment.TickCount64 + 1000L * duration,
            services.PlayerState.ContentId, services.ClientState.TerritoryType,
            input.Integer("max_records", 64, 1, 128), ids is null ? 0 : input.Integer("max_bytes", 256, 8, 1024), direction, isAllowed);
        Hook<ServerRequestCallbackManager.Delegates.ProcessPacket>? hook = null;
        Hook<ServerRequestCallbackManager.Delegates.Request>? request = null;
        Hook<ServerRequestCallbackManager.Delegates.RequestSimple>? simple = null;
        zoneCapture = capture;
        try
        {
            if (needReceive) zoneCallback = hook = services.Interop.HookFromAddress<ServerRequestCallbackManager.Delegates.ProcessPacket>(
                bindings.Receive, Receive);
            if (needSend)
            {
                zoneRequest = request = services.Interop.HookFromAddress<ServerRequestCallbackManager.Delegates.Request>(
                    bindings.Send, Request);
                zoneSimpleRequest = simple = services.Interop.HookFromAddress<ServerRequestCallbackManager.Delegates.RequestSimple>(
                    bindings.SendSimple, RequestSimple);
            }
            hook?.Enable();
            request?.Enable();
            simple?.Enable();
        }
        catch
        {
            EndZoneCapture("enable_failed");
            throw;
        }
        return new { status = "active", captureID = capture.ID, scope = "server_request_callback", direction,
            durationSeconds = duration, capture.MetadataOnly, rawZonePayloads = false, receiveStreamIdentification = false };

        void Receive(ServerRequestCallbackManager* manager, int index, int command, void* payload, nuint length)
        {
            try
            {
                capture.RecordReceive(index, command, (nint)payload, length);
            }
            catch (Exception) { Interlocked.Increment(ref capture.Errors); }
            finally { if (hook is not null) hook.OriginalDisposeSafe(manager, index, command, payload, length); }
        }

        bool Request(ServerRequestCallbackManager* manager, ServerRequestCallbackInterface* callback, int command,
            int* arguments, nuint count)
        {
            if (request is null) return false;
            var outer = RequestDepth++ == 0;
            try
            {
                Span<byte> saved = stackalloc byte[Math.Min(capture.ByteLimit, CallbackPayloadReader.MaxArgumentCount * sizeof(int))];
                var length = -1;
                var copied = 0;
                try
                {
                    if (outer && capture.Wants(command))
                    {
                        if (CallbackPayloadReader.TryCopyArguments((nint)arguments, count, saved, out copied))
                            length = (int)count * sizeof(int);
                        else Interlocked.Increment(ref capture.RejectedBuffers);
                    }
                }
                catch (Exception) { length = -1; Interlocked.Increment(ref capture.Errors); }
                var accepted = request.OriginalDisposeSafe(manager, callback, command, arguments, count);
                if (length >= 0) RecordRequest(command, length, saved[..copied], accepted);
                return accepted;
            }
            finally { RequestDepth--; }
        }

        bool RequestSimple(ServerRequestCallbackManager* manager, ServerRequestCallbackInterface* callback,
            int command, int first, int second)
        {
            if (simple is null) return false;
            var outer = RequestDepth++ == 0;
            try
            {
                var accepted = simple.OriginalDisposeSafe(manager, callback, command, first, second);
                if (outer)
                {
                    ReadOnlySpan<int> arguments = [first, second];
                    var bytes = MemoryMarshal.AsBytes(arguments);
                    RecordRequest(command, bytes.Length, bytes, accepted);
                }
                return accepted;
            }
            finally { RequestDepth--; }
        }

        void RecordRequest(int command, int length, ReadOnlySpan<byte> bytes, bool accepted)
        {
            try
            {
                if (!capture.Wants(command)) return;
                capture.Record("send", "callback_i32_le_args", null, command, length, bytes, accepted);
            }
            catch (Exception) { Interlocked.Increment(ref capture.Errors); }
        }
    }

    private static unsafe CallbackBindings FindCallbackBindings()
    {
        if (!SupportedClientBuild.Value) return default;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        if (module is null) return default;
        var receive = ServerRequestCallbackManager.Addresses.ProcessPacket.Value;
        var send = ServerRequestCallbackManager.Addresses.Request.Value;
        var simple = ServerRequestCallbackManager.Addresses.RequestSimple.Value;
        return new(IsGameCode(receive, module) ? receive : 0, IsGameCode(send, module) ? send : 0,
            IsGameCode(simple, module) ? simple : 0);
    }

    public object ReadZoneCapture(ToolArguments input)
    {
        if (zoneCapture is not { } capture) return new { status = "no_capture" };
        if (input.Has("capture_id") && (!Guid.TryParse(input.Text("capture_id"), out var expected) || expected != capture.ID))
            return new { status = "capture_changed" };
        var include = input.Boolean("include_payload");
        if (include && !input.Has("capture_id")) throw new ArgumentException("读取负载必须指定 capture_id。");
        if (include && capture.MetadataOnly) throw new ArgumentException("本次观察没有留存负载。");
        var offset = input.Integer("offset", 0, 0, 128);
        var limit = input.Integer("limit", 20, 1, 32);
        lock (capture.Sync)
        {
            var items = capture.Records.Skip(offset).Take(limit).Select(record => new
            {
                record.Index, record.ElapsedMs, record.CallbackIndex, record.CommandID, record.Length,
                record.Direction, record.Encoding, record.SendAccepted,
                capturedBytes = record.Bytes.Length, truncated = !capture.MetadataOnly && record.Bytes.Length != record.Length,
                hex = include ? Convert.ToHexString(record.Bytes) : null,
            }).ToArray();
            return new { status = capture.Status, captureID = capture.ID, scope = "server_request_callback", capture.Direction,
                capture.MetadataOnly, rawZonePayloads = false, receiveStreamIdentification = false,
                capture.Errors, capture.Contended, capture.RejectedBuffers, buffered = capture.Records.Count, records = items,
                nextOffset = offset + items.Length < capture.Records.Count ? (int?)offset + items.Length : null };
        }
    }

    private void ObserveZoneCapture()
    {
        if (zoneCallback is null && zoneRequest is null && zoneSimpleRequest is null || zoneCapture is not { } capture) return;
        if (!capture.Allowed() || !services.ClientState.IsLoggedIn ||
            services.PlayerState.ContentId != capture.Character || services.ClientState.TerritoryType != capture.Territory)
            EndZoneCapture("context_changed");
        else if (Environment.TickCount64 >= capture.Deadline) EndZoneCapture("expired");
        else
        {
            bool full;
            lock (capture.Sync) full = capture.Records.Count >= capture.Capacity;
            if (full) EndZoneCapture("capacity_reached");
        }
    }

    public void EndZoneCapture(string reason)
    {
        if (zoneCapture is { } capture)
            lock (capture.Sync)
                if (capture.Status == "active") capture.Status = reason;
        var hook = zoneCallback;
        var request = zoneRequest;
        var simple = zoneSimpleRequest;
        zoneCallback = null;
        zoneRequest = null;
        zoneSimpleRequest = null;
        try { hook?.Dispose(); }
        finally
        {
            try { request?.Dispose(); }
            finally { simple?.Dispose(); }
        }
    }

    private sealed class ZoneCapture(HashSet<int>? commands, long deadline, ulong character, uint territory,
        int capacity, int byteLimit, string direction, Func<bool> allowed)
    {
        public readonly object Sync = new();
        public readonly Guid ID = Guid.NewGuid();
        public readonly long Started = Environment.TickCount64;
        public readonly long Deadline = deadline;
        public readonly ulong Character = character;
        public readonly uint Territory = territory;
        public bool MetadataOnly => commands is null;
        public readonly int Capacity = capacity;
        public readonly int ByteLimit = byteLimit;
        public readonly string Direction = direction;
        public readonly Func<bool> Allowed = allowed;
        public readonly List<ZoneRecord> Records = new(capacity);
        public volatile string Status = "active";
        public int Errors;
        public int Contended;
        public int RejectedBuffers;
        public bool IsActive => Status == "active" && Environment.TickCount64 < Deadline;

        public bool Wants(int command) => IsActive && (commands is null || commands.Contains(command)) && Allowed();

        public void RecordReceive(int index, int command, nint payload, nuint length)
        {
            if (!Wants(command)) return;
            if (MetadataOnly)
            {
                Record("receive", "callback_metadata", index, command,
                    length <= CallbackPayloadReader.MaxPayloadLength ? (int?)length : null, [], null);
                return;
            }

            Span<byte> saved = stackalloc byte[ByteLimit];
            if (CallbackPayloadReader.TryCopyPayload(payload, length, saved, out var copied))
                Record("receive", "callback_payload", index, command, (int)length, saved[..copied], null);
            else Interlocked.Increment(ref RejectedBuffers);
        }

        public void Record(string packetDirection, string encoding, int? index, int command, int? length,
            ReadOnlySpan<byte> bytes, bool? accepted)
        {
            if (!Monitor.TryEnter(Sync))
            {
                Interlocked.Increment(ref Contended);
                return;
            }
            try
            {
                if (!IsActive || Records.Count >= Capacity) return;
                Records.Add(new(Records.Count, Environment.TickCount64 - Started, index, command, length,
                    bytes[..Math.Min(bytes.Length, ByteLimit)].ToArray(), packetDirection, encoding, accepted));
            }
            finally { Monitor.Exit(Sync); }
        }
    }

    private sealed record ZoneRecord(int Index, long ElapsedMs, int? CallbackIndex, int CommandID, int? Length,
        byte[] Bytes, string Direction, string Encoding, bool? SendAccepted);

    private readonly record struct CallbackBindings(nint Receive, nint Send, nint SendSimple)
    {
        public bool CanSend => Send != 0 && SendSimple != 0;
    }
}
