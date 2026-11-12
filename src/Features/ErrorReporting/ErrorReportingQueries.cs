using Dapper;
using Aptabase.Data;

namespace Aptabase.Features.ErrorReporting;

public static class ErrorReportingQueries
{
    /// <summary>
    /// Resets the per-app error counter for every app. Intended to run on a monthly schedule.
    /// </summary>
    public static async Task<int> ResetAllErrorCounts(this IDbContext db, CancellationToken cancellationToken)
    {
        var cmd = new CommandDefinition(
            "UPDATE apps SET error_count = 0 WHERE error_count > 0",
            cancellationToken: cancellationToken
        );

        return await db.Connection.ExecuteAsync(cmd);
    }
}
