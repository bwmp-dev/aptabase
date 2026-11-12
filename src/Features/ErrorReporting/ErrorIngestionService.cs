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
            ErrorType = sanitizer.SanitizeLabel(body.ErrorType, 100),
            StackTrace = sanitizedStackTrace,
            Release = sanitizer.SanitizeLabel(body.Release ?? body.AppVersion, 100),
            Environment = sanitizer.SanitizeLabel(body.Environment, 50),
            Platform = sanitizer.SanitizeLabel(body.Platform, 30),
            OsName = sanitizer.SanitizeLabel(body.OsName, 30),
            OsVersion = sanitizer.SanitizeLabel(body.OsVersion, 100),
            AppVersion = sanitizer.SanitizeLabel(body.AppVersion, 50),
            SdkVersion = sanitizer.SanitizeLabel(body.SdkVersion, 40),
            SessionId = body.SessionId,
            Severity = body.Severity,
            Kind = body.Kind,
            IsDebug = body.IsDebug
        };

        return await issues.Record(trackingError, cancellationToken, env.ErrorQuotaEnabled);
    }
}
