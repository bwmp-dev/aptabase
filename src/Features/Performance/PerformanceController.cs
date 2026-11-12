using Aptabase.Features.Authentication;
using Aptabase.Features.ErrorReporting;
using Aptabase.Features.Ingestion;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aptabase.Features.Performance;

[ApiController]
[ResponseCache(NoStore = true)]
public class PerformanceController(PerformanceStore store, IIssueStore issues) : ControllerBase
{
    [HttpPost("/api/v0/transaction")]
    [EnableCors("AllowAny"), EnableRateLimiting("EventIngestion"), ResolveErrorAppKey]
    [RequestSizeLimit(131072)]
    public async Task<IActionResult> Ingest([FromBody] TransactionBody body, CancellationToken cancellationToken)
    {
        if (!body.IsValid()) return BadRequest(new { error = "Invalid transaction" });
        var app = ResolveAppKeyAttribute.GetApp(HttpContext);
        return await store.Record(app.Id, body, cancellationToken) ? Accepted() : BadRequest(new { error = "App is unavailable" });
    }

    [HttpGet("/api/v0/apps/{appId}/transactions")]
    [IsAuthenticated, EnableRateLimiting("Stats")]
    public async Task<IActionResult> List(string appId, [FromQuery] PerformanceQuery query, CancellationToken cancellationToken)
    {
        if (!(await issues.GetAccess(appId, HttpContext.GetCurrentUserIdentity(), cancellationToken)).CanRead) return StatusCode(403);
        if (!query.TryFilter(appId, out var filter)) return BadRequest(new { error = "Invalid performance query" });
        var page = await store.List(filter!, query.Offset, query.Limit, cancellationToken);
        return Ok(new { transactions = page.Transactions, pagination = new { query.Offset, query.Limit, total = page.Total } });
    }

    [HttpGet("/api/v0/apps/{appId}/performance/summary")]
    [IsAuthenticated, EnableRateLimiting("Stats")]
    public async Task<IActionResult> Summary(string appId, [FromQuery] PerformanceQuery query, CancellationToken cancellationToken)
    {
        if (!(await issues.GetAccess(appId, HttpContext.GetCurrentUserIdentity(), cancellationToken)).CanRead) return StatusCode(403);
        if (!query.TryFilter(appId, out var filter)) return BadRequest(new { error = "Invalid performance query" });
        return Ok(new { groups = await store.Summary(filter!, cancellationToken) });
    }

    [HttpGet("/api/v0/apps/{appId}/transactions/{eventId:guid}")]
    [IsAuthenticated, EnableRateLimiting("Stats")]
    public async Task<IActionResult> Detail(string appId, Guid eventId, [FromQuery] string buildMode = "release", CancellationToken cancellationToken = default)
    {
        if (!(await issues.GetAccess(appId, HttpContext.GetCurrentUserIdentity(), cancellationToken)).CanRead) return StatusCode(403);
        if (buildMode is not ("release" or "debug")) return BadRequest(new { error = "Invalid build mode" });
        var detail = await store.Get(appId, buildMode == "debug", eventId, cancellationToken);
        return detail is null ? NotFound() : Ok(detail);
    }
}

public class PerformanceQuery
{
    public string BuildMode { get; set; } = "release";
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? Environment { get; set; }
    public string? Release { get; set; }
    public string? Name { get; set; }
    public string? Operation { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; } = 50;

    public bool TryFilter(string appId, out PerformanceFilter? filter)
    {
        filter = null;
        var end = EndDate?.ToUniversalTime() ?? DateTime.UtcNow;
        if (end < DateTime.MinValue.AddDays(7)) return false;
        var start = StartDate?.ToUniversalTime() ?? end.AddDays(-7);
        if (BuildMode is not ("release" or "debug") || start > end || end - start > TimeSpan.FromDays(31) ||
            Offset < 0 || Limit < 1 || Limit > 100 || Environment?.Length > 50 || Release?.Length > 100 || Name?.Length > 200 || Operation?.Length > 50)
            return false;
        filter = new(appId, BuildMode == "debug", start, end, Environment, Release, Name, Operation);
        return true;
    }
}
