using DalamudMCP.Shared;

namespace DalamudMCP.Plugin.Tools;

public sealed partial class GameTools
{
    private const string CHARACTER_STATE_TOOL = "inspect_character_controls";
    private const string CHARACTER_REQUEST_TOOL = "request_character_control";
    private int characterRequestActive;

    private object InspectCharacterControls() => state.CharacterRequestSample() is { } sample
        ? new { status = "ok", sample.TerritoryID, sample.JobID, sample.IsDead, sample.CastActionID, sample.Rotation,
            sample.TargetRotation, sample.Autorun, sample.RaiseSourceID, targetID = $"0x{sample.TargetID:X}", sample.FacingTarget }
        : new { status = GameDiagnostics.CurrentCnLayoutKnown ? "context_changed" : "unsupported_sdk" };

    private async Task<object> RequestCharacterControlAsync(ToolArguments input, Func<bool> allowed, CancellationToken token)
    {
        var action = input.Text("action");
        var territory = input.Element("expected_territory_id").GetUInt32();
        var job = input.Element("expected_job_id").GetUInt32();
        var expectedCast = input.Has("expected_cast_action_id") ? input.Element("expected_cast_action_id").GetUInt32() : 0;
        var expectedSource = input.Has("expected_raise_source_id") ? input.Element("expected_raise_source_id").GetUInt32() : 0;
        var expectedTarget = input.Has("expected_target_id") ? input.ObjectID("expected_target_id") : 0;
        var autorun = input.Has("autorun") ? input.Element("autorun").GetBoolean() : (bool?)null;
        var beforeAutorun = input.Has("expected_autorun") ? input.Element("expected_autorun").GetBoolean() : (bool?)null;
        if (territory == 0 || job == 0 || action is not ("cancel_cast" or "accept_raise" or "autorun" or "face_target") ||
            (action == "cancel_cast") != (expectedCast != 0) || (action == "accept_raise") != (expectedSource != 0) ||
            (action == "face_target") != (expectedTarget != 0) || (action == "autorun") != (autorun.HasValue && beforeAutorun.HasValue) ||
            action != "autorun" && (autorun.HasValue || beforeAutorun.HasValue))
            throw new ArgumentException("请为操作指定对应的读回条件：咏唱技能、复活来源、当前目标或自动前进状态。");
        if (Interlocked.CompareExchange(ref characterRequestActive, 1, 0) != 0)
            return new { status = "busy", action, requestDispatched = false, resultObserved = false };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(action == "accept_raise" ? 10000 : 2200);
        var current = deadline.Token;
        GameState.CharacterRequestState? before = null;
        GameState.CharacterRequestState? after = null;
        var started = 0;
        try
        {
            var status = await services.Framework.RunOnTick(() =>
            {
                if (!GameDiagnostics.CurrentCnLayoutKnown) return "unsupported_sdk";
                before = state.CharacterRequestSample();
                if (before is null || before.TerritoryID != territory || before.JobID != job) return "context_changed";
                if (action == "accept_raise" ? !before.IsDead : before.IsDead) return "character_state_changed";
                switch (action)
                {
                    case "cancel_cast" when before.CastActionID != expectedCast: return "cast_changed";
                    case "accept_raise" when before.RaiseSourceID != expectedSource: return "raise_not_pending";
                    case "face_target" when before.TargetID != expectedTarget || !before.TargetRotation.HasValue: return "target_changed";
                    case "face_target" when before.FacingTarget: return "already_matched";
                    case "autorun" when !before.Autorun.HasValue: return "entry_unavailable";
                    case "autorun" when before.Autorun != beforeAutorun: return "autorun_changed";
                    case "autorun" when before.Autorun == autorun: return "already_matched";
                }
                if (action is "autorun" or "face_target" && !services.ClientState.IsClientIdle()) return "client_occupied";
                if (!state.HasCharacterRequestEntry(action)) return "entry_unavailable";
                if (input.Boolean("dry_run")) return "ready";
                current.ThrowIfCancellationRequested();
                if (!allowed()) return "permission_changed";
                if (Interlocked.CompareExchange(ref started, 1, 0) != 0) return "timeout";
                return state.RequestCharacterAction(action, autorun == true) ? "requested" : "not_accepted";
            }, cancellationToken: current).WaitAsync(current).ConfigureAwait(false);
            if (status != "requested") return Result(status, Volatile.Read(ref started) == 1);

            while (true)
            {
                after = await services.Framework.RunOnTick(state.CharacterRequestSample, delayTicks: 1, cancellationToken: current)
                    .WaitAsync(current).ConfigureAwait(false);
                if (before is null || after is null || after.Character != before.Character ||
                    after.TerritoryGeneration != before.TerritoryGeneration || after.TerritoryID != territory || after.JobID != job ||
                    action == "face_target" && after.TargetID != expectedTarget)
                    return Result("context_changed", true);
                var matches = action switch
                {
                    "cancel_cast" => after.CastActionID == 0,
                    "accept_raise" => !after.IsDead,
                    "face_target" => after.FacingTarget,
                    "autorun" => after.Autorun == autorun,
                    _ => false,
                };
                if (matches) return Result("observed", true);
                if (action == "cancel_cast" && after.CastActionID != expectedCast) return Result("cast_changed", true);
                await Task.Delay(40, current).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            var mayHaveStarted = Interlocked.CompareExchange(ref started, 2, 0) == 1;
            return Result(token.IsCancellationRequested ? "cancelled" : "timeout", mayHaveStarted ? null : false);
        }
        finally { Interlocked.Exchange(ref characterRequestActive, 0); }

        object Result(string status, bool? dispatched = false) => new { status, action, requestDispatched = dispatched,
            resultObserved = status == "observed", beforeCastActionID = before?.CastActionID, afterCastActionID = after?.CastActionID,
            beforeAutorun = before?.Autorun, afterAutorun = after?.Autorun, rotation = after?.Rotation,
            facingTarget = after?.FacingTarget, isDead = after?.IsDead };
    }

    private static ToolDefinition CharacterStateTool() => Define(CHARACTER_STATE_TOOL, "读取角色操作状态",
        "读取当前咏唱、硬目标、朝向、自动前进及可确认的复活来源。");

    private static ToolDefinition CharacterRequestTool()
    {
        var id = new { type = "integer", minimum = 1, maximum = uint.MaxValue };
        var flag = new { type = "boolean" };
        return Define(CHARACTER_REQUEST_TOOL, "请求一次角色操作", "核对区域、职业及当前状态，再取消咏唱、接受复活、设置自动前进或转向当前目标。", new()
        {
            ["action"] = new { type = "string", @enum = new[] { "cancel_cast", "accept_raise", "autorun", "face_target" } },
            ["expected_territory_id"] = id, ["expected_job_id"] = id, ["expected_cast_action_id"] = id,
            ["expected_raise_source_id"] = id, ["expected_target_id"] = new { type = "string" },
            ["expected_autorun"] = flag, ["autorun"] = flag, ["dry_run"] = flag,
        }, ["action", "expected_territory_id", "expected_job_id"], ToolAccess.Action);
    }
}
