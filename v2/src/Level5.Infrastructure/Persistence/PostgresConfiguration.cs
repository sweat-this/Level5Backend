using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL;

namespace Level5.Infrastructure.Persistence;

/// <summary>
/// The single definition of how the running API talks to Postgres, shared by DI registration and
/// the integration-test fixtures so tests exercise the same execution strategy production does.
///
/// Connection pooling, connect timeout (<c>Timeout</c>, Npgsql default 15s) and command timeout
/// (<c>Command Timeout</c>, default 30s) are deliberately left to the connection string: they are
/// deployment-sized values (see "Connection budget" in v2/README.md), and every one of them is
/// already finite by default.
/// </summary>
public static class PostgresConfiguration
{
    /// <summary>
    /// Retries after the first failure. Small on purpose: with EF's exponential backoff this adds
    /// roughly 4s of waiting in the worst case, which rides out a dropped pooled connection or a
    /// brief network blip without holding a request open through a real outage.
    /// </summary>
    public const int MaxRetryCount = 3;

    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Schema migrations are a different workload from API requests (e.g. building an index on a
    /// large table), so design-time tooling and migration bundles get a longer, but still finite,
    /// command timeout instead of inheriting the request-oriented default.
    /// </summary>
    public static readonly TimeSpan MigrationCommandTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Runtime configuration. The retrying strategy only retries what Npgsql classifies as
    /// transient (<c>NpgsqlException.IsTransient</c>: network/IO failures, server shutdown,
    /// serialization failures/deadlocks), minus timeouts (see
    /// <see cref="NoTimeoutRetryingExecutionStrategy"/>). Unique violations, FK/check violations
    /// and the zero-rows-affected result of a revision-conditioned update are not transient, so
    /// they surface on the first attempt exactly as before - business and concurrency outcomes are
    /// never silently re-executed. No code path opens an explicit transaction, which a retrying
    /// strategy would otherwise reject.
    /// </summary>
    public static TBuilder UseLevel5Postgres<TBuilder>(this TBuilder options, string connectionString)
        where TBuilder : DbContextOptionsBuilder
    {
        options.UseNpgsql(connectionString, npgsql => npgsql.ExecutionStrategy(
            dependencies => new NoTimeoutRetryingExecutionStrategy(dependencies)));
        return options;
    }

    /// <summary>
    /// Npgsql's retrying strategy, except that nothing caused by a <see cref="TimeoutException"/>
    /// is retried. Npgsql counts timeouts as transient, but a timed-out command or connect has
    /// already spent its whole budget - usually because the server is overloaded or unreachable -
    /// so re-running it up to <see cref="MaxRetryCount"/> more times would multiply both the
    /// request's latency (4 × a 30s command timeout) and the load on a struggling database.
    /// Refused/broken connections, which fail fast, are still retried.
    /// </summary>
    private sealed class NoTimeoutRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : NpgsqlRetryingExecutionStrategy(dependencies, PostgresConfiguration.MaxRetryCount, PostgresConfiguration.MaxRetryDelay, errorCodesToAdd: null)
    {
        protected override bool ShouldRetryOn(Exception exception)
            => base.ShouldRetryOn(exception) && !IsTimeout(exception);

        private static bool IsTimeout(Exception exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                if (current is TimeoutException)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
