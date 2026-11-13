using System.Numerics;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Ipc;
using DalamudMCP.Shared;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Network;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameDiagnostics
{
    internal const string READ_MECHANICS = "read_combat_mechanics";
    internal const string READ_ACR = "read_combat_acr_decisions";
    private readonly object evidenceLock = new();
    private readonly List<FightEvidence> evidence = [];
    private EvidenceWindow? evidenceWindow;
    private int evidenceTerritoryGeneration;
    private void OnEvidenceTerritoryChanged(uint _) => Interlocked.Increment(ref evidenceTerritoryGeneration);
    private int evidenceDropped;
    private int evidenceBytes;
    private int decisionRows;
    private int decisionBytes;
    private int decisionDropped;
    private const int EVIDENCE_LIMIT = 6000;
    private const int EVIDENCE_BYTES = 4 * 1024 * 1024;
    private Hook<PacketDispatcher.Delegates.HandleActorCastPacket>? mechanicCasts;
    private Hook<PacketDispatcher.Delegates.HandleActorControlPacket>? mechanicControls;
    private Hook<ContentDirector.Delegates.ApplyMapEffect>? mechanicFloor;
    private ICallGateProvider<string, string, bool>? acrPublisher;
    private ICallGateProvider<bool>? acrReady;

    internal void StartDecisionPublisher()
    {
        acrPublisher = services.PluginInterface.GetIpcProvider<string, string, bool>("DalamudMCP.Combat.PublishDecision");
        acrPublisher.RegisterFunc(PublishDecision);
        acrReady = services.PluginInterface.GetIpcProvider<bool>("DalamudMCP.Combat.DecisionRecording");
        acrReady.RegisterFunc(CanPublishDecision);
        services.ClientState.TerritoryChanged += OnEvidenceTerritoryChanged;
    }

    internal void StopDecisionPublisher()
    {
        services.ClientState.TerritoryChanged -= OnEvidenceTerritoryChanged;
        acrReady?.UnregisterFunc();
        acrPublisher?.UnregisterFunc();
    }

    private bool CanPublishDecision() => services.Framework.IsInFrameworkUpdateThread &&
        services.Condition[ConditionFlag.InCombat] && Volatile.Read(ref evidenceWindow) is { Started: > 0 } window && window.Allowed();

    private bool PublishDecision(string provider, string payload)
    {
        if (provider is not ("YouShu.PR" or "YouShu.AE") || payload is null or { Length: > 12288 } ||
            !services.Framework.IsInFrameworkUpdateThread || !services.Condition[ConditionFlag.InCombat] ||
            services.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51) ||
            Volatile.Read(ref evidenceWindow) is not { Started: > 0 } window || services.ClientState.TerritoryType != window.Territory ||
            services.ObjectTable.LocalPlayer is not { } player || player.GameObjectId != window.Player) return false;
        var decisionTime = Environment.TickCount64;
        try
        {
            using var data = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 12 });
            if (data.RootElement.ValueKind != JsonValueKind.Object) return false;
            return AppendEvidence("acr_decision", new
            {
                provider, publisherVerified = false, decisionTick = decisionTime, decision = data.RootElement.Clone(),
                player = DecisionActor(player),
                job = readouts.ReadJobMeter(player.ClassJob.RowId),
                target = services.TargetManager.Target is Dalamud.Game.ClientState.Objects.Types.IBattleChara target ? DecisionActor(target) : null,
                party = services.PartyList.Take(8).Select(member => member.GameObject is Dalamud.Game.ClientState.Objects.Types.IBattleChara actor
                    ? DecisionActor(actor) : null).ToArray(),
                action = CaptureCombat(0, false)?.ActionContext,
                recasts = data.RootElement.TryGetProperty("actionIDs", out var ids) && ids.ValueKind == JsonValueKind.Array &&
                    ids.GetArrayLength() <= 8 && services.PlayerState.EffectiveLevel > 0
                    ? readouts.CombatRecasts(ids.EnumerateArray().Where(x => x.TryGetUInt32(out _)).Select(x => x.GetUInt32())
                        .Where(x => x != 0).Distinct().ToArray(), (uint)services.PlayerState.EffectiveLevel) : null,
            });
        }
        catch (Exception) { Interlocked.Exchange(ref decisionDropped, 1); return false; }
    }

    private static object DecisionActor(Dalamud.Game.ClientState.Objects.Types.IBattleChara actor) => new
    {
        id = $"0x{actor.GameObjectId:X}", actor.EntityId, actor.BaseId, jobID = actor.ClassJob.RowId,
        actor.CurrentHp, actor.MaxHp, actor.CurrentMp, actor.ShieldPercentage,
        position = Point(actor.Position), actor.Rotation, actor.HitboxRadius,
        actor.IsTargetable, castActionID = actor.IsCasting ? (uint?)actor.CastActionId : null,
        statuses = actor.StatusList.Where(s => s.StatusId != 0).Take(60).Select(s => new
        { s.StatusId, s.SourceId, s.Param, remainingSeconds = float.IsFinite(s.RemainingTime) ? (float?)s.RemainingTime : null }).ToArray(),
    };

    private static object Point(Vector3 point) => new { point.X, point.Y, point.Z };

    internal bool RecordLocalMechanics(Guid id, MechanicSnapshot snapshot) =>
        IsCurrentFight(id) && Volatile.Read(ref evidenceWindow) is { Started: > 0 } window &&
        window.Territory == snapshot.TerritoryID && AppendEvidence("local_mechanics", snapshot);

    internal (MechanicSnapshot? Snapshot, long Elapsed) ReadLocalMechanics(Guid id, int? at)
    {
        try
        {
            var capture = LoadArchive(id);
            if (capture is null) return (null, -2);
            var row = capture.Evidence?.LastOrDefault(e => e.Kind == "local_mechanics" && (at is null || e.ElapsedMs <= at));
            return row is null ? (null, -1) : (row.Payload.Deserialize<MechanicSnapshot>(ToolJson.Options), row.ElapsedMs);
        }
        catch (Exception error) when (error is InvalidDataException or JsonException) { return (null, -1); }
    }

    private bool IsLightningEffect(uint action, uint sequence) => sequence != 0 &&
        Volatile.Read(ref evidenceWindow) is { Duty: LightningCageRules.Duty } &&
        action is LightningCageRules.Witchgleam or LightningCageRules.Spark2 or LightningCageRules.Spark3;

    private bool lightningChargeSeen;

    private void ObserveLightningCharge()
    {
        if (services.DutyState.ContentFinderCondition.RowId != LightningCageRules.Duty ||
            services.ObjectTable.LocalPlayer is not { } player) return;
        var status = player.StatusList.FirstOrDefault(s => s.StatusId == LightningCageRules.Charge);
        if (lightningChargeSeen)
        {
            if (status is null) lightningChargeSeen = false;
            return;
        }
        if (status is null || !float.IsFinite(status.RemainingTime) || status.RemainingTime <= 0) return;
        var order = status.RemainingTime < 30 ? 1 : 2;
        if (AppendEvidence("lightning_charge", new { order })) lightningChargeSeen = true;
    }

    internal (int? Order, int? Gleams, int? Sparks) LightningCageEvidence(uint playerEntityID)
    {
        if (effectReceiver is null || mechanicCasts is null || fightID is null || partialFight ||
            Volatile.Read(ref evidenceDropped) != 0 ||
            Volatile.Read(ref evidenceWindow) is not { Started: > 0 } window ||
            window.Generation != Volatile.Read(ref evidenceTerritoryGeneration) || !window.Allowed()) return (null, null, null);
        lock (evidenceLock)
        {
            var setup = evidence.FindLastIndex(e => e.Kind == "cast_start" &&
                e.Payload.GetProperty("actionID").GetUInt32() == LightningCageRules.WitchgleamCast);
            if (setup < 0) return (null, null, null);
            var charge = evidence.LastOrDefault(e => e.Kind == "lightning_charge" &&
                e.ElapsedMs >= evidence[setup].ElapsedMs - 10000 && e.ElapsedMs <= evidence[setup].ElapsedMs + 3000);
            var order = charge?.Payload.GetProperty("order").GetInt32();
            var elapsed = Environment.TickCount64 - window.Started;
            var priorGrid = evidence.Skip(setup + 1).Any(e => e.Kind == "cast_start" &&
                e.Payload.GetProperty("actionID").GetUInt32() == LightningCageRules.CellCast &&
                e.ElapsedMs + e.Payload.GetProperty("castTime").GetSingle() * 1000 + 1000 < elapsed);
            var rows = evidence.Skip(setup + 1).Where(e => e.Kind == "lightning_effect").ToArray();
            var gleams = rows.Where(e => e.Payload.GetProperty("actionID").GetUInt32() == LightningCageRules.Witchgleam)
                .GroupBy(e => (e.Payload.GetProperty("source").GetUInt32(), e.Payload.GetProperty("sequence").GetUInt32()))
                .Select(g => g.First()).ToArray();
            var sparks = rows.Where(e => e.Payload.GetProperty("actionID").GetUInt32() is LightningCageRules.Spark2 or LightningCageRules.Spark3)
                .Select(e => (e.Payload.GetProperty("source").GetUInt32(), e.Payload.GetProperty("sequence").GetUInt32())).Distinct().Count();
            if (gleams.Length != 16 || sparks >= 12 || priorGrid && sparks < 4) return (order, null, null);
            return (order, gleams.Count(e => e.Payload.GetProperty("targets").EnumerateArray()
                .Any(t => t.GetUInt32() == playerEntityID)), sparks);
        }
    }

    private static float PacketRotation(ushort rotation) => rotation * (MathF.Tau / ushort.MaxValue) - MathF.PI;

    private bool AppendEvidence(string kind, object payload)
    {
        var window = Volatile.Read(ref evidenceWindow);
        if (window is null || window.Generation != Volatile.Read(ref evidenceTerritoryGeneration) || !window.Allowed()) return false;
        if (!Monitor.TryEnter(evidenceLock)) { MarkEvidenceDropped(); return false; }
        try
        {
            if (!ReferenceEquals(window, evidenceWindow) || window.Generation != Volatile.Read(ref evidenceTerritoryGeneration)) return false;
            var now = Environment.TickCount64;
            if (window.Started == 0) TrimPreCombat(now);
            var json = ToolJson.Element(payload);
            var size = System.Text.Encoding.UTF8.GetByteCount(json.GetRawText());
            var acr = kind == "acr_decision";
            if (acr && (decisionRows >= 700 || decisionBytes + size > EVIDENCE_BYTES / 2))
            {
                Interlocked.Exchange(ref decisionDropped, 1);
                return false;
            }
            if (size > 32768 || evidence.Count >= Math.Min(effectLimit, EVIDENCE_LIMIT) || evidenceBytes + size > EVIDENCE_BYTES)
            {
                if (acr) Interlocked.Exchange(ref decisionDropped, 1);
                else MarkEvidenceDropped();
                return false;
            }
            if (acr) { decisionRows++; decisionBytes += size; }
            evidence.Add(new(window.Started == 0 ? now : now - window.Started, kind, json));
            evidenceBytes += size;
            return true;
        }
        finally { Monitor.Exit(evidenceLock); }
    }

    private void PrepareEvidence(uint territory, ulong player, long started)
    {
        var generation = Volatile.Read(ref evidenceTerritoryGeneration);
        if (recorderAllowed is not { } allowed || Volatile.Read(ref evidenceWindow) is { } existing &&
            existing.Territory == territory && existing.Player == player && existing.Started == started &&
            existing.Generation == generation) return;
        lock (evidenceLock)
        {
            if (evidenceWindow is not { Started: 0 } prior || prior.Territory != territory || prior.Player != player ||
                prior.Generation != generation)
                EndEvidence();
            else if (started != 0)
            {
                TrimPreCombat(started);
                for (var i = 0; i < evidence.Count; i++)
                    evidence[i] = evidence[i] with { ElapsedMs = evidence[i].ElapsedMs - started };
            }
            Volatile.Write(ref evidenceWindow, new(territory, player, started, generation,
                services.DutyState.ContentFinderCondition.RowId, allowed));
        }
    }

    private void TrimPreCombat(long now)
    {
        var count = 0;
        while (count < evidence.Count && (evidence[count].ElapsedMs < now - 1500 || evidence.Count - count > 128))
            evidenceBytes -= System.Text.Encoding.UTF8.GetByteCount(evidence[count++].Payload.GetRawText());
        if (count != 0) evidence.RemoveRange(0, count);
    }

    private (FightEvidence[] Events, bool Truncated, bool DecisionsTruncated) EndEvidence()
    {
        lock (evidenceLock)
        {
            Volatile.Write(ref evidenceWindow, null);
            var result = (evidence.ToArray(), Interlocked.Exchange(ref evidenceDropped, 0) != 0,
                Interlocked.Exchange(ref decisionDropped, 0) != 0);
            evidence.Clear();
            evidenceBytes = 0;
            decisionRows = 0;
            decisionBytes = 0;
            lightningChargeSeen = false;
            return result;
        }
    }

    private unsafe void StartMechanics()
    {
        if (!CurrentCnLayoutKnown) return;
        TryStart(PacketDispatcher.Addresses.HandleActorCastPacket.Value, (PacketDispatcher.Delegates.HandleActorCastPacket)OnCast, ref mechanicCasts);
        TryStart(PacketDispatcher.Addresses.HandleActorControlPacket.Value, (PacketDispatcher.Delegates.HandleActorControlPacket)OnControl, ref mechanicControls);
        TryStart(ContentDirector.Addresses.ApplyMapEffect.Value, (ContentDirector.Delegates.ApplyMapEffect)OnFloor, ref mechanicFloor);
    }

    private void TryStart<T>(nint address, T observer, ref Hook<T>? field) where T : Delegate
    {
        if (!IsResolvedGameCode(address)) return;
        try { field = services.Interop.HookFromAddress(address, observer); field.Enable(); }
        catch (Exception error)
        {
            field?.Dispose();
            field = null;
            services.Log.Warning(error, "战斗机制观察入口不可用。");
        }
    }

    private void StopMechanics()
    {
        mechanicCasts?.Dispose(); mechanicCasts = null;
        mechanicControls?.Dispose(); mechanicControls = null;
        mechanicFloor?.Dispose(); mechanicFloor = null;
    }

    private unsafe void OnCast(uint source, ActorCastPacket* packet)
    {
        var hook = mechanicCasts;
        try
        {
            if (Volatile.Read(ref evidenceWindow) is null || !NativeMemoryReader.TryRead(packet, out var cast) ||
                cast.ActionId == 0 || !float.IsFinite(cast.CastTime)) return;
            var pos = cast.PositionInt;
            const float SCALE = 2000f / ushort.MaxValue;
            AppendEvidence("cast_start", new
            {
                sourceID = $"0x{source:X}", actionID = cast.ActionId, cast.SpellId, cast.ActionType,
                targetID = $"0x{cast.TargetEntityId:X}", cast.CastTime, cast.Interruptible, cast.OmenDelay,
                position = new { x = pos[0] * SCALE - 1000f, y = pos[1] * SCALE - 1000f, z = pos[2] * SCALE - 1000f },
                rotation = PacketRotation(cast.RotationInt), origin = "client_actor_cast",
            });
        }
        catch (Exception) { MarkEvidenceDropped(); }
        finally { hook?.OriginalDisposeSafe(source, packet); }
    }

    private void OnControl(uint actor, uint category, uint p1, uint p2, uint p3, uint p4,
        uint p5, uint p6, uint p7, uint p8, GameObjectId target, bool replay)
    {
        var hook = mechanicControls;
        try
        {
            if (replay || Volatile.Read(ref evidenceWindow) is null) return;
            switch (category)
            {
                case TARGET_ICON:
                    AppendEvidence("target_icon", new { actorID = $"0x{actor:X}", iconID = p1, rawIconID = p1, argument2 = p2, idEncoding = "cn_client_handler_zero_delta" });
                    break;
                case TETHER:
                    AppendEvidence("tether", new { sourceID = $"0x{actor:X}", tetherID = p2, targetID = $"0x{p3:X}" });
                    break;
                case TETHER_CANCEL:
                    AppendEvidence("tether_cancel", new { sourceID = $"0x{actor:X}", tetherID = p2 });
                    break;
            }
        }
        catch (Exception) { MarkEvidenceDropped(); }
        finally { hook?.OriginalDisposeSafe(actor, category, p1, p2, p3, p4, p5, p6, p7, p8, target, replay); }
    }

    private unsafe void OnFloor(ContentDirector* director, uint index, ushort state, ushort timeline)
    {
        var hook = mechanicFloor;
        try
        {
            if (Volatile.Read(ref evidenceWindow) is not null)
                AppendEvidence("map_effect", new { index, state, timelineIndex = timeline, flag = state | ((uint)timeline << 16) });
        }
        catch (Exception) { MarkEvidenceDropped(); }
        finally { hook?.OriginalDisposeSafe(director, index, state, timeline); }
    }

    private void MarkEvidenceDropped() => Interlocked.Exchange(ref evidenceDropped, 1);

    private const uint TARGET_ICON = 34;
    private const uint TETHER = 35;
    private const uint TETHER_CANCEL = 47;

    private string[] MechanicSources() =>
        [.. new[] { (mechanicCasts is not null, "cast_start"), (mechanicControls is not null, "actor_control"),
            (mechanicFloor is not null, "map_effect"), (effectReceiver is not null, "no_target_effect") }.Where(x => x.Item1).Select(x => x.Item2)];

    private sealed record EvidenceWindow(uint Territory, ulong Player, long Started, int Generation, uint Duty, Func<bool> Allowed);
    private sealed record FightEvidence(long ElapsedMs, string Kind, JsonElement Payload);
}
