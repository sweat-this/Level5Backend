using Level5.Application.Abstractions;
using Level5.Application.BloodMoney;
using Level5.Domain.BloodMoney;
using Level5.Domain.Platform;
using Level5.Domain.Social;
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

public sealed class BloodMoneyChallengeMigrationTests
{
    private const string PreviousMigration = "20261009205516_AddBloodCreditReservations";
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string[] OldTables = ["accounts", "player_profiles", "friendships", "product_entitlements", "match_results",
        "blood_money_credit_accounts", "blood_money_credit_transactions", "blood_money_credit_postings", "blood_money_credit_reservations"];

    [Fact]
    public async Task Upgrade_from_reservations_preserves_every_seeded_row_and_adds_empty_restrictive_row_based_challenge_tables()
    {
        await using var container = Container(); await container.StartAsync();
        await using var db = Context(container);
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        await Seed(db);
        var before = await Snapshot(db); var tables = await Tables(db);
        db.ChangeTracker.Clear(); await db.Database.MigrateAsync();
        Assert.Equal(before, await Snapshot(db));
        Assert.Equal(new[] { "blood_money_challenge_participants", "blood_money_challenges", "blood_money_chat_messages", "blood_money_chat_participant_state", "blood_money_chat_reports" }, (await Tables(db)).Except(tables).Order().ToArray());
        Assert.Empty(await db.Set<BloodMoneyChallengeRow>().ToListAsync());
        Assert.Empty(await db.Set<BloodMoneyChallengeParticipantRow>().ToListAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
        var constraints = await db.Database.SqlQueryRaw<ConstraintInfo>("""
            SELECT conname AS "Name", contype::text AS "Type", confdeltype::text AS "DeleteAction", pg_get_constraintdef(oid) AS "Definition"
            FROM pg_constraint WHERE conrelid IN ('blood_money_challenges'::regclass, 'blood_money_challenge_participants'::regclass)
            """).ToListAsync();
        Assert.Equal(2, constraints.Count(c => c.Type == "p"));
        Assert.Equal(4, constraints.Count(c => c.Type == "f"));
        Assert.All(constraints.Where(c => c.Type == "f"), c => Assert.Equal("r", c.DeleteAction));
        Assert.DoesNotContain(constraints, c => c.Definition.Contains("competitive_series") || c.Definition.Contains("match_results"));
        var seat = Assert.Single(constraints, c => c.Name == "CK_blood_participant_seat");
        Assert.Contains(">= 0", seat.Definition); Assert.DoesNotContain("<=", seat.Definition);
        var indexes = await db.Database.SqlQueryRaw<string>("""
            SELECT indexdef AS "Value" FROM pg_indexes WHERE tablename IN ('blood_money_challenges', 'blood_money_challenge_participants')
            """).ToListAsync();
        Assert.Contains(indexes, i => i.Contains("UNIQUE") && i.Contains("\"CreatorPlayerId\", \"ClientRequestId\""));
        Assert.Contains(indexes, i => i.Contains("UNIQUE") && i.Contains("\"ChallengeId\", \"SeatIndex\""));
    }

    [Fact]
    public async Task Migration_failure_rolls_back_first_table_and_history_preserves_old_evidence_and_retries_cleanly()
    {
        await using var container = Container(); await container.StartAsync();
        await using var db = Context(container);
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration); await Seed(db);
        var history = (await db.Database.GetAppliedMigrationsAsync()).ToArray(); var before = await Snapshot(db);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE blood_money_challenge_participants (sentinel integer NOT NULL)");
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
        Assert.Equal(PostgresErrorCodes.DuplicateTable, error.SqlState);
        await using (var fresh = Context(container))
        {
            Assert.Equal(history, await fresh.Database.GetAppliedMigrationsAsync());
            Assert.DoesNotContain("blood_money_challenges", await Tables(fresh));
            Assert.Equal(before, await Snapshot(fresh));
            Assert.Equal(fresh.Database.GetMigrations().SkipWhile(id => id != PreviousMigration).Skip(1), await fresh.Database.GetPendingMigrationsAsync());
        }
        // Remove only this test's sentinel from its dedicated ephemeral database.
        await db.Database.ExecuteSqlRawAsync("DROP TABLE blood_money_challenge_participants");
        await db.Database.MigrateAsync();
        Assert.Equal(before, await Snapshot(db));
        Assert.Empty(await db.Set<BloodMoneyChallengeRow>().ToListAsync());
        Assert.Empty(await db.Set<BloodMoneyChallengeParticipantRow>().ToListAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }

    private static async Task Seed(Level5V2DbContext db)
    {
        var clock = new Clock();
        var creator = await PlayerSeeding.CreatePlayerAsync(db, "UpgradeCreator", clock.UtcNow);
        var friend = await PlayerSeeding.CreatePlayerAsync(db, "UpgradeFriend", clock.UtcNow);
        await new FriendshipStore(db).AddFriendshipAsync(Friendship.Between(creator, friend, clock.UtcNow), Ct);
        await new ProductEntitlementStore(db).AddAsync(ProductEntitlement.Grant(creator, ProductId.Create("level5"), EntitlementKind.Owned, clock.UtcNow), Ct);
        db.MatchResults.Add(new MatchResultRow { Id = Guid.NewGuid(), PlayerId = creator.Value, ClientResultId = Guid.NewGuid(),
            ModeId = 1, LevelId = 2, CharacterId = "hero", ClientVersion = "challenge-upgrade", Platform = "windows",
            MetricsJson = "{\"TotalPoints\":90}", ModifiersJson = "{}", CreatedAt = clock.UtcNow });
        var ledger = new BloodCreditLedgerStore(db); var uow = new EfUnitOfWork(db);
        await new IssueBloodCreditsUseCase(ledger, uow, clock).ExecuteAsync(new(BloodCreditTransactionId.New(), creator, 100, "before-upgrade"), Ct);
        await new SpendBloodCreditsUseCase(ledger, uow, clock).ExecuteAsync(new(BloodCreditTransactionId.New(), creator, 5, "before-upgrade-spend"), Ct);
        var financial = new BloodCreditReservationMutator(ledger, new BloodCreditReservationStore(db), clock);
        var first = new ReserveBloodCreditsRequest(BloodCreditTransactionId.New(), new(Guid.NewGuid()), creator, 30);
        await new ReserveBloodCreditsUseCase(financial, uow).ExecuteAsync(first, Ct);
        await new ReleaseBloodCreditsUseCase(financial, uow).ExecuteAsync(new(BloodCreditTransactionId.New(), first.ChallengeId, creator, BloodCreditReleaseReason.Cancelled), Ct);
        await new ReserveBloodCreditsUseCase(financial, uow).ExecuteAsync(first with { TransactionId = BloodCreditTransactionId.New(), ChallengeId = new(Guid.NewGuid()) }, Ct);
    }

    private static async Task<string[]> Snapshot(Level5V2DbContext db)
    {
        var rows = new List<string>();
        foreach (var table in OldTables)
        {
            // Identifiers come exclusively from the fixed OldTables list, never caller input.
            var sql = $"SELECT coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]'::jsonb)::text AS \"Value\" FROM {table} t";
            rows.Add(await db.Database.SqlQueryRaw<string>(sql).SingleAsync());
        }
        return rows.ToArray();
    }
    private static Task<List<string>> Tables(Level5V2DbContext db) => db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'").ToListAsync();
    private static PostgreSqlContainer Container() => new PostgreSqlBuilder("postgres:18-alpine").WithDatabase("challenge_upgrade").WithUsername("level5").WithPassword("test-password").Build();
    private static Level5V2DbContext Context(PostgreSqlContainer container) => new(new DbContextOptionsBuilder<Level5V2DbContext>().UseNpgsql(container.GetConnectionString()).Options);
    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
    private sealed class ConstraintInfo
    {
        public string Name { get; set; } = null!;
        public string Type { get; set; } = null!;
        public string DeleteAction { get; set; } = null!;
        public string Definition { get; set; } = null!;
    }
}
