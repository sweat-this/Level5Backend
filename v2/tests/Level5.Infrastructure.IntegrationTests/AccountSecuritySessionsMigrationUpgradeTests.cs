using Level5.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

public sealed class AccountSecuritySessionsMigrationUpgradeTests
{
    private const string PreviousMigrationId = "20261005144949_AddPasswordRecoveryAndSessionGeneration";

    [Fact]
    public async Task Existing_sessions_receive_safe_metadata_without_changing_their_generation()
    {
        var container = new PostgreSqlBuilder().WithImage("postgres:18-alpine")
            .WithDatabase("level5_v2_session_upgrade_test")
            .WithUsername("level5")
            .WithPassword("test-password")
            .Build();
        await container.StartAsync();

        try
        {
            var options = new DbContextOptionsBuilder<Level5V2DbContext>()
                .UseNpgsql(container.GetConnectionString())
                .Options;

            await using (var db = new Level5V2DbContext(options))
            {
                var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrator.MigrateAsync(PreviousMigrationId);
            }

            var accountId = Guid.CreateVersion7();
            var sessionId = Guid.CreateVersion7();
            var createdAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
            var username = $"pre{Guid.NewGuid():N}"[..15];
            await using (var db = new Level5V2DbContext(options))
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""INSERT INTO accounts ("Id", "Username", "UsernameCanonical", "Status", "PasswordHash", "SessionGeneration", "CreatedAt") VALUES ({accountId}, {username}, {username.ToLowerInvariant()}, {"Active"}, {"hash"}, {7L}, {createdAt})""");
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""INSERT INTO auth_sessions ("Id", "AccountId", "RefreshTokenHash", "CreatedAt", "ExpiresAt", "Revision", "SessionGeneration") VALUES ({sessionId}, {accountId}, {"old-hash"}, {createdAt}, {createdAt.AddDays(1)}, {0L}, {7L})""");
                await db.Database.MigrateAsync();
            }

            await using var verify = new Level5V2DbContext(options);
            var session = await verify.AuthSessions.AsNoTracking().SingleAsync(row => row.Id == sessionId);
            Assert.Equal(createdAt, session.LastRefreshedAt);
            Assert.Equal("Unknown", session.ClientKind);
            Assert.Equal(7, session.SessionGeneration);
            Assert.Null(session.RevokedAt);
        }
        finally
        {
            await container.DisposeAsync();
        }
    }
}
