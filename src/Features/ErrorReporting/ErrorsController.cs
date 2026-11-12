using Aptabase.Data;
using Aptabase.Features.Authentication;
using Aptabase.Features.ErrorReporting.Buffer;
using Aptabase.Features.Ingestion;
using Aptabase.Features.Stats;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aptabase.Features.ErrorReporting;

[ApiController]
[ResponseCache(NoStore = true)]
public class ErrorsController : ControllerBase
{
    private readonly IIssueStore _issues;
    private readonly IErrorBuffer _buffer;
    private readonly IPiiSanitizer _piiSanitizer;
    private readonly IErrorQueryClient _errorQueryClient;
    private readonly IDbContext _db;
    private readonly EnvSettings _env;
    private readonly ILogger<ErrorsController> _logger;

    public ErrorsController(
        IErrorBuffer buffer,
        IIssueStore issues,
        IPiiSanitizer piiSanitizer,
        IErrorQueryClient errorQueryClient,
        IDbContext db,
        EnvSettings env,
        ILogger<ErrorsController> logger)
    {
        _buffer = buffer;
        _issues = issues;
        _piiSanitizer = piiSanitizer;
        _errorQueryClient = errorQueryClient;
        _db = db;
        _env = env;
        _logger = logger;
    }

    [HttpPost]
    [EnableCors("AllowAny")]
    [EnableRateLimiting("EventIngestion")]
    [ResolveErrorAppKey]
    [Route("/api/v0/error")]
    [RequestSizeLimit(131072)]
    public async Task<IActionResult> PostError([FromBody] ErrorBody body, CancellationToken cancellationToken)
    {
        // Validate the error body
        if (!body.IsValid())
        {
            return BadRequest(new { error = "Invalid error payload" });
        }

        // Normalize timestamp (convert to UTC, clamp to now if in future)
        body.NormalizeTimestamp();

        // Normalize severity/kind against the known values (unknown values become empty)
        body.NormalizeSeverityAndKind();

        // Missing, unknown and locked App-Keys were already rejected by ResolveErrorAppKey, before the body was parsed
        var app = ResolveAppKeyAttribute.GetApp(HttpContext);

        var errorId = body.EventId is null ? Guid.NewGuid().ToString() : Guid.Parse(body.EventId).ToString();

        // Sanitize PII from error message and stack trace
        var sanitizedErrorMessage = _piiSanitizer.Sanitize(body.ErrorMessage);
        var sanitizedStackTrace = _piiSanitizer.Sanitize(body.StackTrace);

        // Create tracking error
        var trackingError = new TrackingError
        {
            ErrorId = errorId,
            AppId = app.Id,
            Timestamp = body.Timestamp!.Value,
            ErrorMessage = sanitizedErrorMessage,
            ErrorType = _piiSanitizer.Sanitize(body.ErrorType),
            StackTrace = sanitizedStackTrace,
            Release = _piiSanitizer.Sanitize(body.Release ?? body.AppVersion),
            Environment = _piiSanitizer.Sanitize(body.Environment),
            Platform = _piiSanitizer.Sanitize(body.Platform),
            OsName = _piiSanitizer.Sanitize(body.OsName),
            OsVersion = _piiSanitizer.Sanitize(body.OsVersion),
            AppVersion = _piiSanitizer.Sanitize(body.AppVersion),
            SdkVersion = _piiSanitizer.Sanitize(body.SdkVersion),
            SessionId = body.SessionId,
            Severity = body.Severity,
            Kind = body.Kind,
            IsDebug = body.IsDebug
        };

        var result = await _issues.Record(trackingError, cancellationToken, _env.ErrorQuotaEnabled);
        if (result == IssueRecordResult.Duplicate) return Accepted();
        if (result == IssueRecordResult.AppUnavailable) return BadRequest(new { error = "App is unavailable" });
        if (result == IssueRecordResult.QuotaExceeded)
        {
            // Existing SDKs drop 403 but retry 429, which is reserved for the rate limiter.
            _logger.LogDebug("Error quota exceeded for app {AppId}", app.Id);
            return StatusCode(403, new { error = "Error quota exceeded" });
        }

        // Add to buffer
        _buffer.Add(ref trackingError);

        // Return 202 Accepted
        return Accepted();
    }

    [HttpGet]
    [IsAuthenticated]
    [EnableRateLimiting("Stats")]
    [Route("/api/v0/apps/{appId}/errors")]
    public async Task<IActionResult> GetErrors(
        string appId,
        [FromQuery] string? buildMode,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        [FromQuery] string? errorType,
        [FromQuery] string? osName,
        [FromQuery] string? severity,
        [FromQuery] string? kind,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        // Check if user has access to the app (always against the raw app id)
        var user = HttpContext.GetCurrentUserIdentity();
        var hasAccess = await _db.HasReadAccessToApp(appId, user, cancellationToken);
        if (!hasAccess)
        {
            return StatusCode(403);
        }

        // Debug builds are stored under a suffixed app id, same as analytics events
        var queryAppId = ResolveAppId(appId, buildMode);

        // Set default date range if not provided (last 7 days)
        var end = endDate ?? DateTime.UtcNow;
        var start = startDate ?? end.AddDays(-7);

        // Validate limit
        if (limit < 1 || limit > 100)
        {
            limit = 50;
        }

        // Validate offset
        if (offset < 0)
        {
            offset = 0;
        }

        // Query errors
        var errors = await _errorQueryClient.GetErrorsAsync(
            queryAppId,
            start,
            end,
            errorType,
            osName,
            severity,
            kind,
            offset,
            limit,
            cancellationToken);

        // Get total count for pagination
        var totalCount = await _errorQueryClient.GetErrorCountAsync(
            queryAppId,
            start,
            end,
            errorType,
            osName,
            severity,
            kind,
            cancellationToken);

        return Ok(new
        {
            errors = errors,
            pagination = new
            {
                offset = offset,
                limit = limit,
                total = totalCount
            }
        });
    }

    // Note on routing: the literal segment "types" takes precedence over the
    // {errorId} template on /errors/{errorId}, so both routes coexist safely.
    [HttpGet]
    [IsAuthenticated]
    [EnableRateLimiting("Stats")]
    [Route("/api/v0/apps/{appId}/errors/types")]
    public async Task<IActionResult> GetErrorTypes(
        string appId,
        [FromQuery] string? buildMode,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        CancellationToken cancellationToken = default)
    {
        // Check if user has access to the app (always against the raw app id)
        var user = HttpContext.GetCurrentUserIdentity();
        var hasAccess = await _db.HasReadAccessToApp(appId, user, cancellationToken);
        if (!hasAccess)
        {
            return StatusCode(403);
        }

        // Debug builds are stored under a suffixed app id, same as analytics events
        var queryAppId = ResolveAppId(appId, buildMode);

        // Set default date range if not provided (last 7 days)
        var end = endDate ?? DateTime.UtcNow;
        var start = startDate ?? end.AddDays(-7);

        var types = await _errorQueryClient.GetErrorTypesAsync(queryAppId, start, end, cancellationToken);

        return Ok(types);
    }

    [HttpGet]
    [IsAuthenticated]
    [EnableRateLimiting("Stats")]
    [Route("/api/v0/apps/{appId}/errors/{errorId}")]
    public async Task<IActionResult> GetErrorById(
        string appId,
        string errorId,
        [FromQuery] string? buildMode = null,
        CancellationToken cancellationToken = default)
    {
        // Check if user has access to the app (always against the raw app id)
        var user = HttpContext.GetCurrentUserIdentity();
        var hasAccess = await _db.HasReadAccessToApp(appId, user, cancellationToken);
        if (!hasAccess)
        {
            return StatusCode(403);
        }

        // Debug builds are stored under a suffixed app id, same as analytics events
        var queryAppId = ResolveAppId(appId, buildMode);

        // Query error by ID
        var error = await _errorQueryClient.GetErrorByIdAsync(queryAppId, errorId, cancellationToken);

        // Return 404 if error not found or doesn't belong to app
        if (error == null)
        {
            return NotFound(new { error = "Error not found" });
        }

        return Ok(error);
    }

    [HttpOptions]
    [EnableCors("AllowAny")]
    [Route("/api/v0/error")]
    public IActionResult OptionsError()
    {
        return Ok();
    }

    // Maps the buildMode query parameter to the stored app id, mirroring Stats.QueryParams.Parse()
    private static string ResolveAppId(string appId, string? buildMode)
    {
        return buildMode?.ToLower() switch
        {
            "debug" => $"{appId}_DEBUG",
            _ => appId,
        };
    }
}
