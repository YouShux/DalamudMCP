using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.DutyState;
using Dalamud.Game.Inventory;
using DalamudMCP.Shared;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using GameMap = FFXIVClientStructs.FFXIV.Client.Game.UI.Map;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using QuestLinkMarker = FFXIVClientStructs.FFXIV.Client.UI.Agent.QuestLinkMarker;

namespace DalamudMCP.Plugin.Tools;

internal sealed unsafe partial class GameState : IDisposable
{
    private readonly GameServices services;
    private readonly Configuration configuration;
    private bool? dutyCompleted;
    private long dutyCompletionSequence;
    private (uint Territory, uint Duty) lastDutyCompletion;
    internal bool DutyAlreadyCompleted => dutyCompleted == true;
    internal long DutyCompletionSequence => dutyCompletionSequence;
    internal bool HasDutyCompletion(long after, uint territory, uint duty) =>
        dutyCompletionSequence > after && lastDutyCompletion == (territory, duty);
    private long territoryGeneration;
    private ((uint TerritoryID, uint JobID, int Language) Key, string? TerritoryName, string? JobName)? playerPlaceAndJob;
    private ((string? Territory, string? Job, Vector3 Position, bool StructuredOnly) State, JsonElement Result)? compactPlayer;

    internal long TerritoryGeneration => Volatile.Read(ref territoryGeneration);

    public GameState(GameServices services, Configuration configuration)
    {
        this.services = services;
        this.configuration = configuration;
        services.DutyState.DutyCompleted += OnDutyCompleted;
        services.DutyState.DutyStarted += OnDutyStarted;
        services.ClientState.TerritoryChanged += OnTerritoryChanged;
    }

    public void Dispose()
    {
        DisposeNavigation();
        services.DutyState.DutyCompleted -= OnDutyCompleted;
        services.DutyState.DutyStarted -= OnDutyStarted;
        services.ClientState.TerritoryChanged -= OnTerritoryChanged;
    }

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        dutyCompleted = true;
        lastDutyCompletion = (args.TerritoryType.RowId, args.ContentFinderCondition.RowId);
        dutyCompletionSequence++;
    }

    private void OnDutyStarted(IDutyStateEventArgs args) => dutyCompleted = false;

    private void OnTerritoryChanged(uint territory)
    {
        dutyCompleted = null;
        Interlocked.Increment(ref territoryGeneration);
    }

    public object Call(string name, ToolArguments arguments, bool structuredOnly)
    {
        if (!services.ClientState.IsLoggedIn || services.ObjectTable.LocalPlayer is null)
            throw new InvalidOperationException("角色尚未登录或未准备就绪。");
        return name switch
        {
            "get_player_context" => Player(arguments.Boolean("compact"), structuredOnly),
            "get_duty_context" => Duty(),
            "get_inventory_summary" => Inventory(),
            "get_fate_context" => Fates(arguments),
            "get_nearby_interactables" => Nearby(arguments).Select(ObjectSnapshot).ToArray(),
            "get_quest_status" => QuestStatus(arguments),
            "get_available_quests" => AvailableQuests(arguments),
            "get_current_quest_objective" => QuestObjectives(),
            "target_object" => Target(arguments.ObjectID("game_object_id")),
            "interact_with_target" => Interact(arguments),
            "move_to_entity" => Move(FindObject(arguments.ObjectID("game_object_id")), arguments.Boolean("allow_flight")),
            "move_to_nearby_interactable" => Move(Nearby(arguments).FirstOrDefault() ?? throw new InvalidOperationException("附近没有匹配对象。"), arguments.Boolean("allow_flight")),
            "teleport_to_aetheryte" => Teleport(arguments.Text("query")),
            "use_duty_action" => DutyAction(arguments.Integer("slot", 1, 1, 2)),
            _ => throw new ArgumentException($"未知游戏工具 {name}。"),
        };
    }

    private object Player(bool compact, bool structuredOnly)
    {
        var player = services.ObjectTable.LocalPlayer ?? throw new InvalidOperationException("角色尚未准备就绪。");
        var territoryID = services.ClientState.TerritoryType;
        var job = player.ClassJob;
        var key = (territoryID, job.RowId, (int)services.ClientState.ClientLanguage);
        if (playerPlaceAndJob is not { } names || names.Key != key)
        {
            names = (key, services.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryID)?.PlaceName.ValueNullable?.Name.ToString(),
                job.ValueNullable?.Name.ToString());
            if (names.TerritoryName is not null && names.JobName is not null)
                playerPlaceAndJob = names;
        }
        var (_, territoryName, jobName) = names;
        var coordinates = player.Position;
        if (compact)
        {
            var snapshot = (territoryName, jobName, coordinates, structuredOnly);
            if (compactPlayer is { } previous && snapshot == previous.State)
                return previous.Result;
            var result = ToolJson.Success(new { territoryName, jobName, position = Position(coordinates) }, structuredOnly);
            compactPlayer = (snapshot, result);
            return result;
        }

        return new
        {
            characterName = player.Name.TextValue,
            gameObjectID = $"0x{player.GameObjectId:X}",
            homeWorld = player.HomeWorld.ValueNullable?.Name.ToString(),
            currentWorld = player.CurrentWorld.ValueNullable?.Name.ToString(),
            jobID = job.RowId,
            jobName,
            jobLevel = player.Level,
            currentHP = player.CurrentHp,
            maxHP = player.MaxHp,
            currentMP = player.CurrentMp,
            maxMP = player.MaxMp,
            territoryID,
            territoryName,
            position = Position(coordinates),
            rotation = player.Rotation,
            inCombat = services.Condition[ConditionFlag.InCombat],
            target = services.TargetManager.Target is { } target ? ObjectSnapshot(target) : null,
        };
    }

    private object Duty()
    {
        return new
        {
            territoryID = services.ClientState.TerritoryType,
            inDuty = services.Condition.Any(ConditionFlag.BoundByDuty, ConditionFlag.BoundByDuty56, ConditionFlag.BoundByDuty95),
            isDutyStarted = services.DutyState.IsDutyStarted,
            isDutyComplete = dutyCompleted,
            completionKnown = dutyCompleted.HasValue,
            dutyName = services.DutyState.ContentFinderCondition.ValueNullable?.Name.ToString(),
        };
    }

    private object Inventory()
    {
        var containers = new List<object>();
        var occupiedSlots = 0;
        var totalSlots = 0;
        var gil = 0;
        foreach (var type in Enum.GetValues<GameInventoryType>())
        {
            var container = type.ToString();
            if (!container.StartsWith("Inventory", StringComparison.Ordinal) &&
                !container.StartsWith("Armory", StringComparison.Ordinal) &&
                type is not GameInventoryType.EquippedItems and not GameInventoryType.Currency and not GameInventoryType.Crystals)
                continue;
            var slots = services.GameInventory.GetInventoryItems(type);
            var items = new List<object>();
            foreach (var item in slots)
            {
                if (item.IsEmpty)
                    continue;
                if (type == GameInventoryType.Currency && item.BaseItemId == 1)
                    gil = item.Quantity;
                items.Add(new { itemID = item.ItemId, baseItemID = item.BaseItemId, quantity = item.Quantity });
            }
            if (type is GameInventoryType.Inventory1 or GameInventoryType.Inventory2 or GameInventoryType.Inventory3 or GameInventoryType.Inventory4)
            {
                totalSlots += slots.Length;
                occupiedSlots += items.Count;
            }
            containers.Add(new
            {
                container,
                totalSlots = slots.Length,
                occupiedSlots = items.Count,
                items,
            });
        }
        return new { currencyGil = gil, occupiedSlots, totalSlots, containers };
    }

    private object Fates(ToolArguments arguments)
    {
        var position = services.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var distance = arguments.Number("max_distance", 120, 1, 1000);
        var maxDistanceSquared = distance * distance;
        var filter = arguments.Text("name_contains", string.Empty);
        return services.FateTable.Where(fate => fate is not null)
            .Select(fate =>
            {
                var fatePosition = fate.Position;
                return (Fate: fate, Position: fatePosition, DistanceSquared: Vector3.DistanceSquared(fatePosition, position));
            })
            .Where(entry => entry.DistanceSquared <= maxDistanceSquared && (filter.Length == 0 || entry.Fate.Name.TextValue.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(entry => entry.DistanceSquared).Take(64)
            .Select(entry => new
            {
                fateID = entry.Fate.FateId,
                name = entry.Fate.Name.TextValue,
                state = entry.Fate.State.ToString(),
                level = entry.Fate.Level,
                progressPercent = entry.Fate.Progress,
                timeRemainingSeconds = entry.Fate.TimeRemaining,
                hasBonus = entry.Fate.HasBonus,
                position = Position(entry.Position),
                distance = float.Sqrt(entry.DistanceSquared),
                objective = entry.Fate.Objective.TextValue,
                description = entry.Fate.Description.TextValue,
            }).ToArray();
    }

    private IGameObject[] Nearby(ToolArguments arguments)
    {
        var player = services.ObjectTable.LocalPlayer;
        var position = player?.Position ?? Vector3.Zero;
        var playerID = player?.GameObjectId;
        var distance = arguments.Number("max_distance", 50, 1, 1000);
        var maxDistanceSquared = distance * distance;
        var filter = arguments.Text("name_contains", string.Empty);
        var includePlayers = arguments.Boolean("include_players");
        var candidates = new List<(IGameObject Object, float DistanceSquared, int Order)>();
        foreach (var gameObject in services.ObjectTable)
        {
            if (!gameObject.IsTargetable || (!includePlayers && gameObject is IPlayerCharacter) || gameObject.GameObjectId == playerID)
                continue;
            var distanceSquared = Vector3.DistanceSquared(gameObject.Position, position);
            if (distanceSquared <= maxDistanceSquared && (filter.Length == 0 || gameObject.Name.TextValue.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                candidates.Add((gameObject, distanceSquared, candidates.Count));
        }
        candidates.Sort(static (left, right) =>
        {
            var comparison = left.DistanceSquared.CompareTo(right.DistanceSquared);
            return comparison != 0 ? comparison : left.Order.CompareTo(right.Order);
        });
        var result = new IGameObject[Math.Min(candidates.Count, 128)];
        for (var i = 0; i < result.Length; i++)
            result[i] = candidates[i].Object;
        return result;
    }

    private object ObjectSnapshot(IGameObject gameObject) => new
    {
        gameObjectID = $"0x{gameObject.GameObjectId:X}",
        entityID = gameObject.EntityId,
        dataID = gameObject.BaseId,
        name = gameObject.Name.TextValue,
        kind = gameObject.ObjectKind.ToString(),
        isTargetable = gameObject.IsTargetable,
        position = Position(gameObject.Position),
        distance = Vector3.Distance(services.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero, gameObject.Position),
    };

    private IGameObject FindObject(ulong id) => services.ObjectTable.FirstOrDefault(gameObject => gameObject.GameObjectId == id) ?? throw new InvalidOperationException("当前区域找不到指定对象。");

    private object Target(ulong id)
    {
        var target = FindObject(id);
        if (!target.IsTargetable)
            throw new InvalidOperationException("该对象当前不可选中。");
        services.TargetManager.Target = target;
        return new { succeeded = true, resultObserved = services.TargetManager.Target?.GameObjectId == id, target = ObjectSnapshot(target) };
    }

    private object Interact(ToolArguments arguments)
    {
        var target = services.TargetManager.Target ?? throw new InvalidOperationException("尚未选中目标。");
        if (arguments.Has("expected_game_object_id") && target.GameObjectId != arguments.ObjectID("expected_game_object_id"))
            throw new InvalidOperationException("当前目标与预期目标不一致。");
        if (!target.IsTargetable || target.Address == 0)
            throw new InvalidOperationException("该对象当前不可交互。");
        if (!CanInteractNative()) throw new InvalidOperationException("目标系统或交互入口未准备就绪。");
        var accepted = InteractNative(target.Address, arguments.Boolean("check_line_of_sight", true));
        return new { succeeded = accepted, requestAccepted = accepted, resultObserved = false, target = ObjectSnapshot(target) };
    }

    internal static bool CanInteractNative() => TargetSystem.Instance() != null && TargetSystem.MemberFunctionPointers.InteractWithObject != null;

    internal static bool InteractNative(nint address, bool checkLineOfSight) =>
        TargetSystem.Instance()->InteractWithObject((NativeGameObject*)address, checkLineOfSight) != 0;

    private object Move(IGameObject target, bool allowFlight)
    {
        var ready = services.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        var path = services.PluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        if (!ready.HasFunction || !path.HasFunction || !ready.InvokeFunc())
            throw new InvalidOperationException("vnavmesh 未加载或导航网格未准备就绪。");
        var destination = target.Position;
        var floor = services.PluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        if (!allowFlight && floor.HasFunction)
            destination = floor.InvokeFunc(destination, false, 4f) ?? destination;
        var accepted = path.InvokeFunc(destination, allowFlight);
        return new { succeeded = accepted, requestAccepted = accepted, resultObserved = false, destination = Position(destination), target = ObjectSnapshot(target) };
    }

    private object Teleport(string query)
    {
        var telepo = Telepo.Instance();
        if (telepo == null)
            throw new InvalidOperationException("传送系统未准备就绪。");
        telepo->UpdateAetheryteList();
        var sheet = services.DataManager.GetExcelSheet<Aetheryte>();
        var matches = new List<TeleportInfo>();
        foreach (var info in telepo->TeleportList.AsSpan())
        {
            var row = sheet.GetRowOrDefault(info.AetheryteId);
            if (info.AetheryteId.ToString(CultureInfo.InvariantCulture) == query || row?.PlaceName.ValueNullable?.Name.ToString().Equals(query, StringComparison.OrdinalIgnoreCase) == true)
                matches.Add(info);
        }
        if (matches.Count > 1)
            throw new InvalidOperationException("目的地不唯一，请使用大水晶 ID。");
        if (matches.Count == 1)
        {
            var info = matches[0];
            var accepted = telepo->Teleport(info.AetheryteId, info.SubIndex);
            return new { succeeded = accepted, requestAccepted = accepted, resultObserved = false, aetheryteID = info.AetheryteId, subIndex = info.SubIndex };
        }
        var teleport = services.PluginInterface.GetIpcSubscriber<string, Task<bool>>("OmniToolbox.Teleport");
        if (!teleport.HasFunction)
            throw new InvalidOperationException("没有匹配的已解锁大水晶；城内传送需要支持传送 IPC 的 Omni，并开启“更好的传送管理”。");
        return teleport.InvokeFunc(query);
    }

    private static object DutyAction(int slot)
    {
        var ui = UIModule.Instance();
        var hotbar = ui == null ? null : ui->GetRaptureHotbarModule();
        if (hotbar == null || !hotbar->DutyActionsPresent)
            throw new InvalidOperationException("当前没有可用的任务技能。");
        var accepted = hotbar->ExecuteDutyActionSlot((uint)(slot - 1));
        return new { succeeded = accepted, requestAccepted = accepted, resultObserved = false, slot };
    }

    private object QuestStatus(ToolArguments arguments)
    {
        var manager = QuestManager.Instance();
        var ui = UIState.Instance();
        if (manager == null || ui == null)
            throw new InvalidOperationException("任务系统未准备就绪。");
        if (!arguments.Has("quest_id") && !arguments.Has("query"))
            throw new ArgumentException("请提供 quest_id 或 query。");
        var query = arguments.Text("query", string.Empty);
        var id = arguments.Has("quest_id") ? arguments.Element("quest_id").GetUInt32() : 0;
        var matches = new List<object>();
        var max = arguments.Integer("max_results", 8, 1, 1000);
        foreach (var row in services.DataManager.GetExcelSheet<Quest>())
        {
            if (id != 0 ? row.RowId != id && row.RowId != (id | 0x10000) : !row.Name.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            matches.Add(QuestSnapshot(row.RowId, row.Name.ToString(), manager, ui));
            if (matches.Count >= max || id != 0)
                break;
        }
        return new { matches };
    }

    private static object QuestSnapshot(uint id, string name, QuestManager* manager, UIState* ui) => new
    {
        questID = id,
        questName = name,
        accepted = manager->IsQuestAccepted(id),
        completed = ui->IsUnlockLinkUnlockedOrQuestCompleted(id, 0, true),
        sequence = QuestManager.GetQuestSequence(id),
    };

    private object AvailableQuests(ToolArguments arguments)
    {
        var map = GameMap.Instance();
        var manager = QuestManager.Instance();
        var ui = UIState.Instance();
        if (map == null || manager == null || ui == null)
            throw new InvalidOperationException("地图任务系统未准备就绪。");
        var filter = arguments.Text("name_contains", string.Empty);
        var max = arguments.Integer("max_results", 20, 1, 1000);
        var quests = new List<object>();
        var seen = new HashSet<uint>();
        var sheet = services.DataManager.GetExcelSheet<Quest>();
        foreach (var marker in map->UnacceptedQuestMarkers)
        {
            var id = marker.ObjectiveId;
            if (!marker.ShouldRender || id == 0 || !seen.Add(id) || manager->IsQuestAccepted(id) || ui->IsUnlockLinkUnlockedOrQuestCompleted(id, 0, true))
                continue;
            var name = sheet.GetRowOrDefault(id | 0x10000)?.Name.ToString() ?? marker.Label.ToString();
            if (!name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            quests.Add(new { questID = id, questName = name, level = marker.RecommendedLevel, marker = MarkerSnapshot(marker) });
            if (quests.Count >= max)
                break;
        }
        return new { territoryID = services.ClientState.TerritoryType, quests };
    }

    private object QuestObjectives()
    {
        var manager = QuestManager.Instance();
        var map = GameMap.Instance();
        var agent = AgentMap.Instance();
        if (manager == null || map == null || agent == null)
            throw new InvalidOperationException("任务追踪系统未准备就绪。");

        var tracked = manager->TrackedQuests;
        Span<uint> ids = stackalloc uint[tracked.Length];
        Span<byte> sequences = stackalloc byte[tracked.Length];
        Span<bool> normal = stackalloc bool[tracked.Length];
        var count = 0;
        foreach (var tracking in tracked)
        {
            uint id;
            byte sequence;
            var isNormal = tracking.QuestType == 1;
            if (isNormal && tracking.Index < manager->NormalQuests.Length)
            {
                ref var work = ref manager->NormalQuests[tracking.Index];
                id = work.QuestId;
                sequence = work.Sequence;
            }
            else if (tracking.QuestType == 2 && tracking.Index < manager->LeveQuests.Length)
            {
                ref var work = ref manager->LeveQuests[tracking.Index];
                id = work.LeveId;
                sequence = work.Sequence;
            }
            else
                continue;
            if (id == 0)
                continue;
            ids[count] = id;
            sequences[count] = sequence;
            normal[count++] = isNormal;
        }

        var quests = new List<object>();
        if (count == 0)
            return new { territoryID = services.ClientState.TerritoryType, quests };

        var markers = map->QuestMarkers;
        var mapLinks = agent->MapQuestLinkContainer.Markers;
        var miniLinks = agent->MiniMapQuestLinkContainer.Markers;
        Span<ulong> markerMasks = stackalloc ulong[count];
        Span<ulong> mapMasks = stackalloc ulong[count];
        Span<ulong> miniMasks = stackalloc ulong[count];
        GroupQuestLocations(ids[..count], markers, mapLinks, miniLinks, markerMasks, mapMasks, miniMasks);

        var sheet = normal[..count].IndexOf(true) >= 0 ? services.DataManager.GetExcelSheet<Quest>() : null;
        for (var i = 0; i < count; i++)
        {
            var id = ids[i];
            var matchingMarkers = new List<object>();
            for (var mask = markerMasks[i]; mask != 0; mask &= mask - 1)
                matchingMarkers.Add(MarkerSnapshot(markers[BitOperations.TrailingZeroCount(mask)]));
            var matchingLinks = new List<object>();
            AppendQuestLinks(mapLinks, mapMasks[i], "map", matchingLinks);
            AppendQuestLinks(miniLinks, miniMasks[i], "minimap", matchingLinks);
            quests.Add(new
            {
                questID = id,
                questName = normal[i] ? sheet?.GetRowOrDefault(id | 0x10000)?.Name.ToString() : null,
                kind = normal[i] ? "normal" : "leve",
                sequence = sequences[i],
                markers = matchingMarkers,
                links = matchingLinks,
            });
        }
        return new { territoryID = services.ClientState.TerritoryType, quests };
    }

    private static void GroupQuestLocations(ReadOnlySpan<uint> ids, Span<MarkerInfo> markers,
        Span<QuestLinkMarker> mapLinks, Span<QuestLinkMarker> miniLinks, Span<ulong> markerMasks, Span<ulong> mapMasks, Span<ulong> miniMasks)
    {
        markerMasks.Clear();
        if (markers.Length > 64 || mapLinks.Length > 64 || miniLinks.Length > 64)
            throw new InvalidOperationException("当前地图标记数量超过支持范围。");
        for (var i = 0; i < markers.Length; i++)
        {
            ref var marker = ref markers[i];
            if (!marker.ShouldRender)
                continue;
            var bit = 1UL << i;
            AddQuestLocation(ids, markerMasks, marker.ObjectiveId, bit);
            foreach (var data in marker.MarkerData)
                if (data.ObjectiveId != marker.ObjectiveId)
                    AddQuestLocation(ids, markerMasks, data.ObjectiveId, bit);
        }
        GroupQuestLinks(ids, mapLinks, mapMasks);
        GroupQuestLinks(ids, miniLinks, miniMasks);
    }

    private static void GroupQuestLinks(ReadOnlySpan<uint> ids, Span<QuestLinkMarker> markers, Span<ulong> masks)
    {
        masks.Clear();
        for (var i = 0; i < markers.Length; i++)
        {
            ref var marker = ref markers[i];
            if (marker.Valid != 0)
                AddQuestLocation(ids, masks, marker.QuestId, 1UL << i);
        }
    }

    private static void AddQuestLocation(ReadOnlySpan<uint> ids, Span<ulong> masks, uint id, ulong bit)
    {
        for (var i = 0; i < ids.Length; i++)
            if (ids[i] == id)
                masks[i] |= bit;
    }

    private static void AppendQuestLinks(Span<QuestLinkMarker> markers, ulong mask, string scope, List<object> output)
    {
        for (; mask != 0; mask &= mask - 1)
        {
            ref var marker = ref markers[BitOperations.TrailingZeroCount(mask)];
            output.Add(new { scope, label = marker.TooltipText.ToString(), marker.SourceMapId, marker.TargetMapId, marker.LevelId });
        }
    }

    private static object MarkerSnapshot(MarkerInfo marker)
    {
        var entries = new List<object>();
        foreach (var data in marker.MarkerData)
            entries.Add(new { objectiveID = data.ObjectiveId, dataID = data.DataId, mapID = data.MapId, territoryID = data.TerritoryTypeId, position = Position(data.Position), radius = data.Radius });
        return new { objectiveID = marker.ObjectiveId, label = marker.Label.ToString(), entries };
    }

    private static object Position(Vector3 position) => new { position.X, position.Y, position.Z };
}
