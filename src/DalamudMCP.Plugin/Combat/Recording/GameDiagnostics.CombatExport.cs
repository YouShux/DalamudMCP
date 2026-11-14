using System.Buffers;
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
                object? analysisPage = null;
                var capture = export ? LoadArchive(id, token) : LoadArchive(id, null, null, out analysisPage, token,
                    new ArchivePage(ArchiveSection.Analysis, offset, limit, From: filter.FromMs, To: filter.ToMs, ActionID: filter.ActionID,
                        Actor: filter.SourceID is { } source ? System.Text.Encoding.ASCII.GetBytes(source) : null,
                        Analysis: new(id, groupBy, timelineLimit, token)));
                if (analysisPage is not null)
                {
                    token.ThrowIfCancellationRequested();
                    return analysisPage;
                }
                if (capture is null) return new { status = "not_found" };
                var matches = MatchEffects(capture, filter, out var status);
                var groups = AggregateEffects(matches, groupBy, token);
                token.ThrowIfCancellationRequested();
                if (export && status != "invalid_capture")
                {
                    var coverage = capture.ActionEffectCoverage ?? "not_observed";
                    var captureTruncated = capture.Truncated || capture.ActionEffectsTruncated == true;
                    return ExportCombat(combatArchiveRoot, combatExportSync, id, new
                    {
                        archiveID = id, capture.CapturedAt, capture.BuildMvid, status, filter, groupBy, groups,
                        timeline = matches, matchingEvents = matches.Length, groupCount = groups.Length,
                        capture.PartialStart, captureTruncated, coverage, interpretationVerified = false,
                    }, coverage, capture.PartialStart, captureTruncated, allowed, token);
                }

                return AnalysisPageResult(capture, id, status, groups, matches.Length, matches.Take(timelineLimit), offset, limit, timelineLimit);
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

    private static object AnalysisPageResult(CombatCapture capture, Guid id, string status, EffectGroup[] groups, int matchCount,
        IEnumerable<ObservedEffect> timeline, int offset, int limit, int timelineLimit)
    {
        var coverage = capture.ActionEffectCoverage ?? "not_observed";
        var captureTruncated = capture.Truncated || capture.ActionEffectsTruncated == true;
        var page = groups.Skip(offset).Take(limit).ToArray();
        return new
        {
            status, archiveID = id, capture.BuildMvid, capture.CapturedAt, matchingEvents = matchCount,
            groupCount = groups.Length, groups = page, timeline = timeline.Select(effect => new
            {
                effect.ObservedAtMs, effect.GlobalSequence, effect.SourceID, effect.TargetID, effect.TargetIndex,
                effect.ActionID, effect.ActionType, effect.BeforeCombat, effects = effect.Effects.Select(DecodeEffect).ToArray(),
            }).ToArray(), timelineTruncated = matchCount > timelineLimit,
            nextOffset = page.Length > 0 && offset + page.Length < groups.Length ? (int?)offset + page.Length : null,
            capture.PartialStart, captureTruncated, coverage, interpretationVerified = false,
        };
    }

    private CombatCapture? LoadArchive(Guid id, CancellationToken token = default) => LoadArchive(id, null, null, out _, token);

    private CombatCapture? LoadArchive(Guid id, ToolArguments? pageArguments, string? pageTool, out object? page, CancellationToken token = default,
        ArchivePage? request = null)
    {
        page = null;
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
        return ReadArchiveData(source, (int)file.Length, compressed, pageArguments, pageTool, out page, token, request);
    }

    private static CombatCapture? ReadArchiveData(Stream source, int fileLength, bool compressed,
        ToolArguments? pageArguments, string? pageTool, out object? page, CancellationToken token, ArchivePage? request)
    {
        page = null;
        var size = compressed ? Math.Clamp(fileLength, 64 * 1024, MAX_ARCHIVE_BYTES) : Math.Max(1, fileLength);
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        var length = 0;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var capacity = Math.Min(buffer.Length, MAX_ARCHIVE_BYTES);
                if (length == capacity)
                {
                    var next = source.ReadByte();
                    if (next < 0) break;
                    if (length == MAX_ARCHIVE_BYTES) throw new InvalidDataException("归档文件过大。");

                    var larger = ArrayPool<byte>.Shared.Rent(Math.Min(capacity * 2, MAX_ARCHIVE_BYTES));
                    buffer.AsSpan(0, length).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                    buffer = larger;
                    buffer[length++] = (byte)next;
                    continue;
                }

                var read = source.Read(buffer.AsSpan(length, Math.Min(64 * 1024, capacity - length)));
                if (read == 0) break;
                length += read;
            }
            var data = buffer.AsSpan(0, length);
            if (length >= 128 * 1024 && data[^1] == (byte)'}' &&
                (request ?? (pageArguments is { } input && pageTool is not null ? GetArchivePage(input, pageTool) : null)) is { } selected &&
                TryReadArchivePage(data, selected, out var capture, out page))
            {
                return capture;
            }
            return JsonSerializer.Deserialize<CombatCapture>(data, ToolJson.Options);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
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
