using Dalamud.Game.ClientState.Conditions;
using DalamudMCP.Shared;

namespace DalamudMCP.Plugin.Tools;

public sealed partial class GameTools
{
    private const string BATTLE_CHECKPOINT_TOOL = "create_battle_checkpoint";
    private const string BATTLE_RESULT_TOOL = "wait_battle_result";
    private readonly Dictionary<Guid, BattleCheckpoint> battleCheckpoints = new();
    private int battleWaiters;

    private object CreateBattleCheckpoint(ToolArguments input)
    {
        var combatID = RepositoryPackages.ParseID(input.Text("combat_id"));
        var territoryID = input.Element("expected_territory_id").GetUInt32();
        var signal = input.Text("signal");
        var addon = input.Has("addon_name") ? input.Text("addon_name") : null;
        var label = input.Has("expected_text") ? input.Text("expected_text") : null;
        if (territoryID == 0 || signal switch
            {
                "duty_completed" => addon is not null || label is not null,
                "system_text" => addon is not null || label is not { Length: > 0 and <= 256 },
                "addon_text" => addon is not { Length: > 0 and <= 64 } || label is not { Length: > 0 and <= 256 },
                _ => true,
            }) throw new ArgumentException("请指定一种通关信号；文本信号需要完整原文。");

        if (!BattleContextReady() || services.ClientState.TerritoryType != territoryID)
            return new { status = "context_changed", combatID };
        if (!diagnostics.IsCurrentFight(combatID)) return new { status = "combat_not_recording", combatID };
        var duty = services.DutyState.ContentFinderCondition.RowId;
        if (signal == "duty_completed" && (duty == 0 || state.DutyAlreadyCompleted))
            return new { status = "duty_not_pending", combatID };
        if (addon is not null && addons.AddonVisible(addon))
            return new { status = "signal_already_visible", combatID };
        if (battleCheckpoints.Count >= 16)
        {
            var oldest = battleCheckpoints.MinBy(entry => entry.Value.Expires);
            battleCheckpoints.Remove(oldest.Key);
        }
        var checkpointID = Guid.NewGuid();
        battleCheckpoints.Add(checkpointID, new(combatID, services.PlayerState.ContentId, territoryID, state.TerritoryGeneration,
            duty, state.DutyCompletionSequence, chat.Sequence, signal, addon, label, Environment.TickCount64 + 900_000));
        return new { status = "ready", checkpointID, combatID, territoryID, signal };
    }

    private async Task<object> WaitBattleResultAsync(ToolArguments input, CancellationToken token)
    {
        var checkpointID = RepositoryPackages.ParseID(input.Text("checkpoint_id"));
        var combatID = RepositoryPackages.ParseID(input.Text("combat_id"));
        var timeout = input.Integer("timeout_ms", 5000, 0, 20000);
        if (Interlocked.Increment(ref battleWaiters) > 2)
        {
            Interlocked.Decrement(ref battleWaiters);
            return Result("busy");
        }
        try
        {
            var deadline = Environment.TickCount64 + timeout;
            while (true)
            {
                var read = await services.Framework.RunOnTick(() => ReadBattleSignal(checkpointID, combatID),
                    cancellationToken: token).WaitAsync(token).ConfigureAwait(false);
                if (read.Status != "waiting") return Result(read.Status, read.Signal, read.Text);
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) return Result("timeout", read.Signal);
                await Task.Delay((int)Math.Min(80, remaining), token).ConfigureAwait(false);
            }
        }
        finally { Interlocked.Decrement(ref battleWaiters); }

        object Result(string status, string? signal = null, string? observedText = null) =>
            new { status, checkpointID, combatID, signal, observedText, resultObserved = status == "observed" };
    }

    private (string Status, string? Signal, string? Text) ReadBattleSignal(Guid id, Guid combatID)
    {
        if (!battleCheckpoints.TryGetValue(id, out var checkpoint) || checkpoint.CombatID != combatID)
            return ("checkpoint_not_found", null, null);
        if (Environment.TickCount64 >= checkpoint.Expires)
        {
            battleCheckpoints.Remove(id);
            return ("checkpoint_expired", checkpoint.Signal, null);
        }
        if (!BattleContextReady() || services.PlayerState.ContentId != checkpoint.Character ||
            services.ClientState.TerritoryType != checkpoint.Territory || state.TerritoryGeneration != checkpoint.Generation ||
            services.DutyState.ContentFinderCondition.RowId != checkpoint.Duty)
            return ("context_changed", checkpoint.Signal, null);
        var phase = diagnostics.BattleOutcomePhase(combatID);
        if (phase is "interrupted" or "not_found") return ("combat_" + phase, checkpoint.Signal, null);
        var observed = checkpoint.Signal switch
        {
            "duty_completed" => state.HasDutyCompletion(checkpoint.DutySequence, checkpoint.Territory, checkpoint.Duty),
            "system_text" => chat.HasBattleSystemText(checkpoint.ChatSequence, checkpoint.Label!),
            "addon_text" => addons.ReadNPCDialogue(checkpoint.Addon!, checkpoint.Label) is { Matches: true },
            _ => false,
        };
        return observed ? ("observed", checkpoint.Signal, checkpoint.Label) : ("waiting", checkpoint.Signal, null);
    }

    private bool BattleContextReady() => services.ClientState.IsLoggedIn && services.PlayerState.IsLoaded &&
        services.PlayerState.ContentId != 0 && services.ObjectTable.LocalPlayer is not null &&
        !services.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51);

    private sealed record BattleCheckpoint(Guid CombatID, ulong Character, uint Territory, long Generation, uint Duty,
        long DutySequence, long ChatSequence, string Signal, string? Addon, string? Label, long Expires);

    private static ToolDefinition BattleCheckpointTool() => Define(BATTLE_CHECKPOINT_TOOL, "记录本场通关观察起点",
        "在指定战斗录制中记录当前区域及信号起点，供独立核对通关。", new()
        {
            ["combat_id"] = new { type = "string" }, ["expected_territory_id"] = new { type = "integer", minimum = 1, maximum = uint.MaxValue },
            ["signal"] = new { type = "string", @enum = new[] { "duty_completed", "system_text", "addon_text" } },
            ["addon_name"] = new { type = "string", minLength = 1, maxLength = 64 },
            ["expected_text"] = new { type = "string", minLength = 1, maxLength = 256 },
        }, ["combat_id", "expected_territory_id", "signal"], ToolAccess.Read);

    private static ToolDefinition BattleResultTool() => Define(BATTLE_RESULT_TOOL, "独立等待本场通关信号",
        "按本场战斗和观察起点等待客户端通关事件、完整系统文本或窗口原文。战斗结束或归档不代表通关。", new()
        {
            ["combat_id"] = new { type = "string" }, ["checkpoint_id"] = new { type = "string" },
            ["timeout_ms"] = IntegerRange(0, 20000),
        }, ["combat_id", "checkpoint_id"], ToolAccess.Read);
}
