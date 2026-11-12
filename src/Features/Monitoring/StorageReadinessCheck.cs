using Aptabase.Data;
using ClickHouse.Client.ADO;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Aptabase.Features.Monitoring;

public sealed class StorageReadinessCheck(IDbContext db, EnvSettings env) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = db.Connection;
            // Npgsql can defer cancellation while connecting/authenticating; bound that phase independently.
            connection.ConnectionString = new NpgsqlConnectionStringBuilder(connection.ConnectionString) {
                Timeout = 2, CommandTimeout = 2, CancellationTimeout = 500, Pooling = false
            }.ConnectionString;
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = 2;
            await command.ExecuteScalarAsync(cancellationToken);
            if (!string.IsNullOrEmpty(env.ClickHouseConnectionString))
            {
                await using var analytics = new ClickHouseConnection(env.ClickHouseConnectionString);
                await analytics.OpenAsync(cancellationToken);
                await using var query = analytics.CreateCommand();
                query.CommandText = "SELECT 1";
                query.CommandTimeout = 3;
                await query.ExecuteScalarAsync(cancellationToken);
            }
            return HealthCheckResult.Healthy();
        }
        catch (Exception)
        {
            // This anonymous probe must never disclose provider errors, endpoints or credentials.
            return HealthCheckResult.Unhealthy("Storage unavailable");
        }
    }
}
