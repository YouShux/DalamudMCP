using Dalamud.Game.ClientState.JobGauge.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using GameAction = Lumina.Excel.Sheets.Action;
using GameStatus = Lumina.Excel.Sheets.Status;
using NativePlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameReadouts
{
    internal const string GAUGE_TOOL = "inspect_job_meter";
    internal const string RECAST_TOOL = "inspect_action_recasts";
    internal const string UNLOCK_TOOL = "inspect_character_unlock";
    internal const string SHEETS_TOOL = "lookup_game_rows";
    internal const string MAP_TOOL = "inspect_area_map";
    internal const string SOCIETIES_TOOL = "inspect_society_dailies";

    internal static readonly string[] UnlockKinds =
    [
        "achievement", "title", "action", "aether_current", "buddy_action", "buddy_equip", "job", "minion",
        "craft_action", "emote", "general_action", "glasses", "guide", "duty", "item", "leve", "mount",
        "orchestrion", "ornament", "quest", "recipe", "trait", "card", "unlock_link",
    ];
    internal static readonly string[] GameTables = ["item", "action", "general_action", "status", "territory", "job", "achievement", "title"];

    public unsafe object ReadJobMeter(uint jobID)
    {
        if (!GameDiagnostics.CurrentCnLayoutKnown) return new { status = "unsupported_sdk" };
        var manager = JobGaugeManager.Addresses.Instance.Value != 0 ? JobGaugeManager.Instance() : null;
        if (manager == null || manager->ClassJobId != jobID)
            return new { status = "unavailable", reason = "gauge_not_ready", jobID };

        var gauge = jobID switch
        {
            19 => Read<PLDGauge>(g => new { g.OathGauge }),
            20 => Read<MNKGauge>(g => new { g.Chakra, g.BeastChakra1, g.BeastChakra2, g.BeastChakra3, g.Nadi, g.BlitzTimeRemaining }),
            21 => Read<WARGauge>(g => new { g.BeastGauge }),
            22 => Read<DRGGauge>(g => new { g.LOTDTimer, g.IsLOTDActive, g.EyeCount, g.FirstmindsFocusCount }),
            23 => Read<BRDGauge>(g => new { g.Song, g.SongTimer, g.Repertoire, g.SoulVoice, g.Coda }),
            24 => Read<WHMGauge>(g => new { g.LilyTimer, g.Lily, g.BloodLily }),
            25 => Read<BLMGauge>(g => new { g.EnochianTimer, g.PolyglotStacks, g.UmbralHearts, g.UmbralIceStacks, g.AstralFireStacks, g.AstralSoulStacks }),
            27 => Read<SMNGauge>(g => new { g.SummonTimerRemaining, g.AttunementTimerRemaining, g.AttunementCount, g.AttunementType, g.AetherflowStacks }),
            28 => Read<SCHGauge>(g => new { g.Aetherflow, g.FairyGauge, g.SeraphTimer, g.DismissedFairy }),
            30 => Read<NINGauge>(g => new { g.Ninki, g.Kazematoi }),
            31 => Read<MCHGauge>(g => new { g.Heat, g.Battery, g.OverheatTimeRemaining, g.SummonTimeRemaining }),
            32 => Read<DRKGauge>(g => new { g.Blood, g.DarksideTimeRemaining, g.ShadowTimeRemaining, g.HasDarkArts, g.DeliriumComboStep }),
            33 => Read<ASTGauge>(g => new { g.Card1, g.Card2, g.Card3, g.DrawnCrownCard, g.ActiveDraw }),
            34 => Read<SAMGauge>(g => new { g.Kenki, g.MeditationStacks, g.HasSetsu, g.HasGetsu, g.HasKa, g.Kaeshi }),
            35 => Read<RDMGauge>(g => new { g.WhiteMana, g.BlackMana, g.ManaStacks }),
            37 => Read<GNBGauge>(g => new { g.Ammo, g.AmmoComboStep, g.MaxTimerDuration }),
            38 => Read<DNCGauge>(g => new { g.Feathers, g.Esprit, g.CompletedSteps, g.IsDancing }),
            39 => Read<RPRGauge>(g => new { g.Soul, g.Shroud, g.EnshroudedTimeRemaining, g.LemureShroud, g.VoidShroud }),
            40 => Read<SGEGauge>(g => new { g.AddersgallTimer, g.Addersgall, g.Addersting, g.Eukrasia }),
            41 => Read<VPRGauge>(g => new { g.RattlingCoilStacks, g.SerpentOffering, g.AnguineTribute, g.DreadCombo, g.SerpentCombo }),
            42 => Read<PCTGauge>(g => new { g.PalleteGauge, g.Paint, g.CreatureMotifDrawn, g.WeaponMotifDrawn, g.LandscapeMotifDrawn, g.CreatureFlags, g.CanvasFlags }),
            _ => null,
        };
        return new { status = gauge is null ? "no_meter" : "ok", jobID, meter = gauge };

        object Read<T>(Func<T, object> project) where T : JobGaugeBase => project(services.JobGauges.Get<T>());
    }

    private unsafe object Recasts(ToolArguments input)
    {
        var ids = ReadIDs(input, "action_ids", 24);
        var kind = input.Text("action_kind", "action");
        var type = kind switch
        {
            "action" => ActionType.Action,
            "general_action" => ActionType.GeneralAction,
            _ => throw new ArgumentException("action_kind 必须为 action 或 general_action。"),
        };
        if (!GameDiagnostics.CurrentCnLayoutKnown) return new { status = "unsupported_sdk" };
        var level = services.PlayerState.EffectiveLevel;
        if (!services.ClientState.IsLoggedIn || !services.PlayerState.IsLoaded || level <= 0)
            return new { status = "unavailable", reason = "player_stats_not_ready" };
        var manager = ActionManagerForReadout();
        if (manager == null || !HasRecastQueries)
            return new { status = "unavailable", reason = "action_manager" };

        var sheet = services.DataManager.GetExcelSheet<GameAction>();
        var generalSheet = services.DataManager.GetExcelSheet<GeneralAction>();
        var rows = new List<object>(ids.Length);
        foreach (var actionID in ids)
        {
            string name;
            GameAction row;
            var adjustedActionID = actionID;
            bool unlocked;
            if (type == ActionType.GeneralAction)
            {
                if (!generalSheet.TryGetRow(actionID, out var general) || general.Name.IsEmpty)
                {
                    rows.Add(new { actionID, status = "unknown_action" });
                    continue;
                }
                name = general.Name.ToString();
                if (general.Action.RowId == 0 || !sheet.TryGetRow(general.Action.RowId, out row))
                {
                    rows.Add(new { actionID, name, status = "no_linked_recast" });
                    continue;
                }
                unlocked = general.UnlockLink == 0 || services.UnlockState.IsGeneralActionUnlocked(general);
            }
            else
            {
                if (!sheet.TryGetRow(actionID, out row))
                {
                    rows.Add(new { actionID, status = "unknown_action" });
                    continue;
                }
                name = row.Name.ToString();
                if (!row.IsPlayerAction)
                {
                    rows.Add(NonPlayerAction(actionID, name, generalSheet));
                    continue;
                }
                unlocked = row.UnlockLink.RowId == 0 || services.UnlockState.IsActionUnlocked(row);
                var adjusted = manager->GetAdjustedActionId(actionID);
                if (adjusted != 0 && sheet.TryGetRow(adjusted, out var effective))
                {
                    adjustedActionID = adjusted;
                    row = effective;
                }
            }

            if (!unlocked)
            {
                rows.Add(new { actionID, status = "locked", name });
                continue;
            }
            var recast = ReadRecast(manager, type, adjustedActionID, row, (uint)level);
            rows.Add(new { actionID, adjustedActionID, status = "ok", name,
                recast.TimerActive, recast.ReportedDurationSeconds, recast.ReportedElapsedSeconds,
                recast.CurrentCharges, recast.MaximumCharges, recast.UntilAvailableSeconds,
                recast.NextChargeSeconds, recast.FullRechargeSeconds, recast.RuntimeGroup,
                recast.GlobalCooldownRemainingSeconds, recast.ReadyByRecast });
        }
        return new { status = "ok", actionKind = kind, effectiveLevel = level, rows };
    }

    private object Unlock(ToolArguments input)
    {
        var kind = input.Text("kind");
        var id = input.Element("row_id").GetUInt32();
        if (id == 0) throw new ArgumentOutOfRangeException("row_id");
        var unlock = services.UnlockState;
        if (kind == "achievement" && !unlock.IsAchievementListLoaded || kind == "title" && !unlock.IsTitleListLoaded)
            return new { status = "not_loaded", kind, rowID = id };
        if (kind == "action" && services.DataManager.GetExcelSheet<GameAction>().TryGetRow(id, out var action) && !action.IsPlayerAction)
            return NonPlayerAction(id, action.Name.ToString(), services.DataManager.GetExcelSheet<GeneralAction>());
        bool? unlocked = kind switch
        {
            "achievement" => Check<Achievement>(unlock.IsAchievementComplete),
            "title" => Check<Title>(unlock.IsTitleUnlocked),
            "action" => Check<GameAction>(row => row.UnlockLink.RowId == 0 || unlock.IsActionUnlocked(row)),
            "aether_current" => Check<AetherCurrent>(unlock.IsAetherCurrentUnlocked),
            "buddy_action" => Check<BuddyAction>(unlock.IsBuddyActionUnlocked),
            "buddy_equip" => Check<BuddyEquip>(unlock.IsBuddyEquipUnlocked),
            "job" => Check<ClassJob>(unlock.IsClassJobUnlocked),
            "minion" => Check<Companion>(unlock.IsCompanionUnlocked),
            "craft_action" => Check<CraftAction>(unlock.IsCraftActionUnlocked),
            "emote" => Check<Emote>(unlock.IsEmoteUnlocked),
            "general_action" => Check<GeneralAction>(row => row.UnlockLink == 0 || unlock.IsGeneralActionUnlocked(row)),
            "glasses" => Check<Glasses>(unlock.IsGlassesUnlocked),
            "guide" => Check<HowTo>(unlock.IsHowToUnlocked),
            "duty" => Check<InstanceContent>(unlock.IsInstanceContentUnlocked),
            "item" => services.DataManager.GetExcelSheet<Item>().TryGetRow(id, out var item) && unlock.IsItemUnlockable(item)
                ? unlock.IsItemUnlocked(item) : null,
            "leve" => Check<Leve>(unlock.IsLeveCompleted),
            "mount" => Check<Mount>(unlock.IsMountUnlocked),
            "orchestrion" => Check<Orchestrion>(unlock.IsOrchestrionUnlocked),
            "ornament" => Check<Ornament>(unlock.IsOrnamentUnlocked),
            "quest" => Check<Quest>(unlock.IsQuestCompleted),
            "recipe" => Check<Recipe>(unlock.IsRecipeUnlocked),
            "trait" => Check<Trait>(unlock.IsTraitUnlocked),
            "card" => Check<TripleTriadCard>(unlock.IsTripleTriadCardUnlocked),
            "unlock_link" => unlock.IsUnlockLinkUnlocked(id),
            _ => throw new ArgumentException("未知解锁类型。"),
        };
        return new { status = unlocked.HasValue ? "ok" : "unknown_or_not_unlockable", kind, rowID = id, unlocked };

        bool? Check<T>(Func<T, bool> predicate) where T : struct, IExcelRow<T> =>
            services.DataManager.GetExcelSheet<T>().TryGetRow(id, out var row) ? predicate(row) : null;
    }

    private static object NonPlayerAction(uint actionID, string name, ExcelSheet<GeneralAction> sheet)
    {
        var general = sheet.Where(row => row.RowId != 0 && row.Action.RowId == actionID)
            .Select(row => row.RowId).Take(8).ToArray();
        return new { status = general.Length == 0 ? "not_player_action" : "use_general_action", actionID, name,
            generalActionIDs = general };
    }

    private object SheetRows(ToolArguments input)
    {
        var table = input.Text("table");
        var limit = input.Integer("limit", 30, 1, 64);
        var offset = input.Integer("offset", 0, 0, 1_000_000);
        var contains = input.Text("name_contains", string.Empty);
        if (contains.Length > 128) throw new ArgumentException("名称筛选最多 128 个字符。");
        var ids = input.Has("row_ids") ? ReadIDs(input, "row_ids", 64, allowZero: true) : null;
        if (ids is not null && (input.Has("offset") || input.Has("name_contains") || input.Has("limit")))
            throw new ArgumentException("按行 ID 查询时不能同时分页或筛选。");
        return table switch
        {
            "item" => Project<Item>(x => x.Name.ToString(), x => new { x.RowId, name = x.Name.ToString(), description = x.Description.ToString(), x.Icon, itemLevel = x.LevelItem.RowId }),
            "action" => Project<GameAction>(x => x.Name.ToString(), x => new { x.RowId, name = x.Name.ToString(), x.Icon, x.ClassJobLevel, x.Recast100ms }),
            "general_action" => Project<GeneralAction>(x => x.Name.ToString(), x => new { x.RowId, name = x.Name.ToString(), actionID = x.Action.RowId, x.UnlockLink }),
            "status" => Project<GameStatus>(x => x.Name.ToString(), x => new { x.RowId, name = x.Name.ToString(), description = x.Description.ToString(), x.Icon }),
            "territory" => Project<TerritoryType>(TerritoryName, x => new { x.RowId, name = TerritoryName(x), mapID = x.Map.RowId }),
            "job" => Project<ClassJob>(x => x.Name.ToString(), x => new { x.RowId, name = x.Name.ToString(), abbreviation = x.Abbreviation.ToString() }),
            "achievement" => Project<Achievement>(x => x.Name.ToString(), x => new { x.RowId, name = x.Name.ToString(), description = x.Description.ToString(), x.Points }),
            "title" => Project<Title>(x => x.Masculine.ToString(), x => new { x.RowId, masculine = x.Masculine.ToString(), feminine = x.Feminine.ToString(), x.IsPrefix },
                x => x.Feminine.ToString()),
            _ => throw new ArgumentException("未知数据表。"),
        };

        static string TerritoryName(TerritoryType row) => row.PlaceName.ValueNullable?.Name.ToString() ?? row.Name.ToString();

        object Project<T>(Func<T, string> name, Func<T, object> projection, Func<T, string>? alternate = null)
            where T : struct, IExcelRow<T>
        {
            var sheet = services.DataManager.GetExcelSheet<T>();
            var rows = new List<object>();
            var missing = new List<uint>();
            var cursor = offset;
            if (ids is not null)
            {
                foreach (var id in ids)
                    if (sheet.TryGetRow(id, out var row)) rows.Add(projection(row)); else missing.Add(id);
            }
            else
            {
                var end = Math.Min(sheet.Count, offset + 2048);
                while (cursor < end && rows.Count < limit)
                {
                    var row = sheet.GetRowAt(cursor++);
                    if (contains.Length == 0 || name(row).Contains(contains, StringComparison.OrdinalIgnoreCase) ||
                        alternate?.Invoke(row).Contains(contains, StringComparison.OrdinalIgnoreCase) == true)
                        rows.Add(projection(row));
                }
            }
            return new { status = "ok", table, source = "client_excel", language = services.ClientState.ClientLanguage.ToString(),
                totalRows = sheet.Count, rows, missingRowIDs = missing, nextOffset = ids is null && cursor < sheet.Count ? (int?)cursor : null };
        }
    }

    private unsafe object AreaMap(ToolArguments input)
    {
        var offset = input.Integer("offset", 0, 0, 132);
        var limit = input.Integer("limit", 30, 1, 64);
        if (!GameDiagnostics.CurrentCnLayoutKnown) return new { status = "unsupported_sdk" };
        var agent = AgentMap.Instance();
        if (agent == null) return new { status = "unavailable", reason = "map_agent" };
        var ui = services.GameGui.GetAddonByName("AreaMap", 1);
        var visible = !ui.IsNull && ui.IsVisible;
        var ready = !ui.IsNull && ui.IsReady;
        var selected = agent->SelectedMapId;
        if (input.Has("expected_map_id") && input.Element("expected_map_id").GetUInt32() != selected)
            return new { status = "map_changed", selectedMapID = selected, visible, ready };
        var markers = new List<object>(limit);
        var count = Math.Min(agent->MapMarkerCount, agent->MapMarkers.Length);
        var end = Math.Min(count, offset + limit);
        if (visible && ready)
            for (var i = offset; i < end; i++)
            {
                ref var marker = ref agent->MapMarkers[i];
                markers.Add(new
                {
                    index = i, iconID = marker.MapMarker.IconId, nativeX = marker.MapMarker.X,
                    nativeY = marker.MapMarker.Y, marker.DataType, marker.DataKey,
                });
            }
        return new
        {
            status = "ok", visible, ready, currentMapID = agent->CurrentMapId,
            currentTerritoryID = agent->CurrentTerritoryId, selectedMapID = selected,
            selectedTerritoryID = agent->SelectedTerritoryId, sizeFactor = agent->SelectedMapSizeFactor,
            offsetX = agent->SelectedOffsetX, offsetY = agent->SelectedOffsetY, markers,
            markersAvailable = visible && ready, scanIncomplete = agent->MapMarkerCount > count,
            nextOffset = visible && ready && end < count ? (int?)end : null,
        };
    }

    private unsafe object Societies()
    {
        if (!GameDiagnostics.CurrentCnLayoutKnown) return new { status = "unsupported_sdk" };
        if (!services.PlayerState.IsLoaded) return new { status = "unavailable", reason = "player_stats_not_ready" };
        var player = NativePlayerState.Addresses.Instance.Value != 0 ? NativePlayerState.Instance() : null;
        var quests = QuestManager.Addresses.Instance.Value != 0 ? QuestManager.Instance() : null;
        if (player == null || quests == null || QuestManager.MemberFunctionPointers.GetBeastTribeAllowance == null ||
            NativePlayerState.MemberFunctionPointers.GetBeastTribeRank == null ||
            NativePlayerState.MemberFunctionPointers.GetBeastTribeCurrentReputation == null ||
            NativePlayerState.MemberFunctionPointers.GetBeastTribeNeededReputation == null)
            return new { status = "unavailable", reason = "society_state" };
        var societies = new List<object>(20);
        foreach (var row in services.DataManager.GetExcelSheet<BeastTribe>())
        {
            if (row.RowId is 0 or > 20) continue;
            var id = (byte)row.RowId;
            var rank = player->GetBeastTribeRank(id);
            societies.Add(new { societyID = id, name = row.Name.ToString(), rank,
                currentReputation = rank == 0 ? (ushort?)null : player->GetBeastTribeCurrentReputation(id),
                neededReputation = rank == 0 ? (ushort?)null : player->GetBeastTribeNeededReputation(id) });
        }
        var daily = new List<object>();
        var questSheet = services.DataManager.GetExcelSheet<Quest>();
        foreach (var work in quests->DailyQuests)
        {
            if (work.QuestId == 0) continue;
            var rowID = (uint)(work.QuestId | 0x10000);
            daily.Add(new { rowID, name = questSheet.GetRowOrDefault(rowID)?.Name.ToString(), work.IsCompleted });
        }
        return new { status = "ok", remainingAllowance = quests->GetBeastTribeAllowance(), societies, dailyQuestLedger = daily };
    }

    private static uint[] ReadIDs(ToolArguments input, string field, int max, bool allowZero = false)
    {
        var items = input.Element(field);
        if (items.ValueKind != System.Text.Json.JsonValueKind.Array || items.GetArrayLength() < 1 || items.GetArrayLength() > max)
            throw new ArgumentException($"{field} 需要 1 至 {max} 个行 ID。");
        var result = new HashSet<uint>();
        foreach (var item in items.EnumerateArray())
            if (!item.TryGetUInt32(out var id) || (!allowZero && id == 0) || !result.Add(id))
                throw new ArgumentException($"{field} 包含无效或重复的行 ID。");
        return [.. result];
    }

    private static float? FiniteNonnegative(float number) => float.IsFinite(number) && number >= 0 ? number : null;
}
