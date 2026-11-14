using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;
using DalamudMCP.Shared;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameDiagnostics
{
    private static ArchivePage? GetArchivePage(ToolArguments input, string tool)
    {
        try
        {
            switch (tool)
            {
                case "read_combat_capture":
                    return new(ArchiveSection.Frames, input.Integer("offset", 0, 0, 18000), input.Integer("limit", 100, 1, 100),
                        !input.Has("offset") && !input.Has("limit"));
                case READ_MECHANICS:
                case READ_ACR:
                    var evidenceKind = ReadKind();
                    return new(tool == READ_ACR ? ArchiveSection.Acr : ArchiveSection.Mechanics,
                        input.Integer("offset", 0, 0, 6000), input.Integer("limit", 20, 1, 64),
                        Kind: evidenceKind, CombatID: input.Text("archive_id"));
                case COMBAT_TIMELINE_TOOL:
                    var timelineOffset = input.Integer("offset", 0, 0, 50000);
                    var timelineLimit = input.Integer("limit", 40, 1, 100);
                    var timeline = EffectFilter.Parse(input);
                    return new(ArchiveSection.Timeline, timelineOffset, timelineLimit, From: timeline.FromMs, To: timeline.ToMs,
                        ActionID: timeline.ActionID, Actor: timeline.SourceID is { } source ? System.Text.Encoding.ASCII.GetBytes(source) : null);
                case "read_combat_activity":
                    var offset = input.Integer("offset", 0, 0, 50000);
                    var limit = input.Integer("limit", 50, 1, 128);
                    var from = input.Integer("from_ms", 0, 0, 86400000);
                    var to = input.Integer("to_ms", 86400000, from, 86400000);
                    var actor = input.Has("actor_id") ? System.Text.Encoding.UTF8.GetBytes($"0x{input.ObjectID("actor_id"):X}") : null;
                    return new(ArchiveSection.Activity, offset, limit, Kind: ReadKind(), Actor: actor, From: from, To: to);
                default:
                    return null;
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            return null;
        }

        byte[]? ReadKind() => input.Has("kind") ? System.Text.Encoding.UTF8.GetBytes(input.Text("kind")) : null;
    }

    private static object FramePageResult(CombatCapture capture, int total, int? effects, int? activity, int offset, int limit) =>
        new { status = "ok", totalFrames = total, capture, actionEffectsAvailable = effects, activityAvailable = activity,
            nextOffset = offset + limit < total ? (int?)offset + limit : null };

    private static bool TryReadArchivePage(ReadOnlySpan<byte> data, ArchivePage request, out CombatCapture? capture, out object? page)
    {
        capture = null;
        page = null;
        ToolJson.Options.MakeReadOnly(populateMissingResolver: true);
        if (BuildArchiveShape(typeof(CombatCapture)) is not { Fields: { } fields }) return false;
        var reader = new Utf8JsonReader(data);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
        var header = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(header);
        writer.WriteStartObject();
        List<CombatFrame>? frames = null;
        List<FightEvidence>? eventPage = null;
        List<BattleActivity>? activityPage = null;
        var matched = 0;
        List<TimelineEntry>? timeline = null;
        var analysis = request.Analysis is { } settings ? new EffectAccumulator(settings.GroupBy) : null;
        int? total = null, effects = null, activity = null, evidence = null;
        ulong seen = 0;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.ValueSpan;
            var field = FindArchiveField(ref reader, fields);
            if (field is null || (seen & field.Mask) != 0 || !reader.Read()) return false;
            seen |= field.Mask;
            if (request.Section is ArchiveSection.Timeline or ArchiveSection.Analysis && name.SequenceEqual("actionEffects"u8))
            {
                if (!ReadTimelineEntries(ref reader, field.Shape, request, analysis, out effects, out timeline)) return false;
                continue;
            }
            if (request.Section == ArchiveSection.Activity && name.SequenceEqual("activity"u8))
            {
                if (!ReadArchiveEvents(data, ref reader, field.Shape, request, out activity, out matched, out activityPage)) return false;
                continue;
            }
            if (request.Section is ArchiveSection.Mechanics or ArchiveSection.Acr && name.SequenceEqual("evidence"u8))
            {
                if (!ReadArchiveEvents(data, ref reader, field.Shape, request, out evidence, out matched, out eventPage)) return false;
                continue;
            }
            if (request.Section == ArchiveSection.Frames && name.SequenceEqual("frames"u8))
            {
                if (reader.TokenType != JsonTokenType.StartArray || field.Shape.Element is not { } frameShape) return false;
                var count = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (count >= 18000) return false;
                    if (count >= request.Offset && count - request.Offset < request.Limit)
                    {
                        var frame = JsonSerializer.Deserialize<CombatFrame>(ref reader, ToolJson.Options);
                        if (frame is null) return false;
                        (frames ??= []).Add(frame);
                    }
                    else if (!ValidateArchiveValue(ref reader, frameShape, out _)) return false;
                    count++;
                }
                if (reader.TokenType != JsonTokenType.EndArray) return false;
                total = count;
                continue;
            }

            var start = (int)reader.TokenStartIndex;
            if (!ValidateArchiveValue(ref reader, field.Shape, out var length)) return false;
            if (name.SequenceEqual("actionEffects"u8)) effects = length;
            else if (name.SequenceEqual("activity"u8)) activity = length;
            else if (name.SequenceEqual("evidence"u8)) evidence = length;
            else if (!name.SequenceEqual("frames"u8))
            {
                writer.WritePropertyName(name);
                writer.WriteRawValue(data[start..(int)reader.BytesConsumed], skipInputValidation: true);
            }
        }
        if (reader.TokenType != JsonTokenType.EndObject || reader.Read()) return false;
        if (request.Section == ArchiveSection.Frames && (total is null ||
            (request.AllowLegacyFull && total <= 300 && (effects ?? 0) <= 360 && (activity ?? 0) <= 128 && (evidence ?? 0) == 0)))
            return false;

        writer.WriteEndObject();
        writer.Flush();
        var metadata = JsonSerializer.Deserialize<CombatCapture>(header.WrittenSpan, ToolJson.Options);
        if (metadata is null) return false;
        capture = metadata;
        page = request.Section switch
        {
            ArchiveSection.Frames => FramePageResult(capture with { Frames = frames?.ToArray() ?? [] },
                total.GetValueOrDefault(), effects, activity, request.Offset, request.Limit),
            ArchiveSection.Timeline => BuildTimelinePage(data, capture, request, effects, timeline),
            ArchiveSection.Analysis when request.Analysis is { } options && analysis is not null =>
                BuildAnalysisPage(data, capture, request, options, analysis, effects, timeline),
            ArchiveSection.Activity => ActivityPageResult(capture, activity.HasValue, matched, activityPage, request.Offset, request.Limit),
            _ => EvidencePageResult(capture, request.CombatID ?? string.Empty, request.Section == ArchiveSection.Acr,
                evidence.HasValue, matched, eventPage, request.Offset, request.Limit),
        };
        return true;
    }

    private static object EvidencePageResult(CombatCapture capture, string id, bool acr, bool recorded, int matched,
        IReadOnlyList<FightEvidence>? events, int offset, int limit) => new
    {
        status = recorded ? "ok" : "not_recorded", combatID = id, capture.MechanicSources, capture.PartialStart,
        truncated = acr ? capture.DecisionsTruncated : capture.EvidenceTruncated, matched,
        events = events ?? Array.Empty<FightEvidence>(), nextOffset = offset + limit < matched ? (int?)offset + limit : null,
    };

    private static object ActivityPageResult(CombatCapture capture, bool recorded, int matched, IReadOnlyList<BattleActivity>? events,
        int offset, int limit) => new
    {
        status = recorded ? "ok" : "not_recorded", capture.BuildMvid, capture.PartialStart, capture.ActivityTruncated, matched,
        events = events ?? Array.Empty<BattleActivity>(), nextOffset = offset + limit < matched ? (int?)offset + limit : null,
        removalMeaning = "status_missing_or_actor_out_of_sampling_range",
    };

    private static bool ReadTimelineEntries(ref Utf8JsonReader reader, ArchiveJsonShape shape, ArchivePage request,
        EffectAccumulator? analysis, out int? observed, out List<TimelineEntry>? matches)
    {
        observed = null;
        matches = null;
        if (reader.TokenType == JsonTokenType.Null) return shape.Nullable;
        if (reader.TokenType != JsonTokenType.StartArray || shape.Element?.Fields is not { } fields) return false;
        var count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject || ++count > 50000) return false;
            if (request.Analysis is { } options && (count & 255) == 0) options.Token.ThrowIfCancellationRequested();
            var start = (int)reader.TokenStartIndex;
            long elapsed = 0;
            ulong sourceID = 0, targetID = 0;
            uint sequence = 0;
            byte actionType = 0;
            uint actionID = 0;
            var sourceMatches = request.Actor is null;
            var hasEffects = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var field = FindArchiveField(ref reader, fields);
                if (field is null || !reader.Read()) return false;
                var name = field.Name.AsSpan();
                if (name.SequenceEqual("effects"u8))
                {
                    if (reader.TokenType != JsonTokenType.StartArray || field.Shape.Element is not { } effectShape) return false;
                    var n = 0;
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        if (++n > 8 || reader.TokenType == JsonTokenType.Null || !ValidateArchiveValue(ref reader, effectShape, out _)) return false;
                    if (reader.TokenType != JsonTokenType.EndArray) return false;
                    hasEffects = true;
                    continue;
                }
                if (!ValidateArchiveValue(ref reader, field.Shape, out _)) return false;
                if (name.SequenceEqual("observedAtMs"u8)) elapsed = reader.GetInt64();
                else if ((analysis is not null || request.ActionID.HasValue) && name.SequenceEqual("actionID"u8)) actionID = reader.GetUInt32();
                else if ((analysis is not null || request.Actor is not null) && name.SequenceEqual("sourceID"u8))
                {
                    if (request.Actor is { } source)
                    {
                        if (reader.TokenType == JsonTokenType.String && !System.Text.Ascii.IsValid(reader.ValueSpan)) return false;
                        sourceMatches = reader.TokenType == JsonTokenType.String && System.Text.Ascii.EqualsIgnoreCase(reader.ValueSpan, source);
                    }
                    if (analysis is not null && !TryReadArchiveActorID(ref reader, out sourceID)) return false;
                }
                else if (analysis is not null)
                {
                    if (name.SequenceEqual("targetID"u8))
                    {
                        if (!TryReadArchiveActorID(ref reader, out targetID)) return false;
                    }
                    else if (name.SequenceEqual("globalSequence"u8)) sequence = reader.GetUInt32();
                    else if (name.SequenceEqual("actionType"u8)) actionType = reader.GetByte();
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || !hasEffects || elapsed is < -1500 or > 86400000) return false;
            if (!sourceMatches || elapsed < request.From || elapsed > request.To ||
                request.ActionID is { } wanted && actionID != wanted) continue;
            analysis?.Add(sourceID, targetID, sequence, actionID, actionType, (int)elapsed);
            if (request.Analysis is not { TimelineLimit: 0 })
                (matches ??= []).Add(new((int)elapsed, start, (int)reader.BytesConsumed - start));
        }
        observed = count;
        return reader.TokenType == JsonTokenType.EndArray;
    }

    private static object BuildTimelinePage(ReadOnlySpan<byte> data, CombatCapture capture, ArchivePage request, int? observed,
        List<TimelineEntry>? matches)
    {
        if (observed is null || capture.ActionEffectCoverage is null or "not_observed")
            return new { status = "no_effect_data", capture.ActionEffectCoverage, capture.BuildMvid };

        var count = matches?.Count ?? 0;
        var size = Math.Clamp(count - request.Offset, 0, request.Limit);
        ObservedEffect[] page = size == 0 ? [] : new ObservedEffect[size];
        long? gap = null;
        if (matches is not null)
        {
            var sorted = SortTimeline(matches);
            if (sorted.Length >= 2)
            {
                var maximum = 0;
                for (var i = 1; i < sorted.Length; i++)
                    maximum = Math.Max(maximum, sorted[i].Time - sorted[i - 1].Time);
                gap = maximum;
            }
            for (var i = 0; i < page.Length; i++)
            {
                page[i] = ReadTimelineEffect(data, sorted[request.Offset + i]);
            }
        }
        return TimelinePageResult(capture, observed.Value, count, page, gap, request.Offset);
    }

    private static object BuildAnalysisPage(ReadOnlySpan<byte> data, CombatCapture capture, ArchivePage request,
        ArchiveAnalysis options, EffectAccumulator accumulator, int? observed, List<TimelineEntry>? matches)
    {
        if (observed is null || capture.ActionEffectCoverage is null or "not_observed")
            return AnalysisPageResult(capture, options.ID, "no_effect_data", [], 0, [], request.Offset, request.Limit, options.TimelineLimit);

        options.Token.ThrowIfCancellationRequested();
        var groups = accumulator.Finish(options.Token);
        ObservedEffect[] timeline = [];
        if (matches is not null)
        {
            var sorted = SortTimeline(matches);
            timeline = new ObservedEffect[Math.Min(sorted.Length, options.TimelineLimit)];
            for (var i = 0; i < timeline.Length; i++) timeline[i] = ReadTimelineEffect(data, sorted[i]);
        }
        return AnalysisPageResult(capture, options.ID, "ok", groups, accumulator.Matched, timeline, request.Offset, request.Limit,
            options.TimelineLimit);
    }

    private static bool TryReadArchiveActorID(ref Utf8JsonReader reader, out ulong id)
    {
        id = 0;
        if (reader.TokenType != JsonTokenType.String) return true;
        var bytes = reader.ValueSpan;
        if (!System.Text.Ascii.IsValid(bytes)) return false;
        if (bytes.Length >= 2 && System.Text.Ascii.EqualsIgnoreCase(bytes[..2], "0x"u8) &&
            ulong.TryParse(bytes[2..], System.Globalization.NumberStyles.HexNumber, null, out var value) && value != 0xE0000000)
            id = value;
        return true;
    }

    private static Span<TimelineEntry> SortTimeline(List<TimelineEntry> matches)
    {
        var sorted = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(matches);
        for (var i = 1; i < sorted.Length; i++)
        {
            if (sorted[i].Time >= sorted[i - 1].Time) continue;
            sorted.Sort(static (a, b) => a.Time == b.Time ? a.Start.CompareTo(b.Start) : a.Time.CompareTo(b.Time));
            break;
        }
        return sorted;
    }

    private static ObservedEffect ReadTimelineEffect(ReadOnlySpan<byte> data, TimelineEntry row) =>
        JsonSerializer.Deserialize<ObservedEffect>(data.Slice(row.Start, row.Length), ToolJson.Options)
        ?? throw new JsonException("归档技能效果无效。");

    private sealed record ArchiveAnalysis(Guid ID, string GroupBy, int TimelineLimit, CancellationToken Token);

    private readonly record struct TimelineEntry(int Time, int Start, int Length);

    private static bool ReadArchiveEvents<T>(ReadOnlySpan<byte> data, ref Utf8JsonReader reader, ArchiveJsonShape shape, ArchivePage request,
        out int? count, out int matched, out List<T>? page)
    {
        count = null;
        matched = 0;
        page = null;
        if (reader.TokenType == JsonTokenType.Null) return shape.Nullable;
        if (reader.TokenType != JsonTokenType.StartArray || shape.Element?.Fields is not { } fields) return false;
        var seen = 0;
        var filter = request.Kind;
        var activity = request.Section == ArchiveSection.Activity;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject) return false;
            var start = (int)reader.TokenStartIndex;
            ReadOnlySpan<byte> kind = default;
            ReadOnlySpan<byte> actor = default;
            long elapsed = 0;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var field = FindArchiveField(ref reader, fields);
                if (field is null || !reader.Read() || !ValidateArchiveValue(ref reader, field.Shape, out _)) return false;
                if (field.Name.AsSpan().SequenceEqual("kind"u8))
                    kind = reader.TokenType == JsonTokenType.String ? reader.ValueSpan : default;
                else if (activity)
                {
                    if (field.Name.AsSpan().SequenceEqual("elapsedMs"u8)) elapsed = reader.GetInt64();
                    else if (request.Actor is not null && field.Name.AsSpan().SequenceEqual("actorID"u8))
                        actor = reader.TokenType == JsonTokenType.String ? reader.ValueSpan : default;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject) return false;
            if (++seen > 50000 && activity) return false;
            if (activity)
            {
                if (elapsed < request.From || elapsed > request.To ||
                    request.Actor is { } wanted && !System.Text.Ascii.EqualsIgnoreCase(actor, wanted)) continue;
            }
            else if (kind.SequenceEqual("acr_decision"u8) != (request.Section == ArchiveSection.Acr)) continue;
            if (filter is not null && !kind.SequenceEqual(filter)) continue;
            if (matched >= request.Offset && matched - request.Offset < request.Limit)
            {
                if (JsonSerializer.Deserialize<T>(data[start..(int)reader.BytesConsumed], ToolJson.Options) is not { } row) return false;
                (page ??= []).Add(row);
            }
            matched++;
        }
        count = seen;
        return reader.TokenType == JsonTokenType.EndArray;
    }

    private static ArchiveJsonField? FindArchiveField(ref Utf8JsonReader reader, ArchiveJsonField[] fields)
    {
        if (reader.ValueIsEscaped) return null;
        var name = reader.ValueSpan;
        foreach (var field in fields)
            if (name.Length == field.Name.Length && name.SequenceEqual(field.Name)) return field;
        return null;
    }

    private static bool ValidateArchiveValue(ref Utf8JsonReader reader, ArchiveJsonShape shape, out int? length)
    {
        length = null;
        if (reader.TokenType == JsonTokenType.Null) return shape.Nullable;
        if (shape.Element is { } element)
        {
            if (reader.TokenType != JsonTokenType.StartArray) return false;
            var count = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (!ValidateArchiveValue(ref reader, element, out _)) return false;
                count++;
            }
            length = count;
            return reader.TokenType == JsonTokenType.EndArray;
        }
        if (shape.Fields is { } fields)
        {
            if (reader.TokenType != JsonTokenType.StartObject) return false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var field = FindArchiveField(ref reader, fields);
                if (field is null || !reader.Read() || !ValidateArchiveValue(ref reader, field.Shape, out _)) return false;
            }
            return reader.TokenType == JsonTokenType.EndObject;
        }

        switch (shape.Scalar)
        {
            case ArchiveScalar.Json:
                reader.Skip();
                return true;
            case ArchiveScalar.Boolean:
                return reader.TokenType is JsonTokenType.True or JsonTokenType.False;
            case ArchiveScalar.String:
                return reader.TokenType == JsonTokenType.String && !reader.ValueIsEscaped && Utf8.IsValid(reader.ValueSpan);
            case ArchiveScalar.Date:
                return reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out _);
            case ArchiveScalar.Guid:
                return reader.TokenType == JsonTokenType.String && reader.TryGetGuid(out _);
            default:
                if (reader.TokenType != JsonTokenType.Number) return false;
                return shape.Scalar switch
                {
                    ArchiveScalar.Byte => reader.TryGetByte(out _),
                    ArchiveScalar.UInt16 => reader.TryGetUInt16(out _),
                    ArchiveScalar.UInt32 => reader.TryGetUInt32(out _),
                    ArchiveScalar.UInt64 => reader.TryGetUInt64(out _),
                    ArchiveScalar.Int32 => reader.TryGetInt32(out _),
                    ArchiveScalar.Int64 => reader.TryGetInt64(out _),
                    ArchiveScalar.Single => reader.TryGetSingle(out _),
                    _ => false,
                };
        }
    }

    private static ArchiveJsonShape? BuildArchiveShape(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        var nullable = !type.IsValueType || underlying is not null;
        type = underlying ?? type;
        if (type.IsSZArray)
            return BuildArchiveShape(type.GetElementType() ?? typeof(void)) is { } item ? new(true, Element: item) : null;
        var scalar = type == typeof(object) || type == typeof(JsonElement) ? ArchiveScalar.Json : Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => ArchiveScalar.Boolean,
            TypeCode.String => ArchiveScalar.String,
            TypeCode.Byte => ArchiveScalar.Byte,
            TypeCode.UInt16 => ArchiveScalar.UInt16,
            TypeCode.UInt32 => ArchiveScalar.UInt32,
            TypeCode.UInt64 => ArchiveScalar.UInt64,
            TypeCode.Int32 => ArchiveScalar.Int32,
            TypeCode.Int64 => ArchiveScalar.Int64,
            TypeCode.Single => ArchiveScalar.Single,
            _ when type == typeof(DateTimeOffset) => ArchiveScalar.Date,
            _ when type == typeof(Guid) => ArchiveScalar.Guid,
            _ => ArchiveScalar.None,
        };
        if (scalar != ArchiveScalar.None) return new(nullable || scalar == ArchiveScalar.Json, scalar);

        var info = ToolJson.Options.GetTypeInfo(type);
        if (info.Kind != JsonTypeInfoKind.Object || info.Properties.Count > 64) return null;
        var fields = new ArchiveJsonField[info.Properties.Count];
        for (var i = 0; i < fields.Length; i++)
        {
            var property = info.Properties[i];
            if (property.Set is null || property.CustomConverter is not null || property.IsExtensionData ||
                property.IsRequired || property.NumberHandling is not null ||
                BuildArchiveShape(property.PropertyType) is not { } value)
                return null;
            fields[i] = new(System.Text.Encoding.UTF8.GetBytes(property.Name), value, 1UL << i);
        }
        return new(nullable, Fields: fields);
    }

    private sealed record ArchivePage(ArchiveSection Section, int Offset, int Limit, bool AllowLegacyFull = false,
        byte[]? Kind = null, string? CombatID = null, byte[]? Actor = null, int From = 0, int To = 0, uint? ActionID = null, ArchiveAnalysis? Analysis = null);
    private enum ArchiveSection { Frames, Mechanics, Acr, Activity, Timeline, Analysis }

    private sealed record ArchiveJsonShape(bool Nullable, ArchiveScalar Scalar = ArchiveScalar.None,
        ArchiveJsonShape? Element = null, ArchiveJsonField[]? Fields = null);
    private sealed record ArchiveJsonField(byte[] Name, ArchiveJsonShape Shape, ulong Mask);
    private enum ArchiveScalar { None, Json, Boolean, String, Date, Guid, Byte, UInt16, UInt32, UInt64, Int32, Int64, Single }
}
