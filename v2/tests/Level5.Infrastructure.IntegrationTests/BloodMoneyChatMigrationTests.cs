using Level5.Application.BloodMoney;
using Level5.Domain.BloodMoney;
using Level5.Infrastructure.BloodMoney;
using Level5.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Level5.Infrastructure.IntegrationTests;

public sealed class BloodMoneyChatMigrationTests
{
    private const string PreviousMigration = "20261010011751_AddBloodMoneyChallenges";

    [Fact]
    public async Task Upgrade_and_down_up_preserve_canonical_challenge_roster_and_financial_evidence()
    {
        await using var container = Container(); await container.StartAsync();
        await using var db = Context(container);
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration); await Seed(db);
        var before = await Snapshot(db); var tables = await Tables(db); db.ChangeTracker.Clear();
        await db.Database.MigrateAsync();
        Assert.Equal(before, await Snapshot(db));
        Assert.Equal(new[] { "blood_money_chat_messages", "blood_money_chat_participant_state" }, (await Tables(db)).Except(tables).Order());
        Assert.Empty(await db.Set<BloodMoneyChatMessageRow>().ToListAsync());
        Assert.Empty(await db.Set<BloodMoneyChatParticipantStateRow>().ToListAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        Assert.Equal(tables.Order(), (await Tables(db)).Order()); Assert.Equal(before, await Snapshot(db));
        await db.Database.MigrateAsync(); Assert.Equal(before, await Snapshot(db));
    }

    [Fact]
    public async Task Failed_second_table_rolls_back_first_table_and_migration_history_then_retries_cleanly()
    {
        await using var container = Container(); await container.StartAsync();
        await using var db = Context(container);
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration); await Seed(db);
        var before = await Snapshot(db); var history = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE blood_money_chat_participant_state (sentinel integer NOT NULL)");
        Assert.Equal(PostgresErrorCodes.DuplicateTable, (await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync())).SqlState);
        await using (var fresh = Context(container))
        {
            Assert.Equal(before, await Snapshot(fresh)); Assert.Equal(history, await fresh.Database.GetAppliedMigrationsAsync());
            Assert.DoesNotContain("blood_money_chat_messages", await Tables(fresh));
            Assert.Single(await fresh.Database.GetPendingMigrationsAsync());
        }
        // Only this test's sentinel in its dedicated disposable database is removed.
        await db.Database.ExecuteSqlRawAsync("DROP TABLE blood_money_chat_participant_state");
        await db.Database.MigrateAsync(); Assert.Equal(before, await Snapshot(db));
        Assert.Empty(await db.Set<BloodMoneyChatMessageRow>().ToListAsync());
        Assert.Empty(await db.Set<BloodMoneyChatParticipantStateRow>().ToListAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }

    private static async Task Seed(Level5V2DbContext db)
    {
        var now = BloodMoneyChatTests.Start; var ct = CancellationToken.None;
        var creator = await PlayerSeeding.CreatePlayerAsync(db, "ChatUpgrade", now);
        var friend = await PlayerSeeding.CreatePlayerAsync(db, "ChatFriend", now);
        var id = new BloodMoneyChallengeId(Guid.NewGuid());
        var ledger = new BloodCreditLedgerStore(db); var uow = new EfUnitOfWork(db); var clock = new BloodMoneyChatTests.Clock(now);
        await new IssueBloodCreditsUseCase(ledger, uow, clock).ExecuteAsync(new(BloodCreditTransactionId.New(), creator, 100, "before-chat"), ct);
        await new ReserveBloodCreditsUseCase(new(ledger, new BloodCreditReservationStore(db), clock), uow)
            .ExecuteAsync(new(BloodCreditTransactionId.New(), id, creator, 10), ct);
        new BloodMoneyChallengeStore(db).Add(BloodMoneyChallenge.Create(id, creator, Guid.NewGuid(), [friend], 10, "classic", 1, now, now.AddHours(1)));
        await db.SaveChangesAsync();
    }

    private static async Task<string[]> Snapshot(Level5V2DbContext db)
    {
        var results = new List<string>();
        foreach (var table in new[] { "accounts", "player_profiles", "blood_money_challenges", "blood_money_challenge_participants",
            "blood_money_credit_accounts", "blood_money_credit_transactions", "blood_money_credit_postings", "blood_money_credit_reservations" })
        {
            var sql = $"SELECT coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]'::jsonb)::text AS \"Value\" FROM {table} t";
            results.Add(await db.Database.SqlQueryRaw<string>(sql).SingleAsync());
        }
        return results.ToArray();
    }
    private static Task<List<string>> Tables(Level5V2DbContext db) => db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'").ToListAsync();
    private static PostgreSqlContainer Container() => new PostgreSqlBuilder("postgres:18-alpine").WithDatabase("chat_upgrade").WithUsername("level5").WithPassword("test-password").Build();
    private static Level5V2DbContext Context(PostgreSqlContainer container) => new(new DbContextOptionsBuilder<Level5V2DbContext>().UseNpgsql(container.GetConnectionString()).Options);
}
