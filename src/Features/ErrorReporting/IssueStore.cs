using System.Text.Json;
using Aptabase.Data;
using Aptabase.Features.Monitoring;
using Aptabase.Features.Authentication;
using Aptabase.Features.ErrorReporting.Buffer;
using Dapper;

namespace Aptabase.Features.ErrorReporting;

public class Issue
{
    public string Id { get; set; } = "";
    public string ErrorType { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public string StackTrace { get; set; } = "";
    public string Status { get; set; } = "unresolved";
    public long OccurrenceCount { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

public enum IssueRecordResult { Accepted, Duplicate, QuotaExceeded, AppUnavailable }

public class IssueEvent
{
    public bool HasNativeFrames { get; set; }
    public Guid EventId { get; set; }
    public DateTime Timestamp { get; set; }
    public string ErrorMessage { get; set; } = "";
    public string StackTrace { get; set; } = "";
    public string Release { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Platform { get; set; } = "";
    public string OsName { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string SdkVersion { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Kind { get; set; } = "";
}

public record IssueEventPage(IEnumerable<IssueEvent> Events, long Total);
public record IssuePage(IEnumerable<Issue> Issues, long Total);
public record IssueAccess(bool CanRead, bool CanManage);

public interface IIssueStore
{
    Task<IssueRecordResult> Record(TrackingError error, CancellationToken cancellationToken, bool enforceQuota = false);
    Task<int> PurgeExpiredReceipts(CancellationToken cancellationToken);
    Task<IssueAccess> GetAccess(string appId, UserIdentity user, CancellationToken cancellationToken);
    Task<IssuePage> List(string appId, bool isDebug, string status, int offset, int limit, CancellationToken cancellationToken, string q = "", string? environment = null, string? release = null);
    Task<IssueEventPage> Events(string appId, bool isDebug, string id, int offset, int limit, CancellationToken cancellationToken, string? environment = null, string? release = null);
    Task<Issue?> Get(string appId, bool isDebug, string id, CancellationToken cancellationToken);
    Task<Issue?> SetStatus(string appId, bool isDebug, string id, string status, string ownerId, CancellationToken cancellationToken);
}

public class IssueStore(IDbContext db) : IIssueStore
{
    public async Task<IssueRecordResult> Record(TrackingError error, CancellationToken cancellationToken, bool enforceQuota = false)
    {
        await using var connection = db.Connection;
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var result = await Record(error, connection, transaction, cancellationToken, enforceQuota);
        if (result == IssueRecordResult.Accepted) await transaction.CommitAsync(cancellationToken);
        return result;
    }

    internal async Task<IssueRecordResult> Record(TrackingError error, System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken, bool enforceQuota)
    {
        // Serialize capture/settings/purge for this app; policy changes also lock the same app row.
        var policy = await connection.QuerySingleOrDefaultAsync<MonitoringSettings>(new CommandDefinition("""
            SELECT a.capture_errors, a.forward_raw_errors, a.local_alerts_enabled, a.alert_cooldown_minutes
            FROM apps a JOIN users u ON u.id=a.owner_id
            WHERE a.id=@AppId AND a.deleted_at IS NULL AND u.lock_reason IS NULL FOR UPDATE OF a
            """, new { error.AppId }, transaction, cancellationToken: cancellationToken));
        if (policy is null || !policy.CaptureErrors) return IssueRecordResult.AppUnavailable;
        var parameters = new { error.AppId, error.IsDebug, eventId = Guid.Parse(error.ErrorId),
            issueId = IssueFingerprint.Create(error.ErrorType, error.ErrorMessage, error.GroupingStackTrace ?? error.StackTrace), error.Timestamp,
            error.ErrorMessage, stackTrace = error.StackTrace ?? "", release = error.Release ?? "", environment = error.Environment ?? "",
            platform = error.Platform ?? "", osName = error.OsName ?? "", osVersion = error.OsVersion ?? "", sdkVersion = error.SdkVersion ?? "",
            severity = error.Severity ?? "", kind = error.Kind ?? "" };
        var inserted = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            INSERT INTO error_receipts (app_id, is_debug, event_id, issue_id, timestamp, error_message, stack_trace,
                release, environment, platform, os_name, os_version, sdk_version, severity, kind)
            SELECT a.id, @IsDebug, @eventId, @issueId, @Timestamp, @ErrorMessage, @stackTrace,
                @release, @environment, @platform, @osName, @osVersion, @sdkVersion, @severity, @kind
            FROM apps a JOIN users u ON u.id = a.owner_id
            WHERE a.id = @AppId AND a.deleted_at IS NULL AND u.lock_reason IS NULL
            ON CONFLICT DO NOTHING RETURNING 1
            """, parameters, transaction, cancellationToken: cancellationToken));
        if (inserted == 0)
        {
            var duplicate = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS (SELECT 1 FROM error_receipts r JOIN apps a ON a.id = r.app_id
                    JOIN users u ON u.id = a.owner_id
                    WHERE r.app_id = @AppId AND r.is_debug = @IsDebug AND r.event_id = @eventId
                    AND a.deleted_at IS NULL AND u.lock_reason IS NULL)
                """, parameters, transaction, cancellationToken: cancellationToken));
            return duplicate ? IssueRecordResult.Duplicate : IssueRecordResult.AppUnavailable;
        }

        if (enforceQuota)
        {
            var consumed = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
                UPDATE apps SET error_count = error_count + 1
                WHERE id = @AppId AND error_count < error_quota RETURNING 1
                """, parameters, transaction, cancellationToken: cancellationToken));
            if (consumed == 0) return IssueRecordResult.QuotaExceeded;
        }

        if (error.NativeFrames is not null) await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE error_receipts SET native_frames=CAST(@frames AS jsonb) WHERE app_id=@AppId AND is_debug=@IsDebug AND event_id=@eventId",
            new { error.AppId, error.IsDebug, eventId = Guid.Parse(error.ErrorId), frames = JsonSerializer.Serialize(error.NativeFrames) },
            transaction, cancellationToken: cancellationToken));

        var alertKind = await connection.ExecuteScalarAsync<string?>(new CommandDefinition("""
            INSERT INTO error_issues (app_id, is_debug, id, error_type, error_message, stack_trace, first_seen, last_seen)
            VALUES (@AppId, @IsDebug, @id, @ErrorType, @ErrorMessage, @stack, @Timestamp, @Timestamp)
            ON CONFLICT (app_id, is_debug, id) DO UPDATE SET
                occurrence_count = error_issues.occurrence_count + 1,
                first_seen = LEAST(error_issues.first_seen, EXCLUDED.first_seen),
                last_seen = GREATEST(error_issues.last_seen, EXCLUDED.last_seen),
                status = CASE WHEN EXCLUDED.last_seen > error_issues.resolved_at THEN 'unresolved' ELSE error_issues.status END,
                resolved_at = CASE WHEN EXCLUDED.last_seen > error_issues.resolved_at THEN NULL ELSE error_issues.resolved_at END,
                last_reopened_event_id = CASE WHEN EXCLUDED.last_seen > error_issues.resolved_at THEN @eventId ELSE error_issues.last_reopened_event_id END
            RETURNING CASE WHEN occurrence_count = 1 THEN 'new_issue'
                WHEN last_reopened_event_id = @eventId THEN 'regression' ELSE NULL END
            """, new { error.AppId, error.IsDebug, eventId = Guid.Parse(error.ErrorId),
                id = IssueFingerprint.Create(error.ErrorType, error.ErrorMessage, error.GroupingStackTrace ?? error.StackTrace),
                error.ErrorType, error.ErrorMessage, stack = error.StackTrace ?? "", error.Timestamp },
            transaction, cancellationToken: cancellationToken));
        if (policy.ForwardRawErrors) await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO error_outbox (app_id, is_debug, event_id, payload)
            VALUES (@AppId, @IsDebug, @eventId, CAST(@payload AS jsonb)) ON CONFLICT DO NOTHING
            """, new { error.AppId, error.IsDebug, eventId = Guid.Parse(error.ErrorId), payload = JsonSerializer.Serialize(error) },
            transaction, cancellationToken: cancellationToken));
        if (policy.LocalAlertsEnabled && alertKind is not null)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                WITH eligible AS (
                    INSERT INTO monitoring_alert_state (app_id, is_debug, issue_id, last_notified_at)
                    VALUES (@AppId, @IsDebug, @issueId, CURRENT_TIMESTAMP)
                    ON CONFLICT (app_id, is_debug, issue_id) DO UPDATE SET last_notified_at=CURRENT_TIMESTAMP
                    WHERE monitoring_alert_state.last_notified_at <= CURRENT_TIMESTAMP - @cooldown * INTERVAL '1 minute'
                    RETURNING 1)
                INSERT INTO monitoring_alerts (app_id, is_debug, event_id, issue_id, kind)
                    SELECT @AppId, @IsDebug, @eventId, @issueId, @alertKind FROM eligible ON CONFLICT DO NOTHING
                """, new { error.AppId, error.IsDebug, eventId = Guid.Parse(error.ErrorId), parameters.issueId,
                    alertKind, cooldown = policy.AlertCooldownMinutes }, transaction, cancellationToken: cancellationToken));
        }
        return IssueRecordResult.Accepted;
    }

    public async Task<int> PurgeExpiredReceipts(CancellationToken cancellationToken)
    {
        await using var connection = db.Connection;
        return await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM monitoring_sessions WHERE (app_id,is_debug,session_key) IN (
                SELECT s.app_id,s.is_debug,s.session_key FROM monitoring_sessions s JOIN apps a ON a.id=s.app_id
                WHERE s.received_at<CURRENT_TIMESTAMP-a.monitoring_retention_days*INTERVAL '1 day' OR a.deleted_at IS NOT NULL
                ORDER BY s.received_at LIMIT 10000 FOR UPDATE OF s SKIP LOCKED);
            DELETE FROM monitoring_client_reports WHERE (app_id,is_debug,report_key) IN (
                SELECT r.app_id,r.is_debug,r.report_key FROM monitoring_client_reports r JOIN apps a ON a.id=r.app_id
                WHERE r.received_at<CURRENT_TIMESTAMP-a.monitoring_retention_days*INTERVAL '1 day' OR a.deleted_at IS NOT NULL
                ORDER BY r.received_at LIMIT 10000 FOR UPDATE OF r SKIP LOCKED);
            DELETE FROM error_receipts WHERE (app_id, is_debug, event_id) IN (
                SELECT r.app_id, r.is_debug, r.event_id FROM error_receipts r JOIN apps a ON a.id=r.app_id
                WHERE r.received_at < CURRENT_TIMESTAMP - a.monitoring_retention_days * INTERVAL '1 day' OR a.deleted_at IS NOT NULL
                ORDER BY r.received_at LIMIT 10000 FOR UPDATE OF r SKIP LOCKED);
            DELETE FROM monitoring_alerts WHERE (app_id, is_debug, event_id) IN (
                SELECT app_id, is_debug, event_id FROM monitoring_alerts WHERE created_at < CURRENT_TIMESTAMP - INTERVAL '30 days'
                    OR app_id IN (SELECT id FROM apps WHERE deleted_at IS NOT NULL)
                ORDER BY created_at LIMIT 10000 FOR UPDATE SKIP LOCKED);
            DELETE FROM monitoring_alert_state WHERE (app_id, is_debug, issue_id) IN (
                SELECT app_id, is_debug, issue_id FROM monitoring_alert_state WHERE last_notified_at < CURRENT_TIMESTAMP - INTERVAL '30 days'
                    OR app_id IN (SELECT id FROM apps WHERE deleted_at IS NOT NULL)
                ORDER BY last_notified_at LIMIT 10000 FOR UPDATE SKIP LOCKED);
            DELETE FROM error_outbox WHERE (app_id, is_debug, event_id) IN (
                SELECT o.app_id, o.is_debug, o.event_id FROM error_outbox o JOIN apps a ON a.id=o.app_id
                WHERE a.deleted_at IS NOT NULL OR NOT a.forward_raw_errors
                    OR o.created_at < CURRENT_TIMESTAMP - a.monitoring_retention_days * INTERVAL '1 day'
                ORDER BY o.created_at LIMIT 10000 FOR UPDATE OF o SKIP LOCKED);
            DELETE FROM error_issues WHERE (app_id, is_debug, id) IN (
                SELECT i.app_id, i.is_debug, i.id FROM error_issues i JOIN apps a ON a.id=i.app_id
                WHERE a.deleted_at IS NOT NULL LIMIT 10000 FOR UPDATE OF i SKIP LOCKED);
            DELETE FROM sentry_projects WHERE id IN (
                SELECT p.id FROM sentry_projects p JOIN apps a ON a.id=p.app_id
                WHERE a.deleted_at IS NOT NULL LIMIT 10000 FOR UPDATE OF p SKIP LOCKED)
            """, cancellationToken: cancellationToken));
    }

    public async Task<IssueAccess> GetAccess(string appId, UserIdentity user, CancellationToken cancellationToken)
    {
        await using var connection = db.Connection;
        return await connection.QuerySingleOrDefaultAsync<IssueAccess>(new CommandDefinition("""
            SELECT true AS CanRead, (a.owner_id = @userId) AS CanManage
            FROM apps a JOIN users u ON u.id = a.owner_id
            WHERE a.id = @appId AND a.deleted_at IS NULL AND u.lock_reason IS NULL
              AND (a.owner_id = @userId OR EXISTS (
                  SELECT 1 FROM app_shares s WHERE s.app_id = a.id AND s.email = @email))
            """, new { appId, userId = user.Id, email = user.Email }, cancellationToken: cancellationToken))
            ?? new IssueAccess(false, false);
    }

    public async Task<IssuePage> List(string appId, bool isDebug, string status, int offset, int limit, CancellationToken cancellationToken, string q = "", string? environment = null, string? release = null)
    {
        await using var connection = db.Connection;
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        using var result = await connection.QueryMultipleAsync(new CommandDefinition("""
            SELECT * FROM error_issues WHERE app_id = @appId AND is_debug = @isDebug
                AND (@status = 'all' OR status = @status)
                AND (@q = '' OR strpos(lower(error_type), lower(@q)) > 0 OR strpos(lower(error_message), lower(@q)) > 0)
                AND ((@environment IS NULL AND @release IS NULL) OR EXISTS (
                    SELECT 1 FROM error_receipts r WHERE r.app_id = error_issues.app_id AND r.is_debug = error_issues.is_debug
                    AND r.issue_id = error_issues.id AND (@environment IS NULL OR r.environment = @environment)
                    AND (@release IS NULL OR r.release = @release)))
                ORDER BY last_seen DESC, id LIMIT @limit OFFSET @offset;
            SELECT COUNT(*) FROM error_issues WHERE app_id = @appId AND is_debug = @isDebug
                AND (@status = 'all' OR status = @status)
                AND (@q = '' OR strpos(lower(error_type), lower(@q)) > 0 OR strpos(lower(error_message), lower(@q)) > 0)
                AND ((@environment IS NULL AND @release IS NULL) OR EXISTS (
                    SELECT 1 FROM error_receipts r WHERE r.app_id = error_issues.app_id AND r.is_debug = error_issues.is_debug
                    AND r.issue_id = error_issues.id AND (@environment IS NULL OR r.environment = @environment)
                    AND (@release IS NULL OR r.release = @release)));
            """, new { appId, isDebug, status, offset, limit, q, environment, release }, transaction, cancellationToken: cancellationToken));
        var issues = (await result.ReadAsync<Issue>()).ToArray();
        var total = await result.ReadSingleAsync<long>();
        await transaction.CommitAsync(cancellationToken);
        return new IssuePage(issues, total);
    }

    public async Task<IssueEventPage> Events(string appId, bool isDebug, string id, int offset, int limit, CancellationToken cancellationToken, string? environment = null, string? release = null)
    {
        await using var connection = db.Connection;
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        const string where = """
            app_id = @appId AND is_debug = @isDebug AND issue_id = @id AND timestamp IS NOT NULL
            AND (@environment IS NULL OR environment = @environment) AND (@release IS NULL OR release = @release)
            """;
        using var rows = await connection.QueryMultipleAsync(new CommandDefinition($"""
            SELECT event_id, timestamp, error_message, stack_trace, release, environment, platform, os_name, os_version, sdk_version, severity, kind, native_frames IS NOT NULL AS has_native_frames
            FROM error_receipts WHERE {where} ORDER BY timestamp DESC, event_id LIMIT @limit OFFSET @offset;
            SELECT COUNT(*) FROM error_receipts WHERE {where};
            """, new { appId, isDebug, id, offset, limit, environment, release }, transaction, cancellationToken: cancellationToken));
        var events = (await rows.ReadAsync<IssueEvent>()).ToArray();
        var total = await rows.ReadSingleAsync<long>();
        await transaction.CommitAsync(cancellationToken);
        return new IssueEventPage(events, total);
    }

    public async Task<Issue?> Get(string appId, bool isDebug, string id, CancellationToken cancellationToken)
    {
        await using var connection = db.Connection;
        return await connection.QuerySingleOrDefaultAsync<Issue>(new CommandDefinition("""
            SELECT * FROM error_issues WHERE app_id = @appId AND is_debug = @isDebug AND id = @id
            """, new { appId, isDebug, id }, cancellationToken: cancellationToken));
    }

    public async Task<Issue?> SetStatus(string appId, bool isDebug, string id, string status, string ownerId, CancellationToken cancellationToken)
    {
        await using var connection = db.Connection;
        return await connection.QuerySingleOrDefaultAsync<Issue>(new CommandDefinition("""
            UPDATE error_issues SET status = @status,
                resolved_at = CASE WHEN @status = 'unresolved' THEN NULL
                    ELSE COALESCE(resolved_at, CURRENT_TIMESTAMP) END
            WHERE app_id = @appId AND is_debug = @isDebug AND id = @id
                AND EXISTS (SELECT 1 FROM apps a JOIN users u ON u.id = a.owner_id
                    WHERE a.id = @appId AND a.owner_id = @ownerId AND a.deleted_at IS NULL AND u.lock_reason IS NULL)
            RETURNING error_issues.*
            """, new { appId, isDebug, id, status, ownerId }, cancellationToken: cancellationToken));
    }
}
