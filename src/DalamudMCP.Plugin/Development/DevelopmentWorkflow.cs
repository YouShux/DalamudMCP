using System.Security.Cryptography;
using System.Text;

namespace DalamudMCP.Plugin.Tools;

internal static class DevelopmentWorkflow
{
    internal const string TOOL = "read_development_workflow";
    private const string GUIDE = """
        # DalamudMCP 开发验收

        1. 先用 `get_session_status` 核对会话，再用 `get_dev_build` 核对登记路径。独立编译后读取本次 DLL 的 MVID。
        2. 使用 `wait_for_dev_build` 核对加载路径和 MVID，要求 `status=ready` 且 `buildVerified=true`。首次注册和手动重载需有授权。网关重载后重新连接并列出工具。
        3. 使用 `get_dalamud_log` 查运行故障，按场景独立回读窗口、控件或游戏状态。派发成功不代表操作生效；超时后先查状态。
        4. 客户端数据先核对语言、表名及行号。默认只观察连接元数据；需要采集载荷时按授权限定范围。
        """;

    private static readonly Lazy<Bundle> Package = new(LoadBundle);

    internal static object Describe(bool includeSkill, bool includeBundle, string? knownRevision)
    {
        if (knownRevision is not null && (knownRevision.Length != 64 || !knownRevision.All(char.IsAsciiHexDigit)))
            throw new ArgumentException("known_revision 必须为 64 位十六进制文本。");
        var assembly = typeof(DevelopmentWorkflow).Assembly;
        var package = Package.Value;
        var skill = package.Files.Single(file => file.Path == "SKILL.md");
        return new
        {
            status = "ok", buildMvid = assembly.ManifestModule.ModuleVersionId,
            assemblyVersion = assembly.GetName().Version?.ToString(), format = "markdown", markdown = GUIDE,
            skill = includeSkill ? new
            {
                name = "dalamudmcp-plugin-workflow", fileName = skill.Path, sha256 = skill.Sha256,
                content = Encoding.UTF8.GetString(skill.Data),
            } : null,
            revision = package.Revision, revisionComparison = knownRevision is null ? "unknown" :
                package.Revision.Equals(knownRevision, StringComparison.OrdinalIgnoreCase) ? "current" : "different_order_unknown",
            clientInstallState = "unknown",
            manifest = new
            {
                format = 1, generation = 2, name = "dalamudmcp-plugin-workflow", revision = package.Revision,
                totalBytes = package.Files.Sum(file => file.Data.Length),
                files = package.Files.Select(file => new { file.Path, bytes = file.Data.Length, file.Sha256 }).ToArray(),
            },
            bundle = includeBundle ? package.Files.Select(file => new { file.Path, base64 = Convert.ToBase64String(file.Data) }).ToArray() : null,
        };
    }

    private static Bundle LoadBundle()
    {
        const string HEADER = """
            ---
            name: dalamudmcp-plugin-workflow
            description: Verify local Dalamud builds using MVID, logs and independent state readback.
            ---
            """;
        var data = Encoding.UTF8.GetBytes(HEADER + "\n\n" + GUIDE + "\n");
        var file = new BundleFile("SKILL.md", data, Convert.ToHexString(SHA256.HashData(data)));
        var canonical = file.Path + "\0" + file.Sha256 + "\n";
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), [file]);
    }

    private sealed record Bundle(string Revision, BundleFile[] Files);
    private sealed record BundleFile(string Path, byte[] Data, string Sha256);
}
