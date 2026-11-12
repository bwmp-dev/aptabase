using Aptabase.Data;
using Aptabase.Features.Authentication;
using Aptabase.Features.ErrorReporting;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aptabase.Features.Monitoring;

public class MonitoringSettings
{
    [System.Text.Json.Serialization.JsonRequired]
    public bool CaptureErrors { get; set; } = true;
    [System.Text.Json.Serialization.JsonRequired]
    public bool CapturePerformance { get; set; } = true;
    [System.Text.Json.Serialization.JsonRequired]
    public bool ForwardRawErrors { get; set; } = true;
    [System.Text.Json.Serialization.JsonRequired]
    public int MonitoringRetentionDays { get; set; } = 30;
    [System.Text.Json.Serialization.JsonRequired]
    public bool LocalAlertsEnabled { get; set; }
    [System.Text.Json.Serialization.JsonRequired]
    public int AlertCooldownMinutes { get; set; } = 60;
}

public record PurgeMonitoringBody(string ConfirmAppId);
public class MonitoringAlert
{
    public Guid EventId { get; set; }
    public string IssueId { get; set; } = "";
    public string Kind { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
}

[ApiController, IsAuthenticated, EnableRateLimiting("Stats")]
[ResponseCache(NoStore = true)]
public class MonitoringController(IDbContext db, IIssueStore issues) : ControllerBase
{
    [HttpGet("/api/v0/apps/{appId}/monitoring/settings")]
    public async Task<IActionResult> GetSettings(string appId, CancellationToken ct)
    {
        if (!(await issues.GetAccess(appId, HttpContext.GetCurrentUserIdentity(), ct)).CanManage) return StatusCode(403);
        await using var connection = db.Connection;
        return Ok(await connection.QuerySingleAsync<MonitoringSettings>(new CommandDefinition("""
            SELECT capture_errors, capture_performance, forward_raw_errors, monitoring_retention_days, local_alerts_enabled, alert_cooldown_minutes
            FROM apps WHERE id=@appId
            """, new { appId }, cancellationToken: ct)));
    }

    [HttpPut("/api/v0/apps/{appId}/monitoring/settings")]
    public async Task<IActionResult> UpdateSettings(string appId, MonitoringSettings body, CancellationToken ct)
    {
        if (body.MonitoringRetentionDays is < 1 or > 30 || body.AlertCooldownMinutes is < 1 or > 1440) return BadRequest(new { error = "Invalid monitoring settings" });
        var user = HttpContext.GetCurrentUserIdentity();
        await using var connection = db.Connection;
        var changed = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE apps a SET capture_errors=@CaptureErrors, capture_performance=@CapturePerformance,
                forward_raw_errors=@ForwardRawErrors, monitoring_retention_days=@MonitoringRetentionDays,
                local_alerts_enabled=@LocalAlertsEnabled, alert_cooldown_minutes=@AlertCooldownMinutes
            FROM users u WHERE a.id=@appId AND a.owner_id=@ownerId AND a.deleted_at IS NULL
                AND u.id=a.owner_id AND u.lock_reason IS NULL
            """, new { appId, ownerId = user.Id, body.CaptureErrors, body.CapturePerformance, body.ForwardRawErrors,
                body.MonitoringRetentionDays, body.LocalAlertsEnabled, body.AlertCooldownMinutes }, cancellationToken: ct));
        return changed == 1 ? Ok(body) : StatusCode(403);
    }

    [HttpGet("/api/v0/apps/{appId}/monitoring/data")]
    public async Task<IActionResult> PreviewPurge(string appId, CancellationToken ct)
    {
        if (!(await issues.GetAccess(appId, HttpContext.GetCurrentUserIdentity(), ct)).CanManage) return StatusCode(403);
        await using var connection = db.Connection;
        var counts = await connection.QuerySingleAsync(new CommandDefinition("""
            SELECT (SELECT COUNT(*) FROM error_issues WHERE app_id=@appId) AS issues,
                (SELECT COUNT(*) FROM error_receipts WHERE app_id=@appId) AS events,
                (SELECT COUNT(*) FROM app_transactions WHERE app_id=@appId) AS transactions,
                (SELECT COUNT(*) FROM error_outbox WHERE app_id=@appId) AS queued,
                (SELECT COUNT(*) FROM monitoring_alerts WHERE app_id=@appId) AS alerts
            """, new { appId }, cancellationToken: ct));
        return Ok(new { counts, excludesDeliveredRawLogs = true });
    }

    [HttpDelete("/api/v0/apps/{appId}/monitoring/data")]
    public async Task<IActionResult> Purge(string appId, PurgeMonitoringBody body, CancellationToken ct)
    {
        if (body.ConfirmAppId != appId) return BadRequest(new { error = "App confirmation does not match" });
        await using var connection = db.Connection;
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var policy = await connection.QuerySingleOrDefaultAsync<MonitoringSettings>(new CommandDefinition("""
            SELECT a.capture_errors, a.capture_performance, a.forward_raw_errors FROM apps a JOIN users u ON u.id=a.owner_id
            WHERE a.id=@appId AND a.owner_id=@ownerId AND a.deleted_at IS NULL AND u.lock_reason IS NULL FOR UPDATE OF a
            """, new { appId, ownerId = HttpContext.GetCurrentUserIdentity().Id }, transaction, cancellationToken: ct));
        if (policy is null) return StatusCode(403);
        if (policy.CaptureErrors || policy.CapturePerformance || policy.ForwardRawErrors)
            return Conflict(new { error = "Disable error capture, performance capture and raw forwarding before clearing monitoring data" });
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM error_outbox WHERE app_id=@appId;
            DELETE FROM error_receipts WHERE app_id=@appId;
            DELETE FROM error_issues WHERE app_id=@appId;
            DELETE FROM app_transactions WHERE app_id=@appId;
            DELETE FROM monitoring_alerts WHERE app_id=@appId;
            DELETE FROM monitoring_alert_state WHERE app_id=@appId;
            """, new { appId }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return NoContent();
    }

    [HttpGet("/api/v0/apps/{appId}/monitoring/alerts")]
    public async Task<IActionResult> Alerts(string appId, string buildMode = "release", int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        if (!(await issues.GetAccess(appId, HttpContext.GetCurrentUserIdentity(), ct)).CanManage) return StatusCode(403);
        if (buildMode is not ("release" or "debug") || offset < 0 || limit is < 1 or > 100) return BadRequest(new { error = "Invalid alert query" });
        await using var connection = db.Connection;
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        using var rows = await connection.QueryMultipleAsync(new CommandDefinition("""
            SELECT event_id, issue_id, kind, created_at, read_at FROM monitoring_alerts WHERE app_id=@appId AND is_debug=@isDebug
                ORDER BY created_at DESC, event_id LIMIT @limit OFFSET @offset;
            SELECT COUNT(*) FROM monitoring_alerts WHERE app_id=@appId AND is_debug=@isDebug;
            """, new { appId, isDebug = buildMode == "debug", offset, limit }, transaction, cancellationToken: ct));
        var alerts = (await rows.ReadAsync<MonitoringAlert>()).ToArray();
        var total = await rows.ReadSingleAsync<long>();
        await transaction.CommitAsync(ct);
        return Ok(new { alerts, pagination = new { offset, limit, total } });
    }

    [HttpPatch("/api/v0/apps/{appId}/monitoring/alerts/{eventId:guid}/read")]
    public async Task<IActionResult> ReadAlert(string appId, Guid eventId, string buildMode = "release", CancellationToken ct = default)
    {
        if (!(await issues.GetAccess(appId, HttpContext.GetCurrentUserIdentity(), ct)).CanManage) return StatusCode(403);
        if (buildMode is not ("release" or "debug")) return BadRequest();
        await using var connection = db.Connection;
        var changed = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE monitoring_alerts SET read_at=COALESCE(read_at,CURRENT_TIMESTAMP)
            WHERE app_id=@appId AND is_debug=@isDebug AND event_id=@eventId
            """, new { appId, isDebug = buildMode == "debug", eventId }, cancellationToken: ct));
        return changed == 1 ? NoContent() : NotFound();
    }
}
