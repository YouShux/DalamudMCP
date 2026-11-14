using System.Reflection;

namespace DalamudMCP.Plugin;

// Compatibility with the current native runtime, whose TraceLog uses a
// process-local lock around a machine-shared filename. Keep each game's
// audit records independent, including calls made outside this gateway.
internal static class NativeTraceLog
{
    public static void Isolate(object trace, int processID)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = trace.GetType();
        var property = type.GetProperty("FilePath", flags);
        if (property?.GetValue(trace) is not string current ||
            !string.Equals(Path.GetFileName(current), "trace.jsonl", StringComparison.OrdinalIgnoreCase)) return;
        var field = type.GetField("<FilePath>k__BackingField", flags);
        if (field is null || field.FieldType != typeof(string)) return;
        var destination = Path.Combine(Path.GetDirectoryName(current) ?? string.Empty, $"trace-{processID}.jsonl");
        field.SetValue(trace, destination);
    }
}
