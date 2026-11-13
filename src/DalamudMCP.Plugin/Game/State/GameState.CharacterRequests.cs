using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using NativeHotbar = FFXIVClientStructs.FFXIV.Client.Game.UI.Hotbar;

namespace DalamudMCP.Plugin.Tools;

internal sealed unsafe partial class GameState
{
    internal CharacterRequestState? CharacterRequestSample()
    {
        if (!GameDiagnostics.CurrentCnLayoutKnown || !services.ClientState.IsLoggedIn || !services.PlayerState.IsLoaded ||
            services.PlayerState.ContentId == 0 || services.ClientState.IsPvP ||
            services.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51) ||
            services.ObjectTable.LocalPlayer is not IPlayerCharacter player) return null;
        var target = services.TargetManager.Target;
        var difference = target is { IsTargetable: true } ? target.Position - player.Position : Vector3.Zero;
        float? targetRotation = FinitePoint(difference) && difference.X * difference.X + difference.Z * difference.Z > .01f
            ? MathF.Atan2(difference.X, difference.Z) : null;
        var revive = player.IsDead && AgentModule.MemberFunctionPointers.GetAgentByInternalId != null ? AgentRevive.Instance() : null;
        var source = revive != null && revive->State == ReviveState.Revivable && player.StatusList.Any(status => status.StatusId == 148)
            ? revive->ResurrectingPlayerId : 0;
        return new(services.PlayerState.ContentId, TerritoryGeneration, services.ClientState.TerritoryType,
            player.ClassJob.RowId, player.IsDead, player.IsCasting ? player.CastActionId : 0, target?.GameObjectId ?? 0,
            player.Rotation, targetRotation, InputManager.MemberFunctionPointers.IsAutoRunning == null ? null : InputManager.IsAutoRunning(),
            source is 0xE0000000 ? 0 : source);
    }

    internal bool HasCharacterRequestEntry(string action) => action switch
    {
        "cancel_cast" => UIState.Addresses.Instance.Value != 0 && UIState.Instance() != null && NativeHotbar.MemberFunctionPointers.CancelCast != null,
        "accept_raise" => GameMain.MemberFunctionPointers.ExecuteCommand != null,
        "autorun" or "face_target" => UIModule.Instance() != null && UIModule.MemberFunctionPointers.ProcessChatBoxEntry != null,
        _ => false,
    };

    private const int REVIVE_COMMAND = 200;
    private const int ACCEPT_RAISE = 5;

    internal bool RequestCharacterAction(string action, bool desired)
    {
        switch (action)
        {
            case "cancel_cast":
                UIState.Instance()->Hotbar.CancelCast();
                return true;
            case "accept_raise":
                return GameMain.ExecuteCommand(REVIVE_COMMAND, ACCEPT_RAISE);
            case "autorun":
                return SubmitClientLine(UIModule.Instance(), desired ? "/automove on" : "/automove off");
            case "face_target":
                return SubmitClientLine(UIModule.Instance(), "/facetarget");
            default:
                return false;
        }
    }

    internal sealed record CharacterRequestState(ulong Character, long TerritoryGeneration, uint TerritoryID, uint JobID,
        bool IsDead, uint CastActionID, ulong TargetID, float Rotation, float? TargetRotation, bool? Autorun, uint RaiseSourceID)
    {
        internal bool FacingTarget => TargetRotation is { } wanted && float.IsFinite(Rotation) &&
            MathF.Abs(MathF.IEEERemainder(Rotation - wanted, MathF.Tau)) < .12f;
    }
}
