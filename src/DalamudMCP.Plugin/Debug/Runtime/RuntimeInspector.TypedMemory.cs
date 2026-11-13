using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class RuntimeInspector
{
    public static object TypedMemory(ToolArguments input, Func<bool> allowed)
    {
        if (!Guid.TryParse(input.Text("expected_sdk_mvid"), out var expected) || expected != Sdk.ManifestModule.ModuleVersionId)
            return new { status = "sdk_changed" };
        var type = ResolveType(input);
        var address = checked((nint)input.ObjectID("address"));
        var totalOffset = 0;
        if (input.Has("field_path"))
        {
            var parts = input.Text("field_path").Split('.');
            if (parts.Length > 16) throw new ArgumentException("字段路径最多 16 层。");
            foreach (var part in parts)
            {
                if (type.IsPointer || type.IsFunctionPointer || type == typeof(nint) || type == typeof(nuint))
                    throw new ArgumentException("字段路径不会自动解引用指针。");
                var bracket = part.IndexOf('[');
                var name = bracket < 0 ? part : part[..bracket];
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new ArgumentException($"找不到字段 {name}。");
                var localOffset = field.GetCustomAttribute<FieldOffsetAttribute>()?.Value ?? Marshal.OffsetOf(type, field.Name).ToInt32();
                if (localOffset < 0 || localOffset > SizeOf(type) - SizeOf(field.FieldType)) throw new ArgumentException("字段超出 SDK 声明范围。");
                totalOffset = checked(totalOffset + localOffset);
                type = field.FieldType;
                if (bracket < 0) continue;
                if (!part.EndsWith(']') || !int.TryParse(part.AsSpan(bracket + 1, part.Length - bracket - 2), out var index) || index < 0)
                    throw new ArgumentException("数组下标格式无效。");
                if (type.GetCustomAttribute<InlineArrayAttribute>() is not { } array || index >= array.Length)
                    throw new ArgumentException("下标超过 SDK 固定数组范围。");
                var storage = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (storage.Length != 1) throw new ArgumentException("SDK 数组存储声明不唯一。");
                type = storage[0].FieldType;
                totalOffset = checked(totalOffset + index * SizeOf(type));
            }
        }
        var size = SizeOf(type);
        var write = input.Boolean("write");
        if (size > (write ? 256 : 4096)) throw new ArgumentException("目标字段超过单次内存访问上限。");
        var arguments = new Dictionary<string, object> { ["address"] = $"0x{checked(address + totalOffset):X}", ["length"] = size };
        if (write)
        {
            arguments["expected_hex"] = input.Text("expected_hex");
            arguments["hex"] = input.Text("hex");
        }
        var memory = Memory(new(JsonSerializer.SerializeToElement(arguments)), write, allowed);
        object? fields = null;
        var result = JsonSerializer.SerializeToElement(memory);
        if (!write && result.TryGetProperty("hex", out var hex)) fields = DecodeTyped(type, Convert.FromHexString(hex.GetString()!));
        return new { sdkMvid = expected, type = type.FullName, offset = totalOffset, size, memory, fields };
    }

    private static object DecodeTyped(Type root, byte[] bytes)
    {
        var rows = new List<object>();
        var truncated = false;
        Expand(root, string.Empty, 0, 0);
        return new { values = rows, truncated, pointerDereferenced = false };

        void Expand(Type type, string path, int offset, int depth)
        {
            if (rows.Count >= 128 || depth > 8) { truncated = true; return; }
            var size = SizeOf(type);
            if (offset < 0 || size > bytes.Length - offset) throw new InvalidOperationException("字段超出已读取的结构范围。");
            if (type.IsPointer || type.IsFunctionPointer || type == typeof(nint) || type == typeof(nuint))
            {
                rows.Add(new { path, offset, size, type = type.FullName, value = $"0x{BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, size)):X}" });
                return;
            }
            if (type.IsPrimitive || type.IsEnum)
            {
                var data = bytes.AsSpan(offset, size);
                var scalar = type.IsEnum ? Enum.GetUnderlyingType(type) : type;
                object value = Type.GetTypeCode(scalar) switch
                {
                    TypeCode.Boolean => data[0] != 0,
                    TypeCode.Byte => data[0], TypeCode.SByte => unchecked((sbyte)data[0]),
                    TypeCode.Int16 => BinaryPrimitives.ReadInt16LittleEndian(data),
                    TypeCode.UInt16 or TypeCode.Char => BinaryPrimitives.ReadUInt16LittleEndian(data),
                    TypeCode.Int32 => BinaryPrimitives.ReadInt32LittleEndian(data), TypeCode.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(data),
                    TypeCode.Int64 => BinaryPrimitives.ReadInt64LittleEndian(data).ToString(CultureInfo.InvariantCulture),
                    TypeCode.UInt64 => BinaryPrimitives.ReadUInt64LittleEndian(data).ToString(CultureInfo.InvariantCulture),
                    TypeCode.Single => Float(BinaryPrimitives.ReadSingleLittleEndian(data)),
                    TypeCode.Double => Float(BinaryPrimitives.ReadDoubleLittleEndian(data)),
                    _ => throw new ArgumentException("不支持的原生标量。"),
                };
                rows.Add(new { path, offset, size, type = type.FullName, value });
                return;
            }
            if (type.GetCustomAttribute<InlineArrayAttribute>() is { } array)
            {
                var item = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single().FieldType;
                for (var index = 0; index < array.Length; index++)
                {
                    if (rows.Count >= 128) { truncated = true; break; }
                    Expand(item, $"{path}[{index}]", checked(offset + index * SizeOf(item)), depth + 1);
                }
                return;
            }
            foreach (var member in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (rows.Count >= 128) { truncated = true; break; }
                var local = member.GetCustomAttribute<FieldOffsetAttribute>()?.Value ?? Marshal.OffsetOf(type, member.Name).ToInt32();
                Expand(member.FieldType, path.Length == 0 ? member.Name : path + "." + member.Name, checked(offset + local), depth + 1);
            }
        }

        static object Float(double value) => double.IsFinite(value) ? value : value.ToString(CultureInfo.InvariantCulture);
    }

    private static int SizeOf(Type type)
    {
        if (type.IsEnum) return SizeOf(Enum.GetUnderlyingType(type));
        if (type.IsPointer || type.IsFunctionPointer || type == typeof(nint) || type == typeof(nuint)) return IntPtr.Size;
        if (type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte)) return 1;
        if (type == typeof(short) || type == typeof(ushort) || type == typeof(char)) return 2;
        if (type == typeof(int) || type == typeof(uint) || type == typeof(float)) return 4;
        if (type == typeof(long) || type == typeof(ulong) || type == typeof(double)) return 8;
        if (type.GetCustomAttribute<InlineArrayAttribute>() is { Length: > 0 and <= 8192 } array)
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fields.Length != 1) throw new ArgumentException("未知数组布局。");
            return checked(array.Length * SizeOf(fields[0].FieldType));
        }
        if (!type.IsValueType || !(type.IsExplicitLayout || type.IsLayoutSequential)) throw new ArgumentException("不是已声明布局的原生结构。");
        var size = type.StructLayoutAttribute?.Size ?? 0;
        return size > 0 ? size : Marshal.SizeOf(type);
    }
}
