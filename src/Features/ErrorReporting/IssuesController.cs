using Aptabase.Features.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aptabase.Features.ErrorReporting;

public class IssueStatusBody
{
    public string? Status { get; set; }
}

[ApiController, IsAuthenticated]
[ResponseCache(NoStore = true)]
[EnableRateLimiting("Stats")]
[Route("/api/v0/apps/{appId}/issues")]
public class IssuesController(IIssueStore store) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(string appId, [FromQuery] string buildMode = "release",
        [FromQuery] string status = "unresolved", [FromQuery] int offset = 0, [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var access = await store.GetAccess(appId, HttpContext.GetCurrentUserIdentity(), cancellationToken);
        if (!access.CanRead) return StatusCode(403);
        if (!ValidMode(buildMode) || status is not ("all" or "resolved" or "unresolved") || offset < 0 || limit < 1 || limit > 100)
            return BadRequest(new { error = "Invalid issue query" });
        var page = await store.List(appId, buildMode == "debug", status, offset, limit, cancellationToken);
        return Ok(new { issues = page.Issues, pagination = new { offset, limit, total = page.Total }, canManage = access.CanManage });
    }

    [HttpGet("{issueId}")]
    public async Task<IActionResult> Get(string appId, string issueId, [FromQuery] string buildMode = "release",
        CancellationToken cancellationToken = default)
    {
        var access = await store.GetAccess(appId, HttpContext.GetCurrentUserIdentity(), cancellationToken);
        if (!access.CanRead) return StatusCode(403);
        if (!ValidMode(buildMode)) return BadRequest(new { error = "Invalid build mode" });
        var issue = await store.Get(appId, buildMode == "debug", issueId, cancellationToken);
        return issue is null ? NotFound() : Ok(new { issue, canManage = access.CanManage });
    }

    [HttpPatch("{issueId}")]
    public async Task<IActionResult> Update(string appId, string issueId, [FromBody] IssueStatusBody body,
        [FromQuery] string buildMode = "release", CancellationToken cancellationToken = default)
    {
        var user = HttpContext.GetCurrentUserIdentity();
        var access = await store.GetAccess(appId, user, cancellationToken);
        if (!access.CanManage) return StatusCode(403);
        if (!ValidMode(buildMode) || body.Status is not ("resolved" or "unresolved"))
            return BadRequest(new { error = "Invalid issue status or build mode" });
        var issue = await store.SetStatus(appId, buildMode == "debug", issueId, body.Status, user.Id, cancellationToken);
        return issue is null ? NotFound() : Ok(new { issue, canManage = true });
    }

    private static bool ValidMode(string mode) => mode is "release" or "debug";
}
