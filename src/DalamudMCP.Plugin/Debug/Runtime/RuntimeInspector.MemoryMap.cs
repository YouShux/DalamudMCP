using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class RuntimeInspector
{
    private const int MAX_SCAN_BYTES = 16 * 1024 * 1024;

    internal static object MemoryMap(string tool, ToolArguments input, Func<bool> allowed, CancellationToken token, long until)
    {
        if (!allowed()) return new { status = "permission_changed" };
        token.ThrowIfCancellationRequested();
        if (Environment.TickCount64 >= until) return new { status = "timeout" };
        return tool switch
        {
            "inspect_address_regions" => AddressRegions(input, allowed, token, until),
            "follow_memory_offsets" => FollowOffsets(input, allowed, token, until),
            "inspect_process_module" => ReadModule(input),
            "scan_process_memory" => ScanRange(input, allowed, token, until),
            _ => throw new ArgumentException("未知的内存诊断工具。"),
        };
    }

    private static object AddressRegions(ToolArguments input, Func<bool> allowed, CancellationToken token, long until)
    {
        var start = Address(input);
        var length = input.Integer("length", 4096, 1, int.MaxValue);
        var end = checked(start + length);
        var cursor = start;
        var rows = new List<object>();
        var status = "ok";
        while (cursor < end && rows.Count < 64)
        {
            token.ThrowIfCancellationRequested();
            if (!allowed()) { status = "permission_changed"; break; }
            if (Environment.TickCount64 >= until) { status = "timeout"; break; }
            if (!Query(cursor, out var region, out var regionEnd)) { status = "partial"; break; }
            var next = regionEnd < end ? regionEnd : end;
            var readable = ReadableRegion(region);
            rows.Add(new
            {
                address = Hex(cursor), endExclusive = Hex(next), regionBase = Hex(region.BaseAddress),
                regionEndExclusive = Hex(regionEnd), length = checked((long)(next - cursor)),
                committed = region.State == 0x1000, state = $"0x{region.State:X}", protection = $"0x{region.Protect:X}",
                readable, writable = Accessible(region) && (region.Protect & 0xff) is (0x04 or 0x08 or 0x40 or 0x80),
                executable = Accessible(region) && (region.Protect & 0xff) is (0x10 or 0x20 or 0x40 or 0x80),
                reason = readable ? null : region.State != 0x1000 ? "not_committed" :
                    (region.Protect & 0x100) != 0 ? "guard_page" : "protection_disallows_read",
            });
            cursor = next;
        }
        return new { status = cursor < end && status == "ok" ? "partial" : status, rows, complete = cursor == end,
            nextAddress = cursor < end ? Hex(cursor) : null };
    }

    private static object FollowOffsets(ToolArguments input, Func<bool> allowed, CancellationToken token, long until)
    {
        var cursor = Address(input);
        var sequence = input.Element("offsets");
        if (sequence.ValueKind != System.Text.Json.JsonValueKind.Array || sequence.GetArrayLength() is < 1 or > 16)
            throw new ArgumentException("offsets 必须包含 1 至 16 个有符号偏移。");
        var offsets = sequence.EnumerateArray().Select(offset => offset.GetInt64()).ToArray();
        var type = input.Text("value_type", "");
        var size = type switch
        {
            "" => 0, "u8" or "i8" => 1, "u16" or "i16" => 2, "u32" or "i32" or "f32" => 4,
            "u64" or "i64" or "f64" => 8, "pointer" => IntPtr.Size, _ => throw new ArgumentException("无效的 value_type。"),
        };
        var steps = new List<object>();
        var seen = new HashSet<nint> { cursor };
        Span<byte> buffer = stackalloc byte[8];
        foreach (var offset in offsets)
        {
            if (Stop() is { } stop) return Failure(stop);
            if (!NativeMemoryReader.TryCopy(cursor, buffer[..IntPtr.Size])) return Failure("unreadable");
            var pointer = IntPtr.Size == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(buffer) : BinaryPrimitives.ReadUInt32LittleEndian(buffer);
            if (pointer == 0) return Failure("null_pointer");
            nint next;
            try { next = checked(checked((nint)pointer) + checked((nint)offset)); }
            catch (OverflowException) { return Failure("address_overflow"); }
            if (next <= 0) return Failure("invalid_address");
            steps.Add(new { address = Hex(cursor), pointer = $"0x{pointer:X}", offset, nextAddress = Hex(next) });
            cursor = next;
            if (!seen.Add(next)) return Failure("cycle", steps.Count - 1);
        }
        string? value = null;
        if (size != 0)
        {
            if (Stop() is { } stop) return Failure(stop);
            if (!NativeMemoryReader.TryCopy(cursor, buffer[..size])) return Failure("final_value_unreadable");
            value = type switch
            {
                "pointer" => $"0x{(IntPtr.Size == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(buffer) : BinaryPrimitives.ReadUInt32LittleEndian(buffer)):X}",
                "u8" => buffer[0].ToString(CultureInfo.InvariantCulture), "i8" => unchecked((sbyte)buffer[0]).ToString(CultureInfo.InvariantCulture),
                "u16" => BinaryPrimitives.ReadUInt16LittleEndian(buffer).ToString(CultureInfo.InvariantCulture),
                "i16" => BinaryPrimitives.ReadInt16LittleEndian(buffer).ToString(CultureInfo.InvariantCulture),
                "u32" => BinaryPrimitives.ReadUInt32LittleEndian(buffer).ToString(CultureInfo.InvariantCulture),
                "i32" => BinaryPrimitives.ReadInt32LittleEndian(buffer).ToString(CultureInfo.InvariantCulture),
                "u64" => BinaryPrimitives.ReadUInt64LittleEndian(buffer).ToString(CultureInfo.InvariantCulture),
                "i64" => BinaryPrimitives.ReadInt64LittleEndian(buffer).ToString(CultureInfo.InvariantCulture),
                "f32" => BinaryPrimitives.ReadSingleLittleEndian(buffer).ToString("R", CultureInfo.InvariantCulture),
                _ => BinaryPrimitives.ReadDoubleLittleEndian(buffer).ToString("R", CultureInfo.InvariantCulture),
            };
        }
        return new { status = "ok", steps, finalAddress = Hex(cursor), value, valueType = size == 0 ? null : type, pointerWidth = IntPtr.Size };

        string? Stop()
        {
            token.ThrowIfCancellationRequested();
            return !allowed() ? "permission_changed" : Environment.TickCount64 >= until ? "timeout" : null;
        }
        object Failure(string status, int? failedStep = null) => new { status, steps, failedStep = failedStep ?? steps.Count, failedAddress = Hex(cursor) };
    }

    private static object ReadModule(ToolArguments input)
    {
        var module = Module(input.Text("module_name", ""));
        return module is null ? new { status = "unavailable", reason = "module_not_loaded" } : new
        {
            status = module.Sections is null ? "partial" : "ok", module.Name, module.Path, baseAddress = Hex(module.Base),
            size = module.Size, baseSource = "current_process_module", sectionSource = module.Sections is null ? null : "disk_pe_headers",
            reason = module.Sections is null ? "disk_pe_unverified" : null,
            sections = module.Sections?.Select(section => new { section.Name, address = Hex(section.Start), section.Size, section.Rva, section.FileOffset, section.FileBytes }).ToArray(),
        };
    }

    private static object ScanRange(ToolArguments input, Func<bool> allowed, CancellationToken token, long until)
    {
        var explicitRange = input.Has("address") || input.Has("length");
        var moduleRange = input.Has("module_name");
        if (explicitRange == moduleRange || explicitRange && (!input.Has("address") || !input.Has("length") || input.Has("section")))
            throw new ArgumentException("请提供 address 和 length，或 module_name 及可选 section。");
        var tokens = input.Text("pattern").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 1 or > 128 || tokens.Any(value => value.Length != 2) || tokens.All(value => value == "??"))
            throw new ArgumentException("模式需要 1 至 128 字节且不能全为通配符。");
        var pattern = tokens.Select(value => value == "??" ? (byte?)null : byte.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        var fixedBytes = pattern.Select((value, index) => (value, index)).Where(pair => pair.value.HasValue).ToArray();
        nint address;
        int length;
        if (explicitRange)
        {
            address = Address(input);
            length = input.Integer("length", 0, 1, int.MaxValue);
        }
        else
        {
            var module = Module(input.Text("module_name"));
            if (module is null) return new { status = "unavailable", reason = "module_not_loaded", complete = false };
            address = module.Base;
            length = module.Size;
            if (input.Has("section"))
            {
                var name = input.Text("section");
                if (module.Sections is null) return new { status = "unavailable", reason = "disk_pe_unverified", complete = false };
                var sections = module.Sections.Where(section => section.Name == name).ToArray();
                if (sections.Length != 1) throw new ArgumentException("PE 节不存在或有歧义。");
                address = sections[0].Start;
                length = sections[0].Size;
            }
        }
        _ = checked(address + length);
        var planned = Math.Min(length, MAX_SCAN_BYTES);
        var pageSize = Environment.SystemPageSize;
        var block = new byte[pageSize + pattern.Length - 1];
        var matches = new List<string>();
        var holes = new List<object>();
        var offset = 0;
        var retained = 0;
        var readBytes = 0;
        var missingBytes = 0;
        var status = "ok";
        nint? resume = null;
        while (offset < planned)
        {
            token.ThrowIfCancellationRequested();
            if (!allowed()) { status = "permission_changed"; break; }
            if (Environment.TickCount64 >= until) { status = "timeout"; break; }
            var cursor = checked(address + offset);
            var count = Math.Min(pageSize - (int)((nuint)cursor % (nuint)pageSize), planned - offset);
            if (!Query(cursor, out var region, out var end) || !ReadableRegion(region) ||
                end - cursor < count || !NativeMemoryReader.TryCopy(cursor, block.AsSpan(retained, count)))
            {
                missingBytes += count;
                if (holes.Count < 64) holes.Add(new { address = Hex(cursor), length = count });
                retained = 0;
                offset += count;
                continue;
            }
            readBytes += count;
            var available = count + retained;
            for (var index = 0; index <= available - pattern.Length; index++)
            {
                var equal = true;
                foreach (var (value, position) in fixedBytes)
                    if (block[index + position] != value) { equal = false; break; }
                if (!equal) continue;
                matches.Add(Hex(checked(cursor - retained + index)));
                if (matches.Count < 4096) continue;
                resume = checked(cursor - retained + index + 1);
                break;
            }
            if (resume.HasValue) break;
            retained = Math.Min(available, pattern.Length - 1);
            block.AsSpan(available - retained, retained).CopyTo(block);
            offset += count;
        }
        if (!resume.HasValue && offset < length) resume = checked(address + Math.Max(0, offset - retained));
        var complete = missingBytes == 0 && resume is null;
        return new
        {
            status = status == "ok" && !complete ? "partial" : status, matches, complete,
            incomplete = !complete, truncated = matches.Count == 4096 || planned < length,
            requestedBytes = length, readBytes, missingBytes, unreadRanges = holes, unreadRangesTruncated = missingBytes > 0 && holes.Count == 64,
            nextAddress = resume.HasValue ? Hex(resume.Value) : null, endExclusive = Hex(checked(address + length)),
        };
    }

    private static ModuleMap? Module(string name)
    {
        if (name.Length > 128 || name.IndexOfAny(['/', '\\', ':']) >= 0) throw new ArgumentException("module_name 必须为已加载模块的文件名。");
        using var process = Process.GetCurrentProcess();
        if (name.Length == 0)
        {
            using var main = process.MainModule;
            return main is null ? null : DescribeModule(main);
        }
        var modules = process.Modules.Cast<ProcessModule>().ToArray();
        try
        {
            var matching = modules.Where(module => module.ModuleName.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matching.Length > 1) throw new ArgumentException("模块名称不唯一。");
            return matching.Length == 1 ? DescribeModule(matching[0]) : null;
        }
        finally { foreach (var module in modules) module.Dispose(); }
    }

    private static ModuleMap DescribeModule(ProcessModule module)
    {
        var address = module.BaseAddress;
        var size = module.ModuleMemorySize;
        PeSection[]? sections = null;
        try
        {
            using var stream = File.Open(module.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(stream);
            Span<byte> header = stackalloc byte[64];
            if (pe.PEHeaders.PEHeader?.SizeOfImage == size && NativeMemoryReader.TryCopy(address, header) &&
                BinaryPrimitives.ReadUInt16LittleEndian(header) == 0x5A4D)
            {
                var offset = BinaryPrimitives.ReadInt32LittleEndian(header[60..]);
                if (offset is >= 64 and <= 65536 && offset <= size - header.Length &&
                    NativeMemoryReader.TryCopy(checked(address + offset), header) && BinaryPrimitives.ReadUInt32LittleEndian(header) == 0x4550 &&
                    BinaryPrimitives.ReadInt32LittleEndian(header[8..]) == pe.PEHeaders.CoffHeader.TimeDateStamp)
                    sections = pe.PEHeaders.SectionHeaders.Where(section => section.VirtualAddress >= 0 && section.VirtualSize > 0 &&
                        section.VirtualAddress <= size - section.VirtualSize).Select(section => new PeSection(section.Name,
                            checked(address + section.VirtualAddress), section.VirtualSize, section.VirtualAddress, section.PointerToRawData,
                            section.SizeOfRawData)).ToArray();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException) { }
        return new(module.ModuleName, module.FileName, address, size, sections);
    }

    private static nint Address(ToolArguments input)
    {
        var address = checked((nint)input.ObjectID("address"));
        return address > 0 ? address : throw new ArgumentException("地址必须大于零。");
    }

    private static bool Query(nint address, out Region region, out nint end)
    {
        end = 0;
        if (VirtualQuery(address, out region, (nuint)Marshal.SizeOf<Region>()) == 0 || region.BaseAddress < 0 ||
            region.BaseAddress > address || region.Size > (nuint)nint.MaxValue - (nuint)region.BaseAddress) return false;
        end = checked(region.BaseAddress + (nint)region.Size);
        return end > address;
    }

    private static bool Accessible(Region region) => region.State == 0x1000 && (region.Protect & 0x100) == 0;
    private static bool ReadableRegion(Region region) => Accessible(region) && (region.Protect & 0xff) is 0x02 or 0x04 or 0x08 or 0x20 or 0x40 or 0x80;
    private static string Hex(nint value) => $"0x{value:X}";
    private sealed record ModuleMap(string Name, string Path, nint Base, int Size, PeSection[]? Sections);
    private sealed record PeSection(string Name, nint Start, int Size, int Rva, int FileOffset, int FileBytes);
}
