using System.Collections;
using System.Reflection;
using Lumina.Data;
using Lumina.Excel;
using Lumina.Text.ReadOnly;
using ClientAction = Lumina.Excel.Sheets.Action;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameReadouts
{
    internal const string NAMED_FIELDS_TOOL = "list_sheet_properties";
    internal const string NAMED_ROW_TOOL = "read_sheet_record";
    internal const string SEARCH_FIELDS_TOOL = "find_sheet_text";

    private static readonly Lazy<Dictionary<string, Type>> SheetTypes = new(() => typeof(ClientAction).Assembly.GetExportedTypes()
        .Where(type => type.Namespace == typeof(ClientAction).Namespace)
        .SelectMany(type => type.GetCustomAttribute<SheetAttribute>()?.Name is { } name
            ? new[] { (Type: type, Name: name) } : [])
        .GroupBy(entry => entry.Name, StringComparer.Ordinal)
        .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single().Type, StringComparer.Ordinal));

    public object NamedData(string tool, ToolArguments input, CancellationToken token)
    {
        var name = input.Text("sheet_name");
        if (name.Length > 128) throw new ArgumentException("表名最多 128 个字符。");
        var excel = services.DataManager.Excel;
        var language = Enum.TryParse<Language>(services.DataManager.Language.ToString(), out var value) ? (Language?)value : null;
        return NamedData(excel, language, tool, name, input, token);
    }

    internal static object NamedData(ExcelModule excel, Language? language, string tool, string name, ToolArguments input, CancellationToken token)
    {
        if (!excel.SheetNames.Contains(name, StringComparer.Ordinal)) return new { status = "unknown_sheet", sheetName = name };
        if (!SheetTypes.Value.TryGetValue(name, out var type))
            return new { status = "no_named_schema", sheetName = name, rawTool = SHEET_CELLS_TOOL };
        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0 &&
                property.Name is not ("ExcelPage" or "RowOffset" or "RowId" or "SubrowId"))
            .OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
        var isSubrow = type.GetInterfaces().Any(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IExcelSubrow<>));
        IExcelSheet sheet;
        try { sheet = excel.GetBaseSheet(type, language, name); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            return new { status = "named_schema_unavailable", sheetName = name, rawTool = SHEET_CELLS_TOOL };
        }
        var sheetType = sheet.GetType();
        if (tool == NAMED_FIELDS_TOOL)
            return new { status = "ok", sheetName = name, language = sheet.Language.ToString(), hasSubrows = isSubrow,
                fields = properties.Select(p => new { name = p.Name, type = p.PropertyType.Name, searchable = IsText(p.PropertyType) }).ToArray() };

        if (tool == NAMED_ROW_TOOL)
        {
            var id = input.Element("row_id").GetUInt32();
            if (isSubrow != input.Has("subrow_id")) throw new ArgumentException("仅子行表必须传入 subrow_id。");
            var sub = isSubrow ? (ushort?)input.Integer("subrow_id", 0, 0, ushort.MaxValue) : null;
            var offset = input.Integer("field_offset", 0, 0, 4096);
            var limit = input.Integer("field_limit", 128, 1, 128);
            var fieldNames = input.Has("fields") ? input.Element("fields") : (System.Text.Json.JsonElement?)null;
            if (fieldNames is not null && (input.Has("field_offset") || input.Has("field_limit")))
                throw new ArgumentException("指定字段时不接受字段分页。");
            if (fieldNames is { } fields)
            {
                if (fields.ValueKind != System.Text.Json.JsonValueKind.Array || fields.GetArrayLength() is < 1 or > 128)
                    throw new ArgumentException("fields 需要 1 至 128 个字段名。");
                var selected = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in fields.EnumerateArray())
                    if (field.GetString() is not { } fieldName || !selected.Add(fieldName) || !properties.Any(p => p.Name == fieldName))
                        throw new ArgumentException("字段未知或重复。");
                properties = properties.Where(p => selected.Contains(p.Name)).ToArray();
                limit = properties.Length;
            }
            var row = sub is { } subID
                ? sheetType.GetMethod("GetSubrowOrDefault")?.Invoke(sheet, [id, subID])
                : sheetType.GetMethod("GetRowOrDefault")?.Invoke(sheet, [id]);
            if (row is null) return new { status = "unknown_row", sheetName = name, rowID = id, subrowID = sub };
            var values = properties.Skip(offset).Take(limit).ToDictionary(p => p.Name, p => Project(p.GetValue(row), 0));
            return new { status = "ok", sheetName = name, language = sheet.Language.ToString(), rowID = id,
                subrowID = sub, fields = values, totalFields = properties.Length,
                nextFieldOffset = offset + values.Count < properties.Length ? (int?)offset + values.Count : null };
        }

        var needle = input.Text("query");
        if (needle.Length > 128) throw new ArgumentException("查询文字最多 128 个字符。");
        var fieldFilter = input.Has("field_name") ? input.Text("field_name") : null;
        var search = properties.Where(p => IsText(p.PropertyType) && (fieldFilter is null || p.Name == fieldFilter)).ToArray();
        if (search.Length == 0) return new { status = "no_searchable_field", sheetName = name };
        var cursor = input.Integer("offset", 0, 0, int.MaxValue - 1024);
        var subCursor = input.Integer("subrow_offset", 0, 0, ushort.MaxValue);
        if (!isSubrow && input.Has("subrow_offset")) throw new ArgumentException("普通表不接受 subrow_offset。");
        var cap = input.Integer("limit", 25, 1, 80);
        var exact = input.Boolean("exact");
        var getAt = sheetType.GetMethod("GetRowAt", [typeof(int)]) ?? throw new InvalidOperationException("当前 SDK 缺少数据表索引读取入口。");
        var idProperty = type.GetProperty("RowId");
        var subProperty = type.GetProperty("SubrowId");
        var rows = new List<object>(cap);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var scanned = 0; cursor < sheet.Count && scanned < 1024 && rows.Count < cap && watch.ElapsedMilliseconds < 80;)
        {
            token.ThrowIfCancellationRequested();
            var current = getAt.Invoke(sheet, [cursor]);
            if (current is null) break;
            if (isSubrow)
            {
                if (current is not IEnumerable) throw new InvalidOperationException("当前 SDK 缺少可索引子行集合。");
                var count = (int)(current.GetType().GetProperty("Count")?.GetValue(current) ?? 0);
                var indexer = current.GetType().GetProperty("Item", [typeof(int)]);
                if (subCursor < count && indexer is not null)
                {
                    var child = indexer.GetValue(current, [subCursor++]);
                    if (child is not null) Observe(child);
                    scanned++;
                }
                if (subCursor >= count || indexer is null) { cursor++; subCursor = 0; }
            }
            else
            {
                Observe(current);
                cursor++;
                scanned++;
            }
        }
        return new { status = "ok", sheetName = name, language = sheet.Language.ToString(), rows,
            nextOffset = cursor < sheet.Count ? (int?)cursor : null,
            nextSubrowOffset = isSubrow && cursor < sheet.Count ? (int?)subCursor : null };

        void Observe(object row)
        {
            foreach (var field in search)
            {
                var text = field.GetValue(row)?.ToString();
                if (text is null || !(exact ? text.Equals(needle, StringComparison.OrdinalIgnoreCase) :
                    text.Contains(needle, StringComparison.OrdinalIgnoreCase))) continue;
                rows.Add(new { rowID = idProperty?.GetValue(row), subrowID = subProperty?.GetValue(row),
                    field = field.Name, text = Clip(text) });
                break;
            }
        }
    }

    private static bool IsText(Type type) => type == typeof(ReadOnlySeString) || type == typeof(string);

    private static object? Project(object? value, int depth)
    {
        if (value is null) return null;
        if (value is ReadOnlySeString text) return Clip(text.ToString());
        if (value is string str) return Clip(str);
        if (value is float f && !float.IsFinite(f) || value is double d && !double.IsFinite(d)) return null;
        var type = value.GetType();
        if (type.IsEnum) return value.ToString();
        if (type.IsPrimitive) return value;
        if (value is RowRef row) return new { rowID = row.RowId };
        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(RowRef<>) || type.Name == "SubrowRef`1"))
            return new { rowID = type.GetProperty("RowId")?.GetValue(value) };
        if (depth < 2 && value is IEnumerable items)
        {
            var values = new List<object?>();
            foreach (var item in items)
            {
                if (values.Count == 32) return new { values, truncated = true };
                values.Add(Project(item, depth + 1));
            }
            return new { values, truncated = false };
        }
        if (depth < 2 && type.Assembly == typeof(ClientAction).Assembly && type.IsValueType)
            return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.GetIndexParameters().Length == 0 && p.Name is not ("ExcelPage" or "RowOffset"))
                .Take(32).ToDictionary(p => p.Name, p => Project(p.GetValue(value), depth + 1));
        return new { status = "unsupported_property_type", type = type.Name };
    }

    private static string Clip(string value) => value[..Math.Min(1024, value.Length)];
}
