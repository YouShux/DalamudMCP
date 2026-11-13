using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace DalamudMCP.Shared;

internal static class LocalBearer
{
    public static string? ReadToken(bool preferUserStore = false)
    {
        if (preferUserStore && OperatingSystem.IsWindows()
            && Environment.GetEnvironmentVariable(ENVIRONMENT_VARIABLE, EnvironmentVariableTarget.User) is { } userValue
            && IsValid(userValue))
            return userValue;
        var value = Environment.GetEnvironmentVariable(ENVIRONMENT_VARIABLE);
        return IsValid(value) ? value : null;
    }

    public static byte[] Hash(string value) => SHA256.HashData(Encoding.ASCII.GetBytes(value));

    public static bool Matches(string? authorization, byte[] expectedHash)
        => AuthenticationHeaderValue.TryParse(authorization, out var header)
           && header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
           && header.Parameter is { } value && IsValid(value)
           && CryptographicOperations.FixedTimeEquals(Hash(value), expectedHash);

    private static bool IsValid(string? value)
        => value is { Length: >= 32 and <= 256 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public const string ENVIRONMENT_VARIABLE = "DALAMUD_MCP_BEARER_TOKEN";
}
