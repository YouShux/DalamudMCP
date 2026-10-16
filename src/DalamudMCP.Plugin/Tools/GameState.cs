using System.Globalization;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Inventory;
using Dalamud.Game.DutyState;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using GameMap = FFXIVClientStructs.FFXIV.Client.Game.UI.Map;
using QuestLinkMarker = FFXIVClientStructs.FFXIV.Client.UI.Agent.QuestLinkMarker;

namespace DalamudMCP.Plugin.Tools;

internal sealed unsafe class GameState : IDisposable
{
    private readonly GameServices services;
    private bool? dutyCompleted;

    public GameState(GameServices services)
    {
        this.services = services;
        services.DutyState.DutyCompleted += OnDutyCompleted;
        services.DutyState.DutyStarted += OnDutyStarted;
        services.ClientState.TerritoryChanged += OnTerritoryChanged;
    }

    public void Dispose()
    {
        services.DutyState.DutyCompleted -= OnDutyCompleted;
        services.DutyState.DutyStarted -= OnDutyStarted;
        services.ClientState.TerritoryChanged -= OnTerritoryChanged;
    }

    private void OnDutyCompleted(IDutyStateEventArgs args) => dutyCompleted = true;

    private void OnDutyStarted(IDutyStateEventArgs args) => dutyCompleted = false;

    private void OnTerritoryChanged(uint territory) => dutyCompleted = null;

    public object Call(string name, ToolArguments arguments)
    {
        if (!services.ClientState.IsLoggedIn || services.ObjectTable.LocalPlayer is null)
            throw new InvalidOperationException("角色尚未登录或未准备就绪。");
        return name switch
        {
            "get_player_context" => Player(),
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

    private object Player()
    {
        var player = services.ObjectTable.LocalPlayer ?? throw new InvalidOperationException("角色尚未准备就绪。");
        return new
        {
            characterName = player.Name.TextValue,
            gameObjectID = $"0x{player.GameObjectId:X}",
            homeWorld = player.HomeWorld.ValueNullable?.Name.ToString(),
            currentWorld = player.CurrentWorld.ValueNullable?.Name.ToString(),
            jobID = player.ClassJob.RowId,
            jobName = player.ClassJob.ValueNullable?.Name.ToString(),
            jobLevel = player.Level,
            currentHP = player.CurrentHp,
            maxHP = player.MaxHp,
            currentMP = player.CurrentMp,
            maxMP = player.MaxMp,
            territoryID = services.ClientState.TerritoryType,
            territoryName = services.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(services.ClientState.TerritoryType)?.PlaceName.ValueNullable?.Name.ToString(),
            position = Position(player.Position),
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
            if (!type.ToString().StartsWith("Inventory", StringComparison.Ordinal) &&
                !type.ToString().StartsWith("Armory", StringComparison.Ordinal) &&
                type is not GameInventoryType.EquippedItems and not GameInventoryType.Currency and not GameInventoryType.Crystals)
                continue;
            var items = services.GameInventory.GetInventoryItems(type).ToArray();
            if (type is GameInventoryType.Inventory1 or GameInventoryType.Inventory2 or GameInventoryType.Inventory3 or GameInventoryType.Inventory4)
            {
                totalSlots += items.Length;
                occupiedSlots += items.Count(item => !item.IsEmpty);
            }
            if (type == GameInventoryType.Currency)
                foreach (var item in items)
                    if (!item.IsEmpty && item.BaseItemId == 1)
                        gil = item.Quantity;
            containers.Add(new
            {
                container = type.ToString(),
                totalSlots = items.Length,
                occupiedSlots = items.Count(item => !item.IsEmpty),
                items = items.Where(item => !item.IsEmpty).Select(item => new { itemID = item.ItemId, baseItemID = item.BaseItemId, quantity = item.Quantity }).ToArray(),
            });
        }
        return new { currencyGil = gil, occupiedSlots, totalSlots, containers };
    }

    private object Fates(ToolArguments arguments)
    {
        var position = services.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var distance = arguments.Number("max_distance", 120, 1, 1000);
        var filter = arguments.Text("name_contains", string.Empty);
        return services.FateTable.Where(fate => fate is not null)
            .Where(fate => Vector3.DistanceSquared(fate.Position, position) <= distance * distance && fate.Name.TextValue.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(fate => Vector3.DistanceSquared(fate.Position, position)).Take(64)
            .Select(fate => new
            {
                fateID = fate.FateId,
                name = fate.Name.TextValue,
                state = fate.State.ToString(),
                level = fate.Level,
                progressPercent = fate.Progress,
                timeRemainingSeconds = fate.TimeRemaining,
                hasBonus = fate.HasBonus,
                position = Position(fate.Position),
                distance = Vector3.Distance(position, fate.Position),
                objective = fate.Objective.TextValue,
                description = fate.Description.TextValue,
            }).ToArray();
    }

    private IGameObject[] Nearby(ToolArguments arguments)
    {
        var position = services.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var distance = arguments.Number("max_distance", 50, 1, 1000);
        var filter = arguments.Text("name_contains", string.Empty);
        var includePlayers = arguments.Boolean("include_players");
        return services.ObjectTable.Where(gameObject => gameObject.IsTargetable &&
                (includePlayers || gameObject is not IPlayerCharacter) &&
                gameObject.GameObjectId != services.ObjectTable.LocalPlayer?.GameObjectId &&
                Vector3.DistanceSquared(gameObject.Position, position) <= distance * distance &&
                gameObject.Name.TextValue.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(gameObject => Vector3.DistanceSquared(gameObject.Position, position)).Take(128).ToArray();
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
        return new { succeeded = true, target = ObjectSnapshot(target) };
    }

    private object Interact(ToolArguments arguments)
    {
        var target = services.TargetManager.Target ?? throw new InvalidOperationException("尚未选中目标。");
        if (arguments.Has("expected_game_object_id") && target.GameObjectId != arguments.ObjectID("expected_game_object_id"))
            throw new InvalidOperationException("当前目标与预期目标不一致。");
        if (!target.IsTargetable || target.Address == 0)
            throw new InvalidOperationException("该对象当前不可交互。");
        var system = TargetSystem.Instance();
        if (system == null)
            throw new InvalidOperationException("目标系统未准备就绪。");
        return new { succeeded = system->InteractWithObject((NativeGameObject*)target.Address, arguments.Boolean("check_line_of_sight", true)) != 0, target = ObjectSnapshot(target) };
    }

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
        return new { succeeded = path.InvokeFunc(destination, allowFlight), destination = Position(destination), target = ObjectSnapshot(target) };
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
            return new { succeeded = telepo->Teleport(info.AetheryteId, info.SubIndex), aetheryteID = info.AetheryteId, subIndex = info.SubIndex };
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
        return new { succeeded = hotbar->ExecuteDutyActionSlot((uint)(slot - 1)), slot };
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
        var quests = new List<object>();
        foreach (var tracking in manager->TrackedQuests)
        {
            uint id;
            byte sequence;
            string kind;
            if (tracking.QuestType == 1 && tracking.Index < manager->NormalQuests.Length)
            {
                var work = manager->NormalQuests[tracking.Index];
                id = work.QuestId;
                sequence = work.Sequence;
                kind = "normal";
            }
            else if (tracking.QuestType == 2 && tracking.Index < manager->LeveQuests.Length)
            {
                var work = manager->LeveQuests[tracking.Index];
                id = work.LeveId;
                sequence = work.Sequence;
                kind = "leve";
            }
            else
                continue;
            if (id == 0)
                continue;
            var markers = new List<object>();
            foreach (var marker in map->QuestMarkers)
            {
                if (!marker.ShouldRender)
                    continue;
                var matches = marker.ObjectiveId == id;
                foreach (var data in marker.MarkerData)
                    matches |= data.ObjectiveId == id;
                if (matches)
                    markers.Add(MarkerSnapshot(marker));
            }
            var links = new List<object>();
            AppendLinks(agent->MapQuestLinkContainer.Markers, id, "map", links);
            AppendLinks(agent->MiniMapQuestLinkContainer.Markers, id, "minimap", links);
            quests.Add(new
            {
                questID = id,
                questName = kind == "normal" ? services.DataManager.GetExcelSheet<Quest>().GetRowOrDefault(id | 0x10000)?.Name.ToString() : null,
                kind,
                sequence,
                markers,
                links,
            });
        }
        return new { territoryID = services.ClientState.TerritoryType, quests };
    }

    private static object MarkerSnapshot(MarkerInfo marker)
    {
        var entries = new List<object>();
        foreach (var data in marker.MarkerData)
            entries.Add(new { objectiveID = data.ObjectiveId, dataID = data.DataId, mapID = data.MapId, territoryID = data.TerritoryTypeId, position = Position(data.Position), radius = data.Radius });
        return new { objectiveID = marker.ObjectiveId, label = marker.Label.ToString(), entries };
    }

    private static void AppendLinks(Span<QuestLinkMarker> markers, uint id, string scope, List<object> output)
    {
        foreach (var marker in markers)
            if (marker.Valid != 0 && marker.QuestId == id)
                output.Add(new { scope, label = marker.TooltipText.ToString(), marker.SourceMapId, marker.TargetMapId, marker.LevelId });
    }

    private static object Position(Vector3 position) => new { position.X, position.Y, position.Z };
}
