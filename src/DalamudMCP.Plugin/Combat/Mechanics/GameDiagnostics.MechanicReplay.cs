using System.Globalization;
using System.Text.Json;
using static DalamudMCP.Plugin.Tools.MechanicGrid;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameDiagnostics
{
    internal const string MECHANIC_REPLAY = "replay_combat_mechanics";
    private const int REPLAY_TIME_LIMIT = 3_600_000;

    internal static Dictionary<string, object> MechanicReplaySchema()
    {
        var id = new { type = "integer", minimum = 0, maximum = uint.MaxValue };
        var time = new { type = "integer", minimum = -2000, maximum = REPLAY_TIME_LIMIT };
        var number = new { type = "number" };
        var grid = MechanicGrid.Schema();
        var shape = new
        {
            type = "object", additionalProperties = false, required = new[] { "kind" },
            properties = new Dictionary<string, object>
            {
                ["kind"] = new { type = "string", @enum = new[] { "circle", "donut", "cone", "rectangle", "cross" } },
                ["anchor"] = new { type = "string", @enum = new[] { "event", "fixed" } },
                ["x"] = number, ["z"] = number, ["radius"] = number, ["inner_radius"] = number,
                ["rotation"] = number, ["half_angle"] = number, ["front"] = number, ["back"] = number, ["half_width"] = number,
            },
        };
        return new()
        {
            ["archive_id"] = new { type = "string", format = "uuid" }, ["from_ms"] = time, ["until_ms"] = time,
            ["offset"] = new { type = "integer", minimum = 0, maximum = 50000 },
            ["limit"] = new { type = "integer", minimum = 1, maximum = 100 },
            ["layout"] = grid["layout"], ["rows"] = grid["rows"], ["columns"] = grid["columns"],
            ["rules"] = new
            {
                type = "array", maxItems = 32,
                items = new
                {
                    type = "object", additionalProperties = false, required = new[] { "id", "event", "event_id", "duration_ms" },
                    properties = new Dictionary<string, object>
                    {
                        ["id"] = new { type = "string", minLength = 1, maxLength = 64 },
                        ["event"] = new { type = "string", @enum = ReplayKinds },
                        ["event_id"] = id, ["map_flag"] = id, ["delay_ms"] = time,
                        ["source_id"] = new { type = "string", maxLength = 64 },
                        ["target_id"] = new { type = "string", maxLength = 64 },
                        ["after_cast"] = new { type = "boolean" },
                        ["duration_ms"] = new { type = "integer", minimum = 1, maximum = 120000 },
                        ["danger_rows"] = new { type = "array", maxItems = 32, items = id },
                        ["danger_columns"] = new { type = "array", maxItems = 32, items = id },
                        ["cells"] = new { type = "array", maxItems = 128, items = new { type = "array", minItems = 2, maxItems = 2, items = id } },
                        ["shape"] = shape,
                    },
                },
            },
        };
    }

    private static readonly string[] ReplayKinds =
        ["cast_start", "map_effect", "target_icon", "tether", "tether_cancel", "status_added", "status_removed", "status_seen", "action_effect", "no_target_effect"];

    internal object ReplayMechanics(ToolArguments input, CancellationToken token)
    {
        var archiveID = RepositoryPackages.ParseID(input.Text("archive_id"));
        CombatCapture? archive;
        try { archive = LoadArchive(archiveID, token); }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        { return new { status = "invalid_capture", archiveID }; }
        if (archive is null) return new { status = "not_found", archiveID };
        return ReplayCapture(archive, archiveID, input, token);
    }

    private static object ReplayCapture(CombatCapture archive, Guid archiveID, ToolArguments input, CancellationToken token)
    {
        if (archive.Frames is not { Length: > 0 }) return new { status = "invalid_capture", archiveID };
        var start = input.Integer("from_ms", -1500, -2000, REPLAY_TIME_LIMIT - 1);
        var end = input.Integer("until_ms", REPLAY_TIME_LIMIT, start + 1, REPLAY_TIME_LIMIT);
        var offset = input.Integer("offset", 0, 0, 50000);
        var limit = input.Integer("limit", 40, 1, 100);
        var grid = input.Has("layout") ? ReadLayout(input.Element("layout")) : null;
        var rows = input.Has("rows") ? input.Integer("rows", 1, 1, 32) : 0;
        var cols = input.Has("columns") ? input.Integer("columns", 1, 1, 32) : 0;
        var rules = input.Has("rules") ? ReadArray(input.Element("rules"), 32).Select(r => ParseReplayRule(r, rows, cols)).ToArray() : [];
        if (rules.Select(r => r.ID).Distinct(StringComparer.Ordinal).Count() != rules.Length) throw new ArgumentException("规则 ID 必须唯一。");
        if (rules.Any(r => r.Cells.Length != 0) && grid is null) throw new ArgumentException("格规则需要场地 layout。");
        if ((rows == 0) != (cols == 0) || grid is not null && rows == 0)
            throw new ArgumentException("场地 layout 需要同时提供 rows 和 columns。");
        var byEvent = rules.ToLookup(r => (r.Event, r.EventID));
        var matchedRules = new HashSet<string>(StringComparer.Ordinal);
        var observed = ReplayEvents(archive).OrderBy(e => e.ElapsedMs).ToArray();
        var hazards = new List<ReplayHazard>();
        var unresolved = 0;
        var clipped = false;
        foreach (var evt in observed)
        {
            token.ThrowIfCancellationRequested();
            foreach (var rule in byEvent[(evt.Kind, evt.ID)])
            {
                if (rule.MapFlag is { } flag && UInt(evt.Data, "flag") != flag ||
                    rule.Source is { } source && !HasID(evt.Data, "sourceID", source) ||
                    rule.Target is { } target && !HasID(evt.Data, "targetID", target) && !HasID(evt.Data, "actorID", target) &&
                    !(evt.Data.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Array &&
                        targets.EnumerateArray().Any(value => MatchesID(value, target)))) continue;
                matchedRules.Add(rule.ID);
                if (rule.AfterCast && Number(evt.Data, "castTime") is not (>= 0 and <= 300)) { unresolved++; continue; }
                var from = evt.ElapsedMs + rule.Delay + (rule.AfterCast ? (long)Math.Ceiling(Number(evt.Data, "castTime")!.Value * 1000) : 0);
                var until = from + rule.Duration;
                if (from >= end || until <= start) continue;
                var area = rule.Shape;
                if (rule.EventAnchor && area is not null)
                {
                    if (!evt.Data.TryGetProperty("position", out var p) || Number(p, "x") is not { } x || Number(p, "z") is not { } z ||
                        (area.Shape is MechanicShape.Rectangle or MechanicShape.Cone or MechanicShape.Cross && Number(evt.Data, "rotation") is null))
                    { unresolved++; continue; }
                    area = area with { X = (float)x, Z = (float)z, Rotation = area.Rotation + (float)(Number(evt.Data, "rotation") ?? 0) };
                }
                if (hazards.Count >= 2048) { clipped = true; break; }
                hazards.Add(new(rule.ID, evt.ElapsedMs, evt.Kind, evt.ID, from, until, area, rule.Cells));
            }
            if (clipped) break;
        }

        var poseFrames = archive.Frames.Where(f => f.Player.Pose is not null).OrderBy(f => f.ElapsedMs).ToArray();
        var validEnd = Math.Clamp(archive.EndElapsedMs ?? archive.Frames.Max(f => f.ElapsedMs), 0, REPLAY_TIME_LIMIT);
        var hazardsPage = hazards.OrderBy(h => h.From).ThenBy(h => h.TriggerMs).Skip(offset).Take(limit).Select(h =>
        {
            var frame = LatestFrame(poseFrames, h.From);
            var occupants = frame is null ? [] : new[] { frame.Player }.Concat(frame.Party ?? [])
                .Where(a => a.Pose is not null).Select(a => new { a.ID, position = a.Pose, inHazard = Covers(h, a.Pose!, grid, rows, cols) }).ToArray();
            return new { h.RuleID, h.TriggerMs, h.Kind, h.EventID, fromMs = h.From, untilMs = h.Until,
                h.Area, cells = h.Cells.Select(c => new[] { c / cols, c % cols }).ToArray(), poseSampleMs = frame?.ElapsedMs, occupants };
        }).ToArray();
        var effects = (archive.ActionEffects ?? []).Where(e => e.ObservedAtMs >= start && e.ObservedAtMs < end &&
            e.Effects.Any(v => v.Type is 3 or 4 or 5 or 6)).OrderBy(e => e.ObservedAtMs).ToArray();
        var damage = effects.Skip(offset).Take(limit).Select(e =>
        {
            var frame = LatestFrame(poseFrames, e.ObservedAtMs);
            var pose = frame is null || !TryID(e.TargetID, out var target) ? null :
                new[] { frame.Player }.Concat(frame.Party ?? []).FirstOrDefault(a => a.ID == target || a.Pose?.EntityID == target)?.Pose;
            var overlapping = pose is null ? null : hazards.Where(h => h.From <= e.ObservedAtMs && e.ObservedAtMs < h.Until &&
                Covers(h, pose, grid, rows, cols)).Select(h => h.RuleID).Distinct().Take(32).ToArray();
            return new { elapsedMs = e.ObservedAtMs, e.ActionID, e.SourceID, e.TargetID, e.GlobalSequence,
                damageEffects = e.Effects.Where(v => v.Type is 3 or 4 or 5 or 6).Select(DecodeEffect).ToArray(),
                poseSampleMs = pose is null ? null : frame?.ElapsedMs, position = pose, overlappingRuleIDs = overlapping };
        }).ToArray();
        var events = observed.Where(e => e.ElapsedMs >= start && e.ElapsedMs < end).ToArray();
        var gridRows = grid is null ? [] : hazards.Where(h => h.Until > 0 && h.From < REPLAY_TIME_LIMIT)
            .OrderBy(h => h.From).Take(65).ToArray();
        var gridHazards = gridRows.Take(64).Select(h => new
        {
            from_ms = Math.Max(0, h.From), until_ms = Math.Min(REPLAY_TIME_LIMIT, h.Until),
            cells = GridCells(h),
        }).Where(h => h.cells.Length != 0).ToArray();
        var incomplete = rules.Length == 0 || matchedRules.Count < rules.Length || unresolved != 0 || clipped ||
            gridRows.Length > 64 || start > 0 || end <= validEnd || archive.PartialStart || archive.Truncated ||
            archive.EvidenceTruncated || archive.ActivityTruncated || archive.ActionEffectsTruncated == true ||
            rules.Any(r => !HasEventSource(archive, r.Event));
        return new
        {
            status = "ok", archiveID, archive.BuildMvid, endReason = archive.Status, endElapsedMs = validEnd, archive.PartialStart,
            archive.Truncated, archive.EvidenceTruncated, archive.ActivityTruncated, archive.ActionEffectsTruncated,
            archive.MechanicSources, archive.ActionEffectCoverage, poseFrames = poseFrames.Length,
            rulesWithoutObservation = rules.Where(r => !matchedRules.Contains(r.ID)).Select(r => r.ID).ToArray(),
            unresolvedRules = unresolved, hazardsClipped = clipped, eventCount = events.Length, hazardCount = hazards.Count,
            damageCount = effects.Length, events = events.Skip(offset).Take(limit).ToArray(), hazards = hazardsPage, damage,
            gridInput = grid is null ? null : new
            {
                rows, columns = cols,
                duration_ms = Math.Clamp(Math.Max(validEnd + 1, gridHazards.Select(h => h.until_ms).DefaultIfEmpty(1).Max()), 1, REPLAY_TIME_LIMIT),
                layout = new { x = grid.X, z = grid.Z, cell_size = grid.CellSize, rotation = grid.Rotation },
                hazards = gridHazards, input_incomplete = incomplete,
            },
            gridHazardsClipped = gridRows.Length > 64 || clipped,
            gridHazardCoverage = "explicit_cells_and_conservative_shape_overlap",
            gridInputIncomplete = incomplete,
            nextOffset = offset + limit < Math.Max(events.Length, Math.Max(hazards.Count, effects.Length)) ? (int?)offset + limit : null,
            coverage = "archive_observations_and_supplied_rules", poseMaxAgeMs = 250,
            damageInterpretation = "time_and_position_overlap_not_causality",
        };

        int[][] GridCells(ReplayHazard hazard)
        {
            token.ThrowIfCancellationRequested();
            if (hazard.Area is null || grid is null) return hazard.Cells.Select(c => new[] { c / cols, c % cols }).ToArray();
            var cellRadius = grid.CellSize / Math.Sqrt(2);
            return Enumerable.Range(0, rows * cols).Where(cell =>
            {
                if (Array.BinarySearch(hazard.Cells, cell) >= 0) return true;
                var center = grid.Center(cell / cols, cell % cols);
                return hazard.Area.MayOverlapCell(center.X, center.Z, cellRadius);
            }).Select(c => new[] { c / cols, c % cols }).ToArray();
        }
    }

    private static bool HasEventSource(CombatCapture capture, string kind) => kind switch
    {
        "action_effect" => capture.ActionEffects is not null,
        "status_added" or "status_removed" or "status_seen" => capture.Activity is not null,
        "tether" or "tether_cancel" or "target_icon" => capture.MechanicSources?.Contains("actor_control") == true,
        _ => capture.MechanicSources?.Contains(kind) == true,
    };

    private static IEnumerable<ReplayEvent> ReplayEvents(CombatCapture capture)
    {
        foreach (var evt in capture.Evidence ?? [])
        {
            var property = evt.Kind switch
            {
                "cast_start" or "no_target_effect" => "actionID", "map_effect" => "index",
                "tether" or "tether_cancel" => "tetherID", "target_icon" => "iconID", _ => null,
            };
            if (property is not null && UInt(evt.Payload, property) is { } id)
                yield return new(evt.ElapsedMs, evt.Kind, id, evt.Payload);
        }
        foreach (var evt in capture.Activity ?? [])
        {
            if (evt.Value is not JsonElement value) continue;
            if (evt.Kind is "status_added" or "status_removed" && UInt(value, "id") is { } id)
                yield return new(evt.ElapsedMs, evt.Kind, id, DalamudMCP.Shared.ToolJson.Element(new { evt.ActorID, evt.OwnerID, status = value }));
            else if (evt.Kind == "actor_seen" && value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("statuses", out var statuses) && statuses.ValueKind == JsonValueKind.Array)
                foreach (var status in statuses.EnumerateArray().Take(60))
                    if (UInt(status, "id") is { } seenID)
                        yield return new(evt.ElapsedMs, "status_seen", seenID,
                            DalamudMCP.Shared.ToolJson.Element(new { evt.ActorID, evt.OwnerID, status }));
        }
        foreach (var group in (capture.ActionEffects ?? []).Select((e, i) => (e, i))
            .GroupBy(v => (v.e.GlobalSequence, v.e.SourceID, v.e.ActionID, Unique: v.e.GlobalSequence == 0 ? v.i : 0)))
        {
            var first = group.First().e;
            yield return new(first.ObservedAtMs, "action_effect", first.ActionID,
                DalamudMCP.Shared.ToolJson.Element(new { first.SourceID, first.GlobalSequence,
                    targets = group.Select(v => v.e.TargetID).Distinct().Take(32).ToArray() }));
        }
    }

    private static ReplayRule ParseReplayRule(JsonElement item, int rows, int cols)
    {
        CheckFields(item, "id", "event", "event_id", "map_flag", "delay_ms", "after_cast", "duration_ms",
            "danger_rows", "danger_columns", "cells", "shape", "source_id", "target_id");
        var id = ReadText(Required(item, "id"), "id");
        var evt = ReadText(Required(item, "event"), "event");
        if (!ReplayKinds.Contains(evt, StringComparer.Ordinal)) throw new ArgumentException("规则事件类型不受支持。");
        var eventID = UInt(item, "event_id") ?? throw new ArgumentException("event_id 须为非负整数。");
        var mapFlag = item.TryGetProperty("map_flag", out _) ? UInt(item, "map_flag") ?? throw new ArgumentException("map_flag 须为非负整数。") : (uint?)null;
        var afterCast = item.TryGetProperty("after_cast", out var flag) && flag.ValueKind switch
        {
            JsonValueKind.True => true, JsonValueKind.False => false,
            _ => throw new ArgumentException("after_cast 须为布尔值。"),
        };
        if (mapFlag is not null && evt != "map_effect" || afterCast && evt != "cast_start") throw new ArgumentException("事件与条件不匹配。");
        var delay = item.TryGetProperty("delay_ms", out var d) ? ReadInteger(d, -2000, REPLAY_TIME_LIMIT, "delay_ms") : 0;
        var duration = ReadInteger(Required(item, "duration_ms"), 1, 120000, "duration_ms");
        var cells = new SortedSet<int>();
        if (item.TryGetProperty("cells", out var ids))
            foreach (var cell in ReadArray(ids, 128)) cells.Add(ReadCell(cell, rows, cols));
        if (item.TryGetProperty("danger_rows", out var ri))
            foreach (var row in ReadArray(ri, 32))
            {
                var r = ReadInteger(row, 0, rows - 1, "row");
                for (var c = 0; c < cols; c++) cells.Add(r * cols + c);
            }
        if (item.TryGetProperty("danger_columns", out var ci))
            foreach (var col in ReadArray(ci, 32))
            {
                var c = ReadInteger(col, 0, cols - 1, "column");
                for (var r = 0; r < rows; r++) cells.Add(r * cols + c);
            }
        var (shape, anchor) = item.TryGetProperty("shape", out var s) ? ReadReplayShape(s) : (null, false);
        if (shape is null && cells.Count == 0) throw new ArgumentException("规则须指定危险形状或格。");
        return new(id, evt, eventID, mapFlag, delay, duration, afterCast, shape, anchor, [.. cells],
            OptionalID("source_id"), OptionalID("target_id"));

        ulong? OptionalID(string field)
        {
            if (!item.TryGetProperty(field, out var value)) return null;
            return TryID(ReadText(value, field), out var parsed) && parsed != 0 ? parsed
                : throw new ArgumentException($"{field} 须为有效的十六进制或十进制对象 ID。");
        }
    }

    private static (MechanicArea, bool) ReadReplayShape(JsonElement shape)
    {
        CheckFields(shape, "kind", "anchor", "x", "z", "radius", "inner_radius", "rotation", "half_angle", "front", "back", "half_width");
        var kind = ReadText(Required(shape, "kind"), "kind") switch
        {
            "circle" => MechanicShape.Circle, "donut" => MechanicShape.Donut, "cone" => MechanicShape.Cone,
            "rectangle" => MechanicShape.Rectangle, "cross" => MechanicShape.Cross,
            _ => throw new ArgumentException("不支持的危险区形状。"),
        };
        var relative = !shape.TryGetProperty("anchor", out var a) || ReadText(a, "anchor") switch
        {
            "event" => true, "fixed" => false, _ => throw new ArgumentException("未知形状锚点。"),
        };
        var x = relative ? 0 : N("x", -10000, 10000);
        var z = relative ? 0 : N("z", -10000, 10000);
        var rotation = N("rotation", -Math.PI, Math.PI, 0);
        var radius = kind == MechanicShape.Rectangle ? 0 : N("radius", 0.01, 500);
        var inner = kind == MechanicShape.Donut ? N("inner_radius", 0.01, radius - 0.001) : 0;
        var angle = kind == MechanicShape.Cone ? N("half_angle", 0.01, Math.PI) : 0;
        var front = kind == MechanicShape.Rectangle ? N("front", 0.01, 500) : 0;
        var back = kind == MechanicShape.Rectangle ? N("back", 0, 500, 0) : 0;
        var width = kind is MechanicShape.Rectangle or MechanicShape.Cross ? N("half_width", 0.01, 500) : 0;
        return (new(kind, x, z, radius, inner, rotation, angle, front, back, width), relative);

        float N(string n, double min, double max, float? fallback = null) =>
            shape.TryGetProperty(n, out var v) ? (float)ReadNumber(v, min, max, n) :
            fallback ?? throw new ArgumentException($"形状缺少 {n}。");
    }

    private static bool Covers(ReplayHazard hazard, CombatPose pose, Layout? grid, int rows, int columns)
    {
        if (hazard.Area?.Contains(pose.X, pose.Z) == true) return true;
        if (grid is null || hazard.Cells.Length == 0) return false;
        var (row, col) = grid.ToGrid(pose.X, pose.Z);
        return row >= 0 && row < rows && col >= 0 && col < columns &&
            Array.BinarySearch(hazard.Cells, (int)row * columns + (int)col) >= 0;
    }

    private static CombatFrame? LatestFrame(CombatFrame[] frames, long tick)
    {
        var lo = 0;
        var hi = frames.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (frames[mid].ElapsedMs <= tick) lo = mid + 1;
            else hi = mid;
        }
        return lo > 0 && tick - frames[lo - 1].ElapsedMs <= 250 ? frames[lo - 1] : null;
    }

    private static uint? UInt(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var n) ? n : null;

    private static double? Number(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;

    private static bool HasID(JsonElement data, string key, ulong id) => data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(key, out var value) && MatchesID(value, id);

    private static bool MatchesID(JsonElement value, ulong id) => value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } s && TryID(s, out var parsed) && parsed == id;

    private static bool TryID(string value, out ulong id) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? ulong.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id)
        : ulong.TryParse(value, CultureInfo.InvariantCulture, out id);

    private sealed record ReplayRule(string ID, string Event, uint EventID, uint? MapFlag, int Delay, int Duration,
        bool AfterCast, MechanicArea? Shape, bool EventAnchor, int[] Cells, ulong? Source, ulong? Target);
    private sealed record ReplayEvent(long ElapsedMs, string Kind, uint ID, JsonElement Data);
    private sealed record ReplayHazard(string RuleID, long TriggerMs, string Kind, uint EventID, long From, long Until,
        MechanicArea? Area, int[] Cells);
}
