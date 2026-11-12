using System.Text.Json;
using Aptabase.Data;
using Aptabase.Features.ErrorReporting;
using Dapper;

namespace Aptabase.Features.Performance;

public class AppTransaction
{
    public Guid EventId { get; set; }
    public DateTime Timestamp { get; set; }
    public string Name { get; set; } = "";
    public string Operation { get; set; } = "";
    public double DurationMs { get; set; }
    public string Status { get; set; } = "";
    public string Release { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Platform { get; set; } = "";
}

public class TransactionGroup
{
    public string Name { get; set; } = "";
    public string Operation { get; set; } = "";
    public long Count { get; set; }
    public long ErrorCount { get; set; }
    public double AvgMs { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
}

public record PerformanceFilter(string AppId, bool IsDebug, DateTime StartDate, DateTime EndDate, string? Environment, string? Release, string? Name, string? Operation);
public record TransactionPage(IEnumerable<AppTransaction> Transactions, long Total);
public record TransactionDetail(AppTransaction Transaction, IEnumerable<TransactionSpan> Spans);

public class PerformanceStore(IDbContext db, IPiiSanitizer sanitizer)
{
    private const string Columns = "event_id, timestamp, name, operation, duration_ms, status, release, environment, platform";
    private const string Where = """
        app_id = @AppId AND is_debug = @IsDebug AND timestamp >= @StartDate AND timestamp <= @EndDate
        AND (@Environment IS NULL OR environment = @Environment) AND (@Release IS NULL OR release = @Release)
        AND (@Name IS NULL OR name = @Name) AND (@Operation IS NULL OR operation = @Operation)
        """;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<bool> Record(string appId, TransactionBody body, CancellationToken cancellationToken)
    {
        await using var connection = db.Connection;
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var accepted = await Record(appId, body, connection, transaction, cancellationToken);
        if (accepted) await transaction.CommitAsync(cancellationToken);
        return accepted;
    }

    internal async Task<bool> Record(string appId, TransactionBody body, System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken)
    {
        // All retained strings pass through the same sanitizer; arbitrary request/user context is never stored.
        var spans = body.Spans!.Select(s => new TransactionSpan {
            SpanId = s.SpanId, ParentSpanId = s.ParentSpanId, Name = sanitizer.SanitizeLabel(s.Name, 200),
            Operation = sanitizer.SanitizeLabel(s.Operation, 50), StartOffsetMs = s.StartOffsetMs, DurationMs = s.DurationMs, Status = s.Status
        });
        var timestamp = body.Timestamp!.Value.ToUniversalTime();
        if (timestamp > DateTime.UtcNow) timestamp = DateTime.UtcNow;
        var active = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT true FROM apps a JOIN users u ON u.id=a.owner_id
            WHERE a.id=@appId AND a.deleted_at IS NULL AND u.lock_reason IS NULL AND a.capture_performance FOR SHARE OF a
            """, new { appId }, transaction, cancellationToken: cancellationToken));
        if (!active) return false;
        var accepted = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            WITH active AS (SELECT a.id FROM apps a JOIN users u ON u.id = a.owner_id
                WHERE a.id = @appId AND a.deleted_at IS NULL AND u.lock_reason IS NULL),
            inserted AS (INSERT INTO app_transactions
                (app_id, is_debug, event_id, timestamp, name, operation, duration_ms, status, release, environment, platform, spans)
                SELECT id, @IsDebug, @eventId, @timestamp, @name, @operation, @DurationMs, @Status, @release, @environment, @platform, CAST(@spans AS jsonb)
                FROM active ON CONFLICT DO NOTHING)
            SELECT EXISTS (SELECT 1 FROM active)
            """, new { appId, body.IsDebug, eventId = Guid.Parse(body.EventId!), timestamp,
                name = sanitizer.SanitizeLabel(body.Name, 200), operation = sanitizer.SanitizeLabel(body.Operation, 50), body.DurationMs, body.Status,
                release = sanitizer.SanitizeLabel(body.Release, 100), environment = sanitizer.SanitizeLabel(body.Environment, 50), platform = sanitizer.SanitizeLabel(body.Platform, 30),
                spans = JsonSerializer.Serialize(spans, JsonOptions) }, transaction, cancellationToken: cancellationToken));
        return accepted;
    }

    public async Task<TransactionPage> List(PerformanceFilter filter, int offset, int limit, CancellationToken cancellationToken)
    {
        await using var connection = db.Connection;
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var args = new DynamicParameters(filter);
        args.Add("offset", offset);
        args.Add("limit", limit);
        using var rows = await connection.QueryMultipleAsync(new CommandDefinition(
            $"SELECT {Columns} FROM app_transactions WHERE {Where} ORDER BY timestamp DESC, event_id LIMIT @limit OFFSET @offset; SELECT COUNT(*) FROM app_transactions WHERE {Where}",
            args, transaction, cancellationToken: cancellationToken));
        var list = (await rows.ReadAsync<AppTransaction>()).ToArray();
        var total = await rows.ReadSingleAsync<long>();
        await transaction.CommitAsync(cancellationToken);
        return new TransactionPage(list, total);
    }

    public async Task<IEnumerable<TransactionGroup>> Summary(PerformanceFilter filter, CancellationToken cancellationToken)
    {
        await using var connection = db.Connection;
        return await connection.QueryAsync<TransactionGroup>(new CommandDefinition($"""
            SELECT name, operation, COUNT(*) AS count, COUNT(*) FILTER (WHERE status = 'error') AS error_count,
                AVG(duration_ms) AS avg_ms, percentile_cont(0.5) WITHIN GROUP (ORDER BY duration_ms) AS p50_ms,
                percentile_cont(0.95) WITHIN GROUP (ORDER BY duration_ms) AS p95_ms
            FROM app_transactions WHERE {Where} GROUP BY name, operation ORDER BY count DESC, name, operation LIMIT 100
            """, filter, cancellationToken: cancellationToken));
    }

    public async Task<TransactionDetail?> Get(string appId, bool isDebug, Guid eventId, CancellationToken cancellationToken)
    {
        await using var connection = db.Connection;
        using var rows = await connection.QueryMultipleAsync(new CommandDefinition($"""
            SELECT {Columns} FROM app_transactions WHERE app_id = @appId AND is_debug = @isDebug AND event_id = @eventId;
            SELECT spans::text FROM app_transactions WHERE app_id = @appId AND is_debug = @isDebug AND event_id = @eventId;
            """, new { appId, isDebug, eventId }, cancellationToken: cancellationToken));
        var item = await rows.ReadSingleOrDefaultAsync<AppTransaction>();
        var spans = await rows.ReadSingleOrDefaultAsync<string>();
        return item is null || spans is null ? null : new TransactionDetail(item, JsonSerializer.Deserialize<TransactionSpan[]>(spans, JsonOptions)!);
    }

    public async Task<int> Purge(CancellationToken cancellationToken)
    {
        await using var connection = db.Connection;
        return await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM app_transactions WHERE (app_id, is_debug, event_id) IN (
                SELECT t.app_id, t.is_debug, t.event_id FROM app_transactions t JOIN apps a ON a.id=t.app_id
                WHERE t.received_at < CURRENT_TIMESTAMP - a.monitoring_retention_days * INTERVAL '1 day' OR a.deleted_at IS NOT NULL
                ORDER BY t.received_at LIMIT 10000 FOR UPDATE OF t SKIP LOCKED)
            """, cancellationToken: cancellationToken));
    }
}
