using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using DalamudMCP.Shared;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using GameAction = Lumina.Excel.Sheets.Action;

namespace DalamudMCP.Plugin.Tools;

public sealed partial class GameTools
{
    private int mechanicSolves;

    private const string MECHANICS_TOOL = "inspect_combat_mechanics";

    private static Dictionary<string, object> MechanicSchema()
    {
        var number = new { type = "number" };
        var xy = new { type = "array", minItems = 2, maxItems = 2, items = number };
        return new()
        {
            ["combat_id"] = new { type = "string", format = "uuid" },
            ["archive_id"] = new { type = "string", format = "uuid" },
            ["at_ms"] = Range(0, 86400000),
            ["record"] = new { type = "boolean" },
            ["points"] = new { type = "array", maxItems = 32, items = new { type = "array", minItems = 3, maxItems = 3, items = number } },
            ["grid_step"] = new { type = "number", minimum = 0.5, maximum = 40 },
            ["arena"] = new
            {
                type = "object", additionalProperties = false, required = new[] { "shape" },
                properties = new Dictionary<string, object>
                {
                    ["shape"] = new { type = "string", @enum = new[] { "circle", "rectangle", "polygon" } },
                    ["x"] = number, ["z"] = number, ["radius"] = number, ["half_length"] = number, ["half_width"] = number,
                    ["vertices"] = new { type = "array", minItems = 3, maxItems = 32, items = xy },
                },
            },
        };
    }

    private object InspectArchivedMechanics(ToolArguments input)
    {
        if (input.Has("combat_id") || input.Boolean("record")) throw new ArgumentException("回看归档不能同时指定当前战斗或写入快照。");
        var id = RepositoryPackages.ParseID(input.Text("archive_id"));
        var at = input.Has("at_ms") ? input.Integer("at_ms", 0, 0, 86400000) : (int?)null;
        var (snapshot, elapsed) = diagnostics.ReadLocalMechanics(id, at);
        if (snapshot is null) return new { status = elapsed == -2 ? "not_found" : "no_local_snapshot", combatID = id };
        var arena = input.Has("arena") ? ReadMechanicArena(input.Element("arena")) : snapshot.Arena;
        if (input.Has("arena")) snapshot = snapshot with { Arena = arena, ArenaSource = "provided" };
        return ReportMechanics(input, snapshot, elapsed);
    }

    private object InspectLocalMechanics(ToolArguments input)
    {
        var id = input.Has("combat_id") ? RepositoryPackages.ParseID(input.Text("combat_id")) : (Guid?)null;
        var record = input.Boolean("record");
        if (record && id is null) throw new ArgumentException("写入快照须指定正在录制的战斗 ID。");
        if (id is { } expected && !diagnostics.IsCurrentFight(expected)) return new { status = "combat_not_recording" };
        if (!services.ClientState.IsLoggedIn || services.ClientState.IsPvP || services.ObjectTable.LocalPlayer is not { } player ||
            services.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51))
            return new { status = "context_changed" };

        if (input.Has("at_ms")) throw new ArgumentException("at_ms 仅用于回看归档。");
        var explicitArena = input.Has("arena") ? ReadMechanicArena(input.Element("arena")) : null;

        var hazards = new List<MechanicHazard>();
        var unmodeled = new HashSet<uint>();
        var actors = services.ObjectTable.OfType<IBattleNpc>().Where(actor => actor.IsCasting && actor.CurrentHp != 0 &&
            actor.CastActionType == 1 && actor.CastActionId != 0 &&
            (actor.StatusFlags.HasFlag(StatusFlags.Hostile) || actor.BattleNpcKind == BattleNpcSubKind.Combatant ||
                actor.CastActionId == LightningCageRules.CellCast)).Take(129).ToArray();
        var truncated = actors.Length > 128;
        var duty = services.DutyState.ContentFinderCondition.RowId;
        var cageCells = new SortedSet<int>();
        var cageTime = long.MaxValue;
        var actions = services.DataManager.GetExcelSheet<GameAction>();
        var sdkVerified = GameDiagnostics.ReadoutLayoutKnown;
        foreach (var actor in actors.Take(128))
        {
            var actionID = actor.CastActionId;
            var remaining = actor.TotalCastTime - actor.CurrentCastTime;
            if (!float.IsFinite(remaining) || remaining is < 0 or > 300) { unmodeled.Add(actionID); continue; }
            var resolve = (long)Math.Ceiling(remaining * 1000);
            var source = $"0x{actor.EntityId:X}";
            if (duty == LightningCageRules.Duty && actionID == LightningCageRules.CellCast)
            {
                if (LightningCageRules.CellAt(XZ(actor.Position)) is { } index)
                {
                    if (cageCells.Add(index)) hazards.Add(new(actionID, source, LightningCageRules.CellArea(index), resolve, "encounter_rule"));
                    cageTime = Math.Min(cageTime, resolve);
                }
                else unmodeled.Add(actionID);
                continue;
            }
            if (!actions.TryGetRow(actionID, out var row)) { unmodeled.Add(actionID); continue; }
            if (row.IsPlayerAction) continue;
            if (row.CastType == 1 && row.VFX.RowId != 25 && row.EffectRange == 0) continue;
            if (sdkVerified && ReadCastShape(actor, row) is { } area) hazards.Add(new(actionID, source, area, resolve, "client_action_sheet_estimate"));
            else unmodeled.Add(actionID);
        }

        object? lightningCage = null;
        if (cageCells.Count != 0)
        {
            var danger = cageCells.ToArray();
            var (order, gleams, sparks) = diagnostics.LightningCageEvidence(player.EntityId);
            var safe = LightningCageRules.SafeCells(danger, order, gleams, sparks);
            lightningCage = new { cellSize = 8, resolvesInMs = cageTime, order, gleams, sparks,
                danger = danger.Select(Cell).ToArray(), safe = safe?.Select(Cell).ToArray(),
                safeStatus = safe is null ? "unresolved" : "rule_result" };
        }

        var arena = explicitArena ?? (cageCells.Count != 0 ? new MechanicArea(MechanicShape.Rectangle, 100, 100,
            Front: 20, Back: 20, HalfWidth: 20) : null);
        var snapshot = new MechanicSnapshot(sdkVerified ? "ok" : "unsupported_sdk", "local", diagnostics.CurrentEvidenceFight(),
            services.ClientState.TerritoryType, duty,
            arena, explicitArena is not null ? "provided" : arena is null ? "unknown" : "encounter_rule",
            hazards.ToArray(), unmodeled.Order().ToArray(), truncated, lightningCage);
        return ReportMechanics(input, snapshot, archiveElapsed: null, record);

        static object Cell(int index)
        {
            var shape = LightningCageRules.CellArea(index);
            return new { index, shape.X, shape.Z };
        }
    }

    private object ReportMechanics(ToolArguments input, MechanicSnapshot snapshot, long? archiveElapsed, bool record = false)
    {
        var points = input.Has("points") ? input.Element("points").EnumerateArray().Select(ReadMechanicPoint).ToArray() : [];
        if (points.Length > 32) throw new ArgumentException("最多检查 32 个点。");
        var grid = input.Has("grid_step") ? SampleGrid(snapshot.Arena ??
            throw new ArgumentException("栅格采样须有场地边界。"), snapshot, (float)input.Number("grid_step", 2, 0.5, 40)) : null;
        return new
        {
            snapshot.Status, snapshot.Source, snapshot.CombatID, snapshot.TerritoryID, snapshot.DutyID,
            archiveElapsedMs = archiveElapsed, arena = snapshot.Arena, snapshot.ArenaSource,
            snapshot.Hazards, snapshot.UnmodeledActionIDs, snapshot.Truncated, snapshot.LightningCage,
            samples = points.Select(p => SamplePoint(p, snapshot)).ToArray(), grid,
            recorded = record ? snapshot.CombatID is { } id && diagnostics.RecordLocalMechanics(id, snapshot) : (bool?)null,
            coverage = "modeled_pending_aoes_only",
        };
    }

    private unsafe MechanicArea? ReadCastShape(IBattleNpc actor, GameAction row)
    {
        if (row.EffectRange == 0 && row.CastType != 8 || row.VFX.RowId == 25) return null;
        if (actor.Address == 0 ||
            !NativeMemoryReader.TryRead(&((BattleChara*)actor.Address)->CastInfo, out var cast) || cast.ActionId != row.RowId) return null;
        var origin = XZ(actor.Position);
        var targetID = actor.CastTargetObjectId;
        var target = targetID is 0 or 0xE0000000 ? null : services.ObjectTable.SearchById(targetID) ??
            (targetID <= uint.MaxValue ? services.ObjectTable.SearchByEntityId((uint)targetID) : null);
        var rotation = cast.Rotation;
        var castTarget = XZ(cast.TargetLocation);
        var hitbox = actor.HitboxRadius;
        if (!Finite(origin) || !float.IsFinite(rotation) || !float.IsFinite(hitbox) || hitbox < 0) return null;
        var aim = row.TargetArea ? castTarget : target is not null ? XZ(target.Position) :
            targetID is 0 or 0xE0000000 ? row.CastType == 8 ? castTarget : origin : (Vector2?)null;
        var radius = (float)row.EffectRange;
        var halfWidth = row.XAxisModifier / 2f;
        var omen = row.Omen.ValueNullable?.Path.ToString() ?? string.Empty;

        switch (row.CastType)
        {
            case 2 when aim is { } p && Finite(p):
                return new(MechanicShape.Circle, p.X, p.Y, radius);
            case 5:
                return new(MechanicShape.Circle, origin.X, origin.Y, radius + hitbox);
            case 3 or 13 when OmenNumber(omen, @"fan([0-9]{3})(?:[^0-9]|$)") is > 0 and <= 360 and var angle:
                return new(MechanicShape.Cone, origin.X, origin.Y, radius + (row.CastType == 3 ? hitbox : 0),
                    Rotation: rotation, HalfAngle: angle * MathF.PI / 360);
            case 4 or 12 when halfWidth > 0:
                return new(MechanicShape.Rectangle, origin.X, origin.Y, Rotation: rotation,
                    Front: radius + (row.CastType == 4 ? hitbox : 0), HalfWidth: halfWidth);
            case 8 when halfWidth > 0 && aim is { } end && Finite(end) && (end - origin).Length() is > 0.01f and var length:
                return new(MechanicShape.Rectangle, origin.X, origin.Y, Rotation: MathF.Atan2(end.X - origin.X, end.Y - origin.Y),
                    Front: length, HalfWidth: halfWidth);
            case 10 when aim is { } center && Finite(center) &&
                OmenNumber(omen, @"(?:sircle_..|sicle_..|circle..)([0-9]{2})(?:[^0-9]|$)") is > 0 and var inner && inner < radius:
                return new(MechanicShape.Donut, center.X, center.Y, radius, inner);
            case 11 when halfWidth > 0:
                return new(MechanicShape.Cross, origin.X, origin.Y, radius, Rotation: rotation, HalfWidth: halfWidth);
            default:
                return null;
        }
    }

    private static int OmenNumber(string path, string pattern)
    {
        var match = Regex.Match(path, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : 0;
    }

    private static Vector2 XZ(Vector3 v) => new(v.X, v.Z);
    private static bool Finite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);

    private static Vector3 ReadMechanicPoint(JsonElement value)
    {
        if (value.GetArrayLength() != 3) throw new ArgumentException("点坐标须为 [x,y,z]。");
        var p = new Vector3(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());
        if (!Finite(XZ(p)) || !float.IsFinite(p.Y)) throw new ArgumentException("坐标须为有限数值。");
        return p;
    }

    private static MechanicArea ReadMechanicArena(JsonElement value)
    {
        var shape = Required("shape").GetString();
        if (shape == "polygon")
        {
            var vertices = Required("vertices").EnumerateArray().Select(row =>
            {
                if (row.GetArrayLength() != 2) throw new ArgumentException("顶点须为 [x,z]。");
                return new[] { Coordinate(row[0]), Coordinate(row[1]) };
            }).ToArray();
            if (vertices.Length is < 3 or > 32) throw new ArgumentException("多边形需要 3 至 32 个顶点。");
            return new(MechanicShape.Polygon, 0, 0, Vertices: vertices);
        }
        var x = Coordinate(Required("x"));
        var z = Coordinate(Required("z"));
        if (shape == "circle") return new(MechanicShape.Circle, x, z, Size("radius"));
        if (shape == "rectangle") return new(MechanicShape.Rectangle, x, z, Front: Size("half_length"),
            Back: Size("half_length"), HalfWidth: Size("half_width"));
        throw new ArgumentException("未知的场地形状。");

        float Size(string name)
        {
            var n = Required(name).GetSingle();
            return n is >= 0.1f and <= 250 ? n : throw new ArgumentException("场地半径、半长和半宽须介于 0.1 至 250。");
        }
        JsonElement Required(string name) => value.TryGetProperty(name, out var field)
            ? field : throw new ArgumentException($"场地缺少参数 {name}。");

        static float Coordinate(JsonElement item)
        {
            var n = item.GetSingle();
            return n is >= -10000 and <= 10000 ? n : throw new ArgumentException("场地坐标超出允许范围。");
        }
    }

    private static object SamplePoint(Vector3 point, MechanicSnapshot snapshot)
    {
        bool? inside = snapshot.Arena?.Contains(point.X, point.Z);
        var hits = snapshot.Hazards.Select((h, i) => (h, i)).Where(pair => pair.h.Area.Contains(point.X, point.Z)).Select(pair => pair.i).ToArray();
        return new { position = new { point.X, point.Y, point.Z }, insideArena = inside, hazardIndexes = hits,
            result = inside == false ? "outside_arena" : hits.Length > 0 ? "modeled_danger" :
                inside is null || snapshot.Truncated || snapshot.UnmodeledActionIDs.Length != 0 || snapshot.Hazards.Length == 0
                    ? "unknown" : "outside_modeled_hazards" };
    }

    private static object SampleGrid(MechanicArea arena, MechanicSnapshot snapshot, float step)
    {
        var (minX, minZ, maxX, maxZ) = arena.Bounds();
        var nx = (int)Math.Ceiling((maxX - minX) / step);
        var nz = (int)Math.Ceiling((maxZ - minZ) / step);
        if ((long)nx * nz > 1024) throw new ArgumentException("栅格最多采样 1024 点，请增大格距。");
        var clear = new List<object>();
        if (!snapshot.Truncated && snapshot.UnmodeledActionIDs.Length == 0 && snapshot.Hazards.Length != 0)
            for (var iz = 0; iz < nz; iz++)
                for (var ix = 0; ix < nx; ix++)
                {
                    var x = minX + (ix + 0.5f) * step;
                    var z = minZ + (iz + 0.5f) * step;
                    if (arena.Contains(x, z) && !snapshot.Hazards.Any(h => h.Area.Contains(x, z))) clear.Add(new { x, z });
                }
        return new { step, examined = nx * nz, coverage = "cell_centers_outside_modeled_hazards", candidates = clear };
    }
}

internal sealed record MechanicSnapshot(string Status, string Source, Guid? CombatID, uint TerritoryID, uint DutyID,
    MechanicArea? Arena, string ArenaSource, MechanicHazard[] Hazards, uint[] UnmodeledActionIDs, bool Truncated, object? LightningCage);
