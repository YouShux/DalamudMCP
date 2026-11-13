using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DalamudMCP.Plugin.Tools;

internal static class PacketFieldDecoder
{
    public static object Decode(byte[] bytes, JsonElement fields, int baseOffset = 0, int offset = 0, int limit = 128, bool strict = false)
    {
        var rows = new List<Field>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var bitBudget = 32768;
        Expand(fields, string.Empty, baseOffset, 0, bytes.Length);
        var status = strict ? "ok" : "decoded_unverified";
        if (rows.Any(row => !row.Available)) status = "short_payload";
        return new
        {
            status,
            source = strict ? "supplied_sample" : "owned_bytes",
            compatibilityVerified = false, payloadBytes = bytes.Length, totalFields = rows.Count,
            conditionalDisplayApplied = false, allFieldsAvailable = rows.All(row => row.Available), fields = rows.Skip(offset).Take(limit).Select(row => new
            {
                name = row.Path, path = row.Path, offset = row.Offset, type = row.Type, length = row.Length,
                available = row.Available, value = row.Value, displayValue = row.DisplayValue, enumName = row.EnumName,
                enumNames = row.EnumNames, metadata = row.Metadata, error = row.Error, hex = row.Hex, payloadOffset = row.Offset - baseOffset,
            }).ToArray(),
            nextOffset = offset + limit < rows.Count ? (int?)offset + limit : null,
        };

        void Expand(JsonElement definitions, string prefix, int origin, int depth, int boundary)
        {
            if (depth > 12 || definitions.ValueKind != JsonValueKind.Array || definitions.GetArrayLength() is < 1 or > 128)
                throw new ArgumentException("字段列表需要 1 至 128 项，嵌套最多 12 层。");
            foreach (var definition in definitions.EnumerateArray())
            {
                if (definition.ValueKind != JsonValueKind.Object) throw new ArgumentException("字段必须为对象。");
                var known = new HashSet<string>(StringComparer.Ordinal)
                    { "name", "offset", "type", "byte_order", "length", "count", "stride", "fields", "scale", "bit_offset", "bit_count", "enum", "enum_labels", "remaining", "metadata", "divisor", "addend" };
                if (definition.EnumerateObject().Any(property => !known.Remove(property.Name)))
                    throw new ArgumentException("字段含有未知或重复属性。");
                var spec = new ToolArguments(definition);
                var name = spec.Text("name");
                if (name.Length > 64 || name.Any(character => char.IsControl(character) || character is '.' or '[' or ']'))
                    throw new ArgumentException("字段名必须为不含路径分隔符的短文本。");
                var path = prefix.Length == 0 ? name : prefix + "." + name;
                var start = checked(origin + spec.Integer("offset", 0, 0, 65536));
                var type = spec.Text("type");
                var count = spec.Integer("count", 1, 1, 1024);
                var remaining = spec.Boolean("remaining");
                if (remaining && (type is not ("bytes" or "utf8" or "bitset") || spec.Has("length") || count != 1))
                    throw new ArgumentException("remaining 仅用于未指定长度的单个字节、文本或位图字段。");
                JsonElement? metadata = spec.Has("metadata") ? spec.Element("metadata") : null;
                if (metadata is { } info && (info.ValueKind != JsonValueKind.Object || info.GetRawText().Length > 32768))
                    throw new ArgumentException("metadata 必须是有界的声明对象。");
                var size = type switch
                {
                    "u8" or "i8" or "bool" => 1,
                    "u16" or "i16" => 2,
                    "u32" or "i32" or "f32" => 4,
                    "u64" or "i64" or "f64" => 8,
                    "bytes" or "utf8" or "bitset" => remaining ? Math.Min(4096, Math.Max(0, boundary - start)) : spec.Integer("length", 0, 1, 4096),
                    "object" => spec.Has("length") ? spec.Integer("length", 0, 1, 65536) : 0,
                    _ => throw new ArgumentException($"未知字段类型 {type}。"),
                };
                var stride = spec.Integer("stride", size, size, 65536);
                if (count > 1 && stride == 0) throw new ArgumentException("对象数组需要正数 stride。");
                var order = spec.Text("byte_order", "little");
                if (order is not ("little" or "big")) throw new ArgumentException("byte_order 必须为 little 或 big。");
                if (spec.Has("bit_offset") || spec.Has("bit_count"))
                {
                    if (type is not ("u8" or "u16" or "u32" or "u64")) throw new ArgumentException("位域只支持无符号整数。");
                    var shift = spec.Integer("bit_offset", 0, 0, size * 8 - 1);
                    _ = spec.Integer("bit_count", 1, 1, size * 8 - shift);
                }
                if (spec.Has("scale") || spec.Has("divisor") || spec.Has("addend"))
                {
                    if (type is "bool" or "bytes" or "utf8" or "bitset" or "object") throw new ArgumentException("scale 只支持数值字段。");
                    _ = spec.Number("scale", 1, -1e12, 1e12);
                    _ = spec.Number("divisor", 1, double.Epsilon, 1e12);
                    _ = spec.Number("addend", 0, -1e12, 1e12);
                }
                if (spec.Has("enum"))
                {
                    var choices = spec.Element("enum");
                    if (choices.ValueKind != JsonValueKind.Object || choices.EnumerateObject().Count() > 128
                        || choices.EnumerateObject().Any(item => item.Value.ValueKind != JsonValueKind.String || item.Value.GetString()!.Length > 128)
                        || choices.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != choices.EnumerateObject().Count())
                        throw new ArgumentException("enum 必须是不超过 128 项的唯一键与短文本标签。");
                }
                if (spec.Has("enum_labels"))
                {
                    var choices = spec.Element("enum_labels");
                    if (choices.ValueKind != JsonValueKind.Object || choices.EnumerateObject().Count() > 1024
                        || choices.EnumerateObject().Any(item => item.Value.ValueKind != JsonValueKind.Array || item.Value.GetArrayLength() is < 1 or > 16
                            || item.Value.EnumerateArray().Any(label => label.ValueKind != JsonValueKind.String || label.GetString()?.Length > 128))
                        || choices.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != choices.EnumerateObject().Count())
                        throw new ArgumentException("enum_labels 必须是有界的枚举标签列表。");
                }
                for (var index = 0; index < count; index++)
                {
                    var itemPath = spec.Has("count") ? $"{path}[{index}]" : path;
                    var position = checked(start + index * stride);
                    if (type == "object")
                    {
                        Expand(spec.Element("fields"), itemPath, position, depth + 1, size == 0 ? boundary : Math.Min(boundary, checked(position + size)));
                        continue;
                    }
                    if (rows.Count == 8192 || !names.Add(itemPath)) throw new ArgumentException("字段重复或展开后超过 8192 项。");
                    if (position > Math.Min(bytes.Length, boundary) - size)
                    {
                        if (strict) throw new ArgumentException($"字段 {itemPath} 超出样本范围。");
                        rows.Add(new(itemPath, position, type, size, false, null, null, null, null, Metadata: metadata));
                        continue;
                    }
                    if (rows.Count < offset || rows.Count >= offset + limit)
                    {
                        rows.Add(new(itemPath, position, type, size, true, null, null, null, null));
                        continue;
                    }
                    var data = bytes.AsSpan(position, size);
                    object? value;
                    string? error = null;
                    ulong raw = 0;
                    if (type is "bytes" or "utf8" or "bitset")
                    {
                        value = Convert.ToHexString(data);
                        if (type == "bitset")
                        {
                            var bits = new List<int>();
                            for (var at = 0; at < data.Length * 8; at++)
                            {
                                if ((data[at / 8] & (1 << (at % 8))) == 0) continue;
                                if (bitBudget == 0) { error = "bitset_expansion_limited"; break; }
                                bits.Add(at);
                                bitBudget--;
                            }
                            value = bits;
                        }
                        if (remaining && boundary - position > size) error = "field_length_limited";
                        if (type == "utf8")
                        {
                            var end = data.IndexOf((byte)0);
                            try { value = new UTF8Encoding(false, true).GetString(end < 0 ? data : data[..end]); }
                            catch (DecoderFallbackException) { value = null; error = "invalid_utf8"; }
                        }
                    }
                    else
                    {
                        var little = order == "little";
                        raw = size switch
                        {
                            1 => data[0],
                            2 => little ? BinaryPrimitives.ReadUInt16LittleEndian(data) : BinaryPrimitives.ReadUInt16BigEndian(data),
                            4 => little ? BinaryPrimitives.ReadUInt32LittleEndian(data) : BinaryPrimitives.ReadUInt32BigEndian(data),
                            _ => little ? BinaryPrimitives.ReadUInt64LittleEndian(data) : BinaryPrimitives.ReadUInt64BigEndian(data),
                        };
                        value = type switch
                        {
                            "bool" => raw != 0,
                            "f32" => Float(BitConverter.Int32BitsToSingle(unchecked((int)raw))),
                            "f64" => Float(BitConverter.Int64BitsToDouble(unchecked((long)raw))),
                            "i8" => unchecked((sbyte)raw).ToString(CultureInfo.InvariantCulture),
                            "i16" => unchecked((short)raw).ToString(CultureInfo.InvariantCulture),
                            "i32" => unchecked((int)raw).ToString(CultureInfo.InvariantCulture),
                            "i64" => unchecked((long)raw).ToString(CultureInfo.InvariantCulture),
                            _ => raw.ToString(CultureInfo.InvariantCulture),
                        };
                    }
                    object? display = null;
                    if (spec.Has("bit_offset") || spec.Has("bit_count"))
                    {
                        if (type[0] != 'u') throw new ArgumentException("位域只支持无符号整数。");
                        var shift = spec.Integer("bit_offset", 0, 0, size * 8 - 1);
                        var bits = spec.Integer("bit_count", 1, 1, size * 8 - shift);
                        display = ((raw >> shift) & (bits == 64 ? ulong.MaxValue : (1UL << bits) - 1)).ToString(CultureInfo.InvariantCulture);
                    }
                    if (spec.Has("scale") || spec.Has("divisor") || spec.Has("addend"))
                    {
                        if (!double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, out var number))
                            throw new ArgumentException("scale 只支持数值字段。");
                        display = Float(number * spec.Number("scale", 1, -1e12, 1e12) / spec.Number("divisor", 1, double.Epsilon, 1e12) + spec.Number("addend", 0, -1e12, 1e12));
                    }
                    string? enumName = null;
                    if (spec.Has("enum"))
                    {
                        var choices = spec.Element("enum");
                        if (choices.ValueKind != JsonValueKind.Object || choices.EnumerateObject().Count() > 128)
                            throw new ArgumentException("enum 最多 128 项。");
                        if (choices.TryGetProperty(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, out var choice))
                        {
                            enumName = choice.GetString();
                            if (enumName?.Length > 128) throw new ArgumentException("枚举标签过长。");
                        }
                    }
                    string[]? enumNames = null;
                    if (spec.Has("enum_labels") && spec.Element("enum_labels").TryGetProperty(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, out var labels))
                        enumNames = labels.EnumerateArray().Select(label => label.GetString() ?? string.Empty).ToArray();
                    enumName ??= enumNames?.FirstOrDefault();
                    rows.Add(new(itemPath, position, type, size, true, value, display, enumName, error,
                        type is "bytes" or "utf8" or "bitset" ? Convert.ToHexString(data) : $"0x{raw:X}", enumNames, metadata));
                }
            }
        }
    }

    private static object Float(double value) => double.IsFinite(value) ? value : value.ToString(CultureInfo.InvariantCulture);
    private sealed record Field(string Path, int Offset, string Type, int Length, bool Available, object? Value,
        object? DisplayValue, string? EnumName, string? Error, string? Hex = null, string[]? EnumNames = null, JsonElement? Metadata = null);
}
