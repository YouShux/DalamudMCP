
namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameDiagnostics
{
    internal const string DECODE_TOOL = "decode_ipc_sample";
    internal const string COMPARE_SAMPLE_TOOL = "compare_ipc_samples";

    public static object DecodeSample(ToolArguments input)
    {
        var bytes = ParseHex(input.Element("hex").GetString() ?? string.Empty);
        return PacketFieldDecoder.Decode(bytes, input.Element("fields"), input.Integer("base_offset", 0, 0, 65536),
            input.Integer("field_offset", 0, 0, 8192), input.Integer("field_limit", 128, 1, 128), strict: true);
    }

    public static object CompareSamples(ToolArguments input)
    {
        var first = ParseHex(input.Element("first_hex").GetString() ?? string.Empty);
        var second = ParseHex(input.Element("second_hex").GetString() ?? string.Empty);
        var offset = input.Integer("offset", 0, 0, 65536);
        var count = input.Integer("count", 64, 1, 256);
        var total = Math.Max(first.Length, second.Length);
        var end = Math.Min(offset + count, total);
        var differences = new List<object>();
        for (var index = offset; index < end; index++)
        {
            var a = index < first.Length ? (byte?)first[index] : null;
            var b = index < second.Length ? (byte?)second[index] : null;
            if (a != b) differences.Add(new { offset = index, first = a, second = b });
        }
        return new { status = "ok", firstBytes = first.Length, secondBytes = second.Length, differences,
            nextOffset = end < total ? (int?)end : null };
    }

    private static byte[] ParseHex(string hex)
    {
        if (hex.Length > 131072 || (hex.Length & 1) != 0 || !hex.All(Uri.IsHexDigit))
            throw new ArgumentException("样本必须为 1 至 65536 字节的连续十六进制文本。");
        return Convert.FromHexString(hex);
    }
}
