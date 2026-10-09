using Level5.Application.Abstractions;
using Level5.Application.BloodMoney;
using Level5.Domain.BloodMoney;
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

public sealed class BloodCreditReservationMigrationTests
{
    private const string PreviousMigration = "20261009200814_AddBloodCreditLedger";
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Upgrade_preserves_shared_Level5_and_existing_ledger_data_with_empty_reservations_and_no_drift()
    {
        await using var container = Container();
        await container.StartAsync();
        await using var db = Context(container);
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        var beforeTables = await Tables(db);
        var player = await PlayerSeeding.CreatePlayerAsync(db, "UpgradeReservation", DateTimeOffset.UtcNow);
        await new ProductEntitlementStore(db).AddAsync(ProductEntitlement.Grant(player, ProductId.Create("level5"), EntitlementKind.Owned, DateTimeOffset.UtcNow), Ct);
        var resultId = Guid.NewGuid();
        db.MatchResults.Add(new MatchResultRow { Id = resultId, PlayerId = player.Value, ClientResultId = Guid.NewGuid(), ModeId = 1, LevelId = 2,
            CharacterId = "hero", ClientVersion = "reservation-upgrade", Platform = "windows", MetricsJson = "{\"TotalPoints\":90}", ModifiersJson = "{}", CreatedAt = DateTimeOffset.UtcNow });
        var ledger = new BloodCreditLedgerStore(db);
        var clock = new Clock();
        var issueId = BloodCreditTransactionId.New();
        await new IssueBloodCreditsUseCase(ledger, new EfUnitOfWork(db), clock).ExecuteAsync(new(issueId, player, 100, "grant-before-upgrade"), Ct);
        await new SpendBloodCreditsUseCase(ledger, new EfUnitOfWork(db), clock).ExecuteAsync(new(BloodCreditTransactionId.New(), player, 20, "spend-before-upgrade"), Ct);
        await new CorrectBloodCreditsUseCase(ledger, new EfUnitOfWork(db), clock).ExecuteAsync(new(BloodCreditTransactionId.New(), player, 5, "fix-before-upgrade"), Ct);
        var transactions = await db.Set<BloodCreditTransactionRow>().AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.Kind, row.SubjectPlayerId, row.PlayerDelta, row.ReferenceCode, row.CreatedAt }).ToListAsync();
        var postings = await db.Set<BloodCreditPostingRow>().AsNoTracking().OrderBy(row => row.TransactionId).ThenBy(row => row.LineNumber).Select(row => new { row.TransactionId, row.LineNumber, row.PostingOwner, row.PlayerId, row.Amount }).ToListAsync();
        db.ChangeTracker.Clear();
        await db.Database.MigrateAsync();
        Assert.Equal(new[] { "blood_money_credit_reservations" }, (await Tables(db)).Except(beforeTables).ToArray());
        Assert.Empty(await db.Set<BloodCreditReservationRow>().ToListAsync());
        Assert.Equal(transactions, await db.Set<BloodCreditTransactionRow>().AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.Kind, row.SubjectPlayerId, row.PlayerDelta, row.ReferenceCode, row.CreatedAt }).ToListAsync());
        Assert.Equal(postings, await db.Set<BloodCreditPostingRow>().AsNoTracking().OrderBy(row => row.TransactionId).ThenBy(row => row.LineNumber).Select(row => new { row.TransactionId, row.LineNumber, row.PostingOwner, row.PlayerId, row.Amount }).ToListAsync());
        var account = (await ledger.FindAccountAsync(player, Ct))!;
        Assert.Equal(85, account.AvailableBalance); Assert.Equal(3, account.Revision);
        var profile = await db.PlayerProfiles.AsNoTracking().SingleAsync(row => row.Id == player.Value);
        Assert.Equal("UpgradeReservation", profile.DisplayName);
        Assert.True(await db.Accounts.AnyAsync(row => row.Id == profile.AccountId));
        Assert.Equal("Owned", (await db.ProductEntitlements.SingleAsync()).Kind);
        var result = await db.MatchResults.SingleAsync(row => row.Id == resultId);
        Assert.Equal("reservation-upgrade", result.ClientVersion); Assert.Equal(1, result.ModeId); Assert.Equal(2, result.LevelId); Assert.Equal("{\"TotalPoints\": 90}", result.MetricsJson);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        var constraints = await db.Database.SqlQueryRaw<ConstraintInfo>("""
            SELECT conname AS "Name", contype::text AS "Type", confdeltype::text AS "DeleteAction"
            FROM pg_constraint WHERE conrelid = 'blood_money_credit_reservations'::regclass
            """).ToListAsync();
        Assert.Single(constraints, row => row.Type == "p");
        Assert.Equal(3, constraints.Count(row => row.Type == "f"));
        Assert.All(constraints.Where(row => row.Type == "f"), row => Assert.Equal("r", row.DeleteAction));
        foreach (var name in new[] { "CK_blood_reservation_identity", "CK_blood_reservation_amount", "CK_blood_reservation_revision", "CK_blood_reservation_state" })
            Assert.Contains(constraints, row => row.Name == name && row.Type == "c");
        var request = new ReserveBloodCreditsRequest(BloodCreditTransactionId.New(), new(Guid.NewGuid()), player, 80);
        var mutator = new BloodCreditReservationMutator(ledger, new BloodCreditReservationStore(db), clock);
        await new ReserveBloodCreditsUseCase(mutator, new EfUnitOfWork(db)).ExecuteAsync(request, Ct);
        await new ReleaseBloodCreditsUseCase(mutator, new EfUnitOfWork(db)).ExecuteAsync(new(BloodCreditTransactionId.New(), request.ChallengeId, player, BloodCreditReleaseReason.PendingExpired), Ct);
        Assert.Equal(85, (await ledger.FindAccountAsync(player, Ct))!.AvailableBalance);
    }

    [Fact]
    public async Task Failed_upgrade_rolls_back_kind_constraint_and_history_and_retries_cleanly()
    {
        await using var container = Container();
        await container.StartAsync();
        await using var db = Context(container);
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        var history = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        var player = await PlayerSeeding.CreatePlayerAsync(db, "FailedUpgrade", DateTimeOffset.UtcNow);
        await new IssueBloodCreditsUseCase(new BloodCreditLedgerStore(db), new EfUnitOfWork(db), new Clock()).ExecuteAsync(new(BloodCreditTransactionId.New(), player, 100, "grant"), Ct);
        var originalConstraint = await KindConstraint(db);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE blood_money_credit_reservations (sentinel integer NOT NULL)");
        Assert.Equal(PostgresErrorCodes.DuplicateTable, (await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync())).SqlState);
        Assert.Equal(history, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(originalConstraint, await KindConstraint(db));
        Assert.Single(await db.Database.GetPendingMigrationsAsync());
        await using (var fresh = Context(container))
        {
            Assert.Equal(100, (await new BloodCreditLedgerStore(fresh).FindAccountAsync(player, Ct))!.AvailableBalance);
            Assert.Single(await fresh.Set<BloodCreditTransactionRow>().ToListAsync());
            Assert.Equal(2, await fresh.Set<BloodCreditPostingRow>().CountAsync());
            new BloodCreditLedgerStore(fresh).AddTransaction(BloodCreditTransaction.Reserve(BloodCreditTransactionId.New(), player, 1, "old-constraint", DateTimeOffset.UtcNow));
            Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(() => fresh.SaveChangesAsync())).InnerException).SqlState);
        }
        // Only remove this test's sentinel from its dedicated database.
        await db.Database.ExecuteSqlRawAsync("DROP TABLE blood_money_credit_reservations");
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Set<BloodCreditReservationRow>().ToListAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.NotEqual(originalConstraint, await KindConstraint(db));
        Assert.Equal(100, (await new BloodCreditLedgerStore(db).FindAccountAsync(player, Ct))!.AvailableBalance);
    }

    private static PostgreSqlContainer Container() => new PostgreSqlBuilder("postgres:18-alpine").WithDatabase("reservation_upgrade").WithUsername("level5").WithPassword("test-password").Build();
    private static Level5V2DbContext Context(PostgreSqlContainer container) => new(new DbContextOptionsBuilder<Level5V2DbContext>().UseNpgsql(container.GetConnectionString()).Options);
    private static Task<List<string>> Tables(Level5V2DbContext db) => db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'").ToListAsync();
    private static Task<string> KindConstraint(Level5V2DbContext db) => db.Database.SqlQueryRaw<string>("SELECT pg_get_constraintdef(oid) AS \"Value\" FROM pg_constraint WHERE conname = 'CK_blood_credit_kind'").SingleAsync();
    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
    private sealed class ConstraintInfo
    {
        public string Name { get; set; } = null!;
        public string Type { get; set; } = null!;
        public string DeleteAction { get; set; } = null!;
    }
}
