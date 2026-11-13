using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DalamudMCP.Shared;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameDiagnostics
{
    internal const string ANALYZE_COMBAT_TOOL = "analyze_combat_capture";
    internal const string EXPORT_COMBAT_TOOL = "export_combat_capture";
    private readonly object combatExportSync = new();
    private int combatAnalysisRequests;

    internal async Task<object> AnalyzeArchiveAsync(ToolArguments input, bool export, Func<bool> allowed, CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        var id = RepositoryPackages.ParseID(input.Text("archive_id"));
        var filter = EffectFilter.Parse(input);
        var groupBy = input.Text("group_by", "source_action");
        if (groupBy is not ("source_action" or "source" or "action")) throw new ArgumentException("无效的 group_by。");
        var offset = input.Integer("offset", 0, 0, 50000);
        var limit = input.Integer("limit", 32, 1, 100);
        var timelineLimit = input.Integer("timeline_limit", 0, 0, 100);
        if (Interlocked.Increment(ref combatAnalysisRequests) > 2)
        {
            Interlocked.Decrement(ref combatAnalysisRequests);
            return new { status = "busy" };
        }

        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter((int)Math.Max(1, 3000 - (Environment.TickCount64 - started)));
        var token = deadline.Token;
        var operation = Task.Run<object>(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var capture = LoadArchive(id, token);
                if (capture is null) return new { status = "not_found" };
                var matches = MatchEffects(capture, filter, out var status);
                var groups = AggregateEffects(matches, groupBy, token);
                token.ThrowIfCancellationRequested();
                var coverage = capture.ActionEffectCoverage ?? "not_observed";
                var captureTruncated = capture.Truncated || capture.ActionEffectsTruncated == true;
                if (export && status != "invalid_capture")
                    return ExportCombat(combatArchiveRoot, combatExportSync, id, new
                    {
                        archiveID = id, capture.CapturedAt, capture.BuildMvid, status, filter, groupBy, groups,
                        timeline = matches, matchingEvents = matches.Length, groupCount = groups.Length,
                        capture.PartialStart, captureTruncated, coverage, interpretationVerified = false,
                    }, coverage, capture.PartialStart, captureTruncated, allowed, token);

                var page = groups.Skip(offset).Take(limit).ToArray();
                return new
                {
                    status, archiveID = id, capture.BuildMvid, capture.CapturedAt, matchingEvents = matches.Length,
                    groupCount = groups.Length, groups = page, timeline = matches.Take(timelineLimit).Select(effect => new
                    {
                        effect.ObservedAtMs, effect.GlobalSequence, effect.SourceID, effect.TargetID, effect.TargetIndex,
                        effect.ActionID, effect.ActionType, effect.BeforeCombat, effects = effect.Effects.Select(DecodeEffect).ToArray(),
                    }).ToArray(), timelineTruncated = matches.Length > timelineLimit,
                    nextOffset = page.Length > 0 && offset + page.Length < groups.Length ? (int?)offset + page.Length : null,
                    capture.PartialStart, captureTruncated, coverage, interpretationVerified = false,
                };
            }
            catch (FileNotFoundException) { return new { status = "not_found" }; }
            catch (InvalidDataException) { return new { status = "invalid_capture" }; }
            catch (JsonException) { return new { status = "invalid_capture" }; }
            finally
            {
                Interlocked.Decrement(ref combatAnalysisRequests);
            }
        });
        try
        {
            // 取消时文件可能已提交，导出以实际写入结果为准。
            return export ? await operation.ConfigureAwait(false) : await operation.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new { status = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout" }; }
        finally
        {
            if (operation.IsCompleted) deadline.Dispose();
            else _ = operation.ContinueWith(task =>
            {
                _ = task.Exception;
                deadline.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private CombatCapture? LoadArchive(Guid id, CancellationToken token = default)
    {
        var fileName = id.ToString("D") + ".json";
        var file = new FileInfo(Path.Combine(ArchiveDirectory(), fileName));
        var compressed = !file.Exists;
        if (compressed) file = new(Path.Combine(ArchiveDirectory(automatic: true), fileName + ".gz"));
        if (!file.Exists) return null;
        if (file.Length > (compressed ? MAX_COMPRESSED_BYTES : MAX_ARCHIVE_BYTES) || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("归档文件不是可读取的常规文件。");
        using var stream = file.OpenRead();
        using var gzip = compressed ? new GZipStream(stream, CompressionMode.Decompress) : null;
        var source = (Stream?)gzip ?? stream;
        using var data = new MemoryStream((int)Math.Min(file.Length, MAX_ARCHIVE_BYTES));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = source.Read(buffer);
            if (read == 0) break;
            if (data.Length + read > MAX_ARCHIVE_BYTES) throw new InvalidDataException("归档文件过大。");
            data.Write(buffer, 0, read);
        }
        return JsonSerializer.Deserialize<CombatCapture>(data.GetBuffer().AsSpan(0, (int)data.Length), ToolJson.Options);
    }

    private static object ExportCombat(string archiveRoot, object sync, Guid archiveID, object value, string coverage, bool partialStart, bool captureTruncated,
        Func<bool> allowed, CancellationToken token)
    {
        const int limit = 16 * 1024 * 1024;
        token.ThrowIfCancellationRequested();
        if (!allowed()) return new { status = "permission_changed" };
        var data = JsonSerializer.SerializeToUtf8Bytes(value, ToolJson.Options);
        token.ThrowIfCancellationRequested();
        if (data.Length > limit) return new { status = "export_too_large", maxBytes = limit };
        var sha256 = Convert.ToHexString(SHA256.HashData(data));
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            if (!allowed()) return new { status = "permission_changed" };
            var root = new DirectoryInfo(Path.Combine(Path.GetDirectoryName(archiveRoot)!, "combat-exports"));
            if (root.Exists && root.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return new { status = "unavailable", reason = "export_directory_is_link" };
            root.Create();
            var existing = root.EnumerateFiles("combat-*.json").Take(129).ToArray();
            if (existing.Length > 128) return new { status = "unavailable", reason = "export_directory_limit" };
            var owned = existing.Where(file => file.Name.Length == 44 &&
                Guid.TryParseExact(file.Name.AsSpan(7, 32), "N", out _) && !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal).ToArray();
            var exportID = Guid.NewGuid();
            var fileName = $"combat-{exportID:N}.json";
            var target = Path.Combine(root.FullName, fileName);
            var pending = target + ".part";
            var ownsPending = false;
            try
            {
                using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsPending = true;
                    stream.Write(data);
                }
                token.ThrowIfCancellationRequested();
                if (!allowed()) return new { status = "permission_changed" };
                long retainedBytes = data.Length;
                var retained = 1;
                foreach (var file in owned)
                {
                    token.ThrowIfCancellationRequested();
                    if (!allowed()) return new { status = "permission_changed" };
                    if (retained >= 10 || retainedBytes + file.Length > 2 * limit) file.Delete();
                    else { retained++; retainedBytes += file.Length; }
                }
                token.ThrowIfCancellationRequested();
                if (!allowed()) return new { status = "permission_changed" };
                File.Move(pending, target);
                ownsPending = false;
            }
            finally { if (ownsPending) File.Delete(pending); }
            return new { status = "ok", exportID, fileName, bytes = data.Length, sha256, archiveID, coverage, partialStart, captureTruncated };
        }
    }
}
