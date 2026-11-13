using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameDiagnostics
{
    internal const string DISPATCH_RECORDER_TOOL = "set_combat_dispatch_recorder";
    private Hook<ActionEffectHandler.Delegates.Receive>? effectReceiver;
    private DispatchEffectBuffer? dispatchEffects;

    private unsafe bool StartEffectReceiver(Func<bool> allowed, string scope, long until)
    {
        var address = ActionEffectHandler.Addresses.Receive.Value;
        if (!IsResolvedGameCode(address)) return false;
        var buffer = new DispatchEffectBuffer(scope, allowed, until, effectLimit);
        Hook<ActionEffectHandler.Delegates.Receive>? hook = null;
        hook = services.Interop.HookFromAddress<ActionEffectHandler.Delegates.Receive>(
            address, Observe);
        effectReceiver = hook;
        dispatchEffects = buffer;
        try { hook.Enable(); }
        catch
        {
            CloseEffectReceiver();
            throw;
        }
        return true;

        void Observe(uint source, Character* caster, Vector3* position, ActionEffectHandler.Header* header,
            ActionEffectHandler.TargetEffects* targets, GameObjectId* ids)
        {
            try
            {
                var tick = Environment.TickCount64;
                if (Volatile.Read(ref buffer.Ready) && tick < buffer.Until && buffer.Allowed() &&
                    NativeMemoryReader.TryRead(header, out var packet) && packet.ActionId != 0 && packet.NumTargets <= 32)
                {
                    if (IsLightningEffect(packet.ActionId, packet.GlobalSequence))
                    {
                        Span<GameObjectId> mechanicTargets = stackalloc GameObjectId[packet.NumTargets];
                        if (mechanicTargets.IsEmpty || NativeMemoryReader.TryCopy((nint)ids, MemoryMarshal.AsBytes(mechanicTargets)))
                            AppendEvidence("lightning_effect", new
                            {
                                actionID = packet.ActionId, source, sequence = packet.GlobalSequence,
                                targets = mechanicTargets.ToArray().Select(t => t.ObjectId).ToArray(),
                            });
                        else MarkEvidenceDropped();
                    }
                    if (packet.NumTargets == 0)
                    {
                        if ((buffer.Started == 0 || buffer.Scope != "nearby") && !buffer.Actors.Contains(source)) return;
                        AppendEvidence("no_target_effect", new
                        {
                            sourceID = $"0x{source:X}", actionID = packet.ActionId, packet.ActionType, packet.SpellId,
                            packet.GlobalSequence, packet.SourceSequence, packet.RotationInt,
                            rotation = PacketRotation(packet.RotationInt), targetCount = packet.NumTargets,
                            position = NativeMemoryReader.TryRead(position, out var pos) &&
                                float.IsFinite(pos.X) && float.IsFinite(pos.Y) && float.IsFinite(pos.Z) ? Point(pos) : null,
                        });
                        return;
                    }
                    if (!Monitor.TryEnter(buffer.Sync)) Interlocked.Increment(ref buffer.Dropped);
                    else try
                    {
                        if (!buffer.Ready || buffer.Started != 0 && buffer.Truncated) return;
                        Span<GameObjectId> actorIDs = stackalloc GameObjectId[packet.NumTargets];
                        Span<ActionEffectHandler.TargetEffects> values = stackalloc ActionEffectHandler.TargetEffects[packet.NumTargets];
                        if (!NativeMemoryReader.TryCopy((nint)ids, MemoryMarshal.AsBytes(actorIDs)) ||
                            !NativeMemoryReader.TryCopy((nint)targets, MemoryMarshal.AsBytes(values)))
                        {
                            Interlocked.Increment(ref buffer.Dropped);
                            return;
                        }
                        if (buffer.Started == 0) buffer.Events.RemoveAll(entry => tick - entry.ObservedAtMs > 1500);
                        for (byte index = 0; index < packet.NumTargets; index++)
                        {
                            var target = actorIDs[index];
                            var involvesPlayer = buffer.Actors.Contains(source) || buffer.Actors.Contains(target.ObjectId);
                            if ((buffer.Started == 0 || buffer.Scope != "nearby") && !involvesPlayer) continue;
                            if (buffer.Started == 0 && buffer.Events.Count == 256)
                            {
                                buffer.Events.RemoveAt(0);
                                buffer.PreRollDroppedAt = tick;
                            }
                            if (buffer.Events.Count >= buffer.Limit) { buffer.Truncated = true; break; }
                            if (buffer.Started != 0 && packet.GlobalSequence != 0 && !buffer.Seen.Add((packet.GlobalSequence, source, index, target.Id))) continue;
                            var effects = new List<EffectValue>(8);
                            foreach (ref var effect in values[index].Effects)
                                if (effect.Type != 0)
                                    effects.Add(new(effect.Type, effect.Value, effect.Param0, effect.Param1, effect.Param2, effect.Param3, effect.Param4));
                            buffer.Events.Add(new(tick, packet.GlobalSequence, index,
                                packet.ActionType, packet.ActionId, $"0x{source:X}", $"0x{target.Id:X}", null, null, [.. effects]));
                        }
                    }
                    finally { Monitor.Exit(buffer.Sync); }
                }
            }
            catch (Exception) { Interlocked.Increment(ref buffer.Dropped); }
            finally { if (hook is not null) hook.OriginalDisposeSafe(source, caster, position, header, targets, ids); }
        }
    }

    private void UpdateEffectReceiver(long now, bool inCombat)
    {
        if (dispatchEffects is not { } buffer || services.ObjectTable.LocalPlayer is not { } player) return;
        var context = (services.ClientState.TerritoryType, player.GameObjectId);
        if (buffer.Ready && buffer.Context == context && buffer.NextActorsRefresh > now && (buffer.Started != 0 || !inCombat)) return;
        lock (buffer.Sync)
        {
            if (buffer.Context != context)
            {
                if (buffer.Started != 0) return;
                buffer.Events.Clear();
                buffer.PreRollDroppedAt = 0;
                Interlocked.Exchange(ref buffer.Dropped, 0);
                buffer.Context = context;
            }
            buffer.Actors.Clear();
            buffer.Actors.Add(player.EntityId);
            if (buffer.Scope == "party")
                for (var index = 0; index < Math.Min(8, services.PartyList.Length); index++)
                    if (services.PartyList[index]?.GameObject is { } member)
                        buffer.Actors.Add(member.EntityId);
            if (inCombat && buffer.Started == 0)
            {
                buffer.Events.RemoveAll(entry => now - entry.ObservedAtMs > 1500);
                buffer.Started = now;
            }
            Volatile.Write(ref buffer.Ready, true);
            buffer.NextActorsRefresh = now + 250;
        }
    }

    private (ObservedEffect[]? Events, string? Coverage, bool? Truncated) CompleteDispatchedEffects()
    {
        if (dispatchEffects is not { } buffer) return (null, null, null);
        lock (buffer.Sync)
        {
            var started = buffer.Started;
            if (started == 0) return (null, null, null);
            buffer.Started = 0;
            var seen = new HashSet<(uint, byte, string, string)>();
            var entries = buffer.Events.Where(entry => entry.GlobalSequence == 0 || seen.Add((entry.GlobalSequence, entry.TargetIndex, entry.SourceID, entry.TargetID)))
                .Select(entry => entry with { ObservedAtMs = entry.ObservedAtMs - started,
                    BeforeCombat = entry.ObservedAtMs < started ? true : null }).ToArray();
            var dropped = Interlocked.Exchange(ref buffer.Dropped, 0);
            var result = (entries, "client_dispatched_" + buffer.Scope, buffer.Truncated || dropped != 0 ||
                buffer.PreRollDroppedAt != 0 && started - buffer.PreRollDroppedAt <= 1500);
            buffer.Events.Clear();
            buffer.Seen.Clear();
            buffer.Truncated = false;
            buffer.PreRollDroppedAt = 0;
            return result;
        }
    }

    private void CloseEffectReceiver()
    {
        if (dispatchEffects is { } buffer) Volatile.Write(ref buffer.Ready, false);
        dispatchEffects = null;
        var hook = effectReceiver;
        effectReceiver = null;
        hook?.Dispose();
    }

    private int? ReadDispatchEffectCount()
    {
        if (dispatchEffects is not { } buffer) return null;
        lock (buffer.Sync) return buffer.Started != 0 ? buffer.Events.Count : 0;
    }

    private sealed class DispatchEffectBuffer(string scope, Func<bool> allowed, long until, int limit)
    {
        public readonly string Scope = scope;
        public readonly int Limit = limit;
        public readonly Func<bool> Allowed = allowed;
        public readonly long Until = until;
        public readonly object Sync = new();
        public readonly HashSet<uint> Actors = [];
        public readonly HashSet<(uint Sequence, uint Source, byte Index, ulong Target)> Seen = [];
        public readonly List<ObservedEffect> Events = [];
        public long Started;
        public long NextActorsRefresh;
        public long PreRollDroppedAt;
        public (uint Territory, ulong Player) Context;
        public int Dropped;
        public bool Ready;
        public bool Truncated;
    }
}
