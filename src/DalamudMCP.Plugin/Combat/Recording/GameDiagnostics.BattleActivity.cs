using Dalamud.Game.ClientState.Objects.Types;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameDiagnostics
{
    private readonly Dictionary<ulong, BattleActorState> battleActors = [];
    private readonly List<BattleActivity> battleActivity = [];
    private bool activityTruncated;

    private void ObserveBattleActivity(long elapsed)
    {
        if (activityTruncated || services.ObjectTable.LocalPlayer is not { } player) return;
        var seen = new HashSet<ulong>();
        foreach (var actor in services.ObjectTable.OfType<IBattleChara>())
        {
            if (seen.Count == 256) break;
            if (fightEffectScope == "self" && actor.GameObjectId != player.GameObjectId && actor.OwnerId != player.EntityId) continue;
            if (fightEffectScope == "party" && actor.GameObjectId != player.GameObjectId && actor.OwnerId != player.EntityId &&
                !services.PartyList.Any(member => member.GameObject?.GameObjectId == actor.GameObjectId)) continue;
            if (!seen.Add(actor.GameObjectId)) continue;
            var statuses = actor.StatusList.Where(status => status.StatusId != 0).Take(60)
                .Select(status => new BattleStatus(status.StatusId, status.SourceId, status.Param)).OrderBy(status => status.ID).ThenBy(status => status.SourceID).ToArray();
            var current = new BattleActorState(actor.OwnerId, actor.CurrentHp == 0, actor.IsCasting ? actor.CastActionId : null, statuses);
            if (!battleActors.TryGetValue(actor.GameObjectId, out var previous)) Add("actor_seen", actor.GameObjectId, current.OwnerID, current);
            else
            {
                if (previous.Dead != current.Dead) Add(current.Dead ? "death" : "alive", actor.GameObjectId, current.OwnerID, current.Dead);
                if (previous.Cast != current.Cast) Add("cast_changed", actor.GameObjectId, current.OwnerID, new { before = previous.Cast, after = current.Cast });
                foreach (var status in current.Statuses.Except(previous.Statuses)) Add("status_added", actor.GameObjectId, current.OwnerID, status);
                foreach (var status in previous.Statuses.Except(current.Statuses)) Add("status_removed", actor.GameObjectId, current.OwnerID, status);
            }
            battleActors[actor.GameObjectId] = current;
        }
        foreach (var missing in battleActors.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            Add("actor_unobserved", missing, battleActors[missing].OwnerID, null);
            battleActors.Remove(missing);
        }

        void Add(string kind, ulong actor, uint owner, object? value)
        {
            if (battleActivity.Count >= effectLimit) { activityTruncated = true; return; }
            battleActivity.Add(new(battleActivity.Count, elapsed, kind, $"0x{actor:X}", $"0x{owner:X}", value));
        }
    }

    private static object ReadBattleActivity(CombatCapture capture, ToolArguments input)
    {
        var offset = input.Integer("offset", 0, 0, 50000);
        var limit = input.Integer("limit", 50, 1, 128);
        var from = input.Integer("from_ms", 0, 0, 86400000);
        var to = input.Integer("to_ms", 86400000, from, 86400000);
        var actor = input.Has("actor_id") ? $"0x{input.ObjectID("actor_id"):X}" : null;
        var kind = input.Has("kind") ? input.Text("kind") : null;
        var all = capture.Activity ?? [];
        if (all.Length > 50000) return new { status = "invalid_capture" };
        List<BattleActivity>? page = null;
        var matched = 0;
        foreach (var row in all)
        {
            if (row.ElapsedMs < from || row.ElapsedMs > to ||
                actor is not null && !actor.Equals(row.ActorID, StringComparison.OrdinalIgnoreCase) || kind is not null && kind != row.Kind)
                continue;
            if (matched >= offset && matched < offset + limit)
                (page ??= new(Math.Min(limit, all.Length - offset))).Add(row);
            matched++;
        }
        return ActivityPageResult(capture, capture.Activity is not null, matched, page, offset, limit);
    }

    private sealed record BattleActorState(uint OwnerID, bool Dead, uint? Cast, BattleStatus[] Statuses);
    private sealed record BattleStatus(uint ID, uint SourceID, ushort Param);
    private sealed record BattleActivity(int Index, long ElapsedMs, string Kind, string ActorID, string OwnerID, object? Value);
}
