using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameDiagnostics
{
    private readonly List<CombatFrame> fight = new(300);
    private Func<bool>? recorderAllowed;
    private Task<ArchiveOutcome>? pendingArchive;
    private ArchiveOutcome? lastArchive;
    private readonly Queue<(Guid ID, CombatCapture Capture)> archiveQueue = new();
    private long armedUntil;
    private int effectLimit = 20000;
    private bool continuousManaged;
    private bool continuousBlocked;
    private string? continuousError;
    private bool partialFight;
    private int savedArchives;
    private int failedArchives;
    private string? archiveError;
    private int QueuedArchives { get { lock (archiveQueue) return archiveQueue.Count; } }
    private ulong armedCharacter;
    private long nextCombatSample;
    private long fightStarted;
    private int sampleInterval = 100;
    private bool includeParty;
    private bool fightTruncated;
    private int droppedFights;
    private bool recordEffects;
    private string fightEffectScope = "self";
    private CombatEffects? fightEffects;
    private (uint Territory, ulong Actor) baselineOwner;

    public object SetRecorder(ToolArguments input, Func<bool> isAllowed, bool dispatched)
    {
        if (!input.Boolean("enabled"))
        {
            if (recorderAllowed is not null && (effectReceiver is not null) != dispatched)
                throw new InvalidOperationException("请用启动当前归档的工具停止。");
            if (input.Has("duration_seconds") || input.Has("sample_interval_ms") || input.Has("include_party") ||
                input.Has("include_action_effects") || input.Has("action_effect_scope") || input.Has("max_events"))
                throw new ArgumentException("停止时不要传采样参数。");
            if (recorderAllowed?.Invoke() == true) FinishFight("stopped");
            Disarm();
            return RecorderStatus();
        }

        if (recorderAllowed is not null) throw new InvalidOperationException("录制已启用，请先停止再调整。");
        if (!isAllowed()) throw new InvalidOperationException("战斗归档权限已关闭。");
        if (!services.ClientState.IsLoggedIn || !services.PlayerState.IsLoaded || services.PlayerState.ContentId == 0)
            throw new InvalidOperationException("请在当前角色准备就绪后开启录制。");
        var duration = input.Integer("duration_seconds", 300, 0, 1800);
        var interval = input.Integer("sample_interval_ms", 100, 50, 1000);
        var effectScope = EffectScope(input);
        if (dispatched && !input.Boolean("include_action_effects"))
            throw new ArgumentException("原生效果归档需要 include_action_effects=true。");
        if (duration is > 0 and < 10) throw new ArgumentException("期限为 0（持续）或 10 至 1800 秒。");
        effectLimit = input.Integer("max_events", 20000, 100, 50000);
        var until = duration == 0 ? long.MaxValue : Environment.TickCount64 + 1000L * duration;
        if (dispatched && !ReadoutLayoutKnown) return new { status = "unsupported_sdk" };
        if (dispatched && !StartEffectReceiver(isAllowed, effectScope, until))
            return new { status = "entry_unavailable" };
        armedCharacter = services.PlayerState.ContentId;
        armedUntil = until;
        sampleInterval = interval;
        includeParty = input.Boolean("include_party");
        recordEffects = input.Boolean("include_action_effects");
        fightEffectScope = effectScope;
        recorderAllowed = isAllowed;
        if (dispatched) StartMechanics();
        partialFight = services.Condition[ConditionFlag.InCombat];
        return RecorderStatus();
    }

    public object RecorderStatus()
    {
        CollectArchiveOutcome();
        return new
        {
            enabled = recorderAllowed is not null,
            remainingMs = armedUntil == long.MaxValue ? (long?)null : Math.Max(0, armedUntil - Environment.TickCount64),
            continuousManaged, continuousError, pendingArchives = QueuedArchives, effectLimit, savedArchives = Volatile.Read(ref savedArchives),
            failedArchives = Volatile.Read(ref failedArchives), archiveError = Volatile.Read(ref archiveError),
            activeCombatID = fightID,
            bufferedFrames = fight.Count,
            mechanicSources = MechanicSources(),
            includeParty = recorderAllowed is not null && includeParty,
            includeActionEffects = recorderAllowed is not null && recordEffects,
            actionEffectScope = recorderAllowed is not null && recordEffects ? fightEffectScope : null,
            actionEffectSource = effectReceiver is not null ? "client_dispatch" : recordEffects ? "actor_buffers" : null,
            bufferedActionEffects = ReadDispatchEffectCount() ?? fightEffects?.Events.Count ?? 0,
            saving = pendingArchive is not null,
            droppedFights,
            lastArchive,
            retention = new { maximumCount = Math.Clamp(configuration.CombatArchiveLimit, 1, 1000),
                maximumMiB = Math.Clamp(configuration.CombatArchiveMiB, 1, 100), maximumDays = 7 },
        };
    }

    public void ObserveFrame(IFramework framework, CancellationToken lifetime)
    {
        if (lifetime.IsCancellationRequested || framework.IsFrameworkUnloading) return;
        var now = Environment.TickCount64;
        try { ObserveZoneCapture(); }
        catch (Exception error) { services.Log.Debug(error, "无法结束服务端回调采集。"); }
        try { ObserveZoneTrafficTrace(); }
        catch (Exception error) { services.Log.Debug(error, "无法结束收发观察。"); }
        if (CurrentCnLayoutKnown)
        {
            try { ObserveRetainer(now); }
            catch (Exception error) { services.Log.Debug(error, "无法更新雇员库存观察。"); }
        }
        CollectArchiveOutcome();
        if (recorderAllowed is not { } allowed) return;
        try
        {
            if (!allowed()) { FinishFight("permission_revoked"); Disarm(); return; }
            if (now >= armedUntil)
            {
                FinishFight("expired");
                Disarm();
                return;
            }
            if (!services.ClientState.IsLoggedIn || !services.PlayerState.IsLoaded || services.PlayerState.ContentId != armedCharacter)
            {
                FinishFight("session_changed");
                Disarm();
                return;
            }
            var changingArea = services.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51);
            if (fight.Count != 0 && (changingArea ||
                (services.ClientState.TerritoryType, services.ObjectTable.LocalPlayer?.GameObjectId ?? 0) !=
                (fight[0].TerritoryID, fight[0].Player.ID))) FinishFight("session_changed");
            if (changingArea) { EndEvidence(); return; }
            if (!services.Condition[ConditionFlag.InCombat])
            {
                if (fight.Count != 0 && fightEffects is { } activeEffects && services.ObjectTable.LocalPlayer is { } finalPlayer &&
                    (services.ClientState.TerritoryType, finalPlayer.GameObjectId) == (fight[0].TerritoryID, fight[0].Player.ID))
                    ObserveCombatEffects(activeEffects, finalPlayer, now - fightStarted);
                if (fightID is not null) FinishFight("combat_ended");
                if (services.ObjectTable.LocalPlayer is { } idle) PrepareEvidence(services.ClientState.TerritoryType, idle.GameObjectId, 0);
                UpdateEffectReceiver(now, inCombat: false);
                if (recordEffects && effectReceiver is null && now >= nextCombatSample &&
                    services.ObjectTable.LocalPlayer is { } idlePlayer)
                {
                    nextCombatSample = now + sampleInterval;
                    fightEffects ??= new(fightEffectScope);
                    fightEffects.ResetBaseline();
                    ObserveCombatEffects(fightEffects, idlePlayer, 0, baseline: true);
                    baselineOwner = (services.ClientState.TerritoryType, idlePlayer.GameObjectId);
                }
                return;
            }
            UpdateEffectReceiver(now, inCombat: true);
            if (fight.Count != 0 && now < nextCombatSample) return;
            nextCombatSample = now + sampleInterval;
            if (fight.Count == 0)
            {
                fightStarted = now;
                if (services.ObjectTable.LocalPlayer is { } initial) PrepareEvidence(services.ClientState.TerritoryType, initial.GameObjectId, fightStarted);
            }
            var elapsed = now - fightStarted;
            ObserveBattleActivity(elapsed);
            if (mechanicCasts is not null) ObserveLightningCharge();
            if (fight.Count >= 18000 && (services.ClientState.TerritoryType, services.ObjectTable.LocalPlayer?.GameObjectId ?? 0) ==
                (fight[0].TerritoryID, fight[0].Player.ID))
            {
                fightTruncated = true;
                if (recordEffects && effectReceiver is null && services.ObjectTable.LocalPlayer is { } current)
                {
                    fightEffects ??= new(fightEffectScope);
                    ObserveCombatEffects(fightEffects, current, elapsed);
                }
                return;
            }
            var sample = CaptureCombat(elapsed, includeParty, includePose: true);
            if (sample is null) return;
            if (fight.Count != 0 && (sample.TerritoryID != fight[0].TerritoryID || sample.Player.ID != fight[0].Player.ID))
            {
                FinishFight("session_changed");
                fightStarted = now;
                PrepareEvidence(sample.TerritoryID, sample.Player.ID, fightStarted);
                UpdateEffectReceiver(now, inCombat: true);
                sample = sample with { ElapsedMs = 0 };
            }
            if (recordEffects && effectReceiver is null && services.ObjectTable.LocalPlayer is { } local)
            {
                if (fight.Count == 0 && baselineOwner != (sample.TerritoryID, sample.Player.ID)) fightEffects = null;
                fightEffects ??= new(fightEffectScope);
                ObserveCombatEffects(fightEffects, local, sample.ElapsedMs);
            }
            fight.Add(sample);
            if (!fightID.HasValue)
            {
                var id = Guid.NewGuid();
                recordingProgress.Set(id, new("recording"));
                fightID = id;
            }
        }
        catch (Exception error)
        {
            if (continuousManaged) { continuousBlocked = true; continuousError = ClipError(error); }
            Disarm();
            lastArchive = new(null, ClipError(error));
        }
    }

    private void FinishFight(string status)
    {
        var (dispatched, coverage, truncated) = CompleteDispatchedEffects();
        var (events, evidenceClipped, decisionsClipped) = EndEvidence();
        if (fightID is not { } id) return;
        if (QueuedArchives >= 4 || recorderAllowed is null)
        {
            droppedFights++;
            recordingProgress.Set(id, new("archive_dropped", status));
        }
        else
        {
            var capture = new CombatCapture(status, DateTimeOffset.UtcNow, typeof(GameDiagnostics).Module.ModuleVersionId,
                fight.Count, [.. fight], fightTruncated, dispatched ?? fightEffects?.Events.ToArray(),
                coverage ?? fightEffects?.Coverage, truncated ?? fightEffects?.Truncated);
            var archive = capture with
            {
                Activity = battleActivity.ToArray(), ActivityTruncated = activityTruncated, PartialStart = partialFight,
                Evidence = events, EvidenceTruncated = evidenceClipped, DecisionsTruncated = decisionsClipped,
                MechanicSources = MechanicSources(), EndElapsedMs = Math.Max(fight[^1].ElapsedMs, Environment.TickCount64 - fightStarted),
            };
            recordingProgress.Set(id, new("archiving", status));
            lock (archiveQueue) archiveQueue.Enqueue((id, archive));
            CollectArchiveOutcome();
        }
        fightID = null;
        fight.Clear();
        battleActivity.Clear();
        battleActors.Clear();
        activityTruncated = false;
        partialFight = false;
        fightEffects = null;
        baselineOwner = default;
        fightTruncated = false;
    }

    private void CollectArchiveOutcome()
    {
        if (pendingArchive is { IsCompleted: true } pending)
        {
            lastArchive = pending.IsCompletedSuccessfully ? pending.Result : new(null, "archive_failed");
            pendingArchive = null;
        }
        if (pendingArchive is null && QueuedArchives > 0)
            pendingArchive = Task.Run(() =>
            {
                ArchiveOutcome result = new(null, null);
                while (true)
                {
                    (Guid ID, CombatCapture Capture) queued;
                    lock (archiveQueue)
                    {
                        if (!archiveQueue.TryDequeue(out queued)) return result;
                    }
                    try
                    {
                        result = new(Save(queued.Capture, automatic: true, recordingID: queued.ID), null);
                        recordingProgress.Set(queued.ID, new("archived", queued.Capture.Status, queued.ID));
                        Interlocked.Increment(ref savedArchives);
                    }
                    catch (Exception error)
                    {
                        result = new(null, ClipError(error));
                        recordingProgress.Set(queued.ID, new("archive_failed", queued.Capture.Status, Error: result.Error));
                        Volatile.Write(ref archiveError, result.Error);
                        Interlocked.Increment(ref failedArchives);
                    }
                }
            });
    }

    private void Disarm()
    {
        EndEvidence();
        StopMechanics();
        CloseEffectReceiver();
        recorderAllowed = null;
        armedUntil = 0;
        armedCharacter = 0;
        nextCombatSample = 0;
        fightTruncated = false;
        fightEffects = null;
        baselineOwner = default;
        recordEffects = false;
        if (fightID is { } id) recordingProgress.Set(id, new("interrupted", "recorder_error"));
        fightID = null;
        fight.Clear();
        battleActivity.Clear();
        battleActors.Clear();
        activityTruncated = false;
    }

    public void StopCombatRecorder()
    {
        FinishFight("unloaded");
        Disarm();
        var deadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < deadline)
        {
            CollectArchiveOutcome();
            if (pendingArchive is not { } pending) break;
            try { if (!pending.Wait((int)Math.Max(1, deadline - Environment.TickCount64))) break; }
            catch (AggregateException) { break; }
        }
    }

    public void ResetContinuousRecorder()
    {
        if (continuousManaged) { FinishFight("reconfigured"); Disarm(); }
        continuousManaged = false;
        continuousBlocked = false;
        continuousError = null;
    }

    public void MaintainContinuousRecorder(Configuration configuration, Func<bool> allowed)
    {
        if (!configuration.RecordCombatContinuously || !allowed())
        {
            if (!configuration.RecordCombatContinuously) { continuousBlocked = false; continuousError = null; }
            if (continuousManaged) { FinishFight("paused"); Disarm(); continuousManaged = false; }
            return;
        }
        if (recorderAllowed is not null || continuousBlocked || !services.ClientState.IsLoggedIn || !services.PlayerState.IsLoaded) return;
        try
        {
            var args = new ToolArguments(System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                enabled = true, duration_seconds = 0, include_party = true, include_action_effects = true,
                action_effect_scope = configuration.CombatRecordingScope, max_events = configuration.CombatEventLimit,
            }));
            var result = SetRecorder(args, allowed, dispatched: true);
            continuousManaged = recorderAllowed is not null;
            if (!continuousManaged)
            {
                continuousBlocked = true;
                continuousError = System.Text.Json.JsonSerializer.Serialize(result);
            }
        }
        catch (Exception error) { continuousBlocked = true; continuousError = ClipError(error); }
    }

    private static string ClipError(Exception error)
    {
        var text = error.GetBaseException().Message;
        return text[..Math.Min(512, text.Length)];
    }

    private sealed record ArchiveOutcome(Guid? ID, string? Error);
}
