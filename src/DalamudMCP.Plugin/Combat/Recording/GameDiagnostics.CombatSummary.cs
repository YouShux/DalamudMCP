namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameDiagnostics
{
    private static object Summarize(CombatCapture capture)
    {
        var actors = new Dictionary<ulong, Observations>();
        var frameIndex = 0;
        var previousTime = 0L;
        foreach (var frame in capture.Frames)
        {
            if (frame.ElapsedMs < previousTime || frame.ElapsedMs > 86400000)
                return new { status = "invalid_capture" };
            previousTime = frame.ElapsedMs;
            Observe(frame.Player, frameIndex);
            if (frame.Target is not null) Observe(frame.Target, frameIndex);
            if (frame.Party is not null)
                foreach (var member in frame.Party) Observe(member, frameIndex);
            frameIndex++;
        }
        return new
        {
            status = "ok", captureStatus = capture.Status, capture.Truncated, frameCount = frameIndex,
            elapsedMs = capture.Frames[^1].ElapsedMs - capture.Frames[0].ElapsedMs, actorCount = actors.Count,
            actionEffectsObserved = capture.ActionEffects?.Length,
            capture.ActionEffectCoverage, capture.ActionEffectsTruncated,
            actors = actors.OrderByDescending(row => row.Value.Samples).ThenBy(row => row.Key).Take(64).Select(row => new
            {
                actorID = $"0x{row.Key:X}", row.Value.Samples,
                row.Value.ObservedHPLoss, row.Value.ObservedHPGain, row.Value.ObservedCastStarts,
            }).ToArray(),
            sampleDeltasOnly = true,
        };

        void Observe(CombatActor actor, int index)
        {
            if (actor.ID == 0) return;
            if (!actors.TryGetValue(actor.ID, out var sample))
            {
                actors.Add(actor.ID, new(index, actor, 1, 0, 0, 0));
                return;
            }
            if (sample.FrameIndex == index) return;
            var adjacent = sample.FrameIndex == index - 1;
            var delta = adjacent ? (long)actor.HP - sample.Previous.HP : 0;
            var castStarted = adjacent && actor.CastActionID.HasValue && actor.CastActionID != sample.Previous.CastActionID;
            actors[actor.ID] = new(index, actor, sample.Samples + 1,
                sample.ObservedHPLoss + Math.Max(0, -delta), sample.ObservedHPGain + Math.Max(0, delta),
                sample.ObservedCastStarts + (castStarted ? 1 : 0));
        }
    }

    private static EffectGroup[] AggregateEffects(ObservedEffect[] matches, string groupBy, CancellationToken token)
    {
        var groups = new List<EffectGroup>();
        foreach (var group in matches.GroupBy(effect => (
            SourceID: groupBy == "action" ? null : ActorID(effect.SourceID),
            ActionID: groupBy == "source" ? (uint?)null : effect.ActionID,
            ActionType: groupBy == "source" ? (byte?)null : effect.ActionType)))
        {
            token.ThrowIfCancellationRequested();
            var rows = group.ToArray();
            var targets = rows.Select(row => ActorID(row.TargetID)).OfType<string>().Distinct(StringComparer.Ordinal).Count();
            var canIdentify = rows.All(row => row.GlobalSequence != 0 && row.ActionID != 0 && ActorID(row.SourceID) is not null);
            long[]? times = canIdentify ? rows.GroupBy(row => (row.GlobalSequence, Source: ActorID(row.SourceID), row.ActionType, row.ActionID))
                .Select(batch => batch.Min(row => row.ObservedAtMs)).Order().ToArray() : null;
            var intervals = times?.Zip(times.Skip(1), (left, right) => right - left).ToArray();
            groups.Add(new(group.Key.SourceID, group.Key.ActionID, group.Key.ActionType, rows.Length, targets,
                rows[0].ObservedAtMs, rows[^1].ObservedAtMs, times?.Length, canIdentify ? null : "batch_identity_incomplete",
                intervals is { Length: > 0 } ? new(intervals.Length, intervals.Min(), intervals.Max(), intervals.Average()) : null));
        }
        return groups.OrderByDescending(group => group.EffectRows).ThenBy(group => group.SourceID, StringComparer.Ordinal)
            .ThenBy(group => group.ActionID).ThenBy(group => group.ActionType).ToArray();
    }

    private static string? ActorID(string? id)
    {
        if (id is null || !id.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            !ulong.TryParse(id.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var parsed) || parsed is 0 or 0xE0000000)
            return null;
        return $"0x{parsed:X}";
    }

    private sealed record EffectGroup(string? SourceID, uint? ActionID, byte? ActionType, int EffectRows, int ObservedTargetCount,
        long FirstObservedMs, long LastObservedMs, int? ObservedBatchCount, string? BatchAvailabilityReason, EffectIntervals? ObservedIntervals);
    private sealed record EffectIntervals(int Count, long MinMs, long MaxMs, double MeanMs);

    private readonly record struct Observations(int FrameIndex, CombatActor Previous, int Samples,
        long ObservedHPLoss, long ObservedHPGain, int ObservedCastStarts);
}
