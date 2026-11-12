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
    private readonly ErrorIngestionService _ingestion;
    private readonly IErrorQueryClient _errorQueryClient;
    private readonly IDbContext _db;
    private readonly ILogger<ErrorsController> _logger;

    public ErrorsController(
        ErrorIngestionService ingestion,
        IErrorQueryClient errorQueryClient,
        IDbContext db,
        ILogger<ErrorsController> logger)
    {
        _ingestion = ingestion;
        _errorQueryClient = errorQueryClient;
        _db = db;
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

        var app = ResolveAppKeyAttribute.GetApp(HttpContext);
        var result = await _ingestion.Ingest(app.Id, body, cancellationToken);
        if (result == IssueRecordResult.Duplicate) return Accepted();
        if (result == IssueRecordResult.AppUnavailable) return BadRequest(new { error = "App is unavailable" });
        if (result == IssueRecordResult.QuotaExceeded)
        {
            // Existing SDKs drop 403 but retry 429, which is reserved for the rate limiter.
            _logger.LogDebug("Error quota exceeded for app {AppId}", app.Id);
            return StatusCode(403, new { error = "Error quota exceeded" });
        }

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
