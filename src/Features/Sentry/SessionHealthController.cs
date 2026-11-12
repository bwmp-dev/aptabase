using Aptabase.Features.Authentication;
using Aptabase.Features.ErrorReporting;
using Aptabase.Features.Performance;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aptabase.Features.Sentry;

[ApiController, IsAuthenticated, EnableRateLimiting("Stats")]
[ResponseCache(NoStore = true)]
public class SessionHealthController(SentryMonitoringStore store, IIssueStore issues) : ControllerBase
{
    [HttpGet("/api/v0/apps/{appId}/monitoring/health")]
    public async Task<IActionResult> Summary(string appId, [FromQuery] PerformanceQuery query, CancellationToken ct)
    {
        if (!(await issues.GetAccess(appId, HttpContext.GetCurrentUserIdentity(), ct)).CanRead) return StatusCode(403);
        if (!query.TryFilter(appId, out var filter)) return BadRequest(new { error = "Invalid session health query" });
        return Ok(await store.Summary(filter!, ct));
    }
}
