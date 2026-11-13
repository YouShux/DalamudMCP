using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace DalamudMCP.Plugin.Tools;

internal static class DevPluginPackage
{
    public static void CheckPath(string path)
    {
        var current = Path.GetPathRoot(path) ?? throw new ArgumentException("需要本机绝对路径。");
        if (new DriveInfo(current).DriveType != DriveType.Fixed)
            throw new ArgumentException("开发插件必须位于本机固定磁盘上。");
        var rootLength = current.Length;
        CheckPart(current);
        foreach (var part in path[rootLength..].Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            CheckPart(current);
        }

        static void CheckPart(string current)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("开发插件路径不能经过符号链接或其他重解析点。");
        }
    }

    public static Guid ReadMVID(Stream stream)
    {
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!pe.HasMetadata) throw new BadImageFormatException("目标 DLL 缺少托管元数据。");
        var metadata = pe.GetMetadataReader();
        if (!metadata.IsAssembly) throw new BadImageFormatException("目标 DLL 不是托管程序集。");
        var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        return mvid != Guid.Empty ? mvid : throw new BadImageFormatException("目标 DLL 缺少有效的 MVID。");
    }

    public static string ValidateManifest(Stream stream, string dllName, string hostName, int apiLevel)
    {
        if (stream.Length is <= 0 or > 128 * 1024) throw new ArgumentException("开发插件清单大小无效。");
        using var reader = new StreamReader(stream, leaveOpen: true);
        using var document = JsonDocument.Parse(reader.ReadToEnd(), new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("开发插件清单必须为 JSON 对象。");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in root.EnumerateObject())
            if (!keys.Add(item.Name) || item.Name.StartsWith('$'))
                throw new ArgumentException("开发插件清单包含重复键或不受支持的元数据键。");
        if (!root.TryGetProperty("InternalName", out var nameValue) || nameValue.ValueKind != JsonValueKind.String ||
            nameValue.GetString() is not { Length: > 0 and <= 128 } name ||
            !name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') ||
            !name.Equals(dllName, StringComparison.OrdinalIgnoreCase) || name.Equals(hostName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("清单 InternalName 必须匹配 DLL 文件名，且不能是 DalamudMCP 自身。");
        if (!root.TryGetProperty("Name", out var display) || display.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(display.GetString()))
            throw new ArgumentException("开发插件清单缺少 Name。");
        if (!root.TryGetProperty("DalamudApiLevel", out var api) || api.ValueKind != JsonValueKind.Number ||
            !api.TryGetInt32(out var declaredAPI) || declaredAPI != apiLevel)
            throw new ArgumentException("开发插件清单的 API 等级与当前卫月不匹配。");
        return name;
    }
}
