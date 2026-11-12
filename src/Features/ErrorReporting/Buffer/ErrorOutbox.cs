using System.Text.Json;
using Aptabase.Data;
using Dapper;

namespace Aptabase.Features.ErrorReporting.Buffer;

public class ErrorOutbox(IDbContext db, IErrorIngestionClient client)
{
    private sealed class ForwardingPolicy
    {
        public string Id { get; set; } = "";
        public bool CanSend { get; set; }
    }

    public async Task<int> Flush(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        await using var connection = db.Connection;
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var appIds = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT app_id FROM error_outbox ORDER BY created_at LIMIT 200
            """, transaction: transaction, cancellationToken: token))).Distinct().Order().ToArray();
        if (appIds.Length == 0) return 0;
        var allowed = (await connection.QueryAsync<ForwardingPolicy>(new CommandDefinition("""
            SELECT id, (forward_raw_errors AND deleted_at IS NULL) AS can_send FROM apps WHERE id=ANY(@appIds) ORDER BY id FOR SHARE
            """, new { appIds }, transaction, cancellationToken: token))).Where(p => p.CanSend).Select(p => p.Id).ToHashSet();
        var payloads = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT payload::text FROM error_outbox WHERE app_id=ANY(@appIds) ORDER BY created_at, app_id, is_debug, event_id
            LIMIT 200 FOR UPDATE SKIP LOCKED
            """, new { appIds }, transaction, cancellationToken: token))).ToArray();
        if (payloads.Length == 0) return 0;
        var errors = payloads.Select(p => JsonSerializer.Deserialize<TrackingError>(p)).ToArray();
        var rows = errors.Where(e => allowed.Contains(e.AppId) && e.Timestamp >= DateTime.UtcNow - ErrorRow.ErrorTTL).Select(e => new ErrorRow(ref e)).ToArray();
        var sent = rows.Length == 0 ? 0 : await client.BulkSendErrorAsync(rows, token);
        if (sent != rows.Length) throw new InvalidOperationException("Raw error sink did not acknowledge every row; batch remains queued.");
        foreach (var error in errors)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                DELETE FROM error_outbox WHERE app_id = @AppId AND is_debug = @IsDebug AND event_id = @eventId
                """, new { error.AppId, error.IsDebug, eventId = Guid.Parse(error.ErrorId) }, transaction, cancellationToken: token));
        }
        await transaction.CommitAsync(token);
        return rows.Length;
    }
}
