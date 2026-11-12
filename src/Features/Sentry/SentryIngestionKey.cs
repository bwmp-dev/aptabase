using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aptabase.Features.Sentry;

public static class SentryIngestionKey
{
    public static string Create(SentryProject project)
    {
        // SDK DSN parsers require alphanumeric keys. Derivation preserves app-key rotation
        // and binds each representation to one project without storing a second credential.
        var context = JsonSerializer.Serialize(new[] { "aptabase-sentry-v1", project.AppId,
            project.Id.ToString(CultureInfo.InvariantCulture), project.IsDebug ? "debug" : "release" });
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(project.AppKey), Encoding.UTF8.GetBytes(context))).ToLowerInvariant();
    }

    public static bool Matches(SentryProject project, string supplied)
    {
        var bytes = Encoding.UTF8.GetBytes(supplied);
        return CryptographicOperations.FixedTimeEquals(bytes, Encoding.UTF8.GetBytes(Create(project)))
            || CryptographicOperations.FixedTimeEquals(bytes, Encoding.UTF8.GetBytes(project.AppKey));
    }
}
