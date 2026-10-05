using Level5.Infrastructure.Persistence;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

public sealed class PasswordRecoveryMigrationUpgradeTests
{
    private const string PreviousMigrationId = "20261005122343_AddEmailVerificationFoundation";

    [Fact]
    public async Task Existing_account_and_session_migrate_to_matching_zero_generation_and_remain_refreshable()
    {
        var container = new PostgreSqlBuilder().WithImage("postgres:18-alpine")
            .WithDatabase("level5_v2_password_upgrade_test").WithUsername("level5").WithPassword("test-password").Build();
        await container.StartAsync();
        try
        {
            var options = new DbContextOptionsBuilder<Level5V2DbContext>().UseNpgsql(container.GetConnectionString()).Options;
            await using (var db = new Level5V2DbContext(options))
            {
                var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrator.MigrateAsync(PreviousMigrationId);
            }

            var accountId = Guid.CreateVersion7();
            var sessionId = Guid.CreateVersion7();
            var username = $"pre{Guid.NewGuid():N}"[..15];
            await using (var db = new Level5V2DbContext(options))
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""INSERT INTO accounts ("Id", "Username", "UsernameCanonical", "Status", "PasswordHash", "CreatedAt") VALUES ({accountId}, {username}, {username.ToLowerInvariant()}, {"Active"}, {"hash"}, {DateTimeOffset.UtcNow})""");
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""INSERT INTO auth_sessions ("Id", "AccountId", "RefreshTokenHash", "CreatedAt", "ExpiresAt", "Revision") VALUES ({sessionId}, {accountId}, {"old-hash"}, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow.AddDays(1)}, {0L})""");
                await db.Database.MigrateAsync();
            }

            await using var verifyDb = new Level5V2DbContext(options);
            var account = await verifyDb.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId);
            var session = await new AuthSessionStore(verifyDb).FindByIdAsync(new Level5.Domain.Ids.AuthSessionId(sessionId), default);
            Assert.Equal(0, account.SessionGeneration);
            Assert.Equal(0, session!.SessionGeneration);
            session.Rotate("rotated-hash", DateTimeOffset.UtcNow, TimeSpan.FromDays(1));
            Assert.True(await new AuthSessionStore(verifyDb).TryRotateForActiveGenerationAsync(session, 0, default));
        }
        finally
        {
            await container.DisposeAsync();
        }
    }
}
