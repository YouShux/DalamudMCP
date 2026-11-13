using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace DalamudMCP.Plugin.Tools;

internal sealed class OpcodeCatalogCheck : IDisposable
{
    private readonly HttpClient client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    });
    private int checking;
    private IReadOnlyDictionary<string, Catalog> catalogs = new Dictionary<string, Catalog>(StringComparer.OrdinalIgnoreCase);
    private readonly string? cachePath;
    private readonly Task initialization;
    private readonly CancellationTokenSource lifetime = new();
    private object startup = new { status = "disabled" };
    public Catalog? Snapshot => Volatile.Read(ref catalogs).GetValueOrDefault("CN");
    public object Startup => Volatile.Read(ref startup);

    public OpcodeCatalogCheck(string? directory = null, bool checkOnLoad = false)
    {
        cachePath = directory is null ? null : Path.Combine(directory, "zone-opcodes.json");
        initialization = Task.Run(LoadLocal);
        if (checkOnLoad) _ = CheckOnLoadAsync();
    }

    private async Task CheckOnLoadAsync()
    {
        Volatile.Write(ref startup, new { status = "pending" });
        try
        {
            var input = new ToolArguments(JsonSerializer.SerializeToElement(new { }));
            var result = await CheckAsync(input, lifetime.Token).ConfigureAwait(false);
            if (!lifetime.IsCancellationRequested) Volatile.Write(ref startup, result);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Volatile.Write(ref startup, new { status = "failed", error = error.GetType().Name }); }
    }

    private void LoadLocal()
    {
        using (var embedded = typeof(OpcodeCatalogCheck).Assembly.GetManifestResourceStream("DalamudMCP.CnOpcodes"))
        {
            if (embedded is not null)
            {
                using var bytes = new MemoryStream();
                embedded.CopyTo(bytes);
                if (!lifetime.IsCancellationRequested) Volatile.Write(ref catalogs, ParseRegions(bytes.ToArray()));
            }
        }
        if (cachePath is null) return;
        try
        {
            var file = new FileInfo(cachePath);
            if (!file.Exists || file.Length > MAX_CATALOG_BYTES || file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                file.Directory is not { } parent || parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            var parsed = ParseRegions(File.ReadAllBytes(cachePath));
            var merged = new Dictionary<string, Catalog>(Volatile.Read(ref catalogs), StringComparer.OrdinalIgnoreCase);
            foreach (var pair in parsed) merged[pair.Key] = pair.Value;
            if (!lifetime.IsCancellationRequested) Volatile.Write(ref catalogs, merged);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException) { }
    }

    private string? SaveLocal(ReadOnlyMemory<byte> bytes, Func<bool> allowed, CancellationToken token)
    {
        if (cachePath is null) return null;
        var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".part";
        var owned = false;
        try
        {
            var parent = new DirectoryInfo(Path.GetDirectoryName(cachePath) ?? string.Empty);
            if (!parent.Exists || parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) return "invalid_directory";
            if (File.Exists(cachePath) && File.GetAttributes(cachePath).HasFlag(FileAttributes.ReparsePoint)) return "invalid_file";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                owned = true;
                stream.Write(bytes.Span);
                stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (!allowed()) return "permission_changed";
            File.Move(temporary, cachePath, true);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return error.GetType().Name; }
        finally { if (owned && File.Exists(temporary)) File.Delete(temporary); }
    }

    internal async Task<object> InspectAsync(ToolArguments input, CancellationToken token)
    {
        await initialization.WaitAsync(token).ConfigureAwait(false);
        var region = input.Text("region", "");
        var direction = input.Text("direction", "");
        if (region.Length > 32 || direction is not ("" or "send" or "receive")) throw new ArgumentException("地区或方向无效。");
        var opcode = input.Has("opcode") ? input.Integer("opcode", 0, 1, ushort.MaxValue) : (int?)null;
        var name = input.Text("name", "");
        if (name.Length > 128 || opcode.HasValue && name.Length != 0) throw new ArgumentException("opcode 和 name 只能选一个。");
        var offset = input.Integer("offset", 0, 0, 16384);
        var limit = input.Integer("limit", 30, 1, 100);
        var current = Volatile.Read(ref catalogs);
        var regions = current.Keys.Order(StringComparer.Ordinal).ToArray();
        if (region.Length == 0)
        {
            if (direction.Length != 0 || opcode.HasValue || name.Length != 0 || input.Has("offset") || input.Has("limit"))
                throw new ArgumentException("查询条目需要 region。");
            return new { status = "ok", regions };
        }
        if (!current.TryGetValue(region, out var catalog)) return new { status = "unavailable", reason = "region_not_present", regions };
        var rows = (direction == "receive" ? [] : catalog.Send.Select(row => new { direction = "send", row.Name, row.Opcode }))
            .Concat(direction == "send" ? [] : catalog.Receive.Select(row => new { direction = "receive", row.Name, row.Opcode }))
            .Where(row => (!opcode.HasValue || row.Opcode == opcode) && row.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
        var page = rows.Skip(offset).Take(limit).ToArray();
        return new { status = "ok", region = regions.First(key => key.Equals(region, StringComparison.OrdinalIgnoreCase)), catalog.Version,
            catalog.Sha256, rows = page, matched = rows.Length, nextOffset = page.Length > 0 && offset + page.Length < rows.Length ?
                (int?)offset + page.Length : null, clientCompatibilityVerified = false };
    }

    public async Task<object> CheckAsync(ToolArguments input, CancellationToken token, bool lookup = false, Func<bool>? persist = null)
    {
        await initialization.WaitAsync(token).ConfigureAwait(false);
        var baseline = input.Has("baseline_sha256") ? input.Text("baseline_sha256") : null;
        if (baseline is not null && (baseline.Length != 64 || !baseline.All(char.IsAsciiHexDigit)))
            throw new ArgumentException("baseline_sha256 必须为 64 位十六进制文本。");
        var direction = lookup ? input.Text("direction") : null;
        if (lookup && direction is not ("send" or "receive")) throw new ArgumentException("方向必须为 send 或 receive。");
        var opcode = input.Has("opcode") ? input.Integer("opcode", 0, 1, ushort.MaxValue) : (int?)null;
        var query = input.Has("name") ? input.Text("name") : null;
        if (query is { Length: > 128 } || opcode.HasValue && query is not null)
            throw new ArgumentException("opcode 和 name 只能选一个，名称最多 128 个字符。");
        var offset = input.Integer("offset", 0, 0, 8192);
        var limit = input.Integer("limit", 30, 1, 100);
        if (lookup) return Snapshot is { } current ? Lookup(current) : new { status = "unavailable", reason = "no_cached_catalog" };
        if (Interlocked.CompareExchange(ref checking, 1, 0) != 0) return new { status = "busy" };
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await client.GetAsync(SOURCE_URL, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MAX_CATALOG_BYTES) return new { status = "invalid_catalog" };
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var bytes = new byte[MAX_CATALOG_BYTES + 1];
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, deadline.Token).ConfigureAwait(false);
            if (count > MAX_CATALOG_BYTES) return new { status = "invalid_catalog" };
            var parsed = ParseRegions(bytes.AsMemory(0, count));
            if (!parsed.TryGetValue("CN", out var snapshot)) return new { status = "invalid_catalog" };
            deadline.Token.ThrowIfCancellationRequested();
            string? storageError = null;
            if (persist is not null)
            {
                if (cachePath is null) return new { status = "unavailable", reason = "catalog_cache_unconfigured" };
                storageError = SaveLocal(bytes.AsMemory(0, count), persist, deadline.Token);
                if (storageError is not null) return new { status = storageError == "permission_changed" ? storageError : "storage_failed", storageError };
            }
            if (!lifetime.IsCancellationRequested) Volatile.Write(ref catalogs, parsed);
            return new
            {
                status = baseline is null ? "baseline" : snapshot.Sha256.Equals(baseline, StringComparison.OrdinalIgnoreCase) ? "unchanged" : "changed",
                source = "FFXIVOpcodes", region = "CN", storageError, persisted = persist is not null, regions = parsed.Keys.Order(StringComparer.Ordinal).ToArray(),
                snapshot.Version, snapshot.Sha256, sendCount = snapshot.Send.Length, receiveCount = snapshot.Receive.Length,
                clientCompatibilityVerified = false,
            };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !lifetime.IsCancellationRequested) { return new { status = "timeout" }; }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        { return new { status = "invalid_catalog" }; }
        catch (Exception error) when (error is HttpRequestException or IOException) { return new { status = "source_unavailable" }; }
        finally { Volatile.Write(ref checking, 0); }

        object Lookup(Catalog snapshot)
        {
            var entries = (direction == "send" ? snapshot.Send : snapshot.Receive).Where(entry =>
                (opcode is null || entry.Opcode == opcode) && (query is null || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))).Distinct().ToArray();
            var rows = entries.Skip(offset).Take(limit).ToArray();
            return new
            {
                status = entries.Length == 0 ? "not_found" : opcode.HasValue && entries.Length > 1 ? "ambiguous" : "ok",
                source = "FFXIVOpcodes", region = "CN", direction, snapshot.Version, snapshot.Sha256, rows, matched = entries.Length,
                nextOffset = offset + rows.Length < entries.Length ? (int?)offset + rows.Length : null, clientCompatibilityVerified = false,
            };
        }
    }

    internal static Catalog Parse(ReadOnlyMemory<byte> bytes)
    {
        return ParseRegions(bytes, onlyCn: true).GetValueOrDefault("CN") ?? throw new InvalidDataException("操作码表缺少 CN 区域。");
    }

    internal static IReadOnlyDictionary<string, Catalog> ParseRegions(ReadOnlyMemory<byte> bytes, bool onlyCn = false)
    {
        using var json = JsonDocument.Parse(bytes);
        if (json.RootElement.GetArrayLength() is < 1 or > 16) throw new InvalidDataException("操作码表地区数量无效。");
        var regions = new Dictionary<string, Catalog>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in json.RootElement.EnumerateArray())
        {
            var region = row.GetProperty("region").GetString();
            if (onlyCn && region != "CN") continue;
            var version = row.GetProperty("version").GetString();
            if (string.IsNullOrWhiteSpace(region) || region.Length > 32 || !region.All(char.IsAsciiLetterOrDigit) ||
                string.IsNullOrWhiteSpace(version) || version.Length > 32) throw new InvalidDataException("操作码表地区或版本无效。");
            var lists = row.GetProperty("lists");
            var send = ReadList(lists.GetProperty("ClientZoneIpcType"));
            var receive = ReadList(lists.GetProperty("ServerZoneIpcType"));
            var sha = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { version, send, receive })));
            if (!regions.TryAdd(region, new(version, sha, send, receive))) throw new InvalidDataException("操作码表地区重复。");
        }
        return regions;
    }

    public void Dispose()
    {
        lifetime.Cancel();
        client.Dispose();
    }

    private static Entry[] ReadList(JsonElement list)
    {
        if (list.GetArrayLength() is < 1 or > 8192) throw new InvalidDataException("操作码条目数量无效。");
        return list.EnumerateArray().Select(row =>
        {
            var name = row.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || !row.GetProperty("opcode").TryGetUInt16(out var opcode) || opcode == 0)
                throw new InvalidDataException("操作码条目无效。");
            return new Entry(name, opcode);
        }).OrderBy(row => row.Name, StringComparer.Ordinal).ThenBy(row => row.Opcode).ToArray();
    }

    internal sealed record Catalog(string Version, string Sha256, Entry[] Send, Entry[] Receive);
    internal sealed record Entry(string Name, ushort Opcode);

    private const string SOURCE_URL = "https://raw.githubusercontent.com/karashiiro/FFXIVOpcodes/master/opcodes.min.json";
    private const int MAX_CATALOG_BYTES = 1024 * 1024;
}
