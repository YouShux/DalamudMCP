using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class RuntimeInspector(GameServices services)
{
    private readonly Dictionary<Guid, Retained> retained = [];
    private static readonly Assembly Sdk = typeof(Framework).Assembly;

    public static object Inspect(ToolArguments input)
    {
        var operation = input.Text("operation");
        if (operation == "services")
            return new { status = "ok", services = typeof(GameServices).GetProperties().Select(property => new
                { name = property.Name, type = property.PropertyType.FullName }).ToArray() };
        if (operation is "types" or "managed_types")
        {
            var query = input.Text("query", operation == "types" ? "FFXIVClientStructs.FFXIV" : string.Empty);
            var offset = input.Integer("offset", 0, 0, 100000);
            var types = (operation == "types" ? Sdk.GetExportedTypes() : LoadedTypes()).Where(type => type.FullName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                .OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
            return new { status = "ok", sdkMvid = Sdk.ManifestModule.ModuleVersionId, matched = types.Length,
                types = types.Skip(offset).Take(100).Select(type => new { name = type.FullName, id = ID(type), layout = type.StructLayoutAttribute?.Value.ToString() }).ToArray(),
                nextOffset = offset + 100 < types.Length ? (int?)offset + 100 : null };
        }
        if (operation == "describe")
        {
            var type = input.Has("type_id") ? ResolveLoadedType(input.Text("type_id")) : ResolveType(input);
            var offset = input.Integer("offset", 0, 0, 100000);
            var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(member => member is FieldInfo or PropertyInfo or MethodInfo).OrderBy(member => member.Name, StringComparer.Ordinal).ThenBy(member => member.MetadataToken).ToArray();
            return new { status = "ok", type = type.FullName, id = ID(type), sdkMvid = Sdk.ManifestModule.ModuleVersionId,
                size = type.IsExplicitLayout ? type.StructLayoutAttribute?.Size : null,
                members = members.Skip(offset).Take(100).Select(member => new
                {
                    id = ID(member), member.Name, kind = member.MemberType.ToString(), signature = member.ToString(),
                    offset = member.GetCustomAttribute<FieldOffsetAttribute>()?.Value,
                }).ToArray(), nextOffset = offset + 100 < members.Length ? (int?)offset + 100 : null };
        }
        throw new ArgumentException("operation 必须为 services、types、managed_types 或 describe。");
    }

    public object? ReadScalar(ToolArguments input)
    {
        Expire();
        var id = Guid.ParseExact(input.Text("reference_id"), "D");
        if (!retained.TryGetValue(id, out var entry)) throw new InvalidOperationException("对象引用已过期。");
        var member = PublicMembers(entry.Value.GetType()).SingleOrDefault(member => ID(member) == input.Text("member_id"))
            ?? throw new ArgumentException("成员 ID 不属于当前对象。");
        var value = ReadMember(member, entry.Value);
        if (!IsScalar(value?.GetType())) throw new ArgumentException("连续观察只支持标量成员。");
        return value;
    }

    public object Access(ToolArguments input, Func<bool> allowed)
    {
        Expire();
        if (!allowed()) throw new InvalidOperationException("运行时访问权限已关闭。");
        var operation = input.Text("operation");
        if (operation == "retain_service")
        {
            var property = typeof(GameServices).GetProperty(input.Text("service")) ?? throw new ArgumentException("未知服务。");
            return Output(property.GetValue(services));
        }
        var staticAccess = operation is "static_get" or "static_set" or "static_call";
        object? target;
        Type type;
        if (staticAccess)
        {
            type = ResolveLoadedType(input.Text("type_id"));
            target = null;
            operation = operation[7..];
        }
        else
        {
            var id = Guid.ParseExact(input.Text("reference_id"), "D");
            if (operation == "release") return new { status = retained.Remove(id) ? "released" : "not_found" };
            if (!retained.TryGetValue(id, out var entry)) return new { status = "reference_expired" };
            target = entry.Value;
            type = target.GetType();
        }
        if (operation == "describe")
            return new { status = "ok", type = type.FullName, members = PublicMembers(type).Skip(input.Integer("offset", 0, 0, 10000)).Take(100)
                .Select(member => new { id = ID(member), member.Name, kind = member.MemberType.ToString(), signature = member.ToString() }).ToArray() };
        if (operation == "task")
        {
            if (target is not System.Threading.Tasks.Task task) throw new ArgumentException("对象不是 Task。");
            return new { status = task.Status.ToString(), task.IsCompleted, task.IsCanceled, task.IsFaulted,
                error = task.Exception?.GetBaseException().GetType().Name,
                result = task.IsCompletedSuccessfully && task.GetType().GetProperty("Result") is { } resultProperty ? Output(resultProperty.GetValue(task)) : null };
        }
        if (operation == "enumerate") return Enumerate(target ?? throw new ArgumentException("需要实例引用。"), input, allowed);
        var memberID = input.Text("member_id");
        var member = type.GetMembers(BindingFlags.Public | (staticAccess ? BindingFlags.Static : BindingFlags.Instance)).SingleOrDefault(item => ID(item) == memberID) ?? throw new ArgumentException("成员 ID 不属于当前对象。");
        if (!allowed()) throw new InvalidOperationException("运行时访问权限已关闭。");
        if (operation == "get") return Output(ReadMember(member, target));
        if (operation == "set")
        {
            var before = ReadMember(member, target);
            if (!IsScalar(before?.GetType())) throw new ArgumentException("仅支持设置标量成员。");
            if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(before), input.Element("expected_value")))
                return new { status = "value_changed" };
            switch (member)
            {
                case FieldInfo field when !field.IsInitOnly && !field.IsLiteral: field.SetValue(target, ConvertArgument(input.Element("value"), field.FieldType)); break;
                case PropertyInfo property when property.SetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0:
                    property.SetValue(target, ConvertArgument(input.Element("value"), property.PropertyType)); break;
                default: throw new ArgumentException("成员不可写。");
            }
            return new { status = "assigned", value = Output(ReadMember(member, target)), resultObserved = false };
        }
        if (operation != "call" || member is not MethodInfo method || method.ContainsGenericParameters || method.ReturnType.IsByRef)
            throw new ArgumentException("当前成员不支持该操作。");
        if (method.GetParameters().Any(parameter => parameter.ParameterType.IsPointer)) throw new ArgumentException("对象成员调用仅支持标量参数。");
        var supplied = input.Element("arguments");
        var parameters = method.GetParameters();
        if (supplied.ValueKind != JsonValueKind.Array || parameters.Length > 16 || supplied.GetArrayLength() != parameters.Length)
            throw new ArgumentException("arguments 必须与参数列表完全一致。");
        var arguments = new object?[parameters.Length];
        for (var index = 0; index < arguments.Length; index++)
        {
            if (supplied[index].ValueKind == JsonValueKind.Object && supplied[index].TryGetProperty("reference_id", out var reference))
            {
                if (!Guid.TryParse(reference.GetString(), out var argumentID) || !retained.TryGetValue(argumentID, out var argument) ||
                    !parameters[index].ParameterType.IsInstanceOfType(argument.Value)) throw new ArgumentException("对象参数的引用或类型不匹配。");
                arguments[index] = argument.Value;
            }
            else arguments[index] = ConvertArgument(supplied[index], parameters[index].ParameterType);
        }
        if (!allowed()) throw new InvalidOperationException("运行时访问权限已关闭。");
        return Output(method.Invoke(target, arguments));
    }

    public unsafe object CallSdk(ToolArguments input, Func<bool> allowed)
    {
        if (!Guid.TryParse(input.Text("expected_sdk_mvid"), out var expected) || expected != Sdk.ManifestModule.ModuleVersionId)
            return new { status = "sdk_changed" };
        if (!GameDiagnostics.ReadoutLayoutKnown) return new { status = "unsupported_sdk" };
        var (type, method, _) = ResolveSdkEntry(input);
        using var borrowed = new BorrowedStrings();
        var args = ReadArguments(input, method, borrowed);
        var instance = method.IsStatic ? 0 : checked((nint)input.ObjectID("instance_address"));
        if (!method.IsStatic && (instance == 0 || !type.IsValueType || !Readable(instance, Math.Clamp(type.StructLayoutAttribute?.Size ?? 1, 1, 65536))))
            return new { status = "instance_unreadable" };
        var wrapper = new DynamicMethod("InvokeSdkMember", typeof(object), [typeof(nint), typeof(object[])], typeof(RuntimeInspector).Module, false);
        var il = wrapper.GetILGenerator();
        if (!method.IsStatic) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Conv_U); }
        var parameters = method.GetParameters();
        for (var index = 0; index < parameters.Length; index++)
        {
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldc_I4, index); il.Emit(OpCodes.Ldelem_Ref);
            if (parameters[index].ParameterType.IsPointer) { il.Emit(OpCodes.Unbox_Any, typeof(nint)); il.Emit(OpCodes.Conv_U); }
            else il.Emit(parameters[index].ParameterType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, parameters[index].ParameterType);
        }
        il.Emit(OpCodes.Call, method);
        if (method.ReturnType == typeof(void)) il.Emit(OpCodes.Ldnull);
        else if (method.ReturnType.IsPointer) { il.Emit(OpCodes.Conv_I); il.Emit(OpCodes.Box, typeof(nint)); }
        else if (method.ReturnType.IsValueType) il.Emit(OpCodes.Box, method.ReturnType);
        il.Emit(OpCodes.Ret);
        if (!allowed()) throw new InvalidOperationException("运行时调用权限已关闭。");
        var invoke = (Func<nint, object?[], object?>)wrapper.CreateDelegate(typeof(Func<nint, object?[], object?>));
        return new { status = "invoked", memberID = ID(method), result = Output(invoke(instance, args)), resultObserved = false };
    }

    internal static (Type Type, MethodInfo Method, nint Address) ResolveSdkEntry(ToolArguments input)
    {
        if (!Guid.TryParse(input.Text("expected_sdk_mvid"), out var expected) || expected != Sdk.ManifestModule.ModuleVersionId || !GameDiagnostics.ReadoutLayoutKnown)
            throw new InvalidOperationException("SDK 或客户端构建不匹配。");
        var type = ResolveType(input);
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .SingleOrDefault(member => ID(member) == input.Text("member_id"));
        if (method is null || method.IsGenericMethod || method.IsVirtual || method.ReturnType.IsByRef || method.ReturnType.IsByRefLike ||
            (!method.ReturnType.IsPointer && method.ReturnType != typeof(void) && !IsScalar(method.ReturnType)))
            throw new ArgumentException("仅支持 SDK 中已发现的标量或指针包装函数。");
        if (!method.GetCustomAttributesData().Any(attribute => attribute.AttributeType.Name is "MemberFunctionAttribute" or "StaticAddressAttribute"))
            throw new ArgumentException("该成员没有 SDK 原生入口声明。");
        var addressType = type.GetNestedType("Addresses", BindingFlags.Public | BindingFlags.NonPublic);
        var addressObject = addressType?.GetField(method.Name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var valueMember = addressObject?.GetType().GetField("Value");
        var addressValue = valueMember?.GetValue(addressObject) ?? addressObject?.GetType().GetProperty("Value")?.GetValue(addressObject);
        var address = addressValue is nint pointer ? pointer : 0;
        var staticAddress = method.GetCustomAttributesData().Any(attribute => attribute.AttributeType.Name == "StaticAddressAttribute");
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var resolved = staticAddress ? module is not null && (nuint)address - (nuint)module.BaseAddress < (nuint)module.ModuleMemorySize && Readable(address, IntPtr.Size)
            : GameDiagnostics.IsResolvedGameCode(address);
        if (!resolved) throw new InvalidOperationException("SDK 原生入口不可用。");
        return (type, method, address);
    }

    internal (object Target, MemberInfo Member) ReferencedMember(ToolArguments input)
    {
        Expire();
        var id = Guid.ParseExact(input.Text("reference_id"), "D");
        if (!retained.TryGetValue(id, out var entry)) throw new InvalidOperationException("对象引用已过期。");
        var member = entry.Value.GetType().GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(member => ID(member) == input.Text("member_id")) ?? throw new ArgumentException("成员 ID 不属于该对象。");
        return (entry.Value, member);
    }

    public static object Memory(ToolArguments input, bool write, Func<bool> allowed)
    {
        var until = Environment.TickCount64 + 1000;
        if (!allowed()) throw new InvalidOperationException("内存访问权限已关闭。");
        var address = Address(input);
        var count = input.Integer("length", 64, 1, write ? 4096 : 65536);
        var end = checked(address + count);
        var bytes = new byte[count];
        var expected = write ? ReadHex("expected_hex") : null;
        var replacement = write ? ReadHex("hex") : null;
        var segments = new List<(int Offset, int Size)>();
        for (var cursor = address; cursor < end;)
        {
            if (!allowed()) return new { status = "permission_changed" };
            if (Environment.TickCount64 >= until) return new { status = "timeout" };
            if (!Query(cursor, out var region, out var regionEnd) || !ReadableRegion(region))
                return new { status = "unreadable", failedAddress = Hex(cursor) };
            if (write && (region.Protect & 0xff) is not (0x04 or 0x08))
                return new { status = "not_writable_data", failedAddress = Hex(cursor) };
            var offset = checked((int)(cursor - address));
            var size = Math.Min(checked((int)Math.Min(regionEnd - cursor, count)), Environment.SystemPageSize - (int)((nuint)cursor % (nuint)Environment.SystemPageSize));
            size = Math.Min(size, count - offset);
            if (!NativeMemoryReader.TryCopy(cursor, bytes.AsSpan(offset, size)))
                return new { status = "unreadable", failedAddress = Hex(cursor) };
            segments.Add((offset, size));
            cursor += size;
        }
        if (!write) return new { status = "ok", address = Hex(address), length = count, hex = Convert.ToHexString(bytes) };
        if (!bytes.AsSpan().SequenceEqual(expected)) return new { status = "value_changed" };

        var writtenRanges = new List<object>();
        var verifiedBytes = 0;
        foreach (var (offset, size) in segments)
        {
            var cursor = checked(address + offset);
            if (!allowed()) return Finish("permission_changed");
            if (Environment.TickCount64 >= until) return Finish("timeout");
            if (!Query(cursor, out var region, out var regionEnd) || !ReadableRegion(region) ||
                (region.Protect & 0xff) is not (0x04 or 0x08) || regionEnd - cursor < size) return Finish("not_writable_data");
            var part = bytes.AsSpan(offset, size);
            if (!NativeMemoryReader.TryCopy(cursor, part) || !part.SequenceEqual(expected.AsSpan(offset, size))) return Finish("value_changed");
            var desired = replacement.AsSpan(offset, size);
            var copied = WriteProcessMemory(new nint(-1), cursor, desired.ToArray(), (nuint)size, out var written);
            if (written > 0) writtenRanges.Add(new { address = Hex(cursor), bytes = checked((int)written) });
            if (!copied || written != (nuint)size || !NativeMemoryReader.TryCopy(cursor, part) || !part.SequenceEqual(desired))
                return Finish("write_unconfirmed");
            verifiedBytes += size;
        }
        return Finish("observed");

        byte[] ReadHex(string key)
        {
            var text = input.Element(key).GetString();
            if (text is null || text.Length != count * 2) throw new ArgumentException("写入和预期字节必须与 length 相符。");
            return Convert.FromHexString(text);
        }

        object Finish(string status) => new { status, succeeded = status == "observed", resultObserved = status == "observed",
            writtenRanges, verifiedBytes, atomicCompareExchange = false };
    }

    private object Enumerate(object target, ToolArguments input, Func<bool> allowed)
    {
        if (target is not IEnumerable source || target is string) throw new ArgumentException("对象不是可枚举集合。");
        var offset = input.Integer("offset", 0, 0, 10000);
        var limit = input.Integer("limit", 30, 1, 100);
        var fields = input.Has("fields") ? input.Element("fields").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray() : [];
        if (fields.Length > 16 || fields.Any(field => field.Length is < 1 or > 128)) throw new ArgumentException("投影最多指定 16 个成员名。");
        var filter = input.Has("filter_member") ? input.Text("filter_member") : null;
        if ((filter is not null) != input.Has("equals")) throw new ArgumentException("筛选须同时指定 filter_member 和 equals。");
        var rows = new List<object>();
        var index = 0;
        var complete = false;
        var until = Environment.TickCount64 + 1000;
        var enumerator = source.GetEnumerator();
        try
        {
            while (index < 10000 && rows.Count < limit && Environment.TickCount64 < until)
            {
                if (!allowed()) throw new InvalidOperationException("运行时访问权限已关闭。");
                if (!enumerator.MoveNext()) { complete = true; break; }
                var item = enumerator.Current;
                var current = index++;
                if (current < offset) continue;
                if (filter is not null && !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(ScalarMember(item, filter)), input.Element("equals"))) continue;
                var projection = fields.Length == 0 ? Output(item) : fields.ToDictionary(field => field, field => ScalarMember(item, field));
                rows.Add(new { index = current, value = projection });
            }
        }
        finally { (enumerator as IDisposable)?.Dispose(); }
        return new { status = "ok", rows, scanned = index, complete,
            nextOffset = !complete && index < 10000 ? (int?)index : null, truncated = index == 10000, collectionMayChange = true };

        static object? ScalarMember(object? item, string name)
        {
            if (item is null) return null;
            var members = PublicMembers(item.GetType()).Where(member => member.Name == name && member is FieldInfo or PropertyInfo).ToArray();
            if (members.Length != 1) throw new ArgumentException("投影成员不存在或不唯一。");
            var value = ReadMember(members[0], item);
            if (!IsScalar(value?.GetType())) throw new ArgumentException("投影和筛选只接受标量成员。");
            return value;
        }
    }

    private sealed class BorrowedStrings : IDisposable
    {
        private readonly List<GCHandle> handles = [];
        public nint Add(string text)
        {
            if (text.Length > 4096 || text.Contains('\0')) throw new ArgumentException("借用文本不能包含空字符或超过 4096 个字符。");
            var bytes = new UTF8Encoding(false, true).GetBytes(text + '\0');
            if (bytes.Length > 4097) throw new ArgumentException("借用文本的 UTF-8 长度超过 4096 字节。");
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            handles.Add(handle);
            return handle.AddrOfPinnedObject();
        }
        public void Dispose() { foreach (var handle in handles) handle.Free(); }
    }

    private static IEnumerable<Type> LoadedTypes()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic))
        {
            Type[] types;
            try { types = assembly.GetExportedTypes(); }
            catch (ReflectionTypeLoadException error) { types = error.Types.OfType<Type>().Where(type => type.IsVisible).ToArray(); }
            catch (NotSupportedException) { continue; }
            foreach (var type in types) yield return type;
        }
    }

    private static Type ResolveLoadedType(string id)
    {
        var matches = LoadedTypes().Where(type => ID(type) == id).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : throw new ArgumentException("已加载类型 ID 不存在或有歧义。");
    }

    private static Type ResolveType(ToolArguments input) => Sdk.GetType(input.Text("type_name"), false, false) ?? throw new ArgumentException("当前 SDK 中没有此完整类型名。");
    private static string ID(MemberInfo member) => $"{member.Module.ModuleVersionId:D}:{member.MetadataToken:X8}";
    private static IEnumerable<MemberInfo> PublicMembers(Type type) => type.GetMembers(BindingFlags.Public | BindingFlags.Instance)
        .Where(member => member is FieldInfo or PropertyInfo or MethodInfo or EventInfo);
    private static object? ReadMember(MemberInfo member, object? target) => member switch
    {
        FieldInfo field => field.GetValue(target),
        PropertyInfo property when property.GetIndexParameters().Length == 0 && property.GetMethod?.IsPublic == true => property.GetValue(target),
        _ => throw new ArgumentException("成员不可读取或需要索引。"),
    };
    private static bool IsScalar(Type? type) => type is null || type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid);
    private static object?[] ReadArguments(ToolArguments input, MethodInfo method, BorrowedStrings borrowed)
    {
        var values = input.Element("arguments");
        var parameters = method.GetParameters();
        if (values.ValueKind != JsonValueKind.Array || parameters.Length > 16 || values.GetArrayLength() != parameters.Length)
            throw new ArgumentException("arguments 必须与已发现的参数列表完全一致，最多 16 项。");
        return parameters.Select((parameter, index) => parameter.ParameterType.IsPointer && parameter.ParameterType.GetElementType() == typeof(byte) &&
            values[index].ValueKind == JsonValueKind.Object && values[index].TryGetProperty("utf8", out var text)
            ? (object)borrowed.Add(text.GetString() ?? throw new ArgumentException("utf8 不能为空。")) : ConvertArgument(values[index], parameter.ParameterType)).ToArray();
    }
    private static object? ConvertArgument(JsonElement value, Type type)
    {
        if (type.IsByRef || type.IsByRefLike || type.IsFunctionPointer || (!type.IsPointer && !IsScalar(type)))
            throw new ArgumentException("只支持标量和显式指针参数。");
        if (type.IsPointer || type == typeof(nint) || type == typeof(nuint))
        {
            var text = value.GetString() ?? throw new ArgumentException("指针必须为地址文本。");
            var address = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ulong.Parse(text.AsSpan(2), NumberStyles.HexNumber) : ulong.Parse(text, CultureInfo.InvariantCulture);
            return type == typeof(nuint) ? (object)checked((nuint)address) : checked((nint)address);
        }
        return value.Deserialize(type);
    }
    private object Output(object? value)
    {
        if (value is nint address) return new { address = $"0x{address:X}" };
        if (value is nuint unsignedAddress) return new { address = $"0x{unsignedAddress:X}" };
        if (value is long or ulong) return new { value = Convert.ToString(value, CultureInfo.InvariantCulture) };
        if (value is float single && !float.IsFinite(single)) return new { value = single.ToString(CultureInfo.InvariantCulture) };
        if (value is double number && !double.IsFinite(number)) return new { value = number.ToString(CultureInfo.InvariantCulture) };
        if (value is string text) return new { value = text[..Math.Min(4096, text.Length)], truncated = text.Length > 4096 };
        if (IsScalar(value?.GetType())) return new { value };
        if (retained.Count >= 128) return new { status = "reference_capacity" };
        var id = Guid.NewGuid();
        if (value is null) return new { value };
        retained.Add(id, new(value, Environment.TickCount64 + 120000));
        return new { referenceID = id, type = value.GetType().FullName, expiresInMs = 120000 };
    }
    private void Expire()
    {
        foreach (var id in retained.Where(row => row.Value.Until <= Environment.TickCount64).Select(row => row.Key).ToArray()) retained.Remove(id);
    }
    private static bool Readable(nint address, int count) => NativeMemoryReader.TryCopy(address, new byte[count]);
    private sealed record Retained(object Value, long Until);
    [StructLayout(LayoutKind.Sequential)] private struct Region
    {
        public nint BaseAddress; public nint AllocationBase; public uint AllocationProtect; public ushort PartitionID;
        public nuint Size; public uint State; public uint Protect; public uint Type;
    }
    [DllImport("kernel32.dll")] private static extern nuint VirtualQuery(nint address, out Region region, nuint length);
    [DllImport("kernel32.dll")] private static extern bool WriteProcessMemory(nint process, nint address, byte[] bytes, nuint size, out nuint written);
}
