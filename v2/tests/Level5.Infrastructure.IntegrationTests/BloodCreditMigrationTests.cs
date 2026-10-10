using Level5.Domain.Platform;
using Level5.Infrastructure.BloodMoney;
using Level5.Infrastructure.Persistence;
using Level5.Infrastructure.Persistence.Repositories;
using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

public sealed class BloodCreditMigrationTests
{
    private const string PreviousMigration = "20261006233500_AddEmailChangeReservation";

    [Fact]
    public async Task Upgrade_preserves_Platform_and_Level5_data_adds_empty_credit_tables_and_constraints()
    {
        await using var container = Container();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<Level5V2DbContext>().UseNpgsql(container.GetConnectionString()).Options;
        await using var db = new Level5V2DbContext(options);
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        var beforeTables = await Tables(db);
        var now = DateTimeOffset.UtcNow;
        var player = await PlayerSeeding.CreatePlayerAsync(db, "Upgrade", now);
        await new ProductEntitlementStore(db).AddAsync(ProductEntitlement.Grant(player, ProductId.Create("level5"), EntitlementKind.Owned, now), CancellationToken.None);
        var resultId = Guid.NewGuid();
        db.MatchResults.Add(new MatchResultRow
        {
            Id = resultId, PlayerId = player.Value, ClientResultId = Guid.NewGuid(), ModeId = 1, LevelId = 2,
            CharacterId = "hero", ClientVersion = "upgrade-test", Platform = "windows",
            MetricsJson = "{\"TotalPoints\":90}", ModifiersJson = "{}", CreatedAt = now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await db.Database.MigrateAsync();

        var profile = await db.PlayerProfiles.AsNoTracking().SingleAsync(row => row.Id == player.Value);
        Assert.Equal("Upgrade", profile.DisplayName);
        Assert.True(await db.Accounts.AnyAsync(row => row.Id == profile.AccountId));
        Assert.Equal("Owned", (await db.ProductEntitlements.AsNoTracking().SingleAsync()).Kind);
        var result = await db.MatchResults.AsNoTracking().SingleAsync(row => row.Id == resultId);
        Assert.Equal(1, result.ModeId);
        Assert.Equal(2, result.LevelId);
        Assert.Equal("{\"TotalPoints\": 90}", result.MetricsJson);
        Assert.Equal("upgrade-test", result.ClientVersion);
        Assert.Equal(new[] { "blood_money_challenge_participants", "blood_money_challenges", "blood_money_credit_accounts", "blood_money_credit_postings", "blood_money_credit_reservations", "blood_money_credit_transactions" },
            (await Tables(db)).Except(beforeTables).Order().ToArray());
        Assert.Empty(await db.Set<BloodCreditAccountRow>().ToListAsync());
        Assert.Empty(await db.Set<BloodCreditTransactionRow>().ToListAsync());
        Assert.Empty(await db.Set<BloodCreditPostingRow>().ToListAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());

        var constraints = await db.Database.SqlQueryRaw<ConstraintInfo>("""
            SELECT conname AS "Name", contype::text AS "Type", confdeltype::text AS "DeleteAction"
            FROM pg_constraint
            WHERE conrelid IN ('blood_money_credit_accounts'::regclass,
                'blood_money_credit_transactions'::regclass, 'blood_money_credit_postings'::regclass)
            """).ToListAsync();
        Assert.Equal(3, constraints.Count(row => row.Type == "p"));
        Assert.Equal(4, constraints.Count(row => row.Type == "f"));
        Assert.All(constraints.Where(row => row.Type == "f"), row => Assert.Equal("r", row.DeleteAction));
        foreach (var name in new[] { "CK_blood_credit_balance", "CK_blood_credit_revision", "CK_blood_credit_delta",
                     "CK_blood_credit_kind", "CK_blood_credit_reference", "CK_blood_credit_id",
                     "CK_blood_credit_posting_amount", "CK_blood_credit_posting_owner" })
            Assert.Contains(constraints, row => row.Name == name && row.Type == "c");
    }

    [Fact]
    public async Task Conflicting_later_table_rolls_back_earlier_DDL_and_migration_history_then_can_retry()
    {
        await using var container = Container();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<Level5V2DbContext>().UseNpgsql(container.GetConnectionString()).Options;
        await using var db = new Level5V2DbContext(options);
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        var previousHistory = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE blood_money_credit_postings (sentinel integer NOT NULL)");
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
        Assert.Equal(PostgresErrorCodes.DuplicateTable, error.SqlState);
        var tables = await Tables(db);
        Assert.DoesNotContain("blood_money_credit_accounts", tables);
        Assert.DoesNotContain("blood_money_credit_transactions", tables);
        Assert.Contains("blood_money_credit_postings", tables);
        Assert.Equal(previousHistory, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(3, (await db.Database.GetPendingMigrationsAsync()).Count());
        // Drop only the sentinel created by this fixture inside its dedicated container.
        await db.Database.ExecuteSqlRawAsync("DROP TABLE blood_money_credit_postings");
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(4, (await Tables(db)).Count(name => name.StartsWith("blood_money_credit_", StringComparison.Ordinal)));
    }

    private static PostgreSqlContainer Container() => new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("blood_credit_upgrade").WithUsername("level5").WithPassword("test-password").Build();

    private static Task<List<string>> Tables(Level5V2DbContext db) => db.Database.SqlQueryRaw<string>(
        "SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'").ToListAsync();

    private sealed class ConstraintInfo
    {
        public string Name { get; set; } = null!;
        public string Type { get; set; } = null!;
        public string DeleteAction { get; set; } = null!;
    }
}
