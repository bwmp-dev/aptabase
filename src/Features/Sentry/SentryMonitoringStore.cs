using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aptabase.Data;
using Aptabase.Features.ErrorReporting;
using Aptabase.Features.Performance;
using Dapper;

namespace Aptabase.Features.Sentry;

public enum SentryWriteResult { Accepted, Unavailable, ErrorQuota, MetadataQuota, Conflict }

public class SessionHealthCounts
{
    public long Observed { get; set; }
    public long WithStart { get; set; }
    public long Eligible { get; set; }
    public long Crashed { get; set; }
    public long EligibleCrashed { get; set; }
    public long Abnormal { get; set; }
    public long Active { get; set; }
    public long Errored { get; set; }
    public long Incomplete => Observed - Eligible;
    public double? CrashFreeRate => Eligible == 0 ? null : (Eligible - EligibleCrashed) * 100.0 / Eligible;
}

public class SessionHealthGroup : SessionHealthCounts
{
    public string Release { get; set; } = "";
    public string Environment { get; set; } = "";
}

public class SentryMonitoringStore(IDbContext db, IPiiSanitizer sanitizer, ErrorIngestionService errors, EnvSettings env)
{
    public const int MaxSessionsPerApp = 10_000;
    public const int MaxClientReportsPerApp = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SentryWriteResult> Ingest(SentryProject project, SentryEnvelope envelope, CancellationToken ct)
    {
        await using var connection = db.Connection;
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var policy = await connection.QuerySingleOrDefaultAsync<Policy>(new CommandDefinition("""
            SELECT a.capture_errors,a.capture_performance,a.monitoring_session_salt
            FROM apps a JOIN users u ON u.id=a.owner_id JOIN sentry_projects p ON p.app_id=a.id
            WHERE a.id=@AppId AND a.app_key=@AppKey AND p.id=@Id AND p.is_debug=@IsDebug
                AND a.deleted_at IS NULL AND u.lock_reason IS NULL FOR UPDATE OF a
            """, project, transaction, cancellationToken: ct));
        if (policy is null || ((envelope.Error is not null || envelope.Sessions.Count > 0) && !policy.CaptureErrors)
            || (envelope.Transaction is not null && !policy.CapturePerformance)
            || (envelope.ClientReport is not null && !policy.CaptureErrors && !policy.CapturePerformance)) return SentryWriteResult.Unavailable;

        if (envelope.Sessions.Count > 0)
        {
            if (policy.MonitoringSessionSalt is null)
            {
                policy.MonitoringSessionSalt = RandomNumberGenerator.GetBytes(32);
                await connection.ExecuteAsync(new CommandDefinition("UPDATE apps SET monitoring_session_salt=@salt WHERE id=@AppId",
                    new { project.AppId, salt = policy.MonitoringSessionSalt }, transaction, cancellationToken: ct));
            }
            foreach (var session in envelope.Sessions)
            {
                var result = await RecordSession(project, session, policy.MonitoringSessionSalt, connection, transaction, ct);
                if (result != SentryWriteResult.Accepted) return result;
            }
        }
        if (envelope.ClientReport is not null)
        {
            var result = await RecordReport(project, envelope, connection, transaction, ct);
            if (result != SentryWriteResult.Accepted) return result;
        }
        if (envelope.Error is not null)
        {
            envelope.Error.IsDebug = project.IsDebug;
            var result = await new IssueStore(db).Record(errors.Prepare(project.AppId, envelope.Error), connection, transaction, ct, env.ErrorQuotaEnabled);
            if (result == IssueRecordResult.AppUnavailable) return SentryWriteResult.Unavailable;
            if (result == IssueRecordResult.QuotaExceeded) return SentryWriteResult.ErrorQuota;
        }
        if (envelope.Transaction is not null)
        {
            envelope.Transaction.IsDebug = project.IsDebug;
            if (!await new PerformanceStore(db, sanitizer).Record(project.AppId, envelope.Transaction, connection, transaction, ct)) return SentryWriteResult.Unavailable;
        }
        await transaction.CommitAsync(ct);
        return SentryWriteResult.Accepted;
    }

    private async Task<SentryWriteResult> RecordSession(SentryProject project, SentrySessionUpdate session, byte[] salt,
        DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var context = JsonSerializer.Serialize(new[] { "session-v1", project.AppId, project.IsDebug ? "debug" : "release", session.Id.ToString("N") });
        var key = Convert.ToHexString(HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(context))).ToLowerInvariant();
        var release = sanitizer.SanitizeLabel(session.Release, 100);
        var environment = sanitizer.SanitizeLabel(session.Environment, 50);
        var existing = await connection.QuerySingleOrDefaultAsync<SessionIdentity>(new CommandDefinition("""
            SELECT started_at,release,environment FROM monitoring_sessions WHERE app_id=@AppId AND is_debug=@IsDebug AND session_key=@key
            """, new { project.AppId, project.IsDebug, key }, transaction, cancellationToken: ct));
        if (existing is not null && (existing.StartedAt != session.Started || existing.Release != release || existing.Environment != environment))
            return SentryWriteResult.Conflict;
        if (existing is null && await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM monitoring_sessions WHERE app_id=@AppId", project, transaction, cancellationToken: ct)) >= MaxSessionsPerApp)
            return SentryWriteResult.MetadataQuota;
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO monitoring_sessions (app_id,is_debug,session_key,started_at,updated_at,sequence,saw_init,status,errors,duration_seconds,release,environment)
            VALUES (@AppId,@IsDebug,@key,@Started,@Timestamp,@Sequence,@Init,@Status,@Errors,@Duration,@release,@environment)
            ON CONFLICT (app_id,is_debug,session_key) DO UPDATE SET
                updated_at=GREATEST(monitoring_sessions.updated_at,EXCLUDED.updated_at),
                sequence=GREATEST(monitoring_sessions.sequence,EXCLUDED.sequence),
                saw_init=monitoring_sessions.saw_init OR EXCLUDED.saw_init,
                errors=GREATEST(monitoring_sessions.errors,EXCLUDED.errors),
                duration_seconds=GREATEST(monitoring_sessions.duration_seconds,EXCLUDED.duration_seconds),
                status=CASE WHEN monitoring_sessions.status='crashed' OR EXCLUDED.status='crashed' THEN 'crashed'
                    WHEN monitoring_sessions.status='abnormal' OR EXCLUDED.status='abnormal' THEN 'abnormal'
                    WHEN monitoring_sessions.status='exited' OR EXCLUDED.status='exited' THEN 'exited' ELSE 'ok' END
            """, new { project.AppId, project.IsDebug, key, session.Started, session.Timestamp, session.Sequence, session.Init,
                session.Status, session.Errors, session.Duration, release, environment }, transaction, cancellationToken: ct));
        return SentryWriteResult.Accepted;
    }

    private async Task<SentryWriteResult> RecordReport(SentryProject project, SentryEnvelope envelope,
        DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var report = envelope.ClientReport!;
        var counters = JsonSerializer.Serialize(report.Counters, JsonOptions);
        // An accompanying event ID distinguishes independent clients with identical counters/timestamps.
        // Metadata-only reports have no stable SDK identifier, so exact content remains the fallback.
        var canonical = JsonSerializer.Serialize(new { envelope.EventId, report.Timestamp, report.Counters }, JsonOptions);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var args = new { project.AppId, project.IsDebug, key, report.Timestamp, counters,
            release = sanitizer.SanitizeLabel(envelope.Error?.Release ?? envelope.Transaction?.Release, 100),
            environment = sanitizer.SanitizeLabel(envelope.Error?.Environment ?? envelope.Transaction?.Environment, 50) };
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM monitoring_client_reports WHERE app_id=@AppId AND is_debug=@IsDebug AND report_key=@key)
            """, args, transaction, cancellationToken: ct));
        if (exists) return SentryWriteResult.Accepted;
        if (await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM monitoring_client_reports WHERE app_id=@AppId", project, transaction, cancellationToken: ct)) >= MaxClientReportsPerApp)
            return SentryWriteResult.MetadataQuota;
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO monitoring_client_reports (app_id,is_debug,report_key,reported_at,release,environment,counters)
            VALUES (@AppId,@IsDebug,@key,@Timestamp,@release,@environment,CAST(@counters AS jsonb)) ON CONFLICT DO NOTHING
            """, args, transaction, cancellationToken: ct));
        return SentryWriteResult.Accepted;
    }

    public async Task<object> Summary(PerformanceFilter filter, CancellationToken ct)
    {
        await using var connection = db.Connection;
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        const string counts = """
            COUNT(*) AS observed, COUNT(*) FILTER(WHERE saw_init) AS with_start,
            COUNT(*) FILTER(WHERE saw_init AND status IN ('exited','crashed')) AS eligible,
            COUNT(*) FILTER(WHERE status='crashed') AS crashed,
            COUNT(*) FILTER(WHERE saw_init AND status='crashed') AS eligible_crashed,
            COUNT(*) FILTER(WHERE status='abnormal') AS abnormal,
            COUNT(*) FILTER(WHERE status='ok') AS active,
            COUNT(*) FILTER(WHERE errors>0) AS errored
            """;
        const string sessionsWhere = """
            FROM monitoring_sessions s JOIN apps a ON a.id=s.app_id WHERE s.app_id=@AppId AND s.is_debug=@IsDebug
                AND s.started_at>=@StartDate AND s.started_at<=@EndDate
                AND s.received_at>=CURRENT_TIMESTAMP-a.monitoring_retention_days*INTERVAL '1 day'
                AND (@Release IS NULL OR s.release=@Release) AND (@Environment IS NULL OR s.environment=@Environment)
            """;
        const string reportsWhere = """
            FROM monitoring_client_reports r JOIN apps a ON a.id=r.app_id WHERE r.app_id=@AppId AND r.is_debug=@IsDebug
                AND r.reported_at>=@StartDate AND r.reported_at<=@EndDate
                AND r.received_at>=CURRENT_TIMESTAMP-a.monitoring_retention_days*INTERVAL '1 day'
                AND (@Release IS NULL OR r.release=@Release) AND (@Environment IS NULL OR r.environment=@Environment)
            """;
        var sessions = await connection.QuerySingleAsync<SessionHealthCounts>(new CommandDefinition(
            $"SELECT {counts} {sessionsWhere}", filter, transaction, cancellationToken: ct));
        var groups = (await connection.QueryAsync<SessionHealthGroup>(new CommandDefinition(
            $"SELECT s.release,s.environment,{counts} {sessionsWhere} GROUP BY s.release,s.environment ORDER BY observed DESC,s.release,s.environment LIMIT 101",
            filter, transaction, cancellationToken: ct))).ToArray();
        var reports = await connection.ExecuteScalarAsync<long>(new CommandDefinition($"SELECT COUNT(*) {reportsWhere}", filter, transaction, cancellationToken: ct));
        var reportGroups = (await connection.QueryAsync<SentryDiscardCounter>(new CommandDefinition($"""
            WITH selected AS (SELECT r.counters {reportsWhere})
            SELECT c.reason,c.category,SUM(c.quantity)::bigint AS quantity FROM selected,
                jsonb_to_recordset(selected.counters) AS c(reason text,category text,quantity bigint)
            GROUP BY c.reason,c.category ORDER BY quantity DESC,c.reason,c.category
            """, filter, transaction, cancellationToken: ct))).ToArray();
        var retentionDays = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT monitoring_retention_days FROM apps WHERE id=@AppId", filter, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return new { sessions, groups = groups.Take(100), groupsTruncated = groups.Length > 100,
            clientReports = new { reports, discardedEvents = reportGroups.Sum(g => g.Quantity), groups = reportGroups }, retentionDays };
    }

    private sealed class Policy
    {
        public bool CaptureErrors { get; set; }
        public bool CapturePerformance { get; set; }
        public byte[]? MonitoringSessionSalt { get; set; }
    }

    private sealed class SessionIdentity
    {
        public DateTime StartedAt { get; set; }
        public string Release { get; set; } = "";
        public string Environment { get; set; } = "";
    }
}
