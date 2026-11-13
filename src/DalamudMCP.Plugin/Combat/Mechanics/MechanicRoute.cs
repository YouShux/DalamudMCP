using System.Text.Json;
using static DalamudMCP.Plugin.Tools.MechanicGrid;

namespace DalamudMCP.Plugin.Tools;

internal static class MechanicRoute
{
    internal const string ROUTE = "plan_mechanic_route";
    internal const string KNOCKBACK = "evaluate_mechanic_knockback";

    internal static Dictionary<string, object> Schema(bool knockback)
    {
        var result = SchemaGrid();
        var cell = new { type = "array", minItems = 2, maxItems = 2, items = new { type = "integer", minimum = 0, maximum = 31 } };
        var point = new { type = "array", minItems = 2, maxItems = 2, items = new { type = "number", minimum = -10000, maximum = 10000 } };
        if (knockback)
        {
            result["position"] = point; result["origin"] = point;
            result["distance"] = new { type = "number", minimum = 0, maximum = 500 };
            result["mode"] = new { type = "string", @enum = new[] { "away", "toward", "direction" } };
            result["rotation"] = new { type = "number", minimum = -Math.PI, maximum = Math.PI };
            result["check_crossing"] = new { type = "boolean" };
            result["at_ms"] = Time();
        }
        else
        {
            result["start"] = cell; result["goals"] = new { type = "array", minItems = 1, maxItems = 128, items = cell };
            result["start_ms"] = Time(); result["deadline_ms"] = Time(); result["hold_ms"] = Time();
            result["speed"] = new { type = "number", minimum = 0.1, maximum = 100 };
        }
        return result;
    }

    internal static Dictionary<string, object> SchemaGrid()
    {
        var result = MechanicGrid.Schema();
        result.Remove("offset"); result.Remove("limit"); result.Remove("at_ms");
        return result;
    }

    internal static object Time() => new { type = "integer", minimum = 0, maximum = 3_600_000 };

    internal static object Plan(ToolArguments input, CancellationToken token)
    {
        var grid = Parse(input);
        var layout = grid.Layout ?? throw new ArgumentException("路线计算须提供 layout 和格距。");
        var start = grid.Cell(input.Element("start"));
        var goals = ReadArray(input.Element("goals"), 128).Select(grid.Cell).ToHashSet();
        if (goals.Count == 0) throw new ArgumentException("至少需要一个目的格。");
        var begin = input.Integer("start_ms", 0, 0, grid.Duration - 1);
        var deadline = input.Integer("deadline_ms", grid.Duration - 1, begin, grid.Duration - 1);
        var hold = input.Integer("hold_ms", 0, 0, grid.Duration);
        if (!input.Has("speed")) throw new ArgumentException("须提供移动速度 speed。");
        var travel = (int)Math.Ceiling(1000 * layout.CellSize / input.Number("speed", 0, 0.1, 100));
        var entryOffset = travel / 2;
        var exitOffset = (travel + 1) / 2;
        var safe = grid.Danger.Select((d, cell) => grid.Excluded[cell] ? [] : Complement(d, grid.Duration)).ToArray();
        var first = Array.FindIndex(safe[start], w => w.FromMs <= begin && begin < w.UntilMs);
        if (first < 0) return new { status = "unsafe_start" };

        var initial = new Step(start, first);
        var todo = new PriorityQueue<Step, int>();
        var visited = new Dictionary<Step, Arrival> { [initial] = new(begin, begin, null) };
        todo.Enqueue(initial, begin);
        var checks = 0;
        while (todo.TryDequeue(out var current, out var time))
        {
            token.ThrowIfCancellationRequested();
            if (visited[current].Time != time) continue;
            var interval = safe[current.Cell][current.Interval];
            if (goals.Contains(current.Cell) && time + hold <= interval.UntilMs)
            {
                var path = new List<object>();
                for (Step? s = current; s is { } step; s = visited[step].Previous)
                {
                    var hit = visited[step];
                    path.Add(new { cell = grid.Describe(step.Cell), arrivedMs = hit.Time, departedPreviousMs = hit.Depart });
                }
                path.Reverse();
                return new { status = "ok", arrivalMs = time, holdUntilMs = time + hold, deadlineMs = deadline,
                    path, inputIncomplete = input.Boolean("input_incomplete"), coverage = "orthogonal_cell_centers_with_timed_boundary_crossings" };
            }
            foreach (var cell in Neighbors(current.Cell, grid))
                for (var i = 0; i < safe[cell].Length; i++)
                {
                    if (++checks > 2_000_000) return new { status = "search_limit" };
                    var next = safe[cell][i];
                    var depart = Math.Max(time, next.FromMs - entryOffset);
                    var crossing = depart + exitOffset;
                    var arrival = depart + travel;
                    if (crossing > interval.UntilMs || arrival > deadline) break;
                    if (arrival >= next.UntilMs) continue;
                    var state = new Step(cell, i);
                    if (visited.TryGetValue(state, out var old) && old.Time <= arrival) continue;
                    visited[state] = new(arrival, depart, current);
                    todo.Enqueue(state, arrival);
                }
        }
        return new { status = "no_route_in_model", deadlineMs = deadline, coverage = "orthogonal_cell_centers_with_timed_boundary_crossings" };
    }

    private static IEnumerable<int> Neighbors(int cell, Grid grid)
    {
        if (cell >= grid.Columns) yield return cell - grid.Columns;
        if (cell + grid.Columns < grid.Count) yield return cell + grid.Columns;
        if (cell % grid.Columns != 0) yield return cell - 1;
        if (cell % grid.Columns + 1 < grid.Columns) yield return cell + 1;
    }

    internal static object Knockback(ToolArguments input)
    {
        var grid = Parse(input);
        var layout = grid.Layout ?? throw new ArgumentException("击退计算须提供 layout。");
        var position = Point(input.Element("position"));
        var at = input.Integer("at_ms", 0, 0, grid.Duration - 1);
        if (!input.Has("distance")) throw new ArgumentException("须提供位移距离。");
        var distance = input.Number("distance", 0, 0, 500);
        var mode = input.Text("mode", "away");
        double dx, dz;
        if (mode == "direction")
        {
            if (!input.Has("rotation") || input.Has("origin")) throw new ArgumentException("固定方向须提供 rotation，不能同时指定 origin。");
            var rotation = input.Number("rotation", 0, -Math.PI, Math.PI);
            dx = Math.Sin(rotation);
            dz = Math.Cos(rotation);
        }
        else if (mode is "away" or "toward")
        {
            if (input.Has("rotation")) throw new ArgumentException("来源模式请提供 origin。");
            var origin = Point(input.Element("origin"));
            dx = position.X - origin.X;
            dz = position.Z - origin.Z;
            var magnitude = Math.Sqrt(dx * dx + dz * dz);
            if (magnitude < 1e-9 && distance != 0) return new { status = "direction_undefined" };
            var sign = mode == "toward" ? -1 : 1;
            dx = magnitude == 0 ? 0 : sign * dx / magnitude;
            dz = magnitude == 0 ? 0 : sign * dz / magnitude;
        }
        else throw new ArgumentException("未知击退模式。");

        var landing = new GridPoint(position.X + dx * distance, position.Z + dz * distance);
        var start = layout.ToGrid(position.X, position.Z);
        var end = layout.ToGrid(landing.X, landing.Z);
        var a = CellAt(start);
        var b = CellAt(end);
        if (a is null || grid.Excluded[a.Value]) return new { status = "invalid_start", position };
        var crossing = new List<object>();
        if (input.Boolean("check_crossing"))
        {
            for (var cell = 0; cell < grid.Count; cell++)
            {
                if (grid.Clear(cell, at, at + 1)) continue;
                if (Touches(start, end, cell / grid.Columns, cell % grid.Columns))
                    crossing.Add(new { cell = grid.Describe(cell), kind = grid.Excluded[cell] ? "excluded" : "danger" });
            }
        }
        var result = b is not { } cellID ? "outside_arena" : grid.Excluded[cellID] ? "excluded" :
            !grid.Clear(cellID, at, at + 1) ? "danger" : "candidate";
        return new { status = "ok", atMs = at, position, landing, result, cell = b is { } id ? grid.Describe(id) : null,
            crossing, inputIncomplete = input.Boolean("input_incomplete"), coverage = "straight_displacement_without_wall_stop_or_immunity" };

        int? CellAt((double Row, double Column) p) => p.Row >= 0 && p.Row < grid.Rows && p.Column >= 0 && p.Column < grid.Columns
            ? (int)p.Row * grid.Columns + (int)p.Column : null;
    }

    private static bool Touches((double Row, double Column) a, (double Row, double Column) b, int row, int col)
    {
        var enter = 0d;
        var exit = 1d;
        return Axis(a.Row, b.Row - a.Row, row) && Axis(a.Column, b.Column - a.Column, col);

        bool Axis(double start, double delta, double min)
        {
            if (Math.Abs(delta) < 1e-12) return start >= min && start <= min + 1;
            var t0 = (min - start) / delta;
            var t1 = (min + 1 - start) / delta;
            enter = Math.Max(enter, Math.Min(t0, t1));
            exit = Math.Min(exit, Math.Max(t0, t1));
            return enter <= exit;
        }
    }

    private static GridPoint Point(JsonElement v) => v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 2
        ? new(ReadNumber(v[0], -10000, 10000, "x"), ReadNumber(v[1], -10000, 10000, "z"))
        : throw new ArgumentException("位置须为 [x,z]。");

    private readonly record struct Step(int Cell, int Interval);
    private readonly record struct Arrival(int Time, int Depart, Step? Previous);
}
