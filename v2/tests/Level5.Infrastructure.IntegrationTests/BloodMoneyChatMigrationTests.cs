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

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Report_upgrade_failure_retry_and_down_up_preserve_existing_messages_private_state_and_finances(bool failFirst)
    {
        const string chatMigration = "20261010174730_AddBloodMoneyChat";
        await using var container = Container(); await container.StartAsync(); await using var db = Context(container);
        await db.GetService<IMigrator>().MigrateAsync(chatMigration); await Seed(db);
        var now = BloodMoneyChatTests.Start; var ct = CancellationToken.None;
        var row = await db.Set<BloodMoneyChallengeRow>().Include(r => r.Participants).SingleAsync();
        var creator = new Level5.Domain.Ids.PlayerId(row.CreatorPlayerId);
        var friend = new Level5.Domain.Ids.PlayerId(row.Participants.Single(p => p.PlayerId != creator.Value).PlayerId);
        var id = new BloodMoneyChallengeId(row.Id); var clock = new BloodMoneyChatTests.Clock(now);
        var ledger = new BloodCreditLedgerStore(db); var reservations = new BloodCreditReservationStore(db); var uow = new EfUnitOfWork(db);
        await new IssueBloodCreditsUseCase(ledger, uow, clock).ExecuteAsync(new(BloodCreditTransactionId.New(), friend, 100, "report-upgrade"), ct);
        await new AcceptBloodMoneyChallengeUseCase(new(new BloodMoneyChallengeStore(db), reservations,
            new(ledger, reservations, clock), uow, clock, new(TimeSpan.FromHours(1), TimeSpan.FromHours(2))))
            .ExecuteAsync(id, friend, ct);
        var store = BloodMoneyChatTests.Store(db, clock);
        var message = await store.SendAsync(new(id, creator, Guid.NewGuid(), "retained evidence"), BloodMoneyChatTests.Rate, ct);
        await store.AdvanceReadAsync(id, friend, 1, ct); await store.SetNotificationsMutedAsync(id, friend, true, ct);
        var authority = await Snapshot(db);
        var chat = await ChatSnapshot(db);
        if (failFirst)
        {
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE blood_money_chat_reports (sentinel integer NOT NULL)");
            Assert.Equal(PostgresErrorCodes.DuplicateTable, (await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync())).SqlState);
            Assert.Equal(chat, await ChatSnapshot(db)); Assert.Equal(authority, await Snapshot(db));
            Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'AK_blood_money_chat_messages_ChallengeId_Id') AS \"Value\"").SingleAsync());
            Assert.Single(await db.Database.GetPendingMigrationsAsync());
            // Disposable test database sentinel only.
            await db.Database.ExecuteSqlRawAsync("DROP TABLE blood_money_chat_reports");
        }
        await db.Database.MigrateAsync(); Assert.Equal(chat, await ChatSnapshot(db)); Assert.Equal(authority, await Snapshot(db));
        var accepted = await store.ReportAsync(new(id, friend, message.Message.MessageId, BloodMoneyChatReportReason.Threat), ct);
        await using (var fresh = Context(container))
            Assert.Equal(accepted.ReportId, (await fresh.Set<BloodMoneyChatReportRow>().SingleAsync()).ReportId);
        await db.GetService<IMigrator>().MigrateAsync(chatMigration);
        Assert.Equal(chat, await ChatSnapshot(db)); Assert.Equal(authority, await Snapshot(db));
        await db.Database.MigrateAsync(); Assert.Equal(chat, await ChatSnapshot(db)); Assert.Equal(authority, await Snapshot(db));
        Assert.Empty(await db.Set<BloodMoneyChatReportRow>().ToListAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }

    private static async Task<string[]> ChatSnapshot(Level5V2DbContext db)
    {
        var results = new List<string>();
        foreach (var table in new[] { "blood_money_chat_messages", "blood_money_chat_participant_state" })
        {
            var sql = $"SELECT coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]'::jsonb)::text AS \"Value\" FROM {table} t";
            results.Add(await db.Database.SqlQueryRaw<string>(sql).SingleAsync());
        }
        return results.ToArray();
    }

    [Fact]
    public async Task Upgrade_and_down_up_preserve_canonical_challenge_roster_and_financial_evidence()
    {
        await using var container = Container(); await container.StartAsync();
        await using var db = Context(container);
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration); await Seed(db);
        var before = await Snapshot(db); var tables = await Tables(db); db.ChangeTracker.Clear();
        await db.Database.MigrateAsync();
        Assert.Equal(before, await Snapshot(db));
        Assert.Equal(new[] { "blood_money_chat_messages", "blood_money_chat_participant_state", "blood_money_chat_reports" }, (await Tables(db)).Except(tables).Order());
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
            Assert.Equal(2, (await fresh.Database.GetPendingMigrationsAsync()).Count());
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
