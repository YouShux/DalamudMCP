using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DalamudMCP.Plugin.Tools;

internal sealed class PacketSourceUpdates : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly HttpClient client;
    private readonly object publication = new();
    private readonly Action<string>? log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly bool enabled;
    private UpdateState state;
    private Result? cached;
    private long expires;

    public PacketSourceUpdates(bool checkOnLoad, Action<string>? log = null, HttpMessageHandler? handler = null)
    {
        this.log = log;
        client = new(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All,
        }) { Timeout = Timeout.InfiniteTimeSpan };
        enabled = checkOnLoad;
        state = new(checkOnLoad ? "pending" : "disabled", checkOnLoad ? "startup" : null, null, null, null, null);
        if (checkOnLoad) _ = Task.Run(() => CheckAsync(false, lifetime.Token, "startup"));
    }

    internal object Inspect()
    {
        var current = Volatile.Read(ref state);
        var result = current.Result;
        var packages = result?.Sources.Where(source => source.Name is "network_package" or "struct_package")
            .Select(source => source.Data as PackageVersion).OfType<PackageVersion>().ToArray();
        var comparison = packages is { Length: 2 } ? CompareVersion(packages[0].LatestVersion, packages[1].LatestVersion) : null;
        var references = result?.Sources.Where(source => source.Name is "structure_source" or "alias_directory").ToArray();
        bool? referenceChanged = references?.Any(source => Changed(source.Data) == true) == true ? true :
            references is { Length: 2 } && references.All(source => Changed(source.Data) == false) ? false : null;
        return new
        {
            status = "ok", enabled, current.Phase, current.Trigger, current.StartedAt, current.FinishedAt, current.Error,
            sources = result?.Sources, sourcesComplete = current.Phase == "finished" && current.Error is null && result?.Status == "complete",
            checkFinished = current.Phase == "finished", usingPriorResult = result is not null && (current.Phase == "running" || current.Error is not null),
            dataAgeSeconds = result is null ? (double?)null : Math.Max(0, (DateTimeOffset.UtcNow - result.CheckedAt).TotalSeconds),
            packageVersionsAligned = comparison.HasValue ? comparison == 0 : (bool?)null,
            comparisonStatus = comparison.HasValue ? "semver_core" : "unknown", referenceChanged,
            referenceComparisonStatus = referenceChanged.HasValue ? "available" : "unknown", compatibilityVerified = false,
        };

        static bool? Changed(object? data) => data is null ? null :
            JsonSerializer.SerializeToElement(data).TryGetProperty("changed", out var changed) ? changed.GetBoolean() : null;
    }

    public async Task<object> CheckAsync(bool force, CancellationToken cancellationToken, string trigger = "manual")
    {
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new { status = "busy", applied = false };
        var started = DateTimeOffset.UtcNow;
        try
        {
            if (!force && cached is not null && Environment.TickCount64 < expires)
                return new { status = cached.Status, fromCache = true, result = cached };
            Publish(new("running", trigger, started, null, cached, null));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var token = deadline.Token;
            var baseline = BundledPacketLayouts.Provenance;
            var tasks = new[]
            {
                Observe("network_package", () => Package("elpis-network", baseline.GetProperty("packages")[0], token)),
                Observe("struct_package", () => Package("elpis-struct", baseline.GetProperty("packages")[1], token)),
                Observe("structure_source", async () =>
                {
                    var bytes = await Fetch("https://codeload.github.com/thewakingsands/elpis/zip/refs/heads/main", 4 * 1024 * 1024, token).ConfigureAwait(false);
                    var digest = SourceDigest(bytes, token);
                    var source = baseline.GetProperty("structureSource");
                    return new { contentSha256 = digest, baseline = source, changed = digest != source.GetProperty("contentSha256").GetString(), publishedPackageChanged = (bool?)null };
                }),
                Observe("alias_directory", async () =>
                {
                    var bytes = await Fetch("https://raw.githubusercontent.com/zhyupe/ffxiv-opcode-worker/master/packets.yaml", 1024 * 1024, token).ConfigureAwait(false);
                    var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    var source = baseline.GetProperty("aliasSource");
                    return new { sha256 = digest, baseline = source, changed = digest != source.GetProperty("sha256").GetString() };
                }),
            };
            var sources = await Task.WhenAll(tasks).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lifetime.Token.ThrowIfCancellationRequested();
            var succeeded = sources.Count(source => source.Status == "ok");
            cached = new(succeeded == 4 ? "complete" : succeeded == 0 ? "failed" : "partial", DateTimeOffset.UtcNow, sources);
            expires = Environment.TickCount64 + (succeeded == 4 ? 300000 : 30000);
            Publish(new("finished", trigger, started, cached.CheckedAt, cached, null));
            return new { status = cached.Status, fromCache = false, result = cached };
        }
        catch (OperationCanceledException)
        {
            Publish(new("finished", trigger, started, DateTimeOffset.UtcNow, cached, "cancelled"));
            return new { status = "cancelled", applied = false };
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Publish(new("finished", trigger, started, DateTimeOffset.UtcNow, cached, error.GetType().Name));
            return new { status = "failed", error = error.Message, applied = false };
        }
        finally { gate.Release(); }
    }

    private async Task<object> Package(string suffix, JsonElement baseline, CancellationToken token)
    {
        var bytes = await Fetch($"https://registry.npmjs.org/@ffcafe/{suffix}/latest", 1024 * 1024, token).ConfigureAwait(false);
        using var parsed = JsonDocument.Parse(bytes);
        var root = parsed.RootElement;
        if (root.GetProperty("name").GetString() != "@ffcafe/" + suffix) throw new InvalidDataException("发布包名称不匹配。");
        var latest = root.GetProperty("version").GetString() ?? throw new InvalidDataException("发布包缺少版本。");
        var integrity = root.GetProperty("dist").GetProperty("integrity").GetString() ?? string.Empty;
        if (latest.Length > 128 || !integrity.StartsWith("sha512-", StringComparison.Ordinal) || integrity.Length > 128
            || Convert.FromBase64String(integrity[7..]).Length != 64) throw new InvalidDataException("发布包元数据无效。");
        var installed = baseline.GetProperty("version").GetString();
        var comparison = CompareVersion(latest, installed);
        var expectedIntegrity = baseline.GetProperty("integrity").GetString();
        return new PackageVersion(installed, latest, latest != installed, comparison.HasValue ? "semver_core" : "unavailable",
            comparison.HasValue ? comparison > 0 : null, integrity, expectedIntegrity,
            latest == installed ? integrity != expectedIntegrity : null);
    }

    private async Task<byte[]> Fetch(string url, int limit, CancellationToken token)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("来源响应超过容量限制。");
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (memory.Length + read > limit) throw new InvalidDataException("来源响应超过容量限制。");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private static string SourceDigest(byte[] bytes, CancellationToken token)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (archive.Entries.Count > 4096) throw new InvalidDataException("源码目录过大。");
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string? root = null;
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var parts = entry.FullName.Split('/');
            if (parts.Length < 2 || parts.Any(part => part is "." or "..") || entry.FullName.Contains('\\'))
                throw new InvalidDataException("源码路径无效。");
            root ??= parts[0];
            if (root != parts[0]) throw new InvalidDataException("源码根目录不唯一。");
            var relative = string.Join('/', parts.Skip(1));
            const string prefix = "packages/network/src/definitions/ipc/";
            if (!relative.StartsWith(prefix, StringComparison.Ordinal) || relative.EndsWith('/')) continue;
            if (relative.Any(c => c > 127)) throw new InvalidDataException("协议源码路径必须为 ASCII。");
            if (entry.Length > 1024 * 1024 || files.Count >= 512 || (total += entry.Length) > 8 * 1024 * 1024)
                throw new InvalidDataException("协议源码超过容量限制。");
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = stream.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                if (memory.Length + read > entry.Length) throw new InvalidDataException("源码长度不一致。");
                memory.Write(buffer, 0, read);
            }
            if (memory.Length != entry.Length || !files.TryAdd(relative[prefix.Length..], Convert.ToHexString(SHA256.HashData(memory.ToArray())).ToLowerInvariant()))
                throw new InvalidDataException("源码长度或路径重复。");
        }
        if (files.Count == 0) throw new InvalidDataException("来源不含协议目录。");
        var text = string.Concat(files.Select(file => file.Key + '\0' + file.Value + '\n'));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static async Task<Source> Observe(string name, Func<Task<object>> action)
    {
        try { return new(name, "ok", await action().ConfigureAwait(false), null); }
        catch (Exception error) when (error is not OutOfMemoryException) { return new(name, "error", null, error is OperationCanceledException ? "cancelled_or_deadline" : error.Message); }
    }

    internal static int? CompareVersion(string? left, string? right)
    {
        var first = Core(left);
        var second = Core(right);
        return first is not null && second is not null ? first.CompareTo(second) : null;

        static Version? Core(string? value)
        {
            if (value is null || value.Length > 128) return null;
            var parts = value.Split('.');
            return parts.Length == 3 && parts.All(part => part.Length != 0 &&
                (part.Length == 1 || part[0] != '0') && part.All(char.IsAsciiDigit)) && Version.TryParse(value, out var version)
                ? version : null;
        }
    }

    private void Publish(UpdateState next)
    {
        lock (publication)
        {
            if (lifetime.IsCancellationRequested) return;
            Volatile.Write(ref state, next);
            if (next.Phase == "finished")
                log?.Invoke($"协议来源检查（{next.Trigger}）：{next.Error ?? next.Result?.Status ?? "unknown"}。");
        }
    }

    private sealed record PackageVersion(string? InstalledVersion, string LatestVersion, bool VersionChanged,
        string VersionComparison, bool? NewerVersionAvailable, string LatestIntegrity, string? ExpectedIntegrity, bool? SameVersionArtifactChanged);
    private sealed record UpdateState(string Phase, string? Trigger, DateTimeOffset? StartedAt,
        DateTimeOffset? FinishedAt, Result? Result, string? Error);

    public void Dispose()
    {
        lock (publication) lifetime.Cancel();
        client.Dispose();
    }

    private sealed record Source(string Name, string Status, object? Data, string? Error);
    private sealed record Result(string Status, DateTimeOffset CheckedAt, Source[] Sources)
    {
        public bool Applied => false;
        public bool CompatibilityVerified => false;
    }
}
