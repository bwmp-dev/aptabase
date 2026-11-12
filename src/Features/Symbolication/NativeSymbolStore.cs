using System.Security.Cryptography;
using System.Text.Json;
using Aptabase.Data;
using Aptabase.Features.ErrorReporting;
using Dapper;

namespace Aptabase.Features.Symbolication;

public record SymbolArtifact(Guid Id, string? BuildId, string Status, int SizeBytes, DateTime CreatedAt, DateTime ExpiresAt);
public record ArtifactUpload(string Result, SymbolArtifact? Artifact);

public class NativeSymbolStore(IDbContext db, INativeSymbolProcessor processor, IPiiSanitizer sanitizer)
{
    public const int MaxAppBytes = 33554432;
    public const int MaxAppArtifacts = 8;
    private const string Columns = "s.id,s.build_id,s.status,s.size_bytes,s.created_at,LEAST(s.expires_at,s.created_at+a.monitoring_retention_days*INTERVAL '1 day') AS expires_at";
    private const string Active = "a.deleted_at IS NULL AND s.expires_at>CURRENT_TIMESTAMP AND s.created_at+a.monitoring_retention_days*INTERVAL '1 day'>CURRENT_TIMESTAMP";
    public async Task<IEnumerable<SymbolArtifact>> List(string appId, bool debug, CancellationToken ct)
    {
        await using var connection = db.Connection;
        return await connection.QueryAsync<SymbolArtifact>(new CommandDefinition($"SELECT {Columns} FROM native_symbols s JOIN apps a ON a.id=s.app_id WHERE s.app_id=@appId AND s.is_debug=@debug AND {Active} ORDER BY s.created_at DESC LIMIT 8", new { appId, debug }, cancellationToken: ct));
    }
    public async Task<ArtifactUpload> Upload(string appId, string ownerId, bool debug, byte[] bytes, CancellationToken ct)
    {
        await using var connection = db.Connection; await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var days = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition("""
            SELECT a.monitoring_retention_days FROM apps a JOIN users u ON u.id=a.owner_id
            WHERE a.id=@appId AND a.owner_id=@ownerId AND a.deleted_at IS NULL AND u.lock_reason IS NULL FOR UPDATE OF a
            """, new { appId, ownerId }, transaction, cancellationToken: ct));
        if (days is null) return new("forbidden", null);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM native_symbols WHERE app_id=@appId AND (expires_at<=CURRENT_TIMESTAMP OR created_at+@days*INTERVAL '1 day'<=CURRENT_TIMESTAMP)", new { appId, days }, transaction, cancellationToken: ct));
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var existing = await connection.QuerySingleOrDefaultAsync<SymbolArtifact>(new CommandDefinition($"SELECT {Columns} FROM native_symbols s JOIN apps a ON a.id=s.app_id WHERE s.app_id=@appId AND s.is_debug=@debug AND s.content_hash=@hash", new { appId, debug, hash }, transaction, cancellationToken: ct));
        if (existing is not null) { await transaction.CommitAsync(ct); return new("accepted", existing); }
        var budget = await connection.QuerySingleAsync<(long Count, long Bytes)>(new CommandDefinition("SELECT COUNT(*),COALESCE(SUM(size_bytes),0)::bigint FROM native_symbols WHERE app_id=@appId", new { appId }, transaction, cancellationToken: ct));
        if (budget.Count >= MaxAppArtifacts || budget.Bytes + bytes.Length > MaxAppBytes) return new("budget", null);
        var artifact = await connection.QuerySingleAsync<SymbolArtifact>(new CommandDefinition("""
            INSERT INTO native_symbols(id,app_id,is_debug,content_hash,content,size_bytes,expires_at)
            VALUES(@id,@appId,@debug,@hash,@bytes,@size,CURRENT_TIMESTAMP+@days*INTERVAL '1 day')
            RETURNING id,build_id,status,size_bytes,created_at,expires_at
            """, new { id = Guid.NewGuid(), appId, debug, hash, bytes, size = bytes.Length, days }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct); return new("accepted", artifact);
    }
    public async Task<bool> Delete(string appId, string ownerId, bool debug, Guid id, CancellationToken ct)
    {
        await using var connection = db.Connection;
        return await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM native_symbols s USING apps a,users u WHERE s.app_id=a.id AND u.id=a.owner_id
            AND s.app_id=@appId AND s.id=@id AND s.is_debug=@debug AND a.owner_id=@ownerId AND a.deleted_at IS NULL AND u.lock_reason IS NULL
            """, new { appId, ownerId, debug, id }, cancellationToken: ct)) == 1;
    }
    public async Task<int> Cleanup(CancellationToken ct)
    {
        await using var connection = db.Connection;
        return await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM native_symbols WHERE id IN (SELECT s.id FROM native_symbols s JOIN apps a ON a.id=s.app_id
            WHERE a.deleted_at IS NOT NULL OR s.expires_at<=CURRENT_TIMESTAMP OR s.created_at+a.monitoring_retention_days*INTERVAL '1 day'<=CURRENT_TIMESTAMP
            ORDER BY s.created_at LIMIT 100 FOR UPDATE OF s SKIP LOCKED)
            """, cancellationToken: ct));
    }
    public async Task<bool> ProcessOne(CancellationToken ct)
    {
        if (!processor.Enabled) return false;
        await using var connection = db.Connection; await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // One isolated worker across collector instances; crashes release this transaction lock.
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT pg_try_advisory_xact_lock(610428220)", transaction: transaction, cancellationToken: ct))) return false;
        var job = await connection.QuerySingleOrDefaultAsync<(Guid Id, byte[] Content)?>(new CommandDefinition($"SELECT s.id,s.content FROM native_symbols s JOIN apps a ON a.id=s.app_id WHERE s.status='pending' AND {Active} ORDER BY s.created_at LIMIT 1 FOR UPDATE OF s SKIP LOCKED", transaction: transaction, cancellationToken: ct));
        if (job is null) return false;
        var index = await processor.Process(job.Value.Content, ct);
        await connection.ExecuteAsync(new CommandDefinition("UPDATE native_symbols SET status=@status,build_id=@buildId,symbol_index=CAST(@index AS jsonb),content=CASE WHEN @status='invalid' THEN ''::bytea ELSE content END WHERE id=@id",
            new { id = job.Value.Id, status = index is null ? "invalid" : "ready", buildId = index?.BuildId, index = index is null ? null : JsonSerializer.Serialize(index) }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct); return true;
    }
    public async Task<object[]?> Resolve(string appId, bool debug, string issueId, Guid eventId, CancellationToken ct)
    {
        await using var connection = db.Connection;
        var json = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
            SELECT r.native_frames::text FROM error_receipts r JOIN apps a ON a.id=r.app_id
            WHERE r.app_id=@appId AND r.is_debug=@debug AND r.event_id=@eventId AND r.issue_id=@issueId
            AND a.deleted_at IS NULL AND r.received_at+a.monitoring_retention_days*INTERVAL '1 day'>CURRENT_TIMESTAMP
            """, new { appId, debug, eventId, issueId }, cancellationToken: ct));
        if (json is null) return null;
        var frames = JsonSerializer.Deserialize<NativeFrame[]>(json)!;
        var indices = (await connection.QueryAsync<string>(new CommandDefinition($"SELECT s.symbol_index::text FROM native_symbols s JOIN apps a ON a.id=s.app_id WHERE s.app_id=@appId AND s.is_debug=@debug AND s.status='ready' AND {Active}", new { appId, debug }, cancellationToken: ct)))
            .Select(x => JsonSerializer.Deserialize<NativeSymbolIndex>(x)!).GroupBy(x => x.BuildId).ToDictionary(g => g.Key, g => g.ToArray());
        return frames.Select(frame =>
        {
            var matches = indices.GetValueOrDefault(frame.BuildId);
            // Different artifacts claiming one build ID are ambiguous: never guess a winner.
            var function = matches?.Length == 1 ? matches[0].Resolve(frame.Offset) : null;
            return (object)new
            {
                frame.BuildId,
                frame.Address,
                status = matches?.Length > 1 ? "ambiguous" : function is null ? "unresolved" : "resolved",
                function = function is null ? null : sanitizer.SanitizeLabel(function.Name, 512),
                offset = function is null ? (ulong?)null : frame.Offset - function.Address
            };
        }).ToArray();
    }
}
