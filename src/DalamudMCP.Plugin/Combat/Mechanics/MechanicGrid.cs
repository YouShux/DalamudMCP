using System.Text.Json;

namespace DalamudMCP.Plugin.Tools;

internal static class MechanicGrid
{
    internal const string TOOL = "solve_mechanic_grid";
    private const int MAX_SIDE = 32;
    private const int MAX_WINDOWS = 64;
    private const int MAX_DURATION = 3_600_000;
    private const int MAX_PAGE = 32;

    internal static Dictionary<string, object> Schema()
    {
        static object Integer(int min, int max) => new { type = "integer", minimum = min, maximum = max };
        var coordinate = new { type = "number", minimum = -10000, maximum = 10000 };
        var index = Integer(0, MAX_SIDE - 1);
        var indexes = new { type = "array", maxItems = MAX_SIDE, items = index };
        var cells = new
        {
            type = "array", maxItems = MAX_SIDE * MAX_SIDE,
            items = new { type = "array", minItems = 2, maxItems = 2, items = index },
        };
        return new()
        {
            ["rows"] = Integer(1, MAX_SIDE), ["columns"] = Integer(1, MAX_SIDE),
            ["duration_ms"] = Integer(1, MAX_DURATION),
            ["hazards"] = new
            {
                type = "array", maxItems = MAX_WINDOWS,
                items = new
                {
                    type = "object", additionalProperties = false, required = new[] { "from_ms", "until_ms" },
                    properties = new Dictionary<string, object>
                    {
                        ["from_ms"] = Integer(0, MAX_DURATION - 1), ["until_ms"] = Integer(1, MAX_DURATION),
                        ["rows"] = indexes, ["columns"] = indexes, ["cells"] = cells,
                    },
                },
            },
            ["excluded_cells"] = cells,
            ["layout"] = new
            {
                type = "object", additionalProperties = false, required = new[] { "x", "z", "cell_size" },
                properties = new
                {
                    x = coordinate, z = coordinate,
                    cell_size = new { type = "number", minimum = 0.1, maximum = 100 },
                    rotation = new { type = "number", minimum = -Math.PI, maximum = Math.PI },
                },
            },
            ["at_ms"] = Integer(0, MAX_DURATION - 1),
            ["input_incomplete"] = new { type = "boolean", description = "保留归档规则输出的不完整标记。" },
            ["offset"] = Integer(0, MAX_SIDE * MAX_SIDE),
            ["limit"] = Integer(1, MAX_PAGE),
        };
    }

    internal static Grid Parse(ToolArguments input)
    {
        var rows = input.Integer("rows", 0, 1, MAX_SIDE);
        var columns = input.Integer("columns", 0, 1, MAX_SIDE);
        var duration = input.Integer("duration_ms", 0, 1, MAX_DURATION);
        var count = rows * columns;
        var excluded = new bool[count];
        if (input.Has("excluded_cells"))
            foreach (var cell in ReadCells(input.Element("excluded_cells"), rows, columns)) excluded[cell] = true;

        var windows = new List<Window>?[count];
        foreach (var item in ReadArray(input.Element("hazards"), MAX_WINDOWS))
        {
            CheckFields(item, "from_ms", "until_ms", "rows", "columns", "cells");
            var from = ReadInteger(Required(item, "from_ms"), 0, duration - 1, "from_ms");
            var until = ReadInteger(Required(item, "until_ms"), from + 1, duration, "until_ms");
            var mask = new bool[count];
            if (item.TryGetProperty("rows", out var rowIDs))
                foreach (var row in ReadArray(rowIDs, MAX_SIDE))
                    Array.Fill(mask, true, ReadInteger(row, 0, rows - 1, "row") * columns, columns);
            if (item.TryGetProperty("columns", out var columnIDs))
                foreach (var column in ReadArray(columnIDs, MAX_SIDE))
                {
                    var c = ReadInteger(column, 0, columns - 1, "column");
                    for (var i = c; i < count; i += columns) mask[i] = true;
                }
            if (item.TryGetProperty("cells", out var cellIDs))
                foreach (var cell in ReadCells(cellIDs, rows, columns)) mask[cell] = true;
            if (!mask.Contains(true)) throw new ArgumentException("每个危险时段须至少指定一个格、行或列。");
            for (var i = 0; i < count; i++)
                if (mask[i] && !excluded[i]) (windows[i] ??= []).Add(new(from, until));
        }

        var layout = input.Has("layout") ? ReadLayout(input.Element("layout")) : null;
        return new(rows, columns, duration, excluded, windows.Select(Merge).ToArray(), layout);
    }

    internal static object Solve(ToolArguments input)
    {
        var grid = Parse(input);
        var (rows, columns, duration, excluded, windows, layout) = grid;
        var count = rows * columns;
        var changes = new SortedDictionary<int, int> { [0] = 0, [duration] = 0 };
        var offset = input.Integer("offset", 0, 0, count);
        var limit = input.Integer("limit", 16, 1, MAX_PAGE);
        var at = input.Has("at_ms") ? input.Integer("at_ms", 0, 0, duration - 1) : (int?)null;
        var page = new List<object>(Math.Min(limit, count - offset));
        var available = count - excluded.Count(v => v);
        for (var i = 0; i < count; i++)
        {
            var danger = windows[i];
            foreach (var interval in danger)
            {
                changes[interval.FromMs] = changes.GetValueOrDefault(interval.FromMs) + 1;
                changes[interval.UntilMs] = changes.GetValueOrDefault(interval.UntilMs) - 1;
            }
            if (i < offset || page.Count == limit) continue;

            var row = i / columns;
            var column = i % columns;
            var status = excluded[i] ? "excluded" : at is not { } tick ? null :
                danger.Any(w => tick >= w.FromMs && tick < w.UntilMs) ? "danger" : "candidate";
            page.Add(new
            {
                row, column, excluded = excluded[i], center = layout?.Center(row, column), status, danger,
                candidates = excluded[i] ? [] : Complement(danger, duration),
            });
        }

        var phases = new List<object>();
        var start = 0;
        var active = changes[0];
        foreach (var (end, delta) in changes.Skip(1))
        {
            phases.Add(new { fromMs = start, untilMs = end, danger = active, candidates = available - active });
            start = end;
            active += delta;
        }
        return new
        {
            status = "ok", rows, columns, durationMs = duration, atMs = at, totalCells = count,
            excludedCount = count - available, phases, cells = page,
            nextOffset = offset + page.Count < count ? (int?)offset + page.Count : null,
            coverage = "provided_grid_and_hazards_only", inputIncomplete = input.Boolean("input_incomplete"), timeInterval = "[from_ms,until_ms)", indexBase = 0,
        };
    }

    private static Window[] Merge(List<Window>? values)
    {
        if (values is null) return [];
        values.Sort(static (a, b) => a.FromMs.CompareTo(b.FromMs));
        var result = new List<Window>(values.Count);
        var current = values[0];
        foreach (var next in values.Skip(1))
        {
            if (next.FromMs <= current.UntilMs) current = current with { UntilMs = Math.Max(current.UntilMs, next.UntilMs) };
            else { result.Add(current); current = next; }
        }
        result.Add(current);
        return [.. result];
    }

    internal static Window[] Complement(Window[] danger, int duration)
    {
        var result = new List<Window>();
        var cursor = 0;
        foreach (var interval in danger)
        {
            if (cursor < interval.FromMs) result.Add(new(cursor, interval.FromMs));
            cursor = interval.UntilMs;
        }
        if (cursor < duration) result.Add(new(cursor, duration));
        return [.. result];
    }

    internal static IEnumerable<int> ReadCells(JsonElement value, int rows, int columns) =>
        ReadArray(value, MAX_SIDE * MAX_SIDE).Select(cell => ReadCell(cell, rows, columns));

    internal static int ReadCell(JsonElement cell, int rows, int columns)
    {
        if (cell.ValueKind != JsonValueKind.Array || cell.GetArrayLength() != 2) throw new ArgumentException("格号须为 [行,列]。");
        return ReadInteger(cell[0], 0, rows - 1, "row") * columns + ReadInteger(cell[1], 0, columns - 1, "column");
    }

    internal static Layout ReadLayout(JsonElement item)
    {
        CheckFields(item, "x", "z", "cell_size", "rotation");
        return new(ReadNumber(Required(item, "x"), -10000, 10000, "x"),
            ReadNumber(Required(item, "z"), -10000, 10000, "z"),
            ReadNumber(Required(item, "cell_size"), 0.1, 100, "cell_size"),
            item.TryGetProperty("rotation", out var angle) ? ReadNumber(angle, -Math.PI, Math.PI, "rotation") : 0);
    }

    internal static JsonElement Required(JsonElement obj, string name) => obj.TryGetProperty(name, out var value)
        ? value : throw new ArgumentException($"缺少参数 {name}。");

    internal static void CheckFields(JsonElement item, params string[] allowed)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException("配置须为对象。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in item.EnumerateObject())
            if (!allowed.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name))
                throw new ArgumentException($"参数未知或重复：{field.Name}。");
    }

    internal static JsonElement.ArrayEnumerator ReadArray(JsonElement value, int max)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > max)
            throw new ArgumentException($"须传入不超过 {max} 项的数组。");
        return value.EnumerateArray();
    }

    internal static int ReadInteger(JsonElement value, int min, int max, string name) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= min && n <= max
            ? n : throw new ArgumentException($"{name} 须为 {min} 至 {max} 的整数。");

    internal static double ReadNumber(JsonElement value, double min, double max, string name) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && n >= min && n <= max
            ? n : throw new ArgumentException($"{name} 须为 {min} 至 {max} 的数值。");

    internal static string ReadText(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 64 } s && !string.IsNullOrWhiteSpace(s)
            ? s : throw new ArgumentException($"{name} 须为 1 至 64 个字符。");

    internal readonly record struct Window(int FromMs, int UntilMs);

    internal sealed record Grid(int Rows, int Columns, int Duration, bool[] Excluded, Window[][] Danger, Layout? Layout)
    {
        internal int Count => Rows * Columns;
        internal int Cell(JsonElement value) => ReadCell(value, Rows, Columns);
        internal object Describe(int cell) => new { row = cell / Columns, column = cell % Columns, center = Layout?.Center(cell / Columns, cell % Columns) };
        internal bool Clear(int cell, int from, int until) => !Excluded[cell] && !Danger[cell].Any(w => w.FromMs < until && from < w.UntilMs);
    }

    internal sealed record Layout(double X, double Z, double CellSize, double Rotation)
    {
        internal GridPoint Center(int row, int column) => ToWorld(row + 0.5, column + 0.5);

        internal GridPoint ToWorld(double row, double column) => new(X + CellSize * (Math.Cos(Rotation) * column + Math.Sin(Rotation) * row),
            Z + CellSize * (-Math.Sin(Rotation) * column + Math.Cos(Rotation) * row));

        internal (double Row, double Column) ToGrid(double x, double z)
        {
            var dx = (x - X) / CellSize;
            var dz = (z - Z) / CellSize;
            return (Math.Sin(Rotation) * dx + Math.Cos(Rotation) * dz, Math.Cos(Rotation) * dx - Math.Sin(Rotation) * dz);
        }
    }

    internal sealed record GridPoint(double X, double Z);
}
