using Aptabase.Data;
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
        var parameters = new { error.AppId, error.IsDebug, eventId = Guid.Parse(error.ErrorId),
            issueId = IssueFingerprint.Create(error.ErrorType, error.ErrorMessage, error.StackTrace), error.Timestamp,
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

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO error_issues (app_id, is_debug, id, error_type, error_message, stack_trace, first_seen, last_seen)
            VALUES (@AppId, @IsDebug, @id, @ErrorType, @ErrorMessage, @stack, @Timestamp, @Timestamp)
            ON CONFLICT (app_id, is_debug, id) DO UPDATE SET
                occurrence_count = error_issues.occurrence_count + 1,
                first_seen = LEAST(error_issues.first_seen, EXCLUDED.first_seen),
                last_seen = GREATEST(error_issues.last_seen, EXCLUDED.last_seen),
                status = CASE WHEN EXCLUDED.last_seen > error_issues.resolved_at THEN 'unresolved' ELSE error_issues.status END,
                resolved_at = CASE WHEN EXCLUDED.last_seen > error_issues.resolved_at THEN NULL ELSE error_issues.resolved_at END
            """, new { error.AppId, error.IsDebug,
                id = IssueFingerprint.Create(error.ErrorType, error.ErrorMessage, error.StackTrace),
                error.ErrorType, error.ErrorMessage, stack = error.StackTrace ?? "", error.Timestamp },
            transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return IssueRecordResult.Accepted;
    }

    public async Task<int> PurgeExpiredReceipts(CancellationToken cancellationToken)
    {
        await using var connection = db.Connection;
        return await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM error_receipts WHERE (app_id, is_debug, event_id) IN (
                SELECT app_id, is_debug, event_id FROM error_receipts
                WHERE received_at < CURRENT_TIMESTAMP - INTERVAL '30 days'
                ORDER BY received_at LIMIT 10000 FOR UPDATE SKIP LOCKED)
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
            SELECT event_id, timestamp, error_message, stack_trace, release, environment, platform, os_name, os_version, sdk_version, severity, kind
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
