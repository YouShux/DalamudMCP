using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace DalamudMCP.Plugin;

internal static class TraceLogs
{
    private static readonly Regex SessionFile = new(@"\Atrace-([1-9][0-9]{0,9})\.jsonl(?:\.1)?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static string? GetDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.IsPathFullyQualified(profile) ? Path.Combine(profile, "XIVLauncherCN", "dalamud-mcp") : null;
    }

    public static (int Recycled, int Failed) RecycleInactive()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var path = GetDirectory() ?? throw new IOException("XIVLauncherCN app data path is unavailable.");
        var launcher = new DirectoryInfo(Path.GetDirectoryName(path)!);
        if (!launcher.Exists) return (0, 0);

        var root = launcher.ResolveLinkTarget(true)?.FullName ?? launcher.FullName;
        var directory = new DirectoryInfo(Path.Combine(root, Path.GetFileName(path)));
        if (!directory.Exists) return (0, 0);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The MCP trace directory is a link.");

        var recycled = 0;
        var failed = 0;
        foreach (var file in directory.GetFiles())
        {
            var match = SessionFile.Match(file.Name);
            if (!match.Success || !int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                continue;

            try
            {
                if (IsRunning(pid) || (File.GetAttributes(file.FullName) & FileAttributes.ReparsePoint) != 0) continue;
                FileSystem.DeleteFile(file.FullName, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                recycled++;
            }
            catch (OperationCanceledException)
            {
                failed++;
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }
        return (recycled, failed);
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
