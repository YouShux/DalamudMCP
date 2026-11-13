using System.Security.Cryptography;
using System.Text.Json;

namespace DalamudMCP.Plugin.Tools;

internal sealed class PacketLayoutCatalog(string directory)
{
    private readonly object sync = new();

    public object Read(ToolArguments input)
    {
        if (input.Text("kind", "packets") == "structures") return BundledPacketLayouts.ReadStructures(input);
        if (input.Text("kind", "packets") != "packets") throw new ArgumentException("kind 必须为 packets 或 structures。");
        var origin = ReadOrigin(input);
        lock (sync)
        {
            var entries = All().Where(entry => origin == "all" || entry.Origin == origin).ToArray();
            var query = input.Has("name") ? input.Text("name") : null;
            var matches = entries.Where(entry => query is null || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            var offset = input.Integer("offset", 0, 0, 1024);
            var limit = input.Integer("limit", 20, 1, 32);
            var includeFields = input.Boolean("include_fields");
            return new { status = "ok", total = matches.Length, layouts = matches.Skip(offset).Take(limit).Select(entry => new
                { entry.Name, entry.Direction, entry.Version, entry.Source, entry.License, entry.OpcodeNames, entry.Sha256, entry.OmittedFields,
                    entry.Origin, entry.Channel, entry.MinimumBytes, selectable = entry.Channel == "zone" && entry.Direction is "send" or "receive",
                    sdkReview = entry.Origin == "bundled" ? BundledPacketLayouts.CompareSdk(entry) : null,
                    fields = includeFields ? (JsonElement?)entry.Fields : null }).ToArray(),
                nextOffset = offset + limit < matches.Length ? (int?)offset + limit : null,
                compatibilityVerified = false, catalog = BundledPacketLayouts.Catalog };
        }
    }

    public object Save(ToolArguments input, Func<bool> allowed)
    {
        var name = input.Text("name");
        if (name.Length > 128 || name.Any(char.IsControl)) throw new ArgumentException("布局名称无效。");
        var direction = input.Text("direction");
        if (direction is not ("send" or "receive")) throw new ArgumentException("布局方向必须为 send 或 receive。");
        var version = input.Text("version");
        var source = input.Text("source");
        var license = input.Text("license");
        if (version.Length > 128 || source.Length > 512 || license.Length > 128) throw new ArgumentException("来源说明过长。");
        var fields = input.Element("fields").Clone();
        _ = PacketFieldDecoder.Decode([], fields);
        var aliases = input.Element("opcode_names");
        if (aliases.ValueKind != JsonValueKind.Array || aliases.GetArrayLength() is < 1 or > 32) throw new ArgumentException("需要 1 至 32 个操作码名称。");
        var names = aliases.EnumerateArray().Select(item => item.GetString()).ToArray();
        if (names.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128) || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new ArgumentException("操作码名称无效或重复。");
        var content = JsonSerializer.SerializeToUtf8Bytes(new { name, direction, version, source, license, names, fields });
        var hash = Convert.ToHexString(SHA256.HashData(content));
        lock (sync)
        {
            var entries = Load().ToList();
            var old = entries.SingleOrDefault(entry => entry.Name == name && entry.Direction == direction);
            if (old is not null && (!input.Has("expected_sha256") || input.Text("expected_sha256") != old.Sha256))
                return new { status = "layout_changed", old.Sha256 };
            if (old is null && input.Has("expected_sha256")) return new { status = "layout_not_found" };
            if (old is not null) entries.Remove(old);
            if (entries.Count >= 256) throw new InvalidOperationException("布局最多保存 256 项。");
            entries.Add(new(name, direction, version, source, license, names.Select(value => value ?? string.Empty).ToArray(), fields, hash));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entries);
            if (bytes.Length > 4 * 1024 * 1024) throw new InvalidOperationException("布局目录超过 4 MiB。");
            var path = FilePath();
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
            var owned = false;
            try
            {
                if (!allowed()) throw new InvalidOperationException("布局管理权限已关闭。");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { owned = true; stream.Write(bytes); stream.Flush(true); }
                if (!allowed()) throw new InvalidOperationException("布局管理权限已关闭。");
                File.Move(temporary, path, true);
            }
            finally { if (owned && File.Exists(temporary)) File.Delete(temporary); }
            return new { status = "saved", sha256 = hash, compatibilityVerified = false };
        }
    }

    public Layout[] Select(string direction, string[] names, string? exact, string origin)
    {
        lock (sync) return All().Where(entry => entry.Direction == direction && entry.Channel == "zone" &&
            (origin == "all" || entry.Origin == origin) && (exact is not null ? entry.Name == exact : entry.OpcodeNames.Any(names.Contains))).ToArray();
    }

    internal static string ReadOrigin(ToolArguments input)
    {
        var origin = input.Text("layout_source", "all");
        return origin is "all" or "bundled" or "local" or "sdk" ? origin : throw new ArgumentException("布局来源无效。");
    }

    private IEnumerable<Layout> All() => Load().Concat(BundledPacketLayouts.Entries).Concat(SdkPacketLayouts.Entries.Value);

    private Layout[] Load()
    {
        var path = FilePath();
        var file = new FileInfo(path);
        if (!file.Exists) return [];
        if (file.Length > 4 * 1024 * 1024) throw new InvalidDataException("布局目录过大。");
        var rows = JsonSerializer.Deserialize<Layout[]>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("布局目录为空。");
        if (rows.Length > 256 || rows.Any(row => row is null || row.OpcodeNames is null || row.OpcodeNames.Length > 32 ||
            row.Direction is not ("send" or "receive") || string.IsNullOrWhiteSpace(row.Name))) throw new InvalidDataException("布局目录格式错误。");
        if (rows.Select(row => (row.Name, row.Direction)).Distinct().Count() != rows.Length) throw new InvalidDataException("布局名称和方向重复。");
        foreach (var row in rows)
        {
            if (row.Version is null || row.Source is null || row.License is null || row.Name.Length > 128 || row.Version.Length > 128
                || row.Source.Length > 512 || row.License.Length > 128 || row.OpcodeNames.Length == 0
                || row.OpcodeNames.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 128)) throw new InvalidDataException("布局来源和名称无效。");
            _ = PacketFieldDecoder.Decode([], row.Fields);
            var content = JsonSerializer.SerializeToUtf8Bytes(new { name = row.Name, direction = row.Direction, version = row.Version,
                source = row.Source, license = row.License, names = row.OpcodeNames, fields = row.Fields });
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(content)), row.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("布局指纹与内容不一致。");
        }
        return rows.Select(row => row with { Origin = "local", Channel = "zone", MinimumBytes = 0 }).ToArray();
    }

    private string FilePath()
    {
        var parent = new DirectoryInfo(directory);
        if (!parent.Exists || parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("布局目录不可用。");
        var path = Path.Combine(parent.FullName, "packet-layouts.json");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("布局文件不能是链接。");
        return path;
    }

    internal sealed record Layout(string Name, string Direction, string Version, string Source, string License,
        string[] OpcodeNames, JsonElement Fields, string Sha256, string[]? OmittedFields = null,
        string Origin = "local", string Channel = "zone", int MinimumBytes = 0);
}
