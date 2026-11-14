using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;
using ExcelItem = Lumina.Excel.Sheets.Item;
using NativeFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameDiagnostics(GameServices services, GameReadouts readouts, Configuration configuration)
{
    internal static readonly bool NativeLayoutKnown = typeof(NativeFramework).Module.ModuleVersionId ==
        new Guid("d5a6c4cf-8f1a-4d66-bbcb-130678162b0b");
    private int sampling;
    private int samplingConnection;
    private readonly object archiveSync = new();
    private readonly string combatArchiveRoot = Path.Combine(services.PluginInterface.GetPluginConfigDirectory(), "combat-captures");
    private readonly RetainerWindowOwner retainerOwner = new();
    private long nextRetainerSave;
    private ulong retainerCacheCharacter;
    private readonly Dictionary<(ulong Owner, GameInventoryType Type), RetainerSnapshot> retainerCache = new();
    private static readonly (GameInventoryType Type, string Scope)[] Containers = Enum.GetValues<GameInventoryType>()
        .Select(type => (Type: type, Scope: InventoryScope(type)))
        .Where(item => item.Scope is not null)
        .Select(item => (item.Type, item.Scope ?? string.Empty)).ToArray();

    public unsafe object Holdings(ToolArguments input)
    {
        var scope = input.Text("scope", "all");
        if (input.Has("item_id") && input.Has("item_name") || !input.Has("item_id") && !input.Has("item_name") && scope != "retainer")
            throw new ArgumentException("请指定 item_id 或 item_name，两者只能选一个；scope=retainer 可以不筛选物品。");
        var itemID = input.Has("item_id") ? input.Element("item_id").GetUInt32() : (uint?)null;
        if (itemID == 0) throw new ArgumentException("item_id 必须是基础物品 ID。");
        var itemName = input.Has("item_name") ? input.Text("item_name") : null;
        if (itemName is { Length: > 128 }) throw new ArgumentException("item_name 最多 128 个字符。");
        if (itemName is null && input.Has("contains_match")) throw new ArgumentException("contains_match 需要 item_name。");
        var contains = input.Boolean("contains_match");
        var highQuality = input.Has("high_quality") ? input.Boolean("high_quality") : (bool?)null;
        if (scope is not ("all" or "carried" or "equipped" or "armory" or "saddlebag" or "retainer"))
            throw new ArgumentException("未知的库存范围。");
        var limit = input.Integer("limit", 50, 1, 200);
        var offset = input.Integer("offset", 0, 0, 4096);
        if (!CurrentCnLayoutKnown) return new { status = "unsupported_sdk" };
        if (!services.ClientState.IsLoggedIn || !services.PlayerState.IsLoaded)
            return new { status = "unavailable", reason = "player_not_ready" };
        var manager = InventoryForRead();
        if (manager == null || InventoryManager.MemberFunctionPointers.GetInventoryContainer == null)
            return new { status = "unavailable", reason = "inventory_manager" };

        var (retainerOpen, owner) = ObserveRetainer(Environment.TickCount64);
        var includeCached = input.Boolean("include_cached");
        var now = DateTimeOffset.UtcNow;
        var locations = new List<Holding>();
        var coverage = new List<object>();
        var sheet = itemName is null ? null : services.DataManager.GetExcelSheet<ExcelItem>();
        var names = new Dictionary<uint, string>();
        foreach (var (type, group) in Containers)
        {
            if (scope != "all" && scope != group) continue;
            var name = type.ToString();
            var retainerID = group == "retainer" && owner != 0 ? owner.ToString("X") : null;
            string state;
            if (group == "retainer" && retainerID is null)
                state = retainerOpen ? "owner_unconfirmed" : "not_open";
            else
            {
                var container = manager->GetInventoryContainer((InventoryType)(uint)type);
                if (container == null || !container->IsLoaded || container->Size is < 1 or > 1024)
                    state = "not_loaded";
                else
                {
                    var slots = services.GameInventory.GetInventoryItems(type);
                    state = slots.Length == container->Size ? "live" : "incomplete";
                    if (state == "live")
                        foreach (var slot in slots)
                        {
                            if (slot.IsEmpty || slot.Quantity <= 0 || itemID is uint id && slot.BaseItemId != id ||
                                !MatchesInventoryQuality(slot.IsCollectable, slot.IsHq, highQuality)) continue;
                            string? localized = null;
                            if (sheet is not null)
                            {
                                if (!names.TryGetValue(slot.BaseItemId, out localized))
                                    names[slot.BaseItemId] = localized = sheet.GetRowOrDefault(slot.BaseItemId)?.Name.ToString() ?? string.Empty;
                                if (!MatchesInventoryName(localized, itemName ?? string.Empty, contains)) continue;
                            }
                            locations.Add(new(name, group, slot.InventorySlot, slot.Quantity,
                                slot.IsCollectable ? "collectible" : slot.IsHq ? "high_quality" : "normal", retainerID, slot.BaseItemId, localized, "live", now));
                        }
                }
            }
            coverage.Add(new { container = name, scope = group, state, retainerID });
            if (group == "retainer" && includeCached)
                foreach (var (key, snapshot) in retainerCache)
                {
                    if (key.Type != type || key.Owner == owner) continue;
                    var cachedOwner = key.Owner.ToString("X");
                    coverage.Add(new { container = name, scope = group, state = "cached", retainerID = cachedOwner,
                        observedAt = snapshot.ObservedAt, ageMs = Math.Max(0, (now - snapshot.ObservedAt).TotalMilliseconds) });
                    foreach (var item in snapshot.Items)
                    {
                        if (itemID is uint id && item.ItemID != id || !MatchesInventoryQuality(item.Quality == "collectible", item.Quality == "high_quality", highQuality)) continue;
                        string? localized = null;
                        if (sheet is not null)
                        {
                            if (!names.TryGetValue(item.ItemID, out localized))
                                names[item.ItemID] = localized = sheet.GetRowOrDefault(item.ItemID)?.Name.ToString() ?? string.Empty;
                            if (!MatchesInventoryName(localized, itemName ?? string.Empty, contains)) continue;
                        }
                        locations.Add(item with { ItemName = localized, ObservedAt = snapshot.ObservedAt, Source = "cached",
                            AgeMs = Math.Max(0, (now - snapshot.ObservedAt).TotalMilliseconds) });
                    }
                }
        }
        return new
        {
            status = "ok", itemID, itemName, highQuality, scope, coverage, includeCached,
            cachedLocations = locations.Count(item => item.Source == "cached"),
            retainerRoster = scope is "all" or "retainer" ? ReadRetainerCoverage(owner, includeCached) : null,
            observedNormal = locations.Where(x => x.Quality == "normal").Sum(x => (long)x.Quantity),
            observedHighQuality = locations.Where(x => x.Quality == "high_quality").Sum(x => (long)x.Quantity),
            observedCollectible = locations.Where(x => x.Quality == "collectible").Sum(x => (long)x.Quantity),
            observedLocations = locations.Count, locations = locations.Skip(offset).Take(limit).ToArray(),
            nextOffset = locations.Count > offset + limit ? (int?)offset + limit : null,
        };
    }

    internal static bool MatchesInventoryQuality(bool collectible, bool hq, bool? highQuality) =>
        !highQuality.HasValue || !collectible && hq == highQuality.Value;

    internal static bool MatchesInventoryName(string localized, string query, bool contains) => contains
        ? localized.Contains(query, StringComparison.OrdinalIgnoreCase)
        : string.Equals(localized, query, StringComparison.OrdinalIgnoreCase);

    private static unsafe InventoryManager* InventoryForRead() =>
        InventoryManager.Addresses.Instance.Value != 0 ? InventoryManager.Instance() : null;

    private unsafe (bool Open, ulong Owner) ObserveRetainer(long now)
    {
        var character = services.ClientState.IsLoggedIn && services.PlayerState.IsLoaded ? services.PlayerState.ContentId : 0;
        var open = character != 0 && RetainerAddons.Any(name =>
            services.GameGui.GetAddonByName(name, 1) is { IsNull: false, IsVisible: true, IsReady: true });
        var manager = open && RetainerManager.Addresses.Instance.Value != 0 ? RetainerManager.Instance() : null;
        var selected = manager != null && manager->IsReady ? manager->LastSelectedRetainerId : 0;
        var owner = retainerOwner.Observe(character, open, selected, now);
        if (retainerCacheCharacter != character)
        {
            retainerCache.Clear();
            retainerCacheCharacter = character;
            nextRetainerSave = 0;
        }
        if (owner != 0 && now >= nextRetainerSave)
        {
            nextRetainerSave = now + 1000;
            CacheRetainer(owner);
        }
        return (open, owner);
    }

    public unsafe object Connection()
    {
        if (!ReadoutLayoutKnown) return new { status = "unsupported_sdk", sdkMvid = typeof(NativeFramework).Module.ModuleVersionId };
        var framework = NativeFramework.Addresses.Instance.Value == 0 ? null : NativeFramework.Instance();
        var initialized = framework != null && framework->IsNetworkModuleInitialized;
        var proxy = initialized ? framework->NetworkModuleProxy : null;
        var network = proxy == null ? null : proxy->NetworkModule;
        return new
        {
            status = network == null ? "unavailable" : "ok",
            sdkMvid = typeof(NativeFramework).Module.ModuleVersionId,
            loggedIn = services.ClientState.IsLoggedIn,
            territoryID = services.ClientState.TerritoryType,
            networkModuleInitialized = initialized,
            winSockInitialized = network == null ? (bool?)null : network->WinSockInitialized,
            instance = network == null ? (short?)null : network->CurrentInstance,
            crossWorldDuty = network == null ? (bool?)null : network->IsInCrossWorldDuty,
            frameworkUpdateMs = services.Framework.UpdateDelta.TotalMilliseconds,
            payloadCaptured = false,
        };
    }

    public async Task<object> SampleConnectionAsync(ToolArguments input, CancellationToken token)
    {
        var count = input.Integer("samples", 8, 1, 20);
        var interval = input.Integer("interval_ms", 500, 250, 2000);
        if (Interlocked.CompareExchange(ref samplingConnection, 1, 0) != 0) return new { status = "busy" };
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(15000);
            var clock = Stopwatch.StartNew();
            var samples = new List<object>(count);
            var status = "complete";
            try
            {
                for (var index = 0; index < count; index++)
                {
                    if (index != 0) await Task.Delay(interval, deadline.Token).ConfigureAwait(false);
                    var elapsedMs = clock.ElapsedMilliseconds;
                    var tcp = await Task.Run(ProcessTcpSummary.Read, deadline.Token).ConfigureAwait(false);
                    var client = await services.Framework.RunOnTick(() =>
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        return services.Framework.IsFrameworkUnloading ? null : Connection();
                    }, cancellationToken: deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                    samples.Add(new { elapsedMs, clientElapsedMs = clock.ElapsedMilliseconds, tcp, client });
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                status = "timeout";
            }
            return new { status, scope = "current_process_tcp", samples, payloadCaptured = false };
        }
        finally { Volatile.Write(ref samplingConnection, 0); }
    }

    public async Task<object> SampleAsync(bool archive, ToolArguments input, Func<bool> isAllowed, CancellationToken cancellationToken)
    {
        var count = input.Integer("frames", 60, 1, 300);
        var timeout = input.Integer("timeout_ms", 4000, 250, 8000);
        var includeParty = input.Boolean("include_party");
        var scope = EffectScope(input);
        var effects = input.Boolean("include_action_effects") ? new CombatEffects(scope) : null;
        var includeMeter = input.Boolean("include_job_meter");
        uint[]? actionIDs = null;
        if (input.Has("action_ids"))
        {
            var array = input.Element("action_ids");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 8)
                throw new ArgumentException("需要 1 至 8 个普通技能 ID。");
            var seen = new HashSet<uint>();
            foreach (var value in array.EnumerateArray())
                if (!value.TryGetUInt32(out var id) || id == 0 || !seen.Add(id))
                    throw new ArgumentException("技能 ID 无效或重复。");
            actionIDs = [.. seen];
        }
        if ((includeMeter || actionIDs is not null) && count > 100)
            throw new ArgumentException("附带量谱或技能冷却时最多采集 100 帧。");
        if (Interlocked.CompareExchange(ref sampling, 1, 0) != 0) return new { status = "busy" };
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var clock = Stopwatch.StartNew();
            var samples = new List<CombatFrame>(count);
            var status = "complete";
            try
            {
                while (samples.Count < count)
                {
                    var sample = await services.Framework.RunOnTick(() =>
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        if (!services.Framework.IsInFrameworkUpdateThread || services.Framework.IsFrameworkUnloading)
                            return null;
                        var frame = CaptureCombat(clock.ElapsedMilliseconds, includeParty, includeMeter, actionIDs);
                        if (frame is not null && (samples.Count == 0 || frame.TerritoryID == samples[0].TerritoryID &&
                            frame.Player.ID == samples[0].Player.ID) && services.ObjectTable.LocalPlayer is { } local)
                            if (effects is not null) ObserveCombatEffects(effects, local, frame.ElapsedMs);
                        return frame;
                    }, delayTicks: 1, cancellationToken: deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                    if (sample is null) { status = "unavailable"; break; }
                    if (samples.Count != 0 && (sample.TerritoryID != samples[0].TerritoryID ||
                        sample.Player.ID != samples[0].Player.ID)) { status = "session_changed"; break; }
                    samples.Add(sample);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                status = "timeout";
            }
            var result = new CombatCapture(status, DateTimeOffset.UtcNow, typeof(GameDiagnostics).Module.ModuleVersionId,
                count, [.. samples], ActionEffects: effects?.Events.ToArray(),
                ActionEffectCoverage: effects?.Coverage, ActionEffectsTruncated: effects?.Truncated);
            Guid? archiveID = null;
            if (archive && samples.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!isAllowed()) throw new InvalidOperationException("战斗归档权限已关闭。");
                archiveID = await Task.Run(() => Save(result), cancellationToken).ConfigureAwait(false);
            }
            return new { result.Status, result.CapturedAt, result.BuildMvid, result.RequestedFrames, result.Frames,
                result.ActionEffects, result.ActionEffectCoverage, result.ActionEffectsTruncated, archiveID };
        }
        finally { Volatile.Write(ref sampling, 0); }
    }

    public object ReadArchive(ToolArguments input, string tool)
    {
        lock (archiveSync)
        {
            var directory = ArchiveDirectory();
            var automatic = ArchiveDirectory(automatic: true);
            if (input.Has("archive_id"))
            {
                CombatCapture? capture;
                object? page;
                try { capture = LoadArchive(RepositoryPackages.ParseID(input.Text("archive_id")), input, tool, out page); }
                catch (Exception error) when (error is InvalidDataException or JsonException) { return new { status = "invalid_capture" }; }
                if (page is not null) return page;
                if (capture is null) return new { status = "not_found" };
                if (tool == "read_combat_capture")
                {
                    if (capture?.Frames is null || capture.Frames.Length > 18000) return new { status = "invalid_capture" };
                    var legacyFull = !input.Has("offset") && !input.Has("limit") && capture.Frames.Length <= 300 && (capture.Evidence?.Length ?? 0) == 0
                        && (capture.ActionEffects?.Length ?? 0) <= 360 && (capture.Activity?.Length ?? 0) <= 128;
                    if (legacyFull) return new { status = "ok", capture, totalFrames = capture.Frames.Length, nextOffset = (int?)null };
                    var offset = input.Integer("offset", 0, 0, 18000);
                    var limit = input.Integer("limit", 100, 1, 100);
                    return FramePageResult(capture with { Frames = capture.Frames.Skip(offset).Take(limit).ToArray(), ActionEffects = null,
                        Activity = null, Evidence = null }, capture.Frames.Length, capture.ActionEffects?.Length, capture.Activity?.Length, offset, limit);
                }
                if (tool is READ_MECHANICS or READ_ACR)
                {
                    var rows = capture.Evidence;
                    var kind = input.Has("kind") ? input.Text("kind") : null;
                    var offset = input.Integer("offset", 0, 0, 6000);
                    var limit = input.Integer("limit", 20, 1, 64);
                    var acr = tool == READ_ACR;
                    var matched = 0;
                    List<FightEvidence>? events = null;
                    if (rows is not null)
                        foreach (var row in rows)
                        {
                            if ((row.Kind == "acr_decision") != acr || kind is not null && kind != row.Kind) continue;
                            if (matched >= offset && matched < offset + limit)
                                (events ??= new(Math.Min(limit, rows.Length - offset))).Add(row);
                            matched++;
                        }
                    return EvidencePageResult(capture, input.Text("archive_id"), acr, rows is not null, matched, events, offset, limit);
                }
                if (tool == "read_combat_activity") return capture is null ? new { status = "invalid_capture" } : ReadBattleActivity(capture, input);
                if (tool == COMBAT_TIMELINE_TOOL)
                    return capture is null ? new { status = "invalid_capture" } : CombatTimeline(capture, input);
                if (capture?.Frames is not { Length: > 0 and <= 18000 } frames || frames.Any(frame => frame?.Player is null ||
                    frame.Party is { Length: > 7 } || frame.Party?.Any(actor => actor is null) == true))
                    return new { status = "invalid_capture" };
                return Summarize(capture);
            }
            var archives = ArchiveFiles(directory).Select(file => (File: file, Compressed: false))
                .Concat(ArchiveFiles(automatic, automatic: true).Select(file => (File: file, Compressed: true)))
                .OrderByDescending(item => item.File.LastWriteTimeUtc).Select(item => new
                {
                    archiveID = item.File.Name[..36], item.File.LastWriteTimeUtc, bytes = item.File.Length, compressed = item.Compressed,
                }).ToArray();
            return new { status = "ok", archives };
        }
    }

    private unsafe CombatFrame? CaptureCombat(long elapsedMs, bool includeParty, bool includeMeter = false, uint[]? actionIDs = null, bool includePose = false)
    {
        if (!services.ClientState.IsLoggedIn || services.ObjectTable.LocalPlayer is not { } player) return null;
        var action = GameReadouts.ActionManagerForReadout();
        var party = includeParty ? new List<CombatActor>(7) : null;
        if (party is not null)
        {
            var members = services.PartyList;
            for (var slot = 0; slot < Math.Min(members.Length, 8) && party.Count < 7; slot++)
                if (members[slot]?.GameObject is IBattleChara member && member.GameObjectId != player.GameObjectId)
                    party.Add(Actor(member, includePose: includePose));
        }
        var gcd = action != null && ActionManager.MemberFunctionPointers.GetRecastGroupDetail != null
            ? action->GetRecastGroupDetail(57) : null;
        var actionFrame = action == null ? null : new ActionFrame(
            action->ActionQueued ? action->QueuedActionType.ToString() : null,
            action->ActionQueued ? $"0x{action->QueuedTargetId.Id:X}" : null,
            player.IsCasting ? action->CastActionId : null,
            player.IsCasting ? $"0x{action->CastTargetId.Id:X}" : null,
            player.IsCasting && float.IsFinite(action->CastTimeElapsed) ? action->CastTimeElapsed : null,
            player.IsCasting && float.IsFinite(action->CastTimeTotal) ? action->CastTimeTotal : null,
            gcd == null ? null : !gcd->IsActive ? 0f :
                float.IsFinite(gcd->Total - gcd->Elapsed) ? Math.Max(0, gcd->Total - gcd->Elapsed) : null);
        return new(elapsedMs, services.ClientState.TerritoryType, services.Condition[ConditionFlag.InCombat],
            Actor(player, includeStatus: true, includePose: includePose),
            services.TargetManager.Target is IBattleChara target ? Actor(target, includeStatus: true, includePose: includePose) : null,
            action != null && action->ActionQueued ? action->QueuedActionId : null,
            action != null && float.IsFinite(action->AnimationLock) ? action->AnimationLock : null,
            party?.ToArray(), actionFrame, includeMeter ? readouts.ReadJobMeter(player.ClassJob.RowId) : null,
            actionIDs is not null && services.PlayerState.EffectiveLevel > 0
                ? readouts.CombatRecasts(actionIDs, (uint)services.PlayerState.EffectiveLevel) : null);
    }

    private static CombatActor Actor(IBattleChara actor, bool includeStatus = false, bool includePose = false)
    {
        List<uint>? effects = includeStatus ? new(8) : null;
        if (effects is not null)
            foreach (var effect in actor.StatusList)
            {
                if (effect.StatusId == 0) continue;
                effects.Add(effect.StatusId);
                if (effects.Count >= 60) break;
            }
        CombatPose? pose = null;
        if (includePose && actor.Position is var p && float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) &&
            float.IsFinite(actor.Rotation)) pose = new(p.X, p.Y, p.Z, actor.Rotation, actor.EntityId);
        return new(actor.GameObjectId, actor.CurrentHp, actor.MaxHp, actor.CurrentMp,
            actor.IsCasting ? actor.CastActionId : null, effects?.ToArray(), pose);
    }

    private Guid Save(CombatCapture capture, bool automatic = false, Guid? recordingID = null)
    {
        lock (archiveSync)
        {
            var directory = ArchiveDirectory(automatic);
            var existing = ArchiveFiles(directory, automatic);
            var maxArchives = Math.Clamp(configuration.CombatArchiveLimit, 1, 1000);
            var maxBytes = Math.Clamp(configuration.CombatArchiveMiB, 1, 100) * 1024L * 1024;
            if (!automatic && existing.Count >= maxArchives)
                throw new InvalidOperationException("战斗归档已满，请在插件配置目录清理后再保存。");
            Directory.CreateDirectory(directory);
            _ = ArchiveDirectory(automatic);
            var data = JsonSerializer.SerializeToUtf8Bytes(capture, DalamudMCP.Shared.ToolJson.Options);
            while (data.Length > MAX_ARCHIVE_BYTES && automatic)
            {
                if (capture.Frames.Length <= 1 && (capture.ActionEffects?.Length ?? 0) <= 1 && (capture.Activity?.Length ?? 0) <= 1 && (capture.Evidence?.Length ?? 0) <= 1) break;
                capture = capture with
                {
                    Frames = capture.Frames.Take(Math.Max(1, capture.Frames.Length / 2)).ToArray(), Truncated = true,
                    ActionEffects = capture.ActionEffects?.Take(capture.ActionEffects.Length / 2).ToArray(), ActionEffectsTruncated = true,
                    Activity = capture.Activity?.Take(capture.Activity.Length / 2).ToArray(), ActivityTruncated = true,
                    Evidence = capture.Evidence?.Take(capture.Evidence.Length / 2).ToArray(), EvidenceTruncated = true,
                    DecisionsTruncated = true,
                };
                data = JsonSerializer.SerializeToUtf8Bytes(capture, DalamudMCP.Shared.ToolJson.Options);
            }
            if (data.Length > MAX_ARCHIVE_BYTES) throw new InvalidOperationException("战斗归档超过大小限制。");
            var id = recordingID ?? Guid.NewGuid();
            var path = Path.Combine(directory, id.ToString("D") + (automatic ? ".json.gz" : ".json"));
            var pending = path + ".part";
            var ownsPending = false;
            try
            {
                using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsPending = true;
                    if (automatic)
                    {
                        using var gzip = new GZipStream(stream, CompressionLevel.Fastest);
                        gzip.Write(data);
                    }
                    else stream.Write(data);
                }
                File.Move(pending, path);
                ownsPending = false;
            }
            finally { if (ownsPending) File.Delete(pending); }
            if (automatic)
            {
                var kept = 1;
                var bytes = new FileInfo(path).Length;
                if (bytes > maxBytes)
                {
                    File.Delete(path);
                    throw new InvalidOperationException("单场归档超过配置保留容量。");
                }
                var cutoff = DateTime.UtcNow.AddDays(-7);
                foreach (var file in existing.OrderByDescending(file => file.LastWriteTimeUtc))
                {
                    if (file.LastWriteTimeUtc < cutoff || kept >= maxArchives || file.Length > maxBytes - bytes)
                        file.Delete();
                    else { bytes += file.Length; kept++; }
                }
            }
            return id;
        }
    }

    private string ArchiveDirectory(bool automatic = false)
    {
        var path = combatArchiveRoot;
        EnsurePlainDirectory(path);
        if (automatic)
        {
            path = Path.Combine(path, "automatic");
            EnsurePlainDirectory(path);
        }
        return path;

        static void EnsurePlainDirectory(string location)
        {
            var directory = new DirectoryInfo(location);
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("战斗归档目录不能是链接。");
        }
    }

    private static IReadOnlyList<FileInfo> ArchiveFiles(string directory, bool automatic = false)
    {
        var root = new DirectoryInfo(directory);
        if (!root.Exists) return [];
        var suffix = automatic ? ".json.gz" : ".json";
        List<FileInfo>? entries = null;
        var scanned = 0;
        foreach (var file in root.EnumerateFiles("*" + suffix))
        {
            if (++scanned > 1024) throw new InvalidOperationException("战斗归档目录文件过多。");
            var name = file.Name;
            if (name.Length == 36 + suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(name.AsSpan(0, 36), "D", out _) && !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                (entries ??= []).Add(file);
        }
        return entries is null ? Array.Empty<FileInfo>() : entries;
    }

    private static string? InventoryScope(GameInventoryType type)
    {
        if (type is GameInventoryType.Inventory1 or GameInventoryType.Inventory2 or GameInventoryType.Inventory3 or
            GameInventoryType.Inventory4 or GameInventoryType.Crystals) return "carried";
        if (type == GameInventoryType.EquippedItems) return "equipped";
        var name = type.ToString();
        if (name.StartsWith("Armory", StringComparison.Ordinal)) return "armory";
        if (name.Contains("SaddleBag", StringComparison.Ordinal)) return "saddlebag";
        if (name.StartsWith("RetainerPage", StringComparison.Ordinal) ||
            type is GameInventoryType.RetainerEquippedItems or GameInventoryType.RetainerCrystals) return "retainer";
        return null;
    }

    private sealed record Holding(string Container, string Scope, uint Slot, int Quantity, string Quality, string? RetainerID,
        uint ItemID, string? ItemName, string? Source = null, DateTimeOffset? ObservedAt = null, double? AgeMs = null);
    private sealed record CombatPose(float X, float Y, float Z, float Rotation, uint EntityID);
    private sealed record CombatActor(ulong ID, uint HP, uint MaxHP, uint MP, uint? CastActionID, uint[]? StatusIDs = null, CombatPose? Pose = null);
    private sealed record CombatFrame(long ElapsedMs, uint TerritoryID, bool InCombat, CombatActor Player, CombatActor? Target,
        uint? QueuedActionID, float? AnimationLock, CombatActor[]? Party = null, ActionFrame? ActionContext = null,
        object? JobMeter = null, object[]? Recasts = null);
    private sealed record CombatCapture(string Status, DateTimeOffset CapturedAt, Guid BuildMvid, int RequestedFrames,
        CombatFrame[] Frames, bool Truncated = false, ObservedEffect[]? ActionEffects = null,
        string? ActionEffectCoverage = null, bool? ActionEffectsTruncated = null, BattleActivity[]? Activity = null, bool ActivityTruncated = false, bool PartialStart = false,
        FightEvidence[]? Evidence = null, bool EvidenceTruncated = false, string[]? MechanicSources = null,
        bool DecisionsTruncated = false, long? EndElapsedMs = null);
    private static readonly string[] RetainerAddons = ["InventoryRetainer", "InventoryRetainerLarge"];
    private const int MAX_ARCHIVE_BYTES = 16 * 1024 * 1024;
    private const int MAX_COMPRESSED_BYTES = MAX_ARCHIVE_BYTES + 8192;
}
