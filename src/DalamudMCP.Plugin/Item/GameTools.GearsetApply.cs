using DalamudMCP.Shared;

namespace DalamudMCP.Plugin.Tools;

public sealed partial class GameTools
{
    private int gearsetActive;

    private async Task<object> ApplyGearsetAsync(ToolArguments input, Func<bool> allowed, CancellationToken token)
    {
        if (input.Has("slot") == input.Has("name")) throw new ArgumentException("请选择槽位或完整套装名称，两者只能传一个。");
        var slot = input.Has("slot") ? input.Integer("slot", 0, 0, 99) : -1;
        var name = input.Has("name") ? input.Text("name") : null;
        if (name is { Length: > 64 }) throw new ArgumentException("套装名称最多 64 个字符。");
        var expectedSlot = input.Integer("expected_current_slot", -1, 0, 99);
        var job = input.Element("expected_job_id").GetUInt32();
        var territory = input.Element("expected_territory_id").GetUInt32();
        if (Interlocked.CompareExchange(ref gearsetActive, 1, 0) != 0) return new { status = "busy", requestDispatched = false };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(4500);
        var current = deadline.Token;
        GameReadouts.GearsetState? before = null;
        GameReadouts.GearsetState? after = null;
        var started = 0;
        int? nativeStatus = null;
        int[]? matchedSlots = null;
        long territoryGeneration = 0;
        try
        {
            var status = await services.Framework.RunOnTick(() =>
            {
                if (!GameDiagnostics.CurrentCnLayoutKnown) return "unsupported_sdk";
                if (!services.ClientState.IsLoggedIn || !services.PlayerState.IsLoaded || services.ClientState.TerritoryType != territory)
                    return "context_changed";
                if (name is not null)
                {
                    matchedSlots = readouts.FindGearsetSlots(name);
                    if (matchedSlots.Length != 1) return matchedSlots.Length == 0 ? "not_found" : "ambiguous";
                    slot = matchedSlots[0];
                }
                territoryGeneration = state.TerritoryGeneration;
                before = readouts.GearsetStateForApply(slot);
                if (before is null || before.TerritoryID != territory || before.SavedJobID != job || before.ActiveSlot != expectedSlot)
                    return "context_changed";
                if (before.MainHandMissing || before.EquipmentMatches is null || !services.ClientState.IsClientIdle() ||
                    services.ClientState.IsPvP) return "unavailable";
                current.ThrowIfCancellationRequested();
                if (!allowed()) return "permission_changed";
                if (Interlocked.CompareExchange(ref started, 1, 0) != 0) return "timeout";
                nativeStatus = readouts.EquipGearset(slot);
                return nativeStatus is null ? "entry_unavailable" : nativeStatus == 0 ? "requested" : "not_accepted";
            }, cancellationToken: current).WaitAsync(current).ConfigureAwait(false);
            if (status != "requested") return Result(status, nativeStatus.HasValue);
            do
            {
                after = await services.Framework.RunOnTick(() => readouts.GearsetStateForApply(slot),
                    delayTicks: 1, cancellationToken: current).WaitAsync(current).ConfigureAwait(false);
                current.ThrowIfCancellationRequested();
                if (after is null || before is null || after.Character != before.Character || after.TerritoryID != before.TerritoryID ||
                    state.TerritoryGeneration != territoryGeneration || after.Name != before.Name ||
                    after.SavedJobID != before.SavedJobID || !after.Items.AsSpan().SequenceEqual(before.Items))
                    return Result("context_changed", true);
                if (after.ActiveSlot == slot && after.JobID == job && after.EquipmentMatches == true)
                    return Result("observed", true);
                await Task.Delay(100, current).ConfigureAwait(false);
            } while (true);
        }
        catch (OperationCanceledException)
        {
            var mayHaveStarted = Interlocked.CompareExchange(ref started, 2, 0) == 1;
            return Result(token.IsCancellationRequested ? "cancelled" : "timeout", nativeStatus.HasValue ? true : mayHaveStarted ? null : false);
        }
        finally { Interlocked.Exchange(ref gearsetActive, 0); }

        object Result(string status, bool? dispatched) => new
        {
            status, slot = slot >= 0 ? (int?)slot : null, name, matchedSlots, requestedJobID = job, previousSlot = before?.ActiveSlot, activeSlot = after?.ActiveSlot,
            jobID = after?.JobID, equipmentMatches = after?.EquipmentMatches, nativeStatus,
            requestDispatched = dispatched, resultObserved = status == "observed",
        };
    }
}
