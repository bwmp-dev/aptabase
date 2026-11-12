using System.IO.Compression;
using System.Text.Json;
using Aptabase.Data;
using Aptabase.Features.Authentication;
using Aptabase.Features.ErrorReporting;
using Aptabase.Features.Performance;
using Dapper;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aptabase.Features.Sentry;

public class SentryProject
{
    public long Id { get; set; }
    public string AppId { get; set; } = "";
    public bool IsDebug { get; set; }
    public string AppKey { get; set; } = "";
}

[ApiController]
[ResponseCache(NoStore = true)]
public class SentryController(IDbContext db, IIssueStore issues, ErrorIngestionService errors, PerformanceStore performance, EnvSettings env) : ControllerBase
{
    [HttpGet("/api/v0/apps/{appId}/sentry-projects")]
    [IsAuthenticated, EnableRateLimiting("Stats")]
    public async Task<IActionResult> Projects(string appId, CancellationToken cancellationToken)
    {
        if (!(await issues.GetAccess(appId, HttpContext.GetCurrentUserIdentity(), cancellationToken)).CanManage) return StatusCode(403);
        return await ProjectResponse(appId, cancellationToken);
    }

    [HttpPost("/api/v0/apps/{appId}/sentry-projects")]
    [IsAuthenticated, EnableRateLimiting("Stats")]
    public async Task<IActionResult> Provision(string appId, CancellationToken cancellationToken)
    {
        var user = HttpContext.GetCurrentUserIdentity();
        if (!(await issues.GetAccess(appId, user, cancellationToken)).CanManage) return StatusCode(403);
        await using var connection = db.Connection;
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO sentry_projects (app_id, is_debug)
            SELECT a.id, modes.is_debug FROM apps a JOIN users u ON u.id = a.owner_id
            CROSS JOIN (VALUES (false), (true)) AS modes(is_debug)
            WHERE a.id = @appId AND a.owner_id = @ownerId AND a.deleted_at IS NULL AND u.lock_reason IS NULL
            ON CONFLICT DO NOTHING
            """, new { appId, ownerId = user.Id }, cancellationToken: cancellationToken));
        return await ProjectResponse(appId, cancellationToken);
    }

    private async Task<IActionResult> ProjectResponse(string appId, CancellationToken cancellationToken)
    {
        await using var connection = db.Connection;
        var rows = await connection.QueryAsync<SentryProject>(new CommandDefinition("""
            SELECT p.id, p.app_id, p.is_debug, a.app_key FROM sentry_projects p JOIN apps a ON a.id = p.app_id
            WHERE p.app_id = @appId ORDER BY p.is_debug
            """, new { appId }, cancellationToken: cancellationToken));
        var baseUri = new Uri(env.SelfBaseUrl);
        return Ok(new { projects = rows.Select(p => new { id = p.Id.ToString(), buildMode = p.IsDebug ? "debug" : "release",
            dsn = $"{baseUri.Scheme}://{SentryIngestionKey.Create(p)}@{baseUri.Authority}/{p.Id}" }) });
    }

    [HttpPost("/api/{projectId:long}/envelope/")]
    [EnableCors("SentryIngestion"), EnableRateLimiting("EventIngestion")]
    [RequestSizeLimit(131072)]
    public async Task<IActionResult> Envelope(long projectId, CancellationToken cancellationToken)
    {
        if (Request.ContentLength > 131072) return StatusCode(413);
        var encoding = Request.Headers.ContentEncoding.ToString();
        if (encoding is not ("" or "identity" or "gzip")) return StatusCode(415, new { error = "Unsupported content encoding" });
        SentryEnvelope envelope;
        try
        {
            await using var compressed = encoding == "gzip" ? new GZipStream(Request.Body, CompressionMode.Decompress, leaveOpen: true) : null;
            var stream = compressed is null ? Request.Body : compressed;
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + count > SentryEnvelopeParser.MaxBytes) return StatusCode(413);
                await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
            }
            envelope = SentryEnvelopeParser.Parse(buffer.ToArray());
        }
        catch (UnsupportedSentryItemException) { return UnprocessableEntity(new { error = "Only one event or transaction item is supported; attachments, sessions, profiles and other item types are unsupported" }); }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException)
        { return BadRequest(new { error = "Invalid or unsupported envelope structure" }); }

        var keys = new List<string>();
        if (Request.Query.TryGetValue("sentry_key", out var queryKey)) keys.Add(queryKey.ToString());
        var auth = Request.Headers["X-Sentry-Auth"].ToString();
        if (auth.Length > 0)
        {
            if (!auth.StartsWith("Sentry ", StringComparison.Ordinal)) return StatusCode(403);
            foreach (var part in auth[7..].Split(','))
            {
                var pair = part.Trim().Split('=', 2);
                if (pair.Length == 2 && pair[0] == "sentry_key") keys.Add(pair[1]);
            }
        }
        if (envelope.Dsn is not null)
        {
            if (!Uri.TryCreate(envelope.Dsn, UriKind.Absolute, out var dsn) || dsn.Scheme is not ("http" or "https") ||
                dsn.AbsolutePath.Trim('/') != projectId.ToString()) return StatusCode(403);
            keys.Add(Uri.UnescapeDataString(dsn.UserInfo.Split(':')[0]));
        }
        if (keys.Count == 0 || keys.Any(k => k.Length == 0 || k.Length > 100 || !string.Equals(k, keys[0], StringComparison.Ordinal))) return StatusCode(403);
        await using var connection = db.Connection;
        var project = await connection.QuerySingleOrDefaultAsync<SentryProject>(new CommandDefinition("""
            SELECT p.id, p.app_id, p.is_debug, a.app_key FROM sentry_projects p JOIN apps a ON a.id = p.app_id JOIN users u ON u.id = a.owner_id
            WHERE p.id = @projectId AND a.deleted_at IS NULL AND u.lock_reason IS NULL
            """, new { projectId }, cancellationToken: cancellationToken));
        if (project is null || !SentryIngestionKey.Matches(project, keys[0])) return StatusCode(403);
        if (envelope.Error is not null)
        {
            envelope.Error.IsDebug = project.IsDebug;
            var result = await errors.Ingest(project.AppId, envelope.Error, cancellationToken);
            if (result == IssueRecordResult.QuotaExceeded) return StatusCode(403, new { error = "Error quota exceeded" });
            if (result == IssueRecordResult.AppUnavailable) return StatusCode(403);
        }
        else
        {
            envelope.Transaction!.IsDebug = project.IsDebug;
            if (!await performance.Record(project.AppId, envelope.Transaction, cancellationToken)) return StatusCode(403);
        }
        return Ok(new { id = Guid.Parse(envelope.EventId).ToString("N") });
    }
}
