namespace Aptabase.Features.ErrorReporting;

public static class PiiSanitizerExtensions
{
    public static string SanitizeLabel(this IPiiSanitizer sanitizer, string? value, int maximumLength)
    {
        var sanitized = sanitizer.Sanitize(value);
        if (sanitized.Length <= maximumLength) return sanitized;
        // Replacement markers can expand input; keep stored labels within the matching query bounds.
        if (char.IsHighSurrogate(sanitized[maximumLength - 1])) maximumLength--;
        return sanitized[..maximumLength];
    }
}
