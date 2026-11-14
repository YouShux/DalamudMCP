using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameDiagnostics
{
    internal const string COMBAT_TIMELINE_TOOL = "read_combat_timeline";
    private static string EffectScope(ToolArguments input)
    {
        var scope = input.Text("action_effect_scope", "self");
        if (scope is not ("self" or "party" or "nearby")) throw new ArgumentException("未知的动作效果采样范围。");
        if (input.Has("action_effect_scope") && !input.Boolean("include_action_effects"))
            throw new ArgumentException("设置效果范围前需要 include_action_effects。");
        return scope;
    }

    private void ObserveCombatEffects(CombatEffects effects, IBattleChara local, long elapsedMs, bool baseline = false)
    {
        effects.Observe(local.GameObjectId, local.Address, elapsedMs);
        if (effects.Scope == "self" || effects.Truncated) return;
        var covered = new HashSet<ulong> { local.GameObjectId };
        var party = services.PartyList;
        for (var i = 0; i < Math.Min(8, party.Length) && !effects.Truncated; i++)
            if (party[i]?.GameObject is IBattleChara member && covered.Add(member.GameObjectId))
                effects.Observe(member.GameObjectId, member.Address, elapsedMs);
        if (effects.Scope != "nearby" || effects.Truncated) return;
        if (services.TargetManager.Target is IBattleChara target && covered.Add(target.GameObjectId))
            effects.Observe(target.GameObjectId, target.Address, elapsedMs);
        if (baseline) return;
        foreach (var actor in services.ObjectTable.OfType<IBattleChara>())
        {
            if (covered.Count >= 12 || effects.Truncated) break;
            if (Vector3.DistanceSquared(local.Position, actor.Position) <= 900 && covered.Add(actor.GameObjectId))
                effects.Observe(actor.GameObjectId, actor.Address, elapsedMs);
        }
    }

    private sealed class CombatEffects(string scope)
    {
        private readonly HashSet<(uint Sequence, byte Index, ulong Source, ulong Target)> seen = [];
        private readonly Dictionary<ulong, nint> actors = [];

        public string Scope { get; } = scope;
        public List<ObservedEffect> Events { get; } = [];
        public bool Truncated { get; private set; }
        public string Coverage { get; private set; } = "not_observed";

        public void ResetBaseline()
        {
            seen.Clear();
            actors.Clear();
            Events.Clear();
            Coverage = "not_observed";
            Truncated = false;
        }

        public unsafe void Observe(ulong actorID, nint address, long elapsedMs)
        {
            if (actorID == 0 || address == 0 || !ReadoutLayoutKnown || Truncated) return;
            var entries = ((BattleChara*)address)->ActionEffectHandler.IncomingEffects;
            Coverage = Scope switch { "nearby" => "nearby_actor_effect_buffers", "party" => "party_effect_buffers", _ => "local_player_effect_buffer" };
            var seeded = actors.TryGetValue(actorID, out var previous) && previous == address;
            actors[actorID] = address;
            foreach (ref var entry in entries)
            {
                if (entry.GlobalSequence == 0 || entry.ActionId == 0) continue;
                var identity = (entry.GlobalSequence, entry.TargetIndex, entry.Source.Id, entry.Target.Id);
                if (!seen.Add(identity)) continue;
                if (seen.Count > 8192 || seeded && Events.Count >= 360)
                {
                    Truncated = true;
                    break;
                }
                if (!seeded) continue;
                var effects = new List<EffectValue>(8);
                foreach (ref var effect in entry.Effects.Effects)
                    if (effect.Type != 0)
                        effects.Add(new(effect.Type, effect.Value, effect.Param0, effect.Param1, effect.Param2, effect.Param3, effect.Param4));
                Events.Add(new(elapsedMs, entry.GlobalSequence, entry.TargetIndex, entry.ActionType, entry.ActionId,
                    $"0x{entry.Source.Id:X}", $"0x{entry.Target.Id:X}", entry.SourceConfirmed, entry.TargetConfirmed, [.. effects]));
            }
        }
    }

    private static object CombatTimeline(CombatCapture capture, ToolArguments input)
    {
        var offset = input.Integer("offset", 0, 0, 50000);
        var limit = input.Integer("limit", 40, 1, 100);
        var matches = MatchEffects(capture, EffectFilter.Parse(input), out var status);
        if (status != "ok") return new { status, capture.ActionEffectCoverage, capture.BuildMvid };
        var events = capture.ActionEffects!;
        var page = matches.Skip(offset).Take(limit).ToArray();
        var maximumGap = 0L;
        for (var i = 1; i < matches.Length; i++)
            maximumGap = Math.Max(maximumGap, matches[i].ObservedAtMs - matches[i - 1].ObservedAtMs);
        return TimelinePageResult(capture, events.Length, matches.Length, page, matches.Length < 2 ? null : maximumGap, offset);
    }

    private static object TimelinePageResult(CombatCapture capture, int observed, int matched, ObservedEffect[] page, long? gap, int offset) =>
        new
        {
            status = "ok", capture.BuildMvid, capture.CapturedAt, capture.ActionEffectCoverage,
            capture.ActionEffectsTruncated, frameCaptureTruncated = capture.Truncated, observedEvents = observed,
            matchingEvents = matched, events = page, decoded = page.Select(effect => new { effect.GlobalSequence, effect.TargetIndex, effect.SourceID, effect.TargetID, effects = effect.Effects.Select(DecodeEffect).ToArray() }).ToArray(),
            maximumObservedGapMs = gap,
            capture.PartialStart,
            nextOffset = offset + page.Length < matched ? (int?)offset + page.Length : null,
        };

    private static ObservedEffect[] MatchEffects(CombatCapture capture, EffectFilter filter, out string status)
    {
        var events = capture.ActionEffects;
        if (events is null || capture.ActionEffectCoverage is null or "not_observed")
        {
            status = "no_effect_data";
            return [];
        }
        if (events.Length > 50000 || events.Any(effect => effect is null || effect.Effects is not { Length: <= 8 } ||
            effect.Effects.Any(value => value is null) || effect.ObservedAtMs is < -1500 or > 86400000))
        {
            status = "invalid_capture";
            return [];
        }
        status = "ok";
        return events.Where(effect => effect.ObservedAtMs >= filter.FromMs && effect.ObservedAtMs <= filter.ToMs &&
            (!filter.ActionID.HasValue || effect.ActionID == filter.ActionID) &&
            (filter.SourceID is null || string.Equals(filter.SourceID, effect.SourceID, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(effect => effect.ObservedAtMs).ToArray();
    }

    private readonly record struct EffectFilter(int FromMs, int ToMs, uint? ActionID, string? SourceID)
    {
        public static EffectFilter Parse(ToolArguments input)
        {
            var actionID = input.Has("action_id") ? input.Element("action_id").GetUInt32() : (uint?)null;
            if (actionID == 0) throw new ArgumentException("action_id 必须大于零。");
            var sourceID = input.Has("source_id") ? $"0x{input.ObjectID("source_id"):X}" : null;
            var from = input.Integer("from_ms", -1500, -1500, 86400000);
            return new(from, input.Integer("to_ms", 86400000, from, 86400000), actionID, sourceID);
        }
    }

    private static object DecodeEffect(EffectValue effect) => new
    {
        kind = effect.Type is 3 or 5 or 6 ? "damage" : effect.Type == 4 ? "healing" : "other",
        amount = effect.Type is 3 or 4 or 5 or 6 ? (uint?)(effect.Value + ((effect.Param4 & 64) != 0 ? effect.Param3 * 65536u : 0)) : null,
        critical = effect.Type is 3 or 4 or 5 or 6 ? (bool?)((effect.Param0 & 32) != 0) : null,
        directHit = effect.Type is 3 or 4 or 5 or 6 ? (bool?)((effect.Param0 & 64) != 0) : null,
        appliedAtSource = (effect.Param4 & 128) != 0, originatedAtTarget = (effect.Param4 & 32) != 0,
        interpretationVerified = false,
    };

    private sealed record ObservedEffect(long ObservedAtMs, uint GlobalSequence, byte TargetIndex, byte ActionType,
        uint ActionID, string SourceID, string TargetID, bool? SourceConfirmed, bool? TargetConfirmed, EffectValue[] Effects, bool? BeforeCombat = null);
    private sealed record EffectValue(byte Type, ushort Value, byte Param0, byte Param1, byte Param2, byte Param3, byte Param4);
    private sealed record ActionFrame(string? QueueType, string? QueueTargetID, uint? CastActionID,
        string? CastTargetID, float? CastElapsedSeconds, float? CastTotalSeconds, float? GlobalCooldownRemainingSeconds);
}
