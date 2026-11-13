using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ContentFinderCondition = Lumina.Excel.Sheets.ContentFinderCondition;
using ContentRoulette = Lumina.Excel.Sheets.ContentRoulette;

namespace DalamudMCP.Plugin.Tools;

internal sealed unsafe partial class AddonTools
{
    internal const string DUTY_BROWSER_TOOL = "inspect_duty_browser";
    internal const string DUTY_ENTRIES_TOOL = "list_duty_entries";
    internal const string DUTY_SELECT_TOOL = "request_duty_selection";
    private const int MAX_DUTY_ROWS = 4096;
    private const int GROUP_COUNT = 10;
    private readonly Dictionary<Guid, (DutyIdentity State, long Expires)> dutyTokens = new();
    private long dutyWindowGeneration;
    private long dutyTerritoryGeneration;

    internal void ObserveDutyLifecycle(bool enable)
    {
        var lifecycle = services.AddonLifecycle;
        if (enable)
        {
            lifecycle.RegisterListener(AddonEvent.PreSetup, "ContentsFinder", OnDutyWindowChanged);
            lifecycle.RegisterListener(AddonEvent.PreHide, "ContentsFinder", OnDutyWindowChanged);
            lifecycle.RegisterListener(AddonEvent.PreClose, "ContentsFinder", OnDutyWindowChanged);
            lifecycle.RegisterListener(AddonEvent.PreFinalize, "ContentsFinder", OnDutyWindowChanged);
            services.ClientState.TerritoryChanged += OnDutyTerritoryChanged;
        }
        else
        {
            services.ClientState.TerritoryChanged -= OnDutyTerritoryChanged;
            lifecycle.UnregisterListener(OnDutyWindowChanged);
        }
    }

    private void OnDutyWindowChanged(AddonEvent _, AddonArgs __) => Interlocked.Increment(ref dutyWindowGeneration);

    private void OnDutyTerritoryChanged(uint _) => Interlocked.Increment(ref dutyTerritoryGeneration);

    internal object DutyBrowser(ToolArguments input, bool list)
    {
        var offset = list ? input.Integer("offset", 0, 0, MAX_DUTY_ROWS) : 0;
        var limit = list ? input.Integer("limit", 40, 1, 100) : 0;
        var group = list && input.Has("group_id") ? input.Integer("group_id", 0, 0, byte.MaxValue) : (int?)null;
        var current = ReadDutyBrowser();
        if (current.State is not { } state) return new { current.Status, current.Reason };
        if (group is { } requested && requested != state.GroupID)
            return new { status = "context_changed", reason = "group_changed", groupID = state.GroupID };
        var now = Environment.TickCount64;
        foreach (var id in dutyTokens.Where(row => row.Value.Expires <= now).Select(row => row.Key).ToArray()) dutyTokens.Remove(id);
        if (dutyTokens.Count >= 32) dutyTokens.Remove(dutyTokens.First().Key);
        var stateToken = Guid.NewGuid();
        dutyTokens.Add(stateToken, (state, now + 5000));
        var page = list ? current.Entries.Skip(offset).Take(limit).ToArray() : null;
        var addon = (AddonContentsFinder*)state.Addon;
        var groups = new DutyGroup[GROUP_COUNT];
        for (byte index = 0; index < groups.Length; index++)
        {
            var available = FindGroupEvent(addon, index) != null;
            groups[index] = new(index, GroupKey(index), index == state.GroupID,
                available && state.QueueState == ContentsFinderQueueState.None, available ? null : "group_listener_unavailable");
        }
        return new
        {
            status = current.EntriesComplete ? "ok" : "partial", current.Reason, addonID = state.AddonID, state.TerritoryID,
            groupID = state.GroupID, windowGeneration = state.WindowGeneration, groups, selectedDuty = DutyRowID(state.Selected), selectedKind = state.Selected.ContentType.ToString(),
            selectedRoulette = state.Selected.ContentType == ContentsType.Roulette && state.Selected.Id != 0 ? (uint?)state.Selected.Id : null,
            queueState = state.QueueState?.ToString(), queueAvailability = state.QueueState.HasValue ? "available" : "unknown",
            stateToken, tokenTtlMs = 5000, entries = page, totalEntries = current.Entries.Length,
            entriesComplete = current.EntriesComplete, nextOffset = page is { Length: > 0 } && offset + page.Length < current.Entries.Length
                ? (int?)offset + page.Length : null,
        };
    }

    internal DutyDispatch DispatchDuty(ToolArguments input, Func<bool> allowed, CancellationToken token, Func<bool> tryBeginDispatch)
    {
        var operation = input.Text("operation");
        if (operation is not ("open_duty" or "select_group") ||
            input.Has("duty_id") != (operation == "open_duty") || input.Has("group_id") != (operation == "select_group"))
            throw new ArgumentException("请为操作提供对应的 duty_id 或 group_id。");
        var dutyID = operation == "open_duty" ? input.Element("duty_id").GetUInt32() : (uint?)null;
        var groupID = operation == "select_group" ? (byte?)input.Integer("group_id", -1, 0, GROUP_COUNT - 1) : null;
        if (dutyID == 0) throw new ArgumentException("duty_id 必须大于零。");
        var territory = input.Element("expected_territory_id").GetUInt32();
        if (territory == 0) throw new ArgumentException("expected_territory_id 必须大于零。");
        var id = RepositoryPackages.ParseID(input.Text("state_token"));
        if (!dutyTokens.Remove(id, out var prior))
            return new("context_changed", "invalid_or_expired_token");
        if (prior.State.TerritoryGeneration != Volatile.Read(ref dutyTerritoryGeneration) ||
            prior.State.TerritoryID != territory || services.ClientState.TerritoryType != territory)
            return new("context_changed", "territory_changed");
        if (prior.State.WindowGeneration != Volatile.Read(ref dutyWindowGeneration))
            return new("context_changed", "window_lifecycle_changed");
        if (prior.Expires <= Environment.TickCount64)
            return new("context_changed", "invalid_or_expired_token");
        var current = ReadDutyBrowser();
        if (current.State is not { } state || state != prior.State)
            return new("context_changed", current.Reason ?? "duty_browser_changed");
        if (current.Reason == "group_refresh_pending")
            return new("context_changed", current.Reason);
        if (state.QueueState != ContentsFinderQueueState.None)
            return new("unavailable", "queue_active_or_unknown");

        var addon = (AddonContentsFinder*)state.Addon;
        AtkEvent* groupEvent = null;
        if (groupID is { } group)
        {
            if (group == state.GroupID) return new("already_matched", null, state, GroupID: group);
            groupEvent = FindGroupEvent(addon, group);
            if (groupEvent == null)
                return new("unavailable", "group_listener_unavailable");
            var sdkTable = AddonContentsFinder.StaticVirtualTablePointer;
            if (sdkTable == null || !GameDiagnostics.IsResolvedGameCode((nint)sdkTable->ReceiveEvent) ||
                addon->VirtualTable == null || addon->VirtualTable->ReceiveEvent == null)
                return new("unsupported_sdk", sdkTable == null ? "group_sdk_vtable_unresolved" : "group_receiver_unavailable");
        }
        else
        {
            if (!GameDiagnostics.IsResolvedGameCode((nint)AgentContentsFinder.MemberFunctionPointers.OpenRegularDuty))
                return new("unsupported_sdk", "open_regular_duty_unresolved");
            if (!current.Entries.Any(row => row.ContentFinderConditionID == dutyID && row.Selectable == true))
                return new("unavailable", "target_not_in_selectable_list");
            if (DutyRowID(state.Selected) == dutyID)
                return new("already_matched", null, state, dutyID);
        }

        if (!allowed()) return new("context_changed", "permission_changed");
        token.ThrowIfCancellationRequested();
        if (!tryBeginDispatch()) return new("cancelled", "dispatch_deadline_closed");
        try
        {
            if (groupEvent != null)
            {
                var notification = *groupEvent;
                notification.NextEvent = null;
                var data = new AtkEventData();
                addon->ReceiveEvent(notification.State.EventType, (int)notification.Param, &notification, &data);
            }
            else if (dutyID is { } duty)
                ((AgentContentsFinder*)state.Agent)->OpenRegularDuty(duty, false);
        }
        catch (Exception error) { return new("dispatch_unconfirmed", error.GetType().Name, state, dutyID, groupID, true); }
        return new("requested", null, state, dutyID, groupID, true);
    }

    internal string ObserveDuty(DutyDispatch dispatch)
    {
        var current = ReadDutyBrowser();
        if (dispatch.State is not { } before || current.State is not { } after ||
            before.Character != after.Character || before.TerritoryID != after.TerritoryID ||
            before.Agent != after.Agent || before.Addon != after.Addon || before.AddonID != after.AddonID ||
            before.QueueState != after.QueueState || before.WindowGeneration != after.WindowGeneration ||
            before.TerritoryGeneration != after.TerritoryGeneration)
            return "context_changed";
        if (current.Reason == "group_refresh_pending") return "requested";
        if (dispatch.GroupID is { } group)
            return after.GroupID == group && current.EntriesComplete ? "observed" :
                after.GroupID == before.GroupID || after.GroupID == group ? "requested" : "context_changed";
        return after.GroupID != before.GroupID ? "context_changed" : DutyRowID(after.Selected) == dispatch.DutyID ? "observed" : "requested";
    }

    private DutySnapshot ReadDutyBrowser()
    {
        if (!GameDiagnostics.CurrentCnLayoutKnown) return new("unsupported_sdk", "client_layout_unverified");
        if (!services.ClientState.IsLoggedIn || !services.PlayerState.IsLoaded ||
            services.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51))
            return new("unavailable", "character_unavailable");
        var wrapper = services.GameGui.GetAddonByName("ContentsFinder", 1);
        if (wrapper.IsNull || !wrapper.IsVisible || !wrapper.IsReady) return new("unavailable", "window_not_ready");
        var addon = (AddonContentsFinder*)wrapper.Address;
        var agent = AgentContentsFinder.Instance();
        if (agent == null || agent->AddonId != addon->Id || addon->NameString != "ContentsFinder") return new("unavailable", "agent_window_mismatch");
        var finder = ContentsFinder.Instance();
        var queue = finder == null ? (ContentsFinderQueueState?)null : finder->QueueInfo.QueueState;
        var state = new DutyIdentity((nint)agent, wrapper.Address, addon->Id, services.PlayerState.ContentId,
            services.ClientState.TerritoryType, agent->SelectedTab, agent->SelectedDuty, queue,
            Volatile.Read(ref dutyWindowGeneration), Volatile.Read(ref dutyTerritoryGeneration));
        if (addon->SelectedRadioButton != agent->SelectedTab || agent->SelectedTab >= GROUP_COUNT)
            return new("partial", "group_refresh_pending", state);
        var tree = addon->DutyList;
        var contentCount = agent->ContentList.LongCount;
        var treeCount = tree == null ? -1L : tree->Items.LongCount;
        if (agent->ListChanged || tree == null || contentCount is < 0 or > MAX_DUTY_ROWS || treeCount is < 0 or > MAX_DUTY_ROWS ||
            treeCount != tree->ItemCount || treeCount != addon->NumEntries)
            return new("partial", "visible_list_mapping_unverified", state);

        var conditions = services.DataManager.GetExcelSheet<ContentFinderCondition>();
        var roulettes = services.DataManager.GetExcelSheet<ContentRoulette>();
        var entries = new List<DutyEntry>();
        var complete = true;
        for (var index = 0; index < treeCount; index++)
        {
            var row = tree->Items[index].Value;
            if (row == null) { complete = false; continue; }
            if ((row->Type & (TreeListItemType.Group | TreeListItemType.SectionHeader)) != 0 || row->IsHidden) continue;
            // 原生点击处理器以 UIntValues[1] 作为从 1 开始的列表键。
            if (row->UIntValues.LongCount is < 2 or > 64) { complete = false; continue; }
            var key = row->UIntValues[1];
            if (key == 0) continue;
            if (key > contentCount) { complete = false; continue; }
            var content = agent->ContentList[(int)key - 1].Value;
            if (content == null || content->Id.Id == 0) { complete = false; continue; }
            var identity = content->Id;
            var name = identity.ContentType switch
            {
                ContentsType.Regular when conditions.TryGetRow(identity.Id, out var duty) => duty.Name.ToString(),
                ContentsType.Roulette when roulettes.TryGetRow(identity.Id, out var roulette) => roulette.Name.ToString(),
                _ => null,
            };
            if (name is null) { complete = false; continue; }
            var disabled = (row->State & TreeListItemState.Disabled) != 0;
            var selectable = queue.HasValue ? queue == ContentsFinderQueueState.None && !disabled : (bool?)null;
            entries.Add(new(index, name, DutyRowID(identity), identity.ContentType == ContentsType.Roulette ? identity.Id : null,
                selectable, selectable.HasValue ? "available" : "unknown",
                disabled ? "row_disabled" : !queue.HasValue ? "queue_state_unverified" : selectable == false ? "queue_active" : null));
        }
        return new(complete ? "ok" : "partial", complete ? null : "some_rows_unidentified", state, entries.ToArray(), complete);
    }

    private static AtkEvent* FindGroupEvent(AddonContentsFinder* addon, byte group)
    {
        var button = group switch
        {
            0 => addon->DutyRouletteRadioButton, 1 => addon->Dungeons1RadioButton, 2 => addon->Dungeons2RadioButton,
            3 => addon->GuildHeistsRadioButton, 4 => addon->Trials1RadioButton, 5 => addon->Trials2RadioButton,
            6 => addon->Raids1RadioButton, 7 => addon->Raids2RadioButton, 8 => addon->PvpRadioButton,
            9 => addon->GoldSaucerRadioButton, _ => null,
        };
        if (button == null || button->OwnerNode == null || !button->IsEnabled || addon->DutyList == null) return null;
        var owner = (AtkResNode*)button->OwnerNode;
        if (owner->NodeId != 41u + group || !AddonNodeScanner.IsEffectivelyVisible(owner) ||
            AddonNodeScanner.GetComponent(owner) != (AtkComponentBase*)button) return null;
        AtkEvent* found = null;
        var count = 0;
        for (var entry = owner->AtkEventManager.Event; entry != null; entry = entry->NextEvent)
        {
            if (++count > 64) return null;
            if (entry->State.EventType != AtkEventType.ButtonClick || entry->Listener != (AtkEventListener*)addon) continue;
            if (found != null || entry->Param != 10u + group) return null;
            found = entry;
        }
        return found;
    }

    private static string GroupKey(byte group) => group switch
    {
        0 => "roulette", 1 => "dungeons_1", 2 => "dungeons_2", 3 => "guildhests", 4 => "trials_1",
        5 => "trials_2", 6 => "raids_1", 7 => "raids_2", 8 => "pvp", 9 => "gold_saucer", _ => "unknown",
    };

    private static uint? DutyRowID(ContentsId id) => id.ContentType == ContentsType.Regular && id.Id != 0 ? id.Id : null;
    internal readonly record struct DutyIdentity(nint Agent, nint Addon, ushort AddonID, ulong Character, uint TerritoryID,
        byte GroupID, ContentsId Selected, ContentsFinderQueueState? QueueState, long WindowGeneration, long TerritoryGeneration);
    internal sealed record DutyDispatch(string Status, string? Reason, DutyIdentity? State = null, uint? DutyID = null,
        byte? GroupID = null, bool Dispatched = false);
    private sealed record DutyGroup(byte GroupID, string Key, bool Selected, bool Selectable, string? Reason);
    private sealed record DutyEntry(int Index, string Name, uint? ContentFinderConditionID, uint? RouletteID,
        bool? Selectable, string Availability, string? Reason);
    private sealed record DutySnapshot(string Status, string? Reason, DutyIdentity? State = null, DutyEntry[]? Rows = null, bool EntriesComplete = false)
    {
        public DutyEntry[] Entries => Rows ?? [];
    }
}
