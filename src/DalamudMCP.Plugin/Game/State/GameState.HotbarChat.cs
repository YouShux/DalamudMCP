using System.Globalization;
using System.Text;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameState
{
    internal const string HOTBAR_READ_TOOL = "inspect_hotbar_slot";
    internal const string HOTBAR_REQUEST_TOOL = "request_hotbar_slot";
    internal const string RECHARGES_TOOL = "list_hotbar_recharges";
    internal const string CHAT_TOOL = "submit_game_chat_line";
    internal static readonly string[] ChatChannels = ["say", "party", "alliance", "echo"];
    private long nextHotbarRequest;
    private long nextChatRequest;

    public unsafe object HotbarRecharges(ToolArguments input)
    {
        var limit = input.Integer("limit", 24, 1, 60);
        if (!GameDiagnostics.CurrentCnLayoutKnown) return new { status = "unsupported_sdk" };
        if (!services.ClientState.IsLoggedIn || services.ObjectTable.LocalPlayer is not { } player ||
            services.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51))
            return new { status = "unavailable", reason = "player_not_ready" };
        var module = RaptureHotbarModule.Instance();
        var manager = GameReadouts.ActionManagerForReadout();
        if (module == null || manager == null || RaptureHotbarModule.MemberFunctionPointers.GetSlotById == null ||
            ActionManager.MemberFunctionPointers.IsRecastTimerActive == null ||
            ActionManager.MemberFunctionPointers.GetRecastTime == null || ActionManager.MemberFunctionPointers.GetRecastTimeElapsed == null)
            return new { status = "unavailable", reason = "recast_manager" };
        var seen = new HashSet<(ActionType, uint)>();
        var results = new List<object>();
        var active = 0;
        for (uint bar = 0; bar < 10; bar++)
            for (uint slot = 0; slot < 12; slot++)
            {
                var entry = module->GetSlotById(bar, slot);
                if (entry == null || entry->IsEmpty) continue;
                var type = entry->CommandType switch
                {
                    RaptureHotbarModule.HotbarSlotType.Action => ActionType.Action,
                    RaptureHotbarModule.HotbarSlotType.GeneralAction => ActionType.GeneralAction,
                    _ => ActionType.None,
                };
                var id = entry->CommandId;
                if (type == ActionType.None || !seen.Add((type, id)) || !manager->IsRecastTimerActive(type, id)) continue;
                if (++active > limit) continue;
                var duration = manager->GetRecastTime(type, id);
                var elapsed = manager->GetRecastTimeElapsed(type, id);
                results.Add(new { bar = bar + 1, slot = slot + 1, actionID = id, actionKind = type.ToString(),
                    durationSeconds = float.IsFinite(duration) && duration >= 0 ? (float?)duration : null,
                    elapsedSeconds = float.IsFinite(elapsed) && elapsed >= 0 ? (float?)elapsed : null });
            }
        return new { status = "ok", jobID = player.ClassJob.RowId, active, truncated = active > limit, rows = results };
    }

    public unsafe object Hotbar(ToolArguments input, bool execute, Func<bool> isAllowed)
    {
        var bar = input.Integer("bar", 0, 1, 10);
        var slot = input.Integer("slot", 0, 1, 12);
        if (!(execute ? GameDiagnostics.NativeLayoutKnown : GameDiagnostics.CurrentCnLayoutKnown))
            return new { status = "unsupported_sdk" };
        if (!services.ClientState.IsLoggedIn || services.ObjectTable.LocalPlayer is not { } player ||
            services.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51))
            return new { status = "unavailable", reason = "player_not_ready" };
        var module = RaptureHotbarModule.Instance();
        if (module == null || RaptureHotbarModule.MemberFunctionPointers.GetSlotById == null)
            return new { status = "unavailable", reason = "hotbar" };
        var entry = module->GetSlotById((uint)(bar - 1), (uint)(slot - 1));
        if (entry == null || entry->IsEmpty) return new { status = "empty", bar, slot };
        var commandID = entry->CommandId;
        var apparentID = entry->ApparentActionId;
        var supported = entry->CommandType is RaptureHotbarModule.HotbarSlotType.Action or RaptureHotbarModule.HotbarSlotType.GeneralAction;
        var commandType = entry->CommandType.ToString();
        var jobID = player.ClassJob.RowId;
        var territoryID = services.ClientState.TerritoryType;
        var targetID = services.TargetManager.Target?.GameObjectId ?? 0;
        if (!execute) return new { status = "ok", bar, slot, commandID, apparentID, commandType, supported, jobID, territoryID, targetID = $"0x{targetID:X}" };
        if (commandID != input.Element("expected_command_id").GetUInt32() ||
            apparentID != input.Element("expected_apparent_id").GetUInt32() ||
            commandType != input.Text("expected_command_type") || jobID != input.Element("expected_job_id").GetUInt32() ||
            territoryID != input.Element("expected_territory_id").GetUInt32() || targetID != input.ObjectID("expected_target_id"))
            return new { status = "context_changed", requestDispatched = false };
        if (!supported || services.ClientState.IsPvP || RaptureHotbarModule.MemberFunctionPointers.ExecuteSlot == null)
            return new { status = "unsupported_command", requestDispatched = false };
        if (Environment.TickCount64 < nextHotbarRequest) return new { status = "rate_limited", requestDispatched = false };
        if (!isAllowed()) throw new InvalidOperationException("技能请求权限已关闭。");
        nextHotbarRequest = Environment.TickCount64 + 750;
        var accepted = module->ExecuteSlot(entry) != 0;
        return new { status = accepted ? "requested" : "not_accepted", requestDispatched = true, requestAccepted = accepted,
            resultObserved = false, bar, slot, commandID, commandType };
    }

    public unsafe object SendChatLine(ToolArguments input, Func<bool> isAllowed)
    {
        var channel = input.Text("channel");
        var message = FormatChatLine(channel, input.Element("message").GetString());
        if (!GameDiagnostics.NativeLayoutKnown) return new { status = "unsupported_sdk" };
        if (!services.ClientState.IsLoggedIn || services.ObjectTable.LocalPlayer is null ||
            services.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51) ||
            services.ClientState.TerritoryType != input.Element("expected_territory_id").GetUInt32())
            return new { status = "context_changed", requestDispatched = false };
        if (Environment.TickCount64 < nextChatRequest) return new { status = "rate_limited", requestDispatched = false };
        var ui = UIModule.Instance();
        if (ui == null || UIModule.MemberFunctionPointers.ProcessChatBoxEntry == null)
            return new { status = "unavailable", reason = "chat_entry" };
        if (!isAllowed()) throw new InvalidOperationException("聊天权限已关闭。");
        if (!SubmitClientLine(ui, message)) return new { status = "unavailable", reason = "chat_text" };
        nextChatRequest = Environment.TickCount64 + 2000;
        return new { status = "requested", requestDispatched = true, deliveryObserved = false, channel };
    }

    private static unsafe bool SubmitClientLine(UIModule* ui, string message)
    {
        if (ui == null || UIModule.MemberFunctionPointers.ProcessChatBoxEntry == null) return false;
        var native = Utf8String.FromString(message);
        if (native == null) return false;
        try { ui->ProcessChatBoxEntry(native, saveToHistory: false); }
        finally { native->Dtor(true); }
        return true;
    }

    internal static string FormatChatLine(string channel, string? text)
    {
        if (!ChatChannels.Contains(channel, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(text) ||
            text.Length > 500 || text.TrimStart().StartsWith('/') || text.Any(c => c is '<' or '>' ||
                char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
            throw new ArgumentException("聊天只接受指定频道内的一行纯文本，不能传命令。");
        var line = $"/{channel} {text.Trim()}";
        return Encoding.UTF8.GetByteCount(line) <= 500 ? line : throw new ArgumentException("聊天内容超过 UTF-8 字节限制。");
    }
}
