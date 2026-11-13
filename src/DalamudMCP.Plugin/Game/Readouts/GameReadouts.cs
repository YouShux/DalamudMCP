using System.Numerics;
using Dalamud.Game.ClientState.Buddy;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Statuses;
using Dalamud.Game.Inventory;
using Dalamud.Game.Player;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameReadouts(GameServices services)
{
    internal const string CHARACTER_TOOL = "inspect_character_stats";
    internal const string PROGRESS_TOOL = "inspect_job_progress";
    internal const string PARTY_TOOL = "inspect_party_roster";
    internal const string TARGETS_TOOL = "inspect_target_slots";
    internal const string EFFECTS_TOOL = "inspect_actor_effects";
    internal const string TRAVEL_TOOL = "inspect_unlocked_travel";
    internal const string EQUIPMENT_TOOL = "inspect_equipped_items";
    internal const string GEARSETS_TOOL = "inspect_saved_loadouts";
    internal const string BUDDIES_TOOL = "inspect_companion_roster";
    internal const string NEARBY_BATTLE_TOOL = "inspect_nearby_battle_actors";

    private static readonly PlayerAttribute[] Attributes = Enum.GetValues<PlayerAttribute>();

    public object Call(string name, ToolArguments input)
    {
        if (name == SHEETS_TOOL) return SheetRows(input);
        if (!services.ClientState.IsLoggedIn || services.ObjectTable.LocalPlayer is not { } player)
            return new { status = "unavailable", reason = "player_not_ready" };
        return name switch
        {
            CHARACTER_TOOL => Character(player),
            PROGRESS_TOOL => Jobs(),
            PARTY_TOOL => Party(input.Boolean("include_effects")),
            TARGETS_TOOL => Targets(),
            EFFECTS_TOOL => Effects(input, player),
            TRAVEL_TOOL => Travel(input),
            EQUIPMENT_TOOL => Equipment(),
            GEARSETS_TOOL => Gearsets(),
            BUDDIES_TOOL => Buddies(),
            GAUGE_TOOL => ReadJobMeter(player.ClassJob.RowId),
            RECAST_TOOL => Recasts(input),
            UNLOCK_TOOL => Unlock(input),
            MAP_TOOL => AreaMap(input),
            SOCIETIES_TOOL => Societies(),
            NEARBY_BATTLE_TOOL => NearbyBattleActors(input, player),
            _ => throw new ArgumentException($"未知游戏工具 {name}。"),
        };
    }

    private object NearbyBattleActors(ToolArguments input, IBattleChara player)
    {
        var radius = input.Number("radius", 30, 1, 100);
        var limit = input.Integer("limit", 16, 1, 50);
        var includePlayers = input.Boolean("include_players");
        var origin = player.Position;
        var entries = services.ObjectTable.OfType<IBattleChara>()
            .Where(actor => actor.GameObjectId != player.GameObjectId && (includePlayers || actor.ObjectKind == ObjectKind.BattleNpc))
            .Select(actor => (Actor: actor, DistanceSquared: Vector3.DistanceSquared(origin, actor.Position)))
            .Where(entry => entry.DistanceSquared <= radius * radius)
            .OrderBy(entry => entry.DistanceSquared).ThenBy(entry => entry.Actor.GameObjectId).Take(limit + 1).ToArray();
        return new { status = "ok", truncated = entries.Length > limit, actors = entries.Take(limit).Select(entry => new
        {
            actor = Actor(entry.Actor), distance = MathF.Sqrt(entry.DistanceSquared),
            currentHP = entry.Actor.CurrentHp, maxHP = entry.Actor.MaxHp,
            castingActionID = entry.Actor.IsCasting ? (uint?)entry.Actor.CastActionId : null,
        }).ToArray() };
    }

    private object Character(IBattleChara player)
    {
        var state = services.PlayerState;
        var attributes = new List<object>();
        if (state.IsLoaded)
            foreach (var attribute in Attributes)
            {
                var value = state.GetAttribute(attribute);
                if (value != 0) attributes.Add(new { name = attribute.ToString(), value });
            }
        return new
        {
            status = state.IsLoaded ? "ok" : "partial",
            currentHP = player.CurrentHp, maxHP = player.MaxHp,
            currentMP = player.CurrentMp, maxMP = player.MaxMp,
            currentGP = player.CurrentGp, maxGP = player.MaxGp,
            currentCP = player.CurrentCp, maxCP = player.MaxCp,
            effectiveLevel = state.IsLoaded ? (int?)state.EffectiveLevel : null,
            isLevelSynced = state.IsLoaded ? (bool?)state.IsLevelSynced : null,
            baseAttributes = state.IsLoaded ? new
            {
                strength = state.BaseStrength, dexterity = state.BaseDexterity, vitality = state.BaseVitality,
                intelligence = state.BaseIntelligence, mind = state.BaseMind, piety = state.BasePiety,
            } : null,
            attributes,
            conditions = services.Condition.AsReadOnlySet().Order().Select(flag => flag.ToString()).ToArray(),
        };
    }

    private object Jobs()
    {
        var state = services.PlayerState;
        if (!state.IsLoaded) return new { status = "unavailable", reason = "character_stats_not_ready" };
        var jobs = services.DataManager.GetExcelSheet<ClassJob>()
            .Where(row => row.ExpArrayIndex >= 0 && row.RowId != 0)
            .Select(row => new
            {
                jobID = row.RowId, name = row.Name.ToString(), abbreviation = row.Abbreviation.ToString(),
                level = state.GetClassJobLevel(row), experience = state.GetClassJobExperience(row),
            }).ToArray();
        return new { status = "ok", activeJobID = state.ClassJob.RowId, jobs };
    }

    private object Party(bool includeEffects)
    {
        var party = services.PartyList;
        var members = new List<object>();
        for (var slot = 0; slot < Math.Min(party.Length, 24); slot++)
        {
            if (party[slot] is not { } member) continue;
            members.Add(new
            {
                slot, name = member.Name.TextValue, jobID = member.ClassJob.RowId, member.Level,
                worldID = member.World.RowId, territoryID = member.Territory.RowId,
                currentHP = member.CurrentHP, maxHP = member.MaxHP,
                currentMP = member.CurrentMP, maxMP = member.MaxMP,
                actor = Actor(member.GameObject),
                effects = includeEffects ? Statuses(member.Statuses) : null,
            });
        }
        return new { status = "ok", party.IsAlliance, party.PartyLeaderIndex, reportedSlots = party.Length, members };
    }

    private object Targets()
    {
        var target = services.TargetManager;
        return new
        {
            status = "ok", current = Actor(target.Target), focus = Actor(target.FocusTarget),
            soft = Actor(target.SoftTarget), previous = Actor(target.PreviousTarget),
            mouseOver = Actor(target.MouseOverTarget), mouseOverNameplate = Actor(target.MouseOverNameplateTarget),
            gpose = Actor(target.GPoseTarget),
        };
    }

    private object Effects(ToolArguments input, IBattleChara player)
    {
        var slot = input.Text("actor", "self");
        var actor = slot switch
        {
            "self" => player,
            "current" => services.TargetManager.Target as IBattleChara,
            "focus" => services.TargetManager.FocusTarget as IBattleChara,
            _ => throw new ArgumentException("actor 必须为 self、current 或 focus。"),
        };
        if (actor is null) return new { status = "unavailable", reason = "actor_not_available", slot };
        return new { status = "ok", slot, actor = Actor(actor), effects = Statuses(actor.StatusList) };
    }

    private static object[] Statuses(IEnumerable<IStatus> statuses)
    {
        var effects = new List<object>();
        foreach (var effect in statuses)
        {
            if (effect.StatusId == 0) continue;
            effects.Add(new { statusID = effect.StatusId, name = effect.GameData.ValueNullable?.Name.ToString(),
                remainingSeconds = float.IsFinite(effect.RemainingTime) ? (float?)effect.RemainingTime : null, effect.Param });
        }
        return [.. effects];
    }

    private object Travel(ToolArguments input)
    {
        var list = services.AetheryteList;
        var limit = input.Integer("limit", 50, 1, 100);
        var offset = input.Integer("offset", 0, 0, 1024);
        var territory = input.Has("territory_id") ? input.Element("territory_id").GetUInt32() : (uint?)null;
        if (territory == 0) throw new ArgumentOutOfRangeException("territory_id");
        var query = input.Text("name_contains", string.Empty);
        var destinations = new List<object>();
        var matched = 0;
        foreach (var entry in list)
        {
            if (territory.HasValue && entry.TerritoryId != territory.Value) continue;
            var name = entry.AetheryteData.ValueNullable?.PlaceName.ValueNullable?.Name.ToString();
            if (query.Length != 0 && (name is null || !name.Contains(query, StringComparison.OrdinalIgnoreCase))) continue;
            if (matched++ < offset) continue;
            if (destinations.Count == limit)
                return new { status = "ok", reportedEntries = list.Length, destinations, nextOffset = (int?)(offset + limit) };
            destinations.Add(new
            {
                aetheryteID = entry.AetheryteId, territoryID = entry.TerritoryId, name,
                entry.SubIndex, entry.GilCost, isFavorite = entry.IsFavourite,
                entry.IsSharedHouse, entry.IsApartment,
            });
        }
        return new { status = "ok", reportedEntries = list.Length, destinations, nextOffset = (int?)null };
    }

    private object Equipment()
    {
        var items = services.GameInventory.GetInventoryItems(GameInventoryType.EquippedItems);
        if (items.IsEmpty) return new { status = "unavailable", reason = "equipment_not_loaded" };
        var sheet = services.DataManager.GetExcelSheet<Item>();
        var equipment = new List<object>();
        foreach (var item in items)
        {
            if (item.IsEmpty) continue;
            var row = sheet.GetRowOrDefault(item.BaseItemId);
            equipment.Add(new
            {
                slot = item.InventorySlot, itemID = item.BaseItemId, name = row?.Name.ToString(),
                itemLevel = row?.LevelItem.RowId, highQuality = item.IsHq,
                condition = item.Condition, glamourItemID = item.GlamourId,
            });
        }
        return new { status = "ok", reportedSlots = items.Length, equipment };
    }

    private unsafe object Gearsets()
    {
        if (!GameDiagnostics.CurrentCnLayoutKnown) return new { status = "unsupported_sdk" };
        if (!services.ClientState.IsLoggedIn || !services.PlayerState.IsLoaded)
            return new { status = "unavailable", reason = "player_not_ready" };
        var module = RaptureGearsetModule.Instance();
        if (module == null) return new { status = "unavailable", reason = "loadouts_not_ready" };
        var current = module->CurrentGearsetIndex;
        var sheet = services.DataManager.GetExcelSheet<ClassJob>();
        var loadouts = new List<object>();
        var entries = module->Entries;
        for (var slot = 0; slot < entries.Length; slot++)
        {
            ref var entry = ref entries[slot];
            if ((entry.Flags & RaptureGearsetModule.GearsetFlag.Exists) == 0) continue;
            var job = sheet.GetRowOrDefault(entry.ClassJob);
            loadouts.Add(new
            {
                slot, entryNumber = entry.Id, name = entry.NameString, jobID = entry.ClassJob,
                jobName = job?.Name.ToString(), entry.ItemLevel, glamourPlate = entry.GlamourSetLink,
                isActive = slot == current,
                mainHandMissing = (entry.Flags & RaptureGearsetModule.GearsetFlag.MainHandMissing) != 0,
            });
        }
        return new { status = "ok", currentSlot = current >= 0 && current < entries.Length &&
            (entries[current].Flags & RaptureGearsetModule.GearsetFlag.Exists) != 0 ? (int?)current : null, loadouts };
    }

    private object Buddies()
    {
        var list = services.BuddyList;
        var dutyMembers = new List<object>();
        for (var slot = 0; slot < Math.Min(list.Length, 24); slot++)
            if (list[slot] is { } member && member.DataID != 0)
                dutyMembers.Add(new { slot, buddy = Buddy(member) });
        return new { status = "ok", companion = Buddy(list.CompanionBuddy), pet = Buddy(list.PetBuddy), dutyMembers };

        static object? Buddy(IBuddyMember? member) => member is null || member.DataID == 0 ? null : new
        {
            dataID = member.DataID, currentHP = member.CurrentHP, maxHP = member.MaxHP, actor = Actor(member.GameObject),
        };
    }

    private static object? Actor(IGameObject? actor) => actor is null ? null : new
    {
        gameObjectID = $"0x{actor.GameObjectId:X}", name = actor.Name.TextValue,
        kind = actor.ObjectKind.ToString(), actor.IsTargetable, actor.IsDead,
    };
}
