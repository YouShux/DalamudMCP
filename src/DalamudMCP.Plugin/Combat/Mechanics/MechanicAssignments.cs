using System.Text.Json;
using static DalamudMCP.Plugin.Tools.MechanicGrid;

namespace DalamudMCP.Plugin.Tools;

internal static class MechanicAssignments
{
    internal const string TOOL = "solve_mechanic_assignments";

    internal static Dictionary<string, object> Schema()
    {
        var result = MechanicRoute.SchemaGrid();
        var grid = MechanicGrid.Schema();
        var cell = new { type = "array", minItems = 2, maxItems = 2, items = new { type = "integer", minimum = 0, maximum = 31 } };
        var cells = new { type = "array", minItems = 1, maxItems = 128, items = cell };
        var text = new { type = "string", minLength = 1, maxLength = 64 };
        var players = new { type = "array", minItems = 1, maxItems = 8, items = text };
        var distance = new { type = "number", minimum = 0, maximum = 500 };
        object Object(string[] required, Dictionary<string, object> properties) =>
            new { type = "object", additionalProperties = false, required, properties };
        result["at_ms"] = MechanicRoute.Time(); result["hold_ms"] = MechanicRoute.Time();
        result["candidates"] = cells;
        result["min_separation"] = distance;
        result["players"] = new { type = "array", minItems = 1, maxItems = 8, items = Object(["id"], new()
        {
            ["id"] = text, ["role"] = text, ["cells"] = cells, ["avoid_cells"] = grid["excluded_cells"], ["start"] = cell,
        }) };
        result["towers"] = new { type = "array", maxItems = 8, items = Object(["cell", "count"], new()
        {
            ["cell"] = cell, ["count"] = new { type = "integer", minimum = 1, maximum = 8 }, ["roles"] = players,
        }) };
        result["stacks"] = new { type = "array", maxItems = 4, items = Object(["members"], new() { ["members"] = players }) };
        result["links"] = new { type = "array", maxItems = 28, items = Object(["a", "b"], new()
        {
            ["a"] = text, ["b"] = text, ["min"] = distance, ["max"] = distance,
        }) };
        return result;
    }

    internal static object Solve(ToolArguments input, CancellationToken token)
    {
        var grid = Parse(input);
        var scale = grid.Layout?.CellSize ?? 1;
        var at = input.Integer("at_ms", 0, 0, grid.Duration - 1);
        var hold = input.Integer("hold_ms", 0, 0, grid.Duration - at);
        var until = at + Math.Max(1, hold);
        var separation = input.Number("min_separation", 0, 0, 500);
        var candidates = (input.Has("candidates") ? ReadCells(input.Element("candidates"), grid.Rows, grid.Columns).Distinct()
            : Enumerable.Range(0, grid.Count)).Where(c => grid.Clear(c, at, until)).ToArray();
        var players = ReadArray(input.Element("players"), 8).Select(ReadPlayer).ToArray();
        if (players.Length == 0 || players.Select(p => p.ID).Distinct(StringComparer.Ordinal).Count() != players.Length)
            throw new ArgumentException("需要 1 至 8 个 ID 互不重复的成员。");
        var indexes = players.Select((p, i) => (p.ID, i)).ToDictionary(v => v.ID, v => v.i, StringComparer.Ordinal);
        var groups = Enumerable.Repeat(-1, players.Length).ToArray();
        if (input.Has("stacks"))
        {
            var group = 0;
            foreach (var item in ReadArray(input.Element("stacks"), 4))
            {
                CheckFields(item, "members");
                var members = ReadArray(Required(item, "members"), 8).Select(PlayerIndex).ToArray();
                if (members.Length < 2 || members.Distinct().Count() != members.Length || members.Any(i => groups[i] != -1))
                    throw new ArgumentException("分摊组须有至少两名成员，同一成员不能重复分组。");
                foreach (var p in members) groups[p] = group;
                group++;
            }
        }
        var towers = new Dictionary<int, Tower>();
        if (input.Has("towers"))
            foreach (var tower in ReadArray(input.Element("towers"), 8).Select(ReadTower))
                if (!towers.TryAdd(tower.Cell, tower)) throw new ArgumentException("同一格不能重复定义塔。");
        if (towers.Values.Sum(t => t.Count) > players.Length) return NoSolution("tower_capacity");
        var links = input.Has("links") ? ReadArray(input.Element("links"), 28).Select(ReadLink).ToArray() : [];
        if (links.Select(l => (l.A, l.B)).Distinct().Count() != links.Length)
            throw new ArgumentException("同一对成员的连线约束须合并为一条。");
        var domains = players.Select((p, i) => candidates.Where(c => (p.Allowed is null || p.Allowed.Contains(c)) &&
            !p.Avoid.Contains(c) && (!towers.TryGetValue(c, out var tower) || tower.Roles is null || p.Role is { } role && tower.Roles.Contains(role)))
            .OrderBy(c => p.Start is { } start ? Distance(start, c) : 0).ThenBy(c => c).ToArray()).ToArray();
        if (domains.Any(d => d.Length == 0)) return NoSolution("player_has_no_candidate");
        var sequence = Enumerable.Range(0, players.Length).OrderBy(i => domains[i].Length).ToArray();
        var assignments = Enumerable.Repeat(-1, players.Length).ToArray();
        var occupants = new int[grid.Count];
        var checks = 0;
        var limited = false;
        if (!Search(0)) return limited ? new { status = "search_limit" } : NoSolution("constraints_conflict");
        return new { status = "ok", atMs = at, untilMs = until,
            assignments = players.Select((p, i) => new { playerID = p.ID, p.Role, cell = grid.Describe(assignments[i]) }).ToArray(),
            towers = towers.Values.Select(t => new { cell = grid.Describe(t.Cell), t.Count }).ToArray(),
            distanceUnit = grid.Layout is null ? "cells" : "yalms", inputIncomplete = input.Boolean("input_incomplete"), coverage = "provided_constraints_at_resolution" };

        bool Search(int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth == sequence.Length) return towers.Values.All(t => occupants[t.Cell] == t.Count);
            var index = sequence[depth];
            foreach (var cell in domains[index])
            {
                if (++checks > 200_000) { limited = true; return false; }
                if (towers.TryGetValue(cell, out var tower) && occupants[cell] >= tower.Count) continue;
                if (!Compatible(index, cell)) continue;
                assignments[index] = cell;
                occupants[cell]++;
                var canFill = towers.Values.All(t => occupants[t.Cell] + sequence.Skip(depth + 1).Count(i => domains[i].Contains(t.Cell)) >= t.Count);
                if (canFill && Search(depth + 1)) return true;
                occupants[cell]--;
                assignments[index] = -1;
                if (limited) return false;
            }
            return false;
        }

        bool Compatible(int index, int cell)
        {
            for (var j = 0; j < assignments.Length; j++)
            {
                if (assignments[j] < 0) continue;
                var sameStack = groups[index] >= 0 && groups[index] == groups[j];
                var sameCell = assignments[j] == cell;
                var distance = Distance(cell, assignments[j]);
                if (sameStack && !sameCell || !sameStack && (distance < separation || sameCell && !towers.ContainsKey(cell))) return false;
                foreach (var link in links)
                    if ((link.A == index && link.B == j || link.B == index && link.A == j) &&
                        (distance < link.Min || distance > link.Max)) return false;
            }
            return true;
        }

        double Distance(int a, int b)
        {
            var dx = a % grid.Columns - b % grid.Columns;
            var dz = a / grid.Columns - b / grid.Columns;
            return Math.Sqrt(dx * dx + dz * dz) * scale;
        }

        Player ReadPlayer(JsonElement item)
        {
            CheckFields(item, "id", "role", "cells", "avoid_cells", "start");
            return new(ReadText(Required(item, "id"), "id"),
                item.TryGetProperty("role", out var r) ? ReadText(r, "role") : null,
                item.TryGetProperty("cells", out var c) ? ReadCells(c, grid.Rows, grid.Columns).ToHashSet() : null,
                item.TryGetProperty("avoid_cells", out var a) ? ReadCells(a, grid.Rows, grid.Columns).ToHashSet() : [],
                item.TryGetProperty("start", out var s) ? grid.Cell(s) : null);
        }

        Tower ReadTower(JsonElement item)
        {
            CheckFields(item, "cell", "count", "roles");
            return new(grid.Cell(Required(item, "cell")), ReadInteger(Required(item, "count"), 1, 8, "count"),
                item.TryGetProperty("roles", out var r) ? ReadArray(r, 8).Select(v => ReadText(v, "role")).ToHashSet(StringComparer.Ordinal) : null);
        }

        Link ReadLink(JsonElement item)
        {
            CheckFields(item, "a", "b", "min", "max");
            var a = PlayerIndex(Required(item, "a"));
            var b = PlayerIndex(Required(item, "b"));
            if (a == b) throw new ArgumentException("连线须指定两名不同成员。");
            var min = item.TryGetProperty("min", out var l) ? ReadNumber(l, 0, 500, "min") : 0;
            var max = item.TryGetProperty("max", out var h) ? ReadNumber(h, min, 500, "max") : 500;
            return new(Math.Min(a, b), Math.Max(a, b), min, max);
        }

        int PlayerIndex(JsonElement value) => indexes.TryGetValue(ReadText(value, "player ID"), out var id)
            ? id : throw new ArgumentException("约束引用了不存在的成员。");

        object NoSolution(string reason) => new { status = "no_assignment_in_model", reason, atMs = at, untilMs = until };
    }

    private sealed record Player(string ID, string? Role, HashSet<int>? Allowed, HashSet<int> Avoid, int? Start);
    private sealed record Tower(int Cell, int Count, HashSet<string>? Roles);
    private sealed record Link(int A, int B, double Min, double Max);
}
