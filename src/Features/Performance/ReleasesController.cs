using Aptabase.Data;
using Aptabase.Features.Authentication;
using Aptabase.Features.ErrorReporting;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aptabase.Features.Performance;

public class ObservedRelease
{
    public string Release { get; set; } = "";
    public string Environment { get; set; } = "";
    public long ErrorCount { get; set; }
    public long TransactionCount { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
}

[ApiController, IsAuthenticated, EnableRateLimiting("Stats")]
[ResponseCache(NoStore = true)]
public class ReleasesController(IDbContext db, IIssueStore issues) : ControllerBase
{
    [HttpGet("/api/v0/apps/{appId}/releases")]
    public async Task<IActionResult> List(string appId, [FromQuery] string buildMode = "release", CancellationToken cancellationToken = default)
    {
        if (!(await issues.GetAccess(appId, HttpContext.GetCurrentUserIdentity(), cancellationToken)).CanRead) return StatusCode(403);
        if (buildMode is not ("release" or "debug")) return BadRequest(new { error = "Invalid build mode" });
        await using var connection = db.Connection;
        var releases = await connection.QueryAsync<ObservedRelease>(new CommandDefinition("""
            SELECT release, environment, SUM(errors)::bigint AS error_count, SUM(transactions)::bigint AS transaction_count,
                MIN(timestamp) AS first_seen, MAX(timestamp) AS last_seen FROM (
                SELECT release, environment, timestamp, 1 AS errors, 0 AS transactions
                FROM error_receipts WHERE app_id = @appId AND is_debug = @isDebug AND timestamp IS NOT NULL AND release <> ''
                UNION ALL
                SELECT release, environment, timestamp, 0 AS errors, 1 AS transactions
                FROM app_transactions WHERE app_id = @appId AND is_debug = @isDebug AND release <> ''
            ) observed GROUP BY release, environment ORDER BY last_seen DESC, release, environment LIMIT 100
            """, new { appId, isDebug = buildMode == "debug" }, cancellationToken: cancellationToken));
        return Ok(new { releases });
    }
}
