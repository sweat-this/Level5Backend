using Level5.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

public sealed class EmailVerificationMigrationUpgradeTests
{
    private const string PreviousMigrationId = "20260925165539_AddCompetitiveSeriesStatusCreatedAtIndex";

    [Fact]
    public async Task Existing_email_remains_unverified_when_schema_is_upgraded()
    {
        var container = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("level5_v2_email_upgrade_test")
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
            var username = $"pre{Guid.NewGuid():N}"[..15];
            var email = $"pre{Guid.NewGuid():N}@example.com";
            await using (var db = new Level5V2DbContext(options))
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO accounts
                         ("Id", "Username", "UsernameCanonical", "Email", "EmailCanonical", "Status", "PasswordHash", "CreatedAt")
                     VALUES
                         ({accountId}, {username}, {username.ToLowerInvariant()}, {email}, {email.ToLowerInvariant()}, {"Active"}, {"hash"}, {DateTimeOffset.UtcNow})
                     """);
            }

            await using (var db = new Level5V2DbContext(options))
            {
                await db.Database.MigrateAsync();
            }

            await using (var db = new Level5V2DbContext(options))
            {
                var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId);
                Assert.Equal(email, account.Email);
                Assert.Null(account.EmailVerifiedAt);
            }
        }
        finally
        {
            await container.DisposeAsync();
        }
    }
}
