using System.Data.Common;
using Level5.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Level5.Infrastructure.Health;

/// <summary>
/// Readiness: can this instance reach Postgres right now. One <c>SELECT 1</c> on a directly opened
/// (usually pooled) connection, deliberately outside EF's retrying execution strategy -
/// <c>Database.CanConnectAsync</c> runs through that strategy, which made a single probe against an
/// unreachable server take the whole retry budget (measured ~13s) instead of answering promptly.
/// The orchestrator's own probe interval is the retry loop for readiness. Never runs migrations.
/// </summary>
public sealed class DatabaseHealthCheck(Level5V2DbContext db) : IHealthCheck
{
    /// <summary>Upper bound for one probe, so a black-holed host can't hold it for the full connect timeout.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connection = db.Database.GetDbConnection();
        try
        {
            await connection.OpenAsync(cancellationToken);

            // Opening a pooled connection hands back an idle connector without a round trip, so a
            // server that went away after the connection was pooled still "opens" fine (observed:
            // readiness stayed 200 with Postgres stopped). One trivial statement proves it answers.
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken);

            return HealthCheckResult.Healthy();
        }
        catch (DbException ex)
        {
            return HealthCheckResult.Unhealthy("Cannot connect to the V2 database.", ex);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
