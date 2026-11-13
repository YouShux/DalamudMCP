using Lumina.Excel;
using Lumina.Text.ReadOnly;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameReadouts
{
    internal const string SHEET_LIST_TOOL = "browse_client_sheets";
    internal const string SHEET_FIELDS_TOOL = "describe_client_sheet";
    internal const string SHEET_CELLS_TOOL = "read_client_sheet_cells";

    public object BrowseData(string tool, ToolArguments input)
    {
        var excel = services.DataManager.Excel;
        var clientLanguage = services.ClientState.ClientLanguage;
        var language = Enum.TryParse<Lumina.Data.Language>(services.DataManager.Language.ToString(), out var dataLanguage)
            ? (Lumina.Data.Language?)dataLanguage : null;
        if (tool == SHEET_LIST_TOOL)
        {
            var filter = input.Has("name_contains") ? input.Text("name_contains") : string.Empty;
            if (filter.Length > 128) throw new ArgumentException("表名筛选最多 128 个字符。");
            var offset = input.Integer("offset", 0, 0, 10000);
            var limit = input.Integer("limit", 40, 1, 100);
            var names = excel.SheetNames.Where(name => name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
            var rows = names.Skip(offset).Take(limit).ToArray();
            return new { status = "ok", source = "client_excel", clientLanguage = clientLanguage.ToString(), total = names.Length, sheets = rows,
                nextOffset = offset + rows.Length < names.Length ? (int?)(offset + rows.Length) : null };
        }

        var requested = input.Text("sheet_name");
        if (requested.Length > 128) throw new ArgumentException("表名最多 128 个字符。");
        var name = excel.SheetNames.FirstOrDefault(value => value.Equals(requested, StringComparison.Ordinal));
        if (name is null) return new { status = "unknown_sheet", sheetName = requested };
        var raw = excel.GetRawSheet(name, language);
        var subrows = raw is RawSubrowExcelSheet;
        if (tool == SHEET_FIELDS_TOOL)
        {
            var offset = input.Integer("offset", 0, 0, 10000);
            var limit = input.Integer("limit", 64, 1, 128);
            var end = Math.Min(offset + limit, raw.Columns.Count);
            var columns = new List<object>();
            for (var index = offset; index < end; index++)
            {
                var column = raw.Columns[index];
                columns.Add(new { index, type = column.Type.ToString(), byteOffset = column.Offset });
            }
            return new { status = "ok", source = "client_excel", sheetName = name, language = raw.Language.ToString(),
                totalRows = raw.Count, hasSubrows = subrows, totalColumns = raw.Columns.Count, columns,
                nextOffset = end < raw.Columns.Count ? (int?)end : null };
        }

        var ids = ReadIDs(input, "row_ids", 16, allowZero: true);
        var columnsToRead = ReadIDs(input, "columns", 16, allowZero: true);
        if (columnsToRead.Any(index => index >= raw.Columns.Count)) throw new ArgumentOutOfRangeException("columns");
        if (subrows != input.Has("subrow_id"))
            throw new ArgumentException(subrows ? "子行表必须提供 subrow_id。" : "普通表不接受 subrow_id。");
        var output = new List<object>(ids.Length);
        var missing = new List<uint>();
        if (subrows)
        {
            var subrow = (ushort)input.Integer("subrow_id", 0, 0, ushort.MaxValue);
            var typed = excel.GetSubrowSheet<RawSubrow>(language, name);
            foreach (var id in ids)
                if (typed.TryGetSubrow(id, subrow, out var row))
                    output.Add(new { rowID = id, subrowID = subrow, cells = Project(row.ReadColumn) });
                else missing.Add(id);
        }
        else
        {
            var typed = excel.GetSheet<RawRow>(language, name);
            foreach (var id in ids)
                if (typed.TryGetRow(id, out var row)) output.Add(new { rowID = id, cells = Project(row.ReadColumn) });
                else missing.Add(id);
        }
        return new { status = "ok", source = "client_excel", sheetName = name, language = raw.Language.ToString(),
            rows = output, missingRowIDs = missing };

        object[] Project(Func<int, object> read) => columnsToRead.Select(index =>
        {
            var cell = read((int)index);
            object? value = cell switch
            {
                ReadOnlySeString text => Clip(text.ToString()),
                string text => Clip(text),
                float number when !float.IsFinite(number) => null,
                double number when !double.IsFinite(number) => null,
                _ => cell,
            };
            return (object)new { index, value };
        }).ToArray();

        static string Clip(string value) => value[..Math.Min(512, value.Length)];
    }
}
