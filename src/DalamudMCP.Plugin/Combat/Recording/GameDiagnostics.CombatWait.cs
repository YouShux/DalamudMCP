namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameDiagnostics
{
    internal const string WAIT_COMBAT_TOOL = "wait_combat_recording";
    private readonly RecordingProgress recordingProgress = new();
    private Guid? fightID;
    private int combatWaiters;

    internal bool IsCurrentFight(Guid id) => fightID == id;
    internal Guid? CurrentEvidenceFight() => fightID;

    internal string BattleOutcomePhase(Guid id) => recordingProgress.Read(id) switch
    {
        { Phase: "recording" } => "recording",
        { EndReason: "combat_ended" } => "ended",
        null => "not_found",
        _ => "interrupted",
    };

    public async Task<object> WaitCombatRecordingAsync(ToolArguments input, CancellationToken token)
    {
        var id = RepositoryPackages.ParseID(input.Text("combat_id"));
        var untilArchived = input.Text("until", "archived") switch
        {
            "archived" => true,
            "ended" => false,
            _ => throw new ArgumentException("until 必须是 ended 或 archived。"),
        };
        var timeout = input.Integer("timeout_ms", 5000, 0, 20000);
        if (Interlocked.Increment(ref combatWaiters) > 2)
        {
            Interlocked.Decrement(ref combatWaiters);
            return new { status = "busy", combatID = id };
        }
        try
        {
            var deadline = Environment.TickCount64 + timeout;
            var last = recordingProgress.Read(id);
            if (last is null)
            {
                try
                {
                    // 旧版自动归档也可能是普通 JSON；显式采样不会产生录制终态。
                    var capture = await Task.Run(() => LoadArchive(id, token), token).ConfigureAwait(false);
                    if (capture is { Status: "combat_ended" or "stopped" or "expired" or "session_changed" or "permission_revoked" or
                        "unloaded" or "reconfigured" or "paused", Frames.Length: > 0 and <= 18000 }) last = new("archived", capture.Status, id);
                    else if (capture is not null && capture.Frames is not { Length: > 0 and <= 18000 })
                        return new { status = "invalid_capture", combatID = id };
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
                catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException)
                { return new { status = "invalid_capture", combatID = id }; }
                last ??= recordingProgress.Read(id);
            }
            if (last is null) return new { status = "not_found", combatID = id };
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (last.Phase != "recording" && (!untilArchived || last.Phase != "archiving"))
                    return Result(!untilArchived && last.EndReason == "combat_ended" ? "ended" : last.Phase, last);
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) return Result("timeout", last);
                await Task.Delay((int)Math.Min(100, remaining), token).ConfigureAwait(false);
                last = recordingProgress.Read(id) ?? new("not_found");
            }
        }
        finally { Interlocked.Decrement(ref combatWaiters); }

        object Result(string status, RecordingSnapshot snapshot) => new
        {
            status, combatID = id, until = untilArchived ? "archived" : "ended", phase = snapshot.Phase,
            endReason = snapshot.EndReason, archiveID = snapshot.ArchiveID, archiveError = snapshot.Error,
            resultObserved = untilArchived ? snapshot.Phase == "archived" :
                snapshot.Phase is not ("recording" or "not_found") && snapshot.EndReason == "combat_ended",
        };
    }

    private sealed record RecordingSnapshot(string Phase, string? EndReason = null, Guid? ArchiveID = null, string? Error = null);

    private sealed class RecordingProgress
    {
        private readonly object sync = new();
        private readonly Dictionary<Guid, RecordingSnapshot> recordings = new();
        private readonly Queue<Guid> completed = new();

        public RecordingSnapshot? Read(Guid id)
        {
            lock (sync) return recordings.GetValueOrDefault(id);
        }

        public void Set(Guid id, RecordingSnapshot value)
        {
            lock (sync)
            {
                recordings[id] = value;
                if (value.Phase is "recording" or "archiving") return;
                completed.Enqueue(id);
                if (completed.Count > 64) recordings.Remove(completed.Dequeue());
            }
        }
    }
}
