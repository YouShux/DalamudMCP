using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using DalamudMCP.Shared;

namespace DalamudMCP.Plugin.Tools;

public sealed partial class GameTools
{
    private const string NPC_INTERACTION_TOOL = "request_npc_interaction";
    private const float NPC_INTERACTION_MAX_DISTANCE = 4f;
    private static readonly string[] NPC_RESULT_ADDONS = ["Talk", "SelectString", "SelectIconString"];
    private static readonly string[] NPC_DIALOGUES = [.. NPC_RESULT_ADDONS, "SelectYesno", "ContentsFinderConfirm", "Request"];
    private int npcInteractionActive;

    private async Task<object> RequestNPCInteractionAsync(ToolArguments input, Func<bool> allowed, CancellationToken token)
    {
        var id = input.ObjectID("game_object_id");
        var baseID = input.Element("expected_base_id").GetUInt32();
        var territoryID = input.Element("expected_territory_id").GetUInt32();
        var addonName = input.Text("addon_name");
        var label = input.Has("expected_text") ? input.Text("expected_text") : null;
        var timeout = input.Integer("timeout_ms", 3000, 250, 20000);
        var approach = input.Boolean("approach");
        var choice = input.Has("menu_label") ? input.Text("menu_label") : null;
        var resultAddon = input.Has("result_addon") ? input.Text("result_addon") : null;
        var resultText = input.Has("result_text") ? input.Text("result_text") : null;
        if (choice is { Length: < 1 or > 256 } || resultAddon is { Length: < 1 or > 64 } || resultText is { Length: < 1 or > 256 } ||
            choice is null && (resultAddon is not null || resultText is not null) || choice is not null &&
            (addonName is not ("SelectString" or "SelectIconString") || resultAddon is null || resultAddon == addonName || resultAddon == "ContentsFinderConfirm"))
            throw new ArgumentException("菜单选择需要精确标签和不同的后续窗口；不会确认任何窗口。");
        if (id == 0 || baseID == 0 || territoryID == 0 || !NPC_RESULT_ADDONS.Contains(addonName) || label is { Length: > 256 })
            throw new ArgumentException("需要有效的对象、基础和区域 ID，以及支持的对话窗口。文本最多 256 字。");
        if (Interlocked.CompareExchange(ref npcInteractionActive, 1, 0) != 0)
            return new { status = "busy", requestDispatched = false, resultObserved = false };

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var current = deadline.Token;
        var started = 0;
        bool? accepted = null;
        AddonTools.NPCDialogueSample? observed = null;
        AddonTools.NPCDialogueSample? outcome = null;
        GameState.NPCApproach? movement = null;
        nint approachTargetAddress = 0;
        ulong approachCharacter = 0;
        long approachGeneration = 0;
        var menuStarted = 0;
        bool? menuAccepted = null;
        try
        {
            if (approach)
            {
                var preparation = await services.Framework.RunOnTick(() =>
                {
                    current.ThrowIfCancellationRequested();
                    if (!GameDiagnostics.CurrentCnLayoutKnown) return "unsupported_sdk";
                    if (!SessionReady() || !services.ClientState.IsClientIdle() || services.Condition[ConditionFlag.Mounted] ||
                        services.ObjectTable.LocalPlayer is not { } player || AnyDialogueVisible()) return "context_changed";
                    var npc = services.ObjectTable.SearchById(id);
                    if (npc is not { ObjectKind: ObjectKind.EventNpc, IsTargetable: true } || npc.BaseId != baseID || npc.Address == 0)
                        return "npc_mismatch";
                    var distance = Vector3.Distance(player.Position, npc.Position);
                    if (!float.IsFinite(distance) || distance > 20) return "out_of_range";
                    approachTargetAddress = npc.Address;
                    approachCharacter = services.PlayerState.ContentId;
                    approachGeneration = state.TerritoryGeneration;
                    if (distance <= NPC_INTERACTION_MAX_DISTANCE) return "in_range";
                    movement = state.StartNPCApproach(player.Position, npc.Position, territoryID, () =>
                        !current.IsCancellationRequested && allowed() && SessionReady() && !AnyDialogueVisible() &&
                        services.PlayerState.ContentId == approachCharacter && state.TerritoryGeneration == approachGeneration &&
                        services.ObjectTable.SearchById(id) is { IsTargetable: true } target && target.BaseId == baseID && target.Address == approachTargetAddress);
                    return movement is not null ? "approaching" : "navigation_unavailable";
                }, cancellationToken: current).WaitAsync(current).ConfigureAwait(false);
                if (preparation == "approaching")
                {
                    while (true)
                    {
                        var progress = await services.Framework.RunOnTick(() =>
                        {
                            if (movement is null || !SessionReady() || AnyDialogueVisible() || !allowed() ||
                                services.ObjectTable.LocalPlayer is not { } player ||
                                services.ObjectTable.SearchById(id) is not { IsTargetable: true } target || target.BaseId != baseID || target.Address != approachTargetAddress)
                                return "context_changed";
                            return state.PollNPCApproach(movement, Vector3.Distance(player.Position, target.Position) <= NPC_INTERACTION_MAX_DISTANCE);
                        }, delayTicks: 1, cancellationToken: current).WaitAsync(current).ConfigureAwait(false);
                        if (progress == "in_range") break;
                        if (progress != "approaching") return Result(progress, dispatched: false);
                        await Task.Delay(80, current).ConfigureAwait(false);
                    }
                }
                else if (preparation != "in_range") return Result(preparation, dispatched: false);
            }
            var dispatch = await services.Framework.RunOnTick(() =>
            {
                if (!GameDiagnostics.CurrentCnLayoutKnown) return new NPCDispatch("unsupported_sdk");
                if (!SessionReady() || services.ObjectTable.LocalPlayer is not { } player)
                    return new NPCDispatch("context_changed", "player_not_ready");
                if (AnyDialogueVisible()) return new NPCDispatch("dialogue_already_visible");
                if (!services.ClientState.IsClientIdle() || services.Condition[ConditionFlag.Mounted])
                    return new NPCDispatch("context_changed", "client_occupied");
                var npc = services.ObjectTable.SearchById(id);
                if (npc is not { ObjectKind: ObjectKind.EventNpc, IsTargetable: true } || npc.BaseId != baseID || npc.Address == 0)
                    return new NPCDispatch("npc_mismatch");
                var distance = Vector3.Distance(player.Position, npc.Position);
                if (!float.IsFinite(distance) || distance > NPC_INTERACTION_MAX_DISTANCE)
                    return new NPCDispatch("out_of_range");
                if (approach && (npc.Address != approachTargetAddress || services.PlayerState.ContentId != approachCharacter ||
                    state.TerritoryGeneration != approachGeneration)) return new NPCDispatch("context_changed");
                if (!GameState.CanInteractNative()) return new NPCDispatch("entry_unavailable");
                var characterID = services.PlayerState.ContentId;
                var generation = state.TerritoryGeneration;
                current.ThrowIfCancellationRequested();
                if (!allowed()) return new NPCDispatch("permission_changed");
                if (Interlocked.CompareExchange(ref started, 1, 0) != 0) return new NPCDispatch("timeout");
                services.TargetManager.Target = npc;
                if (services.TargetManager.Target?.GameObjectId != id) return new NPCDispatch("target_unavailable", Accepted: null);
                return new NPCDispatch("requested", Accepted: GameState.InteractNative(npc.Address, true),
                    CharacterID: characterID, TerritoryGeneration: generation, Address: npc.Address);
            }, cancellationToken: current).WaitAsync(current).ConfigureAwait(false);
            accepted = dispatch.Accepted;
            if (dispatch.Status != "requested") return Result(dispatch.Status, dispatch.Reason, dispatched: false);
            if (accepted != true) return Result("not_accepted");

            AddonTools.NPCDialogueSample? previous = null;
            while (true)
            {
                var read = await services.Framework.RunOnTick(() =>
                {
                    current.ThrowIfCancellationRequested();
                    if (!SessionReady() || services.PlayerState.ContentId != dispatch.CharacterID ||
                        state.TerritoryGeneration != dispatch.TerritoryGeneration)
                        return ("context_changed", (AddonTools.NPCDialogueSample?)null);
                    var npc = services.ObjectTable.SearchById(id);
                    if (npc is not { ObjectKind: ObjectKind.EventNpc } || npc.BaseId != baseID || npc.Address != dispatch.Address ||
                        services.TargetManager.Target?.GameObjectId != id)
                        return ("context_changed", (AddonTools.NPCDialogueSample?)null);
                    if (AnyDialogueVisible(addonName)) return ("unexpected_dialogue", (AddonTools.NPCDialogueSample?)null);
                    return ("waiting", addons.ReadNPCDialogue(addonName, label));
                }, delayTicks: 1, cancellationToken: current).WaitAsync(current).ConfigureAwait(false);
                if (read.Item1 != "waiting") return Result(read.Item1);
                observed = read.Item2;
                if (observed is { Matches: true } && observed == previous) break;
                previous = observed;
                await Task.Delay(60, current).ConfigureAwait(false);
            }
            if (choice is null || resultAddon is null || observed is null) return Result("observed");
            var menuStatus = await services.Framework.RunOnTick(() =>
            {
                current.ThrowIfCancellationRequested();
                if (!SessionReady() || services.PlayerState.ContentId != dispatch.CharacterID || state.TerritoryGeneration != dispatch.TerritoryGeneration ||
                    services.TargetManager.Target is not { } target || target.GameObjectId != id || target.Address != dispatch.Address ||
                    AnyDialogueVisible(addonName) || addons.AddonVisible(resultAddon))
                    return "menu_context_changed";
                return addons.DispatchExactNPCMenu(addonName, observed, choice, () =>
                {
                    current.ThrowIfCancellationRequested();
                    return allowed() && Interlocked.CompareExchange(ref menuStarted, 1, 0) == 0;
                });
            }, delayTicks: 1, cancellationToken: current).WaitAsync(current).ConfigureAwait(false);
            if (menuStatus != "requested") return Result(menuStatus);
            menuAccepted = true;
            AddonTools.NPCDialogueSample? previousResult = null;
            while (true)
            {
                var result = await services.Framework.RunOnTick(() =>
                {
                    current.ThrowIfCancellationRequested();
                    if (!SessionReady() || services.PlayerState.ContentId != dispatch.CharacterID || state.TerritoryGeneration != dispatch.TerritoryGeneration)
                        return ("context_changed", (AddonTools.NPCDialogueSample?)null);
                    if (NPC_DIALOGUES.Any(name => name != addonName && name != resultAddon && addons.AddonVisible(name)))
                        return ("unexpected_dialogue", (AddonTools.NPCDialogueSample?)null);
                    return ("waiting", addons.ReadNPCDialogue(resultAddon, resultText));
                }, delayTicks: 1, cancellationToken: current).WaitAsync(current).ConfigureAwait(false);
                if (result.Item1 != "waiting") return Result(result.Item1);
                outcome = result.Item2;
                if (outcome is { Matches: true } && outcome == previousResult) return Result("observed");
                previousResult = outcome;
                await Task.Delay(60, current).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Interlocked.CompareExchange(ref started, 2, 0);
            Interlocked.CompareExchange(ref menuStarted, 2, 0);
            return Result(token.IsCancellationRequested ? "cancelled" : "timeout");
        }
        finally
        {
            try
            {
                if (movement is not null)
                    await services.Framework.RunOnTick(() => state.StopNPCApproach(movement), cancellationToken: lifetimeToken)
                        .WaitAsync(TimeSpan.FromSeconds(1), lifetimeToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException or InvalidOperationException)
            { services.Log.Debug(error, "无法确认 NPC 导航收尾。"); }
            finally { Interlocked.Exchange(ref npcInteractionActive, 0); }
        }

        bool SessionReady() => !services.Framework.IsFrameworkUnloading && services.ClientState.IsLoggedIn &&
            services.PlayerState.IsLoaded && services.PlayerState.ContentId != 0 && services.ClientState.TerritoryType == territoryID &&
            !services.ClientState.IsPvP && services.ObjectTable.LocalPlayer is { IsDead: false } &&
            !services.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51, ConditionFlag.InCombat);

        bool AnyDialogueVisible(string? except = null) => NPC_DIALOGUES.Any(name =>
            name != except && services.GameGui.GetAddonByName(name, 1) is { IsNull: false, IsVisible: true });

        object Result(string status, string? reason = null, bool? dispatched = null) => new
        {
            status, reason, gameObjectID = $"0x{id:X}", baseID, territoryID, addonName,
            addonID = observed?.AddonID, observedText = observed?.Text, expectedText = label,
            requestDispatched = dispatched ?? (accepted.HasValue ? true : Volatile.Read(ref started) != 1 ? false : (bool?)null),
            dispatchMayHaveStarted = Volatile.Read(ref started) == 1,
            requestAccepted = accepted, resultObserved = status == "observed", approached = movement is not null,
            menuLabel = choice, menuDispatched = choice is null ? null : menuAccepted == true ? true : Volatile.Read(ref menuStarted) == 1 ? null : (bool?)false,
            resultAddon, resultAddonID = outcome?.AddonID, resultText = outcome?.Text,
        };
    }

    private sealed record NPCDispatch(string Status, string? Reason = null, bool? Accepted = null,
        ulong CharacterID = 0, long TerritoryGeneration = 0, nint Address = 0);
}
