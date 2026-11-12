using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aptabase.Features.ErrorReporting;

public static partial class IssueFingerprint
{
    public static string Create(string errorType, string message, string? stackTrace)
    {
        // Version and JSON tuple keep changes explicit and field boundaries unambiguous.
        var stack = Normalize(stackTrace ?? "");
        var input = JsonSerializer.Serialize(new[] { "v1", Normalize(errorType),
            stack.Length > 0 ? "stack" : "message", stack.Length > 0 ? stack : Normalize(message) });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private static string Normalize(string value) => Whitespace().Replace(value.Trim(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
