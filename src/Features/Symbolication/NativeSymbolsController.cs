using Aptabase.Features;
using Aptabase.Features.Authentication;
using Aptabase.Features.ErrorReporting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
namespace Aptabase.Features.Symbolication;

[ApiController, IsAuthenticated, ResponseCache(NoStore = true), EnableRateLimiting("Stats")]
public class NativeSymbolsController(NativeSymbolStore store, IIssueStore issues, INativeSymbolProcessor processor) : ControllerBase
{
    [HttpGet("/api/v0/apps/{appId}/native-symbols")]
    public async Task<IActionResult> List(string appId, string buildMode = "release", CancellationToken ct = default)
    {
        if (!(await issues.GetAccess(appId, HttpContext.GetCurrentUserIdentity(), ct)).CanManage) return StatusCode(403);
        if (!Mode(buildMode)) return BadRequest();
        return Ok(new { enabled = processor.Enabled, artifacts = await store.List(appId, buildMode == "debug", ct), limits = new { maxArtifactBytes = ElfSymbols.MaxBytes, maxAppBytes = NativeSymbolStore.MaxAppBytes, maxAppArtifacts = NativeSymbolStore.MaxAppArtifacts } });
    }
    [HttpPost("/api/v0/apps/{appId}/native-symbols"), RequestSizeLimit(8388608)]
    public async Task<IActionResult> Upload(string appId, string buildMode = "release", CancellationToken ct = default)
    {
        var user = HttpContext.GetCurrentUserIdentity();
        if (!(await issues.GetAccess(appId, user, ct)).CanManage) return StatusCode(403);
        if (!Mode(buildMode)) return BadRequest();
        if (!processor.Enabled) return StatusCode(503);
        if (Request.ContentType != "application/octet-stream") return StatusCode(415);
        if (Request.ContentLength > ElfSymbols.MaxBytes) return StatusCode(413);
        using var content = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await Request.Body.ReadAsync(buffer, ct)) > 0) { if (content.Length + count > ElfSymbols.MaxBytes) return StatusCode(413); await content.WriteAsync(buffer.AsMemory(0, count), ct); }
        var bytes = content.ToArray();
        if (!ElfSymbols.IsSupportedHeader(bytes)) return BadRequest(new { error = "An ARM64 ELF shared-library symbol artifact is required; dumps and archives are unsupported" });
        var result = await store.Upload(appId, user.Id, buildMode == "debug", bytes, ct);
        return result.Result switch { "accepted" => Accepted(new { artifact = result.Artifact }), "budget" => Conflict(new { error = "App symbol storage budget exceeded" }), _ => StatusCode(403) };
    }
    [HttpDelete("/api/v0/apps/{appId}/native-symbols/{id:guid}")]
    public async Task<IActionResult> Delete(string appId, Guid id, string buildMode = "release", CancellationToken ct = default)
    {
        var user = HttpContext.GetCurrentUserIdentity();
        if (!(await issues.GetAccess(appId, user, ct)).CanManage) return StatusCode(403);
        if (!Mode(buildMode)) return BadRequest();
        return await store.Delete(appId, user.Id, buildMode == "debug", id, ct) ? NoContent() : NotFound();
    }
    [HttpGet("/api/v0/apps/{appId}/issues/{issueId}/events/{eventId:guid}/native-frames")]
    public async Task<IActionResult> Frames(string appId, string issueId, Guid eventId, string buildMode = "release", CancellationToken ct = default)
    {
        if (!(await issues.GetAccess(appId, HttpContext.GetCurrentUserIdentity(), ct)).CanRead) return StatusCode(403);
        if (!Mode(buildMode)) return BadRequest();
        var frames = await store.Resolve(appId, buildMode == "debug", issueId, eventId, ct);
        return frames is null ? NotFound() : Ok(new { capability = "function-only", frames });
    }
    private static bool Mode(string mode) => mode is "release" or "debug";
}
