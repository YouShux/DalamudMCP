using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FFXIVClientStructs.FFXIV.Client.Network;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace DalamudMCP.Plugin.Tools;

internal static class SdkPacketLayouts
{
    internal static readonly Lazy<PacketLayoutCatalog.Layout[]> Entries = new(Build);

    private static PacketLayoutCatalog.Layout[] Build()
    {
        var sdk = typeof(Framework).Assembly;
        var result = new List<PacketLayoutCatalog.Layout>();
        var received = typeof(PacketDispatcher).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(method => method.Name.StartsWith("Handle", StringComparison.Ordinal))
            .SelectMany(method => method.GetParameters()).Select(parameter => parameter.ParameterType)
            .Where(type => type.IsPointer).Select(type => type.GetElementType()).OfType<Type>().ToHashSet();
        foreach (var type in sdk.GetExportedTypes().Where(type => received.Contains(type) && type.Namespace == "FFXIVClientStructs.FFXIV.Client.Game.Network" &&
            type.IsValueType && !type.IsEnum && !type.ContainsGenericParameters && type.Name.EndsWith("Packet", StringComparison.Ordinal)))
        {
            var omitted = new List<string>();
            var fields = Describe(type, 0, omitted, type.Name);
            if (fields.Count == 0 || fields.Count > 128) continue;
            var document = JsonSerializer.SerializeToElement(fields);
            try { _ = PacketFieldDecoder.Decode([], document); }
            catch (ArgumentException) { continue; }
            var version = sdk.ManifestModule.ModuleVersionId.ToString("D");
            var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { version, document, omitted })));
            result.Add(new(type.Name, "receive", version, "installed_sdk:" + type.FullName, "MIT", [type.Name[..^6]], document, digest, omitted.ToArray(), Origin: "sdk", MinimumBytes: type.StructLayoutAttribute?.Size ?? 0));
        }
        return result.OrderBy(layout => layout.Name, StringComparer.Ordinal).ToArray();
    }

    private static List<Dictionary<string, object>> Describe(Type type, int depth, List<string> omitted, string path)
    {
        var output = new List<Dictionary<string, object>>();
        if (depth > 8) { omitted.Add(path); return output; }
        foreach (var member in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var offset = member.GetCustomAttribute<FieldOffsetAttribute>()?.Value;
            if (offset is null)
            {
                if (!type.IsLayoutSequential) { omitted.Add(path + "." + member.Name); continue; }
                try { offset = Marshal.OffsetOf(type, member.Name).ToInt32(); }
                catch (ArgumentException) { omitted.Add(path + "." + member.Name); continue; }
            }
            var fieldType = member.FieldType;
            var count = fieldType.GetCustomAttribute<InlineArrayAttribute>()?.Length;
            if (count is not null)
            {
                var storage = fieldType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (storage.Length != 1 || count is < 1 or > 1024) { omitted.Add(path + "." + member.Name); continue; }
                fieldType = storage[0].FieldType;
            }
            var primitive = fieldType.IsEnum ? Enum.GetUnderlyingType(fieldType) : fieldType;
            var encoding = primitive == typeof(byte) ? "u8" : primitive == typeof(sbyte) ? "i8" :
                primitive == typeof(ushort) ? "u16" : primitive == typeof(short) ? "i16" :
                primitive == typeof(uint) ? "u32" : primitive == typeof(int) ? "i32" :
                primitive == typeof(ulong) ? "u64" : primitive == typeof(long) ? "i64" :
                primitive == typeof(float) ? "f32" : primitive == typeof(double) ? "f64" : primitive == typeof(bool) ? "bool" : null;
            var field = new Dictionary<string, object> { ["name"] = member.Name.TrimStart('_'), ["offset"] = offset.Value };
            if (encoding is not null)
            {
                field["type"] = encoding;
                if (count is not null) field["count"] = count.Value;
                if (fieldType.IsEnum)
                {
                    var values = Enum.GetValues(fieldType);
                    if (values.Length <= 128)
                    {
                        var labels = new Dictionary<string, string>();
                        foreach (var value in values)
                        {
                            var raw = Convert.ChangeType(value, primitive, CultureInfo.InvariantCulture);
                            labels[Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty] = value.ToString() ?? string.Empty;
                        }
                        field["enum"] = labels;
                    }
                }
            }
            else if (fieldType.IsValueType && !fieldType.IsPrimitive && !fieldType.IsPointer && !fieldType.IsFunctionPointer &&
                (fieldType.IsExplicitLayout || fieldType.IsLayoutSequential))
            {
                var children = Describe(fieldType, depth + 1, omitted, path + "." + member.Name);
                if (children.Count is < 1 or > 128) { omitted.Add(path + "." + member.Name); continue; }
                field["type"] = "object";
                field["fields"] = children;
                if (count is not null)
                {
                    var stride = fieldType.StructLayoutAttribute?.Size ?? 0;
                    if (stride <= 0)
                    {
                        try { stride = Marshal.SizeOf(fieldType); }
                        catch (ArgumentException) { omitted.Add(path + "." + member.Name); continue; }
                    }
                    if (stride is < 1 or > 65536) { omitted.Add(path + "." + member.Name); continue; }
                    field["count"] = count.Value;
                    field["stride"] = stride;
                }
            }
            else { omitted.Add(path + "." + member.Name); continue; }
            output.Add(field);
        }
        return output;
    }
}
