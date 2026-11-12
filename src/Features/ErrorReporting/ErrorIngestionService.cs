using Aptabase.Features.ErrorReporting.Buffer;

namespace Aptabase.Features.ErrorReporting;

public class ErrorIngestionService(IIssueStore issues, IPiiSanitizer sanitizer, EnvSettings env)
{
    public async Task<IssueRecordResult> Ingest(string appId, ErrorBody body, CancellationToken cancellationToken)
    {
        // Normalize timestamp (convert to UTC, clamp to now if in future)
        body.NormalizeTimestamp();

        // Normalize severity/kind against the known values (unknown values become empty)
        body.NormalizeSeverityAndKind();

        var errorId = body.EventId is null ? Guid.NewGuid().ToString() : Guid.Parse(body.EventId).ToString();

        // Sanitize PII from error message and stack trace
        var sanitizedErrorMessage = sanitizer.Sanitize(body.ErrorMessage);
        var sanitizedStackTrace = sanitizer.Sanitize(body.StackTrace);

        // Create tracking error
        var trackingError = new TrackingError
        {
            ErrorId = errorId,
            AppId = appId,
            Timestamp = body.Timestamp!.Value,
            ErrorMessage = sanitizedErrorMessage,
            ErrorType = sanitizer.Sanitize(body.ErrorType),
            StackTrace = sanitizedStackTrace,
            Release = sanitizer.Sanitize(body.Release ?? body.AppVersion),
            Environment = sanitizer.Sanitize(body.Environment),
            Platform = sanitizer.Sanitize(body.Platform),
            OsName = sanitizer.Sanitize(body.OsName),
            OsVersion = sanitizer.Sanitize(body.OsVersion),
            AppVersion = sanitizer.Sanitize(body.AppVersion),
            SdkVersion = sanitizer.Sanitize(body.SdkVersion),
            SessionId = body.SessionId,
            Severity = body.Severity,
            Kind = body.Kind,
            IsDebug = body.IsDebug
        };

        return await issues.Record(trackingError, cancellationToken, env.ErrorQuotaEnabled);
    }
}
