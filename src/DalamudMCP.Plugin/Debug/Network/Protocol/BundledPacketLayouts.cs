using System.Security.Cryptography;
using System.Text.Json;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace DalamudMCP.Plugin.Tools;

internal static class BundledPacketLayouts
{
    private static readonly Lazy<Snapshot> Current = new(Load);
    public static PacketLayoutCatalog.Layout[] Entries => Current.Value.Layouts;
    public static JsonElement Provenance => Current.Value.Document.GetProperty("provenance");

    public static object Catalog => new
    {
        source = "bundled", snapshotSha256 = Current.Value.Sha256, packetCount = Entries.Length,
        structureCount = Current.Value.Document.GetProperty("structures").GetArrayLength(),
        aliasCount = Current.Value.Document.GetProperty("aliases").GetArrayLength(),
        selectableZonePackets = Entries.Count(entry => entry.Channel == "zone" && entry.Direction is "send" or "receive"),
        provenance = Provenance, sdkMvid = typeof(Framework).Module.ModuleVersionId, compatibilityVerified = false,
    };

    public static object ReadStructures(ToolArguments input)
    {
        var query = input.Has("name") ? input.Text("name") : null;
        var rows = Current.Value.Document.GetProperty("structures").EnumerateArray()
            .Where(row => query is null || (row.GetProperty("id").GetString() ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var offset = input.Integer("offset", 0, 0, 1024);
        var limit = input.Integer("limit", 20, 1, 32);
        return new { status = "ok", catalog = Catalog, total = rows.Length,
            structures = rows.Skip(offset).Take(limit).Select(row => new
            {
                id = row.GetProperty("id"), name = row.GetProperty("name"), minimumBytes = row.GetProperty("size"),
                definition = input.Boolean("include_fields") ? (JsonElement?)row : null,
            }).ToArray(), nextOffset = offset + limit < rows.Length ? (int?)offset + limit : null };
    }

    public static object ReadAliases(ToolArguments input)
    {
        var query = input.Has("name") ? input.Text("name") : null;
        var direction = input.Has("direction") ? input.Text("direction") : null;
        var channel = input.Has("channel") ? input.Text("channel") : null;
        if (direction is not (null or "send" or "receive") || channel is not (null or "zone" or "lobby" or "chat"))
            throw new ArgumentException("方向或通道无效。");
        var rows = Current.Value.Document.GetProperty("aliases").EnumerateArray().Where(row =>
            (direction is null || row.GetProperty("direction").GetString() == direction) &&
            (channel is null || row.GetProperty("channel").GetString() == channel) &&
            (query is null || (row.GetProperty("name").GetString() ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.GetProperty("providers").EnumerateObject().Any(alias => (alias.Value.GetString() ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase))))
            .ToArray();
        var offset = input.Integer("offset", 0, 0, 1024);
        var limit = input.Integer("limit", 30, 1, 100);
        return new { status = "ok", total = rows.Length, aliases = rows.Skip(offset).Take(limit).ToArray(),
            nextOffset = offset + limit < rows.Length ? (int?)offset + limit : null,
            source = Provenance.GetProperty("aliasSource"), activeProvider = "FFXIVOpcodes", compatibilityVerified = false };
    }

    public static SdkReview CompareSdk(PacketLayoutCatalog.Layout layout)
    {
        var sdk = SdkPacketLayouts.Entries.Value.Where(entry => entry.Name == layout.Name + "Packet").ToArray();
        var mvid = typeof(Framework).Module.ModuleVersionId;
        if (sdk.Length != 1) return new("no_exact_sdk_declaration", mvid, null, [], 0);
        var differences = new List<object>();
        var compared = 0;
        foreach (var field in layout.Fields.EnumerateArray())
        {
            var type = field.GetProperty("type").GetString();
            if (type is "object" or "bytes" or "utf8" or "bitset" || field.TryGetProperty("count", out _)) continue;
            var offset = field.GetProperty("offset").GetInt32();
            var matches = sdk[0].Fields.EnumerateArray().Where(candidate => candidate.GetProperty("offset").GetInt32() == offset &&
                candidate.GetProperty("type").GetString() != "object" && !candidate.TryGetProperty("count", out _)).ToArray();
            if (matches.Length != 1) continue;
            compared++;
            var declared = matches[0].GetProperty("type").GetString();
            if (declared != type) differences.Add(new { offset, bundledField = field.GetProperty("name"), bundledType = type,
                sdkField = matches[0].GetProperty("name"), sdkType = declared });
        }
        var status = differences.Count == 0 ? "no_difference_in_compared_fields" : "declaration_difference";
        if (compared == 0) status = "no_comparable_scalar_fields";
        return new(status, mvid, sdk[0].Name,
            differences.ToArray(), compared);
    }

    private static Snapshot Load()
    {
        using var stream = typeof(BundledPacketLayouts).Assembly.GetManifestResourceStream("DalamudMCP.PacketLayouts")
            ?? throw new InvalidOperationException("缺少内置协议定义。");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        if (memory.Length > 4 * 1024 * 1024) throw new InvalidDataException("内置协议定义过大。");
        var bytes = memory.ToArray();
        using var parsed = JsonDocument.Parse(bytes, new() { MaxDepth = 48 });
        var document = parsed.RootElement.Clone();
        if (document.GetProperty("formatVersion").GetInt32() != 1) throw new InvalidDataException("内置协议格式不兼容。");
        var version = document.GetProperty("provenance").GetProperty("packages")[0].GetProperty("version").GetString() ?? string.Empty;
        var layouts = document.GetProperty("packets").EnumerateArray().Select(row => new PacketLayoutCatalog.Layout(
            row.GetProperty("name").GetString() ?? throw new InvalidDataException("缺少包名。"),
            row.GetProperty("direction").GetString() ?? "unknown", version, "elpis-network", "GPL-3.0-only; AGPL-3.0 directory; MIT notices",
            row.GetProperty("opcodeNames").EnumerateArray().Select(name => name.GetString() ?? string.Empty).ToArray(),
            row.GetProperty("fields"), row.GetProperty("sha256").GetString() ?? string.Empty,
            Origin: "bundled", Channel: row.GetProperty("channel").GetString() ?? "unknown", MinimumBytes: row.GetProperty("minimumBytes").GetInt32())).ToArray();
        if (layouts.Length != document.GetProperty("provenance").GetProperty("expected").GetProperty("packets").GetInt32()
            || layouts.Select(row => row.Name).Distinct(StringComparer.Ordinal).Count() != layouts.Length)
            throw new InvalidDataException("内置协议目录不完整或存在重复。");
        return new(document, layouts, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    internal sealed record SdkReview(string Status, Guid SDKMVID, string? Type, object[] Differences, int ComparedFields)
    {
        public bool CompatibilityVerified => false;
        public string Scope => "same_name_declaration_top_level_scalars";
    }
    private sealed record Snapshot(JsonElement Document, PacketLayoutCatalog.Layout[] Layouts, string Sha256);
}
