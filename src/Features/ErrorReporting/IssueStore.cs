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

public record IssuePage(IEnumerable<Issue> Issues, long Total);
public record IssueAccess(bool CanRead, bool CanManage);

public interface IIssueStore
{
    Task Record(TrackingError error, CancellationToken cancellationToken);
    Task<IssueAccess> GetAccess(string appId, UserIdentity user, CancellationToken cancellationToken);
    Task<IssuePage> List(string appId, bool isDebug, string status, int offset, int limit, CancellationToken cancellationToken);
    Task<Issue?> Get(string appId, bool isDebug, string id, CancellationToken cancellationToken);
    Task<Issue?> SetStatus(string appId, bool isDebug, string id, string status, string ownerId, CancellationToken cancellationToken);
}

public class IssueStore(IDbContext db) : IIssueStore
{
    public async Task Record(TrackingError error, CancellationToken cancellationToken)
    {
        await using var connection = db.Connection;
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO error_issues (app_id, is_debug, id, error_type, error_message, stack_trace, first_seen, last_seen)
            SELECT @AppId, @IsDebug, @id, @ErrorType, @ErrorMessage, @stack, @Timestamp, @Timestamp
            FROM apps WHERE id = @AppId AND deleted_at IS NULL
            ON CONFLICT (app_id, is_debug, id) DO UPDATE SET
                occurrence_count = error_issues.occurrence_count + 1,
                first_seen = LEAST(error_issues.first_seen, EXCLUDED.first_seen),
                last_seen = GREATEST(error_issues.last_seen, EXCLUDED.last_seen)
            """, new { error.AppId, error.IsDebug,
                id = IssueFingerprint.Create(error.ErrorType, error.ErrorMessage, error.StackTrace),
                error.ErrorType, error.ErrorMessage, stack = error.StackTrace ?? "", error.Timestamp },
            cancellationToken: cancellationToken));
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

    public async Task<IssuePage> List(string appId, bool isDebug, string status, int offset, int limit, CancellationToken cancellationToken)
    {
        await using var connection = db.Connection;
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        using var result = await connection.QueryMultipleAsync(new CommandDefinition("""
            SELECT * FROM error_issues WHERE app_id = @appId AND is_debug = @isDebug
                AND (@status = 'all' OR status = @status)
                ORDER BY last_seen DESC, id LIMIT @limit OFFSET @offset;
            SELECT COUNT(*) FROM error_issues WHERE app_id = @appId AND is_debug = @isDebug
                AND (@status = 'all' OR status = @status);
            """, new { appId, isDebug, status, offset, limit }, transaction, cancellationToken: cancellationToken));
        var issues = (await result.ReadAsync<Issue>()).ToArray();
        var total = await result.ReadSingleAsync<long>();
        await transaction.CommitAsync(cancellationToken);
        return new IssuePage(issues, total);
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
