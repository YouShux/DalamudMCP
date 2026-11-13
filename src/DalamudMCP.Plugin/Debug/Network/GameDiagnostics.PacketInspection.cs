using System.Text.Json;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameDiagnostics
{
    public object InspectPacket(string tool, ToolArguments input, PacketLayoutCatalog? layouts = null)
    {
        var trace = zoneTrace;
        if (trace is null) return new { status = "no_capture" };
        if (!Guid.TryParseExact(input.Text("trace_id"), "D", out var expected) || expected != trace.ID)
            return new { status = "trace_changed" };
        var packet = trace.GetPacket(input.Integer("packet_index", 0, 0, 511));
        if (packet is null) return new { status = "packet_not_found" };
        var offset = input.Integer("offset", 0, 0, 65536);
        var count = input.Integer("count", 128, 1, 1024);
        if (tool is "decode_zone_packet" or "parse_zone_packet")
        {
            var origin = input.Text("origin", "payload");
            if (origin is not ("payload" or "captured")) throw new ArgumentException("origin 必须为 payload 或 captured。");
            var start = origin == "captured" ? 0 : packet.Packet.Layout switch
            {
                "zone_send_payload" => 0,
                "zone_receive_ipc" => 16,
                _ => throw new InvalidOperationException("未知的原始包布局。"),
            };
            PacketLayoutCatalog.Layout? layout = null;
            BundledPacketLayouts.SdkReview? review = null;
            if (tool == "parse_zone_packet")
            {
                var selected = layouts?.Select(packet.Direction, packet.OpcodeNames, input.Has("layout_name") ? input.Text("layout_name") : null, PacketLayoutCatalog.ReadOrigin(input)) ?? [];
                if (selected.Length != 1) return new { status = selected.Length == 0 ? "layout_not_found" : "ambiguous_layout", packet.OpcodeNames,
                    candidates = selected.Select(entry => new { entry.Name, entry.Origin, entry.Sha256 }).ToArray() };
                layout = selected[0];
                if (layout.Origin == "bundled")
                {
                    if (origin != "payload") throw new ArgumentException("内置业务包定义必须从 payload 起点解释。");
                    review = BundledPacketLayouts.CompareSdk(layout);
                    if (review.Differences.Length != 0 && (!input.Has("layout_name") || !input.Boolean("accept_sdk_difference")))
                        return new { status = "sdk_declaration_conflict", sdkReview = review, layout.Name, layout.Sha256, compatibilityVerified = false };
                }
                if (input.Has("expected_layout_sha256") && input.Text("expected_layout_sha256") != layout.Sha256)
                    return new { status = "layout_changed", layout.Sha256 };
            }
            var interpretation = DalamudMCP.Shared.ToolJson.Element(PacketFieldDecoder.Decode(packet.Bytes, layout?.Fields ?? input.Element("fields"), start,
                input.Integer("field_offset", 0, 0, 8192), input.Integer("field_limit", 128, 1, 128)));
            var status = interpretation.GetProperty("status").GetString();
            var minimumMissing = layout is not null && packet.Bytes.Length - start < layout.MinimumBytes;
            var truncated = packet.Bytes.Length < packet.Packet.DataLength;
            if (minimumMissing) status = "short_payload";
            else if (truncated && status == "decoded_unverified") status = "truncated_capture";
            return new { status, layout = layout is null ? null : new { layout.Name, layout.Version, layout.Sha256, layout.Source,
                    layout.License, layout.OmittedFields, layout.Origin, layout.MinimumBytes },
                selection = layout is null ? "supplied_fields" : input.Has("layout_name") ? "caller_selected" : "opcode_name", sdkReview = review,
                compatibilityVerified = false, minimumMissing, truncatedCapture = truncated,
                traceID = trace.ID, packet.Index, packet.OpcodeNames, interpretation };
        }
        if (tool == "compare_zone_packets")
        {
            var other = trace.GetPacket(input.Integer("other_packet_index", 0, 0, 511));
            if (other is null) return new { status = "packet_not_found" };
            var arguments = new ToolArguments(JsonSerializer.SerializeToElement(new
            {
                first_hex = Convert.ToHexString(packet.Bytes), second_hex = Convert.ToHexString(other.Bytes), offset, count = Math.Min(count, 256),
            }));
            return new { traceID = trace.ID, firstIndex = packet.Index, secondIndex = other.Index,
                sameLayout = packet.Packet.Layout == other.Packet.Layout, sameOpcode = packet.Packet.Opcode == other.Packet.Opcode,
                comparison = CompareSamples(arguments) };
        }
        var length = Math.Min(count, Math.Max(0, packet.Bytes.Length - offset));
        var bytes = packet.Bytes.AsSpan(Math.Min(offset, packet.Bytes.Length), length);
        var ascii = new char[length];
        for (var index = 0; index < length; index++) ascii[index] = bytes[index] is >= 32 and < 127 ? (char)bytes[index] : '.';
        return new { status = "ok", traceID = trace.ID, packet.Index, packet.Direction, packet.Packet, packet.OpcodeNames,
            capturedLength = packet.Bytes.Length, truncated = packet.Bytes.Length < packet.Packet.DataLength,
            offset, hex = Convert.ToHexString(bytes), ascii = new string(ascii),
            nextOffset = offset + length < packet.Bytes.Length ? (int?)offset + length : null };
    }
}
