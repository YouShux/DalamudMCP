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
        return OrderEffectGroups(groups);
    }

    private static EffectGroup[] OrderEffectGroups(IEnumerable<EffectGroup> groups) =>
        groups.OrderByDescending(group => group.EffectRows).ThenBy(group => group.SourceID, StringComparer.Ordinal)
            .ThenBy(group => group.ActionID).ThenBy(group => group.ActionType).ToArray();

    private sealed class EffectAccumulator(string groupBy)
    {
        private readonly Dictionary<(ulong Source, uint? Action, byte? Type), EffectGroupState> groups = [];
        internal int Matched { get; private set; }

        internal void Add(ulong source, ulong target, uint sequence, uint action, byte type, int time)
        {
            var key = (groupBy == "action" ? 0UL : source, groupBy == "source" ? (uint?)null : action,
                groupBy == "source" ? (byte?)null : type);
            if (!groups.TryGetValue(key, out var group))
                groups.Add(key, group = new EffectGroupState());
            group.Rows++;
            Matched++;
            group.First = Math.Min(group.First, time);
            group.Last = Math.Max(group.Last, time);
            if (target != 0) (group.Targets ??= []).Add(target);
            if (sequence == 0 || action == 0 || source == 0)
            {
                group.Incomplete = true;
                group.Batches = null;
            }
            else if (!group.Incomplete)
            {
                var batches = group.Batches ??= [];
                ref var earliest = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(batches,
                    (sequence, source, action, type), out var exists);
                if (!exists || time < earliest) earliest = time;
            }
        }

        internal EffectGroup[] Finish(CancellationToken token)
        {
            var result = new List<EffectGroup>(groups.Count);
            foreach (var (key, group) in groups)
            {
                token.ThrowIfCancellationRequested();
                var count = group.Batches?.Count;
                EffectIntervals? intervals = null;
                if (group.Batches is { Count: > 1 } batches)
                {
                    var times = batches.Values.ToArray();
                    Array.Sort(times);
                    var min = int.MaxValue;
                    var max = 0;
                    for (var i = 1; i < times.Length; i++)
                    {
                        var gap = times[i] - times[i - 1];
                        min = Math.Min(min, gap);
                        max = Math.Max(max, gap);
                    }
                    intervals = new(times.Length - 1, min, max, (double)(times[^1] - times[0]) / (times.Length - 1));
                }
                result.Add(new(key.Source == 0 ? null : $"0x{key.Source:X}", key.Action, key.Type, group.Rows, group.Targets?.Count ?? 0,
                    group.First, group.Last, count, group.Incomplete ? "batch_identity_incomplete" : null, intervals));
            }
            return OrderEffectGroups(result);
        }
    }

    private sealed class EffectGroupState
    {
        internal int Rows;
        internal int First = int.MaxValue;
        internal int Last = int.MinValue;
        internal HashSet<ulong>? Targets;
        internal bool Incomplete;
        internal Dictionary<(uint Sequence, ulong Source, uint Action, byte Type), int>? Batches;
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
