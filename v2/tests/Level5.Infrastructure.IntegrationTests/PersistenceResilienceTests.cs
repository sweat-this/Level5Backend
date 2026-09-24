using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using Level5.Application.Common;
using Level5.Domain.Identity;
using Level5.Infrastructure.Health;
using Level5.Infrastructure.Persistence;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

/// <summary>
/// The bounded retry policy in <see cref="PostgresConfiguration"/> must cover transient
/// infrastructure failures only: a deterministic business/constraint outcome has to surface on the
/// first attempt, never be silently re-executed.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PersistenceResilienceTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Runtime_configuration_uses_a_bounded_retrying_execution_strategy()
    {
        await using var db = fixture.CreateDbContext();

        Assert.True(db.Database.CreateExecutionStrategy().RetriesOnFailure);
    }

    [Fact]
    public async Task A_unique_violation_is_executed_once_and_surfaces_as_a_conflict_not_a_retry()
    {
        var username = $"u{Guid.NewGuid():N}"[..15];
        await using (var seed = fixture.CreateDbContext())
        {
            await new AccountStore(seed).AddAsync(Account.Register(Username.Create(username), "hash", Now), CancellationToken.None);
            await seed.SaveChangesAsync();
        }

        var counter = new CommandCounter("INSERT INTO accounts");
        await using var db = fixture.CreateDbContext(counter);
        await new AccountStore(db).AddAsync(Account.Register(Username.Create(username), "hash2", Now), CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() => new EfUnitOfWork(db).SaveChangesAsync(CancellationToken.None));
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public async Task A_command_timeout_is_executed_once_not_retried_and_is_classified_as_unavailability()
    {
        // Npgsql counts timeouts as transient; retrying one would multiply a slow query's latency
        // and its load on an already struggling server (PostgresConfiguration's strategy).
        var counter = new CommandCounter("pg_sleep");
        await using var db = fixture.CreateDbContext(counter);
        db.Database.SetCommandTimeout(1);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\" FROM pg_sleep(5)").ToListAsync());

        Assert.IsNotType<RetryLimitExceededException>(exception);
        Assert.Equal(1, counter.Count);
        Assert.True(PersistenceFailures.IsTransientUnavailability(exception));
    }

    [Fact]
    public async Task A_refused_connection_is_retried_within_the_bounded_budget_then_gives_up()
    {
        var failures = new ConnectionFailureCounter();
        await using var db = CreateUnreachableDbContext(failures);

        var exception = await Assert.ThrowsAsync<RetryLimitExceededException>(() => db.Accounts.AnyAsync());

        Assert.Equal(PostgresConfiguration.MaxRetryCount + 1, failures.Count);
        Assert.True(PersistenceFailures.IsTransientUnavailability(exception));
    }

    [Fact]
    public async Task Readiness_check_reports_unhealthy_rather_than_throwing_when_Postgres_is_unreachable()
    {
        await using var db = CreateUnreachableDbContext();

        var result = await new DatabaseHealthCheck(db).CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Readiness_check_reports_healthy_when_Postgres_is_reachable()
    {
        await using var db = fixture.CreateDbContext();

        var result = await new DatabaseHealthCheck(db).CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void Exhausted_retries_and_transient_connection_failures_are_classified_as_unavailability()
    {
        var connectionFailure = new NpgsqlException("Failed to connect", new SocketException((int)SocketError.ConnectionRefused));

        Assert.True(PersistenceFailures.IsTransientUnavailability(connectionFailure));
        Assert.True(PersistenceFailures.IsTransientUnavailability(new RetryLimitExceededException("exhausted", connectionFailure)));
        Assert.True(PersistenceFailures.IsTransientUnavailability(new InvalidOperationException("wrapper", connectionFailure)));
    }

    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation)]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation)]
    [InlineData(PostgresErrorCodes.CheckViolation)]
    [InlineData(PostgresErrorCodes.StringDataRightTruncation)]
    public void Deterministic_constraint_failures_are_not_classified_as_unavailability(string sqlState)
    {
        var failure = new DbUpdateException("save failed", new PostgresException("violation", "ERROR", "ERROR", sqlState));

        Assert.False(PersistenceFailures.IsTransientUnavailability(failure));
    }

    [Fact]
    public void Ordinary_exceptions_are_not_classified_as_unavailability()
    {
        Assert.False(PersistenceFailures.IsTransientUnavailability(new InvalidOperationException("bug")));
        Assert.False(PersistenceFailures.IsTransientUnavailability(new OperationCanceledException()));
    }

    private static Level5V2DbContext CreateUnreachableDbContext(params IInterceptor[] interceptors)
    {
        // A port nothing is listening on: bind an ephemeral port, note it, release it.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var options = new DbContextOptionsBuilder<Level5V2DbContext>()
            .UseLevel5Postgres($"Host=127.0.0.1;Port={port};Database=unreachable;Username=unused;Password=unused;Timeout=3")
            .AddInterceptors(interceptors)
            .Options;
        return new Level5V2DbContext(options);
    }

    private sealed class CommandCounter(string fragment) : DbCommandInterceptor
    {
        public int Count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command)
        {
            if (command.CommandText.Contains(fragment, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Count);
            }
        }
    }

    private sealed class ConnectionFailureCounter : DbConnectionInterceptor
    {
        public int Count;

        public override Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            return base.ConnectionFailedAsync(connection, eventData, cancellationToken);
        }
    }
}

/// <summary>
/// Regression for a readiness false positive found during manual validation: with a connection
/// already pooled, stopping Postgres left <c>/health/ready</c> at 200 because opening a pooled
/// connection does no round trip. Uses its own container so it can stop the server mid-test
/// without affecting the shared <see cref="PostgresFixture"/>.
/// </summary>
public sealed class ReadinessAfterServerLossTests
{
    [Fact]
    public async Task Readiness_turns_unhealthy_when_the_server_stops_after_a_connection_was_pooled()
    {
        var container = new Testcontainers.PostgreSql.PostgreSqlBuilder()
            .WithImage("postgres:18-alpine")
            .WithDatabase("level5_v2_readiness_test")
            .WithUsername("level5")
            .WithPassword("test-password")
            .Build();
        await container.StartAsync();

        try
        {
            var options = new DbContextOptionsBuilder<Level5V2DbContext>()
                .UseLevel5Postgres(container.GetConnectionString() + ";Timeout=3")
                .Options;

            await using (var db = new Level5V2DbContext(options))
            {
                var healthy = await new DatabaseHealthCheck(db).CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
                Assert.Equal(HealthStatus.Healthy, healthy.Status);
            }

            await container.StopAsync();

            await using (var db = new Level5V2DbContext(options))
            {
                var afterStop = await new DatabaseHealthCheck(db).CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
                Assert.Equal(HealthStatus.Unhealthy, afterStop.Status);
            }
        }
        finally
        {
            await container.DisposeAsync();
        }
    }
}
