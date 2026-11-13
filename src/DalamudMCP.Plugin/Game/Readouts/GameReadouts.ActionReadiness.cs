using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using GameAction = Lumina.Excel.Sheets.Action;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameReadouts
{
    internal const string ACTION_CHECK_TOOL = "inspect_action_readiness";
    internal const string JOB_ACTIONS_TOOL = "list_current_job_actions";
    internal const string ACTION_REQUEST_TOOL = "request_checked_action";
    private long nextDirectAction;
    private const byte GCD_SHEET_GROUP = 58;

    internal static unsafe ActionManager* ActionManagerForReadout() => GameDiagnostics.ReadoutLayoutKnown &&
        ActionManager.Addresses.Instance.Value != 0 ? ActionManager.Instance() : null;

    private static unsafe bool HasRecastQueries =>
        ActionManager.MemberFunctionPointers.GetAdjustedActionId != null &&
        ActionManager.MemberFunctionPointers.GetRecastTime != null &&
        ActionManager.MemberFunctionPointers.GetRecastTimeElapsed != null &&
        ActionManager.MemberFunctionPointers.IsRecastTimerActive != null &&
        ActionManager.MemberFunctionPointers.GetCurrentCharges != null &&
        ActionManager.MemberFunctionPointers.GetMaxCharges != null &&
        ActionManager.MemberFunctionPointers.GetAdjustedRecastTime != null &&
        ActionManager.MemberFunctionPointers.GetRecastGroup != null &&
        ActionManager.MemberFunctionPointers.GetAdditionalRecastGroup != null &&
        ActionManager.MemberFunctionPointers.GetRecastGroupDetail != null;

    public unsafe object[]? CombatRecasts(uint[]? ids, uint level)
    {
        if (ids is null || !HasRecastQueries) return null;
        var manager = ActionManagerForReadout();
        if (manager == null) return null;
        var sheet = services.DataManager.GetExcelSheet<GameAction>();
        var output = new List<object>(ids.Length);
        foreach (var id in ids)
        {
            if (!sheet.TryGetRow(id, out var row) || !row.IsPlayerAction)
            {
                output.Add(new { actionID = id, status = "unknown_or_nonplayer_action" });
                continue;
            }
            var adjusted = manager->GetAdjustedActionId(id);
            if (adjusted == 0 || !sheet.TryGetRow(adjusted, out var resolved))
            {
                adjusted = id;
                resolved = row;
            }
            output.Add(new { actionID = id, adjustedActionID = adjusted, recast = ReadRecast(manager, ActionType.Action, adjusted, resolved, level) });
        }
        return [.. output];
    }

    public unsafe object ActionReadiness(string tool, ToolArguments input, Func<bool> isAllowed)
    {
        if (!GameDiagnostics.ReadoutLayoutKnown)
            return new { status = "unsupported_sdk" };
        if (!services.PlayerState.IsLoaded || !services.ClientState.IsLoggedIn ||
            services.ObjectTable.LocalPlayer is not { } player ||
            services.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51))
            return new { status = "unavailable", reason = "player_not_ready" };
        var manager = ActionManagerForReadout();
        if (manager == null || !HasRecastQueries || ActionManager.MemberFunctionPointers.GetActionStatus == null)
            return new { status = "unavailable", reason = "action_manager" };

        var jobID = player.ClassJob.RowId;
        var territoryID = services.ClientState.TerritoryType;
        var effectiveLevel = services.PlayerState.EffectiveLevel;
        if (effectiveLevel <= 0) return new { status = "unavailable", reason = "effective_level_not_ready" };
        var level = (uint)effectiveLevel;
        var target = services.TargetManager.Target;
        var targetID = target?.GameObjectId ?? player.GameObjectId;
        var hardTargetID = target?.GameObjectId ?? 0;
        var sheet = services.DataManager.GetExcelSheet<GameAction>();
        if (tool == JOB_ACTIONS_TOOL)
        {
            var offset = input.Integer("offset", 0, 0, 20000);
            var limit = input.Integer("limit", 30, 1, 60);
            var activeOnly = input.Boolean("recharging_only");
            if (player.ClassJob.ValueNullable is not { } job) return new { status = "unavailable", reason = "job_row" };
            var pvp = services.ClientState.IsPvP;
            var catalogue = CurrentJobRows(sheet, job, level, pvp);
            var entries = new List<object>(limit);
            var cursor = offset;
            for (; cursor < catalogue.Length && entries.Count < limit && cursor < offset + 240; cursor++)
            {
                var row = catalogue[cursor];
                var adjusted = manager->GetAdjustedActionId(row.RowId);
                if (adjusted == 0) adjusted = row.RowId;
                var effective = sheet.GetRowOrDefault(adjusted) ?? row;
                var recast = ReadRecast(manager, ActionType.Action, adjusted, effective, level);
                if (activeOnly && (!recast.TimerActive || effective.CooldownGroup is 0 or GCD_SHEET_GROUP &&
                                   effective.AdditionalCooldownGroup is 0 or GCD_SHEET_GROUP)) continue;
                entries.Add(new { actionID = row.RowId, adjustedActionID = adjusted, name = row.Name.ToString(),
                    requiredLevel = row.ClassJobLevel, roleAction = row.IsRoleAction,
                    unlocked = row.UnlockLink.RowId == 0 || services.UnlockState.IsActionUnlocked(row), recast });
            }
            return new { status = "ok", jobID, effectiveLevel = level, territoryID, totalCandidates = catalogue.Length,
                rows = entries, nextOffset = cursor < catalogue.Length ? (int?)cursor : null };
        }

        var kind = input.Text("action_kind", "action");
        if (kind is not ("action" or "general_action")) throw new ArgumentException("未知技能类型。");
        if (input.Has("action_id") == input.Has("action_name")) throw new ArgumentException("请选择技能 ID 或精确名称。");
        var id = input.Has("action_id") ? input.Element("action_id").GetUInt32() : (uint?)null;
        var name = id.HasValue ? null : input.Text("action_name");
        if (id == 0 || name is { Length: > 128 }) throw new ArgumentException("技能 ID 或名称不合法。");
        var type = kind == "action" ? ActionType.Action : ActionType.GeneralAction;
        var generalSheet = services.DataManager.GetExcelSheet<GeneralAction>();
        var general = default(GeneralAction);
        GameAction rowData = default;
        var hasActionRow = true;
        if (type == ActionType.Action)
        {
            var matches = id.HasValue ? [] : sheet.Where(row => row.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase))
                .Where(row => row.IsPlayerAction || generalSheet.Any(link => link.Action.RowId == row.RowId)).Take(9).ToArray();
            if (matches.Length > 1) return new { status = "ambiguous_action", actionIDs = matches.Select(row => row.RowId).ToArray() };
            if (!(id.HasValue ? sheet.TryGetRow(id.Value, out rowData) : (rowData = matches.FirstOrDefault()).RowId != 0))
                return new { status = "unknown_action" };
            if (!rowData.IsPlayerAction) return NonPlayerAction(rowData.RowId, rowData.Name.ToString(), generalSheet);
        }
        else
        {
            var matches = id.HasValue ? [] : generalSheet.Where(row => row.RowId != 0 &&
                row.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)).Take(9).ToArray();
            if (matches.Length > 1) return new { status = "ambiguous_action", actionIDs = matches.Select(row => row.RowId).ToArray() };
            if (!(id.HasValue ? generalSheet.TryGetRow(id.Value, out general) : (general = matches.FirstOrDefault()).RowId != 0) ||
                string.IsNullOrWhiteSpace(general.Name.ToString()))
                return new { status = "unknown_action" };
            hasActionRow = general.Action.RowId != 0 && sheet.TryGetRow(general.Action.RowId, out rowData);
        }

        var actionID = type == ActionType.Action ? rowData.RowId : general.RowId;
        var adjustedID = type == ActionType.Action ? manager->GetAdjustedActionId(actionID) : actionID;
        if (adjustedID == 0) adjustedID = actionID;
        var effectiveAction = type == ActionType.Action ? sheet.GetRowOrDefault(adjustedID) ?? rowData : rowData;
        var unlocked = type == ActionType.Action
            ? rowData.UnlockLink.RowId == 0 || services.UnlockState.IsActionUnlocked(rowData)
            : general.UnlockLink == 0 || services.UnlockState.IsGeneralActionUnlocked(general);
        var selfTarget = type == ActionType.GeneralAction || effectiveAction.CanTargetSelf &&
            !effectiveAction.CanTargetHostile && !effectiveAction.CanTargetParty && !effectiveAction.CanTargetAlly &&
            !effectiveAction.CanTargetAlliance && !effectiveAction.CanTargetOwnPet && !effectiveAction.CanTargetPartyPet;
        var explicitTarget = input.Has("target_id") ? input.ObjectID("target_id") : (ulong?)null;
        if (explicitTarget is { } requestedTarget &&
            (selfTarget && requestedTarget != player.GameObjectId || !services.ObjectTable.Any(actor => actor.GameObjectId == requestedTarget)))
            return new { status = "target_unavailable", requestDispatched = false };
        var checkedTargetID = explicitTarget ?? (selfTarget ? player.GameObjectId : targetID);
        uint detail = 0;
        var code = manager->GetActionStatus(type, actionID, checkedTargetID, true, true, &detail);
        var message = code == 0 ? null : services.DataManager.GetExcelSheet<LogMessage>().GetRowOrDefault(code)?.Text.ToString();
        var casting = services.Condition[ConditionFlag.Casting];
        var queued = manager->ActionQueued;
        var areaTargeting = manager->AreaTargetingActionId != 0;
        var reason = !unlocked ? "locked" : code != 0 ? "native_blocked" : casting ? "casting" :
            queued ? "action_queued" : areaTargeting ? "area_targeting" :
            manager->AnimationLock > 0.001f ? "animation_lock" : null;
        var primaryGroup = hasActionRow ? (byte?)effectiveAction.CooldownGroup : null;
        var secondaryGroup = hasActionRow ? (byte?)effectiveAction.AdditionalCooldownGroup : null;
        var snapshot = new
        {
            actionKind = kind, actionID, adjustedActionID = adjustedID, relatedActionID = hasActionRow ? (uint?)rowData.RowId : null,
            name = type == ActionType.Action ? rowData.Name.ToString() : general.Name.ToString(),
            jobID, territoryID, effectiveLevel = level, targetID = $"0x{checkedTargetID:X}", hardTargetID = $"0x{hardTargetID:X}",
            targetSource = explicitTarget.HasValue ? "explicit" : selfTarget || target is null ? "self" : "current",
            unlocked, nativeReady = code == 0, availableNow = reason is null, reason,
            nativeStatus = code, nativeDetail = detail, gameMessageTemplate = message,
            casting, actionQueued = queued, areaTargeting,
            animationLockSeconds = FiniteNonnegative(manager->AnimationLock),
            primaryRecastGroup = primaryGroup, additionalRecastGroup = secondaryGroup,
            usesGlobalCooldown = primaryGroup == GCD_SHEET_GROUP || secondaryGroup == GCD_SHEET_GROUP,
            recast = hasActionRow ? ReadRecast(manager, type, adjustedID, effectiveAction, level) : null,
        };
        if (tool != ACTION_REQUEST_TOOL) return new { status = "ok", snapshot };
        if (jobID != input.Element("expected_job_id").GetUInt32() ||
            territoryID != input.Element("expected_territory_id").GetUInt32() ||
            hardTargetID != input.ObjectID("expected_target_id") ||
            adjustedID != input.Element("expected_adjusted_action_id").GetUInt32())
            return new { status = "context_changed", requestDispatched = false, snapshot };
        if (services.ClientState.IsPvP || hasActionRow && (effectiveAction.IsPvP || effectiveAction.TargetArea) ||
            type == ActionType.GeneralAction && actionID is not (2 or 4 or 7 or 8 or 23))
            return new { status = "unsupported_action", requestDispatched = false, snapshot };
        if (!snapshot.availableNow) return new { status = "blocked", requestDispatched = false, snapshot };
        if (ActionManager.MemberFunctionPointers.UseAction == null)
            return new { status = "unavailable", reason = "action_dispatch", requestDispatched = false };
        if (Environment.TickCount64 < nextDirectAction) return new { status = "rate_limited", requestDispatched = false };
        if (!isAllowed()) throw new InvalidOperationException("技能请求权限已关闭。");
        nextDirectAction = Environment.TickCount64 + 750;
        var accepted = manager->UseAction(type, actionID, checkedTargetID);
        return new { status = accepted ? "requested" : "not_accepted", requestDispatched = true,
            requestAccepted = accepted, resultObserved = false, snapshot };
    }

    private readonly Dictionary<(uint Job, bool PVP), GameAction[]> jobActionRows = new();

    private GameAction[] CurrentJobRows(ExcelSheet<GameAction> sheet, ClassJob job, uint level, bool pvp)
    {
        var key = (job.RowId, pvp);
        if (!jobActionRows.TryGetValue(key, out var rows))
        {
            var abbreviation = job.Abbreviation.ToString();
            var membership = typeof(ClassJobCategory).GetProperty(abbreviation);
            var categories = new Dictionary<uint, bool>();
            rows = sheet.Where(row => row.RowId != 0 && row.IsPlayerAction && row.IsPvP == pvp && (pvp || row.ClassJobLevel != 0) &&
                (row.ClassJob.RowId == job.RowId || job.ClassJobParent.RowId != 0 && row.ClassJob.RowId == job.ClassJobParent.RowId ||
                 row.IsRoleAction && IsRoleMember(row.ClassJobCategory.RowId))).ToArray();
            jobActionRows[key] = rows;

            bool IsRoleMember(uint id)
            {
                if (membership?.PropertyType != typeof(bool) || id == 0) return false;
                if (!categories.TryGetValue(id, out var included))
                    categories[id] = included = services.DataManager.GetExcelSheet<ClassJobCategory>().TryGetRow(id, out var category) &&
                        membership.GetValue(category) is true;
                return included;
            }
        }
        return rows.Where(row => row.ClassJobLevel <= level).ToArray();
    }

    private static unsafe ActionRecast ReadRecast(ActionManager* manager, ActionType type, uint id, GameAction row, uint level)
    {
        var active = manager->IsRecastTimerActive(type, id);
        var duration = FiniteNonnegative(manager->GetRecastTime(type, id));
        var elapsed = FiniteNonnegative(manager->GetRecastTimeElapsed(type, id));
        var primary = manager->GetRecastGroup((int)type, id);
        var secondary = type == ActionType.Action && row.AdditionalCooldownGroup != 0
            ? manager->GetAdditionalRecastGroup(type, id) : -1;
        var group = primary;
        var gcd = type != ActionType.Action ? -1 :
            row.CooldownGroup == GCD_SHEET_GROUP ? primary :
            row.AdditionalCooldownGroup == GCD_SHEET_GROUP ? secondary : -1;
        float? gcdRemaining = null;
        if (gcd is >= 0 and < 80 && manager->GetRecastGroupDetail(gcd) is var gcdDetail && gcdDetail != null)
            gcdRemaining = gcdDetail->IsActive ? FiniteNonnegative(Math.Max(0, gcdDetail->Total - gcdDetail->Elapsed)) : 0;
        if (type == ActionType.Action && row.CooldownGroup == GCD_SHEET_GROUP && secondary is >= 0 and < 80 &&
            secondary != primary && manager->GetRecastGroupDetail(secondary) is var extra && extra != null)
        {
            group = secondary;
            active = extra->IsActive;
            duration = FiniteNonnegative(extra->Total);
            elapsed = FiniteNonnegative(extra->Elapsed);
        }
        uint? current = type == ActionType.Action ? manager->GetCurrentCharges(id) : null;
        ushort? maximum = type == ActionType.Action ? ActionManager.GetMaxCharges(id, level) : null;
        var timing = ChargeTiming(active, duration, elapsed, current, maximum,
            type == ActionType.Action ? ActionManager.GetAdjustedRecastTime(type, id) / 1000f : null);
        return new(active, duration, elapsed, current, maximum, timing.UntilAvailable, timing.Next, timing.Full,
            group is >= 0 and < 80 ? group : null, gcdRemaining, timing.UntilAvailable is { } wait ? wait == 0 : null);
    }

    internal static (float? UntilAvailable, float? Next, float? Full) ChargeTiming(
        bool active, float? duration, float? elapsed, uint? current, ushort? maximum, float? interval)
    {
        if (!active || maximum is > 0 && current >= maximum) return (0, 0, 0);
        if (elapsed is not { } passed) return (current is > 0 ? 0 : null, null, null);
        if (maximum is > 1 && current is { } charges && interval is > 0 and var step)
        {
            var next = Math.Clamp(step - passed % step, 0, step);
            return (charges > 0 ? 0 : next, next, next + (maximum.Value - charges - 1) * step);
        }
        if (duration is not { } total) return (current is > 0 ? 0 : null, null, null);
        var remaining = Math.Max(0, total - passed);
        return (current is > 0 ? 0 : remaining, remaining, remaining);
    }

    private sealed record ActionRecast(bool TimerActive, float? ReportedDurationSeconds, float? ReportedElapsedSeconds,
        uint? CurrentCharges, ushort? MaximumCharges, float? UntilAvailableSeconds, float? NextChargeSeconds,
        float? FullRechargeSeconds, int? RuntimeGroup, float? GlobalCooldownRemainingSeconds, bool? ReadyByRecast);
}
