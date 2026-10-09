using Level5.Domain.Identity;
using Level5.Infrastructure.Persistence;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

public sealed class EmailChangeMigrationUpgradeTests
{
    private const string PreviousMigrationId = "20261006180840_AddPlayerNotifications";

    [Fact]
    public async Task Upgrade_preserves_existing_challenge_and_adds_unique_target_reservation()
    {
        var container = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("level5_v2_email_change_upgrade_test")
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

            var now = DateTimeOffset.UtcNow;
            var target = Email.Create($"reserved{Guid.NewGuid():N}@example.com");
            var first = Account.Register(
                Username.Create($"a{Guid.NewGuid():N}"[..15]),
                "hash",
                now,
                target);
            await using (var db = new Level5V2DbContext(options))
            {
                await new AccountStore(db).AddAsync(first, CancellationToken.None);
                await new EmailVerificationChallengeStore(db).AddAsync(
                    EmailVerificationChallenge.Create(
                        first.Id, target, $"hash-{Guid.NewGuid():N}", now, TimeSpan.FromHours(24)),
                    CancellationToken.None);
                await db.SaveChangesAsync();
            }

            await using (var db = new Level5V2DbContext(options))
            {
                await db.Database.MigrateAsync();
                var persisted = await db.EmailVerificationChallenges.AsNoTracking().SingleAsync();
                Assert.Equal(target.Canonical, persisted.TargetEmailCanonical);
            }

            var second = Account.Register(Username.Create($"b{Guid.NewGuid():N}"[..15]), "hash", now);
            await using (var db = new Level5V2DbContext(options))
            {
                await new AccountStore(db).AddAsync(second, CancellationToken.None);
                await db.SaveChangesAsync();

                await new EmailVerificationChallengeStore(db).AddAsync(
                    EmailVerificationChallenge.Create(
                        second.Id,
                        Email.Create(target.Value.ToUpperInvariant()),
                        $"hash-{Guid.NewGuid():N}",
                        now,
                        TimeSpan.FromHours(24)),
                    CancellationToken.None);

                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }
        }
        finally
        {
            await container.DisposeAsync();
        }
    }
}
