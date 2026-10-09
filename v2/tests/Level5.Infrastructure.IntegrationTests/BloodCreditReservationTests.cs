using System.Data.Common;
using Level5.Application.Abstractions;
using Level5.Application.BloodMoney;
using Level5.Application.Common;
using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;
using Level5.Infrastructure.BloodMoney;
using Level5.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class BloodCreditReservationTests(PostgresFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly Clock TestClock = new();
    private static BloodCreditReservationMutator Mutator(Level5V2DbContext db) => new(new BloodCreditLedgerStore(db), new BloodCreditReservationStore(db), TestClock);
    private static ReserveBloodCreditsRequest Request(PlayerId player, long amount = 80) => new(BloodCreditTransactionId.New(), new(Guid.NewGuid()), player, amount);
    private static ReleaseBloodCreditsRequest ReleaseRequest(ReserveBloodCreditsRequest reserve) => new(BloodCreditTransactionId.New(), reserve.ChallengeId, reserve.PlayerId, BloodCreditReleaseReason.Cancelled);
    private static Task<BloodCreditMutationResult> Reserve(Level5V2DbContext db, ReserveBloodCreditsRequest request, IUnitOfWork? uow = null)
        => new ReserveBloodCreditsUseCase(Mutator(db), uow ?? new EfUnitOfWork(db)).ExecuteAsync(request, Ct);
    private static Task<BloodCreditMutationResult> Release(Level5V2DbContext db, ReleaseBloodCreditsRequest request, IUnitOfWork? uow = null)
        => new ReleaseBloodCreditsUseCase(Mutator(db), uow ?? new EfUnitOfWork(db)).ExecuteAsync(request, Ct);

    [Fact]
    public async Task Durable_lifecycle_reconciles_and_replays_without_reopening_or_extra_postings()
    {
        var player = await FundedPlayer();
        var reserve = Request(player);
        await using (var db = fixture.CreateDbContext()) Assert.False((await Reserve(db, reserve)).IsReplay);
        await Reconcile(player, 20, 1);
        var release = ReleaseRequest(reserve);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.True((await Reserve(db, reserve)).IsReplay);
            Assert.False((await Release(db, release)).IsReplay);
        }
        await using (var db = fixture.CreateDbContext())
        {
            Assert.True((await Release(db, release)).IsReplay);
            Assert.True((await Reserve(db, reserve)).IsReplay);
            await Assert.ThrowsAsync<ConflictException>(() => Reserve(db, reserve with { TransactionId = BloodCreditTransactionId.New() }));
            await Assert.ThrowsAsync<ConflictException>(() => Release(db, release with { TransactionId = BloodCreditTransactionId.New() }));
            await Assert.ThrowsAsync<ConflictException>(() => Release(db, release with { Reason = BloodCreditReleaseReason.Declined }));
            Assert.Empty(db.ChangeTracker.Entries());
        }
        await Reconcile(player, 100, 1);
    }

    [Fact]
    public async Task Multiple_staged_mutations_share_one_commit_and_respect_the_uncommitted_projection()
    {
        var player = await FundedPlayer();
        var first = Request(player, 40);
        var second = Request(player, 50);
        await using var db = fixture.CreateDbContext();
        var mutator = Mutator(db);
        await mutator.StageReserveAsync(first, Ct);
        Assert.True((await mutator.StageReserveAsync(first, Ct)).IsReplay);
        await mutator.StageReserveAsync(second, Ct);
        await Assert.ThrowsAsync<InsufficientCreditsException>(() => mutator.StageReserveAsync(Request(player, 11), Ct));
        // A fresh context cannot observe any of the staged state.
        await Reconcile(player, 100, 0);
        await mutator.StageReleaseAsync(ReleaseRequest(first), Ct);
        await new EfUnitOfWork(db).SaveChangesAsync(Ct);
        await Reconcile(player, 50, 2);
        await using var check = fixture.CreateDbContext();
        Assert.Equal(4, (await new BloodCreditLedgerStore(check).FindAccountAsync(player, Ct))!.Revision);
    }

    [Theory]
    [InlineData("overspend")]
    [InlineData("same-id")]
    [InlineData("different-id")]
    [InlineData("spend")]
    [InlineData("negative-correction")]
    [InlineData("positive-correction")]
    public async Task Account_revision_serializes_reservations_with_all_balance_mutations(string race)
    {
        var player = await FundedPlayer();
        var first = Request(player);
        var second = race == "overspend" ? Request(player) : race == "same-id" ? first : first with { TransactionId = BloodCreditTransactionId.New() };
        var barrier = new SaveBarrier();
        async Task<Exception?> Run(bool competing)
        {
            await using var db = fixture.CreateDbContext();
            var uow = new BarrierUnitOfWork(db, barrier);
            return await Record.ExceptionAsync(async () =>
            {
                if (!competing) await Reserve(db, first, uow);
                else if (race == "spend") await new SpendBloodCreditsUseCase(new BloodCreditLedgerStore(db), uow, TestClock).ExecuteAsync(new(second.TransactionId, player, 80, "race-spend"), Ct);
                else if (race.EndsWith("correction", StringComparison.Ordinal)) await new CorrectBloodCreditsUseCase(new BloodCreditLedgerStore(db), uow, TestClock).ExecuteAsync(new(second.TransactionId, player, race == "positive-correction" ? 10 : -80, "race-correction"), Ct);
                else await Reserve(db, second, uow);
            });
        }
        var outcomes = await Task.WhenAll(Run(false), Run(true));
        Assert.Single(outcomes, outcome => outcome is null);
        Assert.IsType<ConflictException>(Assert.Single(outcomes, outcome => outcome is not null));
        await using var retry = fixture.CreateDbContext();
        if (race == "same-id") Assert.True((await Reserve(retry, first)).IsReplay);
        else if (race == "different-id") await Assert.ThrowsAsync<ConflictException>(() => Reserve(retry, outcomes[0] is null ? second : first));
        else if (race == "overspend") await Assert.ThrowsAsync<InsufficientCreditsException>(() => Reserve(retry, outcomes[0] is null ? second : first));
        else if (race == "positive-correction")
        {
            if (outcomes[0] is null) await new CorrectBloodCreditsUseCase(new BloodCreditLedgerStore(retry), new EfUnitOfWork(retry), TestClock).ExecuteAsync(new(second.TransactionId, player, 10, "race-correction"), Ct);
            else await Reserve(retry, first);
        }
        else if (outcomes[0] is null)
        {
            if (race == "spend") await Assert.ThrowsAsync<InsufficientCreditsException>(() => new SpendBloodCreditsUseCase(new BloodCreditLedgerStore(retry), new EfUnitOfWork(retry), TestClock).ExecuteAsync(new(second.TransactionId, player, 80, "race-spend"), Ct));
            else await Assert.ThrowsAsync<InsufficientCreditsException>(() => new CorrectBloodCreditsUseCase(new BloodCreditLedgerStore(retry), new EfUnitOfWork(retry), TestClock).ExecuteAsync(new(second.TransactionId, player, -80, "race-correction"), Ct));
        }
        else await Assert.ThrowsAsync<InsufficientCreditsException>(() => Reserve(retry, first));
        await Reconcile(player, race == "positive-correction" ? 30 : 20,
            race is "spend" or "negative-correction" && outcomes[0] is not null ? 0 : 1);
    }

    [Theory]
    [InlineData("same-id")]
    [InlineData("different-id")]
    [InlineData("changed-reason")]
    public async Task Concurrent_release_returns_the_stake_once_and_resolves_exact_retry(string race)
    {
        var player = await FundedPlayer();
        var reserve = Request(player);
        await using (var db = fixture.CreateDbContext()) await Reserve(db, reserve);
        var first = ReleaseRequest(reserve);
        var second = race == "same-id" ? first : race == "different-id" ? first with { TransactionId = BloodCreditTransactionId.New() } : first with { Reason = BloodCreditReleaseReason.PendingExpired };
        var barrier = new SaveBarrier();
        async Task<Exception?> Run(ReleaseBloodCreditsRequest request)
        {
            await using var db = fixture.CreateDbContext();
            return await Record.ExceptionAsync(() => Release(db, request, new BarrierUnitOfWork(db, barrier)));
        }
        var outcomes = await Task.WhenAll(Run(first), Run(second));
        Assert.Single(outcomes, outcome => outcome is null);
        Assert.IsType<ConflictException>(Assert.Single(outcomes, outcome => outcome is not null));
        await using var retry = fixture.CreateDbContext();
        Assert.True((await Release(retry, outcomes[0] is null ? first : second)).IsReplay);
        if (race != "same-id") await Assert.ThrowsAsync<ConflictException>(() => Release(retry, outcomes[0] is null ? second : first));
        await Reconcile(player, 100, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Operation_ids_are_globally_unique_across_players_and_reservation_lifecycles(bool releasing)
    {
        var firstPlayer = await FundedPlayer();
        var secondPlayer = await FundedPlayer();
        var first = Request(firstPlayer);
        var second = Request(secondPlayer) with { ChallengeId = first.ChallengeId };
        var operationId = BloodCreditTransactionId.New();
        if (releasing)
        {
            await using var firstDb = fixture.CreateDbContext();
            await using var secondDb = fixture.CreateDbContext();
            await Reserve(firstDb, first);
            await Reserve(secondDb, second);
        }
        else
        {
            first = first with { TransactionId = operationId };
            second = second with { TransactionId = operationId };
        }
        var firstRelease = ReleaseRequest(first) with { TransactionId = operationId };
        var secondRelease = ReleaseRequest(second) with { TransactionId = operationId };
        var barrier = new SaveBarrier();
        async Task<Exception?> Run(ReserveBloodCreditsRequest reserve, ReleaseBloodCreditsRequest release)
        {
            await using var db = fixture.CreateDbContext();
            var uow = new BarrierUnitOfWork(db, barrier);
            return await Record.ExceptionAsync(async () =>
            {
                if (releasing) await Release(db, release, uow);
                else await Reserve(db, reserve, uow);
            });
        }
        var outcomes = await Task.WhenAll(Run(first, firstRelease), Run(second, secondRelease));
        Assert.Single(outcomes, outcome => outcome is null);
        Assert.IsType<ConflictException>(Assert.Single(outcomes, outcome => outcome is not null));
        var firstWon = outcomes[0] is null;
        await using var retry = fixture.CreateDbContext();
        if (releasing)
            await Assert.ThrowsAsync<ConflictException>(() => Release(retry, firstWon ? secondRelease : firstRelease));
        else
            await Assert.ThrowsAsync<ConflictException>(() => Reserve(retry, firstWon ? second : first));
        await Reconcile(firstWon ? firstPlayer : secondPlayer, releasing ? 100 : 20, 1);
        await Reconcile(firstWon ? secondPlayer : firstPlayer, releasing ? 20 : 100, releasing ? 1 : 0);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Commit_between_account_and_reservation_reads_resolves_replay_or_conflict(bool releasing, bool sameIntent)
    {
        var player = await FundedPlayer();
        var reserve = Request(player, 100);
        var release = ReleaseRequest(reserve);
        if (releasing) { await using var db = fixture.CreateDbContext(); await Reserve(db, reserve); }
        await using var delayed = fixture.CreateDbContext();
        var paused = new PausedLedger(new BloodCreditLedgerStore(delayed));
        var mutator = new BloodCreditReservationMutator(paused, new BloodCreditReservationStore(delayed), TestClock);
        var call = releasing
            ? mutator.StageReleaseAsync(sameIntent ? release : release with { Reason = BloodCreditReleaseReason.Declined }, Ct)
            : mutator.StageReserveAsync(sameIntent ? reserve : reserve with { ChallengeId = new(Guid.NewGuid()) }, Ct);
        try
        {
            await paused.Read.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await using var winner = fixture.CreateDbContext();
            if (releasing) await Release(winner, release); else await Reserve(winner, reserve);
        }
        finally { paused.Resume.TrySetResult(); }
        if (sameIntent) Assert.True((await call).IsReplay);
        else await Assert.ThrowsAsync<ConflictException>(() => call);
        Assert.Empty(delayed.ChangeTracker.Entries());
        await Reconcile(player, releasing ? 100 : 0, 1);
    }

    [Theory]
    [InlineData(false, "INSERT INTO blood_money_credit_postings")]
    [InlineData(false, "INSERT INTO blood_money_credit_reservations")]
    [InlineData(false, "UPDATE blood_money_credit_accounts")]
    [InlineData(true, "INSERT INTO blood_money_credit_postings")]
    [InlineData(true, "UPDATE blood_money_credit_reservations")]
    [InlineData(true, "UPDATE blood_money_credit_accounts")]
    public async Task Failure_after_executed_piece_rolls_back_every_financial_piece_and_fresh_retry_succeeds(bool releasing, string command)
    {
        var player = await FundedPlayer();
        var reserve = Request(player);
        var release = ReleaseRequest(reserve);
        if (releasing) { await using var db = fixture.CreateDbContext(); await Reserve(db, reserve); }
        var fault = new FailAfterCommand(command);
        await using (var db = fixture.CreateDbContext(fault))
            await Assert.ThrowsAsync<DbUpdateException>(async () => { if (releasing) await Release(db, release); else await Reserve(db, reserve); });
        Assert.True(fault.Fired);
        await Reconcile(player, releasing ? 20 : 100, releasing ? 1 : 0);
        await using (var check = fixture.CreateDbContext())
        {
            var operationId = releasing ? release.TransactionId.Value : reserve.TransactionId.Value;
            Assert.False(await check.Set<BloodCreditTransactionRow>().AnyAsync(row => row.Id == operationId));
            Assert.False(await check.Set<BloodCreditPostingRow>().AnyAsync(row => row.TransactionId == operationId));
            Assert.Equal(releasing ? 2 : 1, (await new BloodCreditLedgerStore(check).FindAccountAsync(player, Ct))!.Revision);
        }
        await using (var retry = fixture.CreateDbContext())
        {
            Assert.False((releasing ? await Release(retry, release) : await Reserve(retry, reserve)).IsReplay);
        }
        await Reconcile(player, releasing ? 100 : 20, 1);
    }

    [Theory]
    [InlineData("zero-amount", PostgresErrorCodes.CheckViolation)]
    [InlineData("negative-amount", PostgresErrorCodes.CheckViolation)]
    [InlineData("zero-revision", PostgresErrorCodes.CheckViolation)]
    [InlineData("empty-challenge", PostgresErrorCodes.CheckViolation)]
    [InlineData("empty-player", PostgresErrorCodes.CheckViolation)]
    [InlineData("unknown-player", PostgresErrorCodes.ForeignKeyViolation)]
    [InlineData("unknown-reserve", PostgresErrorCodes.ForeignKeyViolation)]
    [InlineData("unknown-release", PostgresErrorCodes.ForeignKeyViolation)]
    [InlineData("status", PostgresErrorCodes.CheckViolation)]
    [InlineData("reserved-id", PostgresErrorCodes.CheckViolation)]
    [InlineData("reserved-time", PostgresErrorCodes.CheckViolation)]
    [InlineData("reserved-reason", PostgresErrorCodes.CheckViolation)]
    [InlineData("released-no-id", PostgresErrorCodes.CheckViolation)]
    [InlineData("released-no-time", PostgresErrorCodes.CheckViolation)]
    [InlineData("released-no-reason", PostgresErrorCodes.CheckViolation)]
    [InlineData("released-reason", PostgresErrorCodes.CheckViolation)]
    [InlineData("released-time", PostgresErrorCodes.CheckViolation)]
    [InlineData("released-same-id", PostgresErrorCodes.CheckViolation)]
    [InlineData("duplicate-pair", PostgresErrorCodes.UniqueViolation)]
    [InlineData("duplicate-reserve", PostgresErrorCodes.UniqueViolation)]
    [InlineData("duplicate-release", PostgresErrorCodes.UniqueViolation)]
    public async Task Reservation_constraints_reject_malformed_or_reused_evidence(string invalid, string sqlState)
    {
        var player = await FundedPlayer();
        var request = Request(player);
        await using (var setup = fixture.CreateDbContext())
        {
            await Reserve(setup, request);
            await Release(setup, ReleaseRequest(request));
        }
        await using var db = fixture.CreateDbContext();
        var row = await db.Set<BloodCreditReservationRow>().AsNoTracking().SingleAsync(row => row.PlayerId == player.Value);
        if (invalid is "empty-challenge" or "empty-player" or "unknown-player")
        {
            // Exercise the database directly: EF disallows changing tracked natural keys, and
            // inserting a copy would hit transaction uniqueness before the intended player FK.
            var challengeId = invalid == "empty-challenge" ? Guid.Empty : row.ChallengeId;
            var playerId = invalid == "empty-player" ? Guid.Empty : invalid == "unknown-player" ? Guid.NewGuid() : player.Value;
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE blood_money_credit_reservations SET \"ChallengeId\" = {challengeId}, \"PlayerId\" = {playerId} WHERE \"ChallengeId\" = {row.ChallengeId} AND \"PlayerId\" = {player.Value}"));
            Assert.Equal(sqlState, error.SqlState);
            return;
        }
        if (invalid == "duplicate-pair") { db.Add(row); }
        else if (invalid == "duplicate-reserve") { row.ChallengeId = Guid.NewGuid(); db.Add(row); }
        else if (invalid == "duplicate-release")
        {
            var extra = BloodCreditTransaction.Reserve(BloodCreditTransactionId.New(), player, 1, "constraint-fixture", TestClock.UtcNow);
            new BloodCreditLedgerStore(db).AddTransaction(extra);
            await db.SaveChangesAsync();
            row.ChallengeId = Guid.NewGuid(); row.ReserveTransactionId = extra.TransactionId.Value;
            db.Add(row);
        }
        else
        {
            db.Attach(row);
            switch (invalid)
            {
                case "zero-amount": row.Amount = 0; break;
                case "negative-amount": row.Amount = -1; break;
                case "zero-revision": row.Revision = 0; break;
                case "unknown-reserve": row.ReserveTransactionId = Guid.NewGuid(); break;
                case "unknown-release": row.ReleaseTransactionId = Guid.NewGuid(); break;
                case "status": row.Status = "Unknown"; break;
                case "reserved-id": row.Status = "Reserved"; row.ReleasedAt = null; row.ReleaseReason = null; break;
                case "reserved-time": row.Status = "Reserved"; row.ReleaseTransactionId = null; row.ReleaseReason = null; break;
                case "reserved-reason": row.Status = "Reserved"; row.ReleaseTransactionId = null; row.ReleasedAt = null; break;
                case "released-no-id": row.ReleaseTransactionId = null; break;
                case "released-no-time": row.ReleasedAt = null; break;
                case "released-no-reason": row.ReleaseReason = null; break;
                case "released-reason": row.ReleaseReason = "Winner"; break;
                case "released-time": row.ReleasedAt = row.ReservedAt.AddSeconds(-1); break;
                case "released-same-id": row.ReleaseTransactionId = row.ReserveTransactionId; break;
            }
        }
        Assert.Equal(sqlState, Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqlState);
    }

    [Theory]
    [InlineData("Reserve", 1)]
    [InlineData("Release", -1)]
    public async Task Ledger_kind_constraint_rejects_wrong_reservation_posting_sign(string kind, long delta)
    {
        var player = await FundedPlayer();
        await using var db = fixture.CreateDbContext();
        db.Add(new BloodCreditTransactionRow { Id = Guid.NewGuid(), SubjectPlayerId = player.Value, Kind = kind, PlayerDelta = delta, ReferenceCode = "bad-sign", CreatedAt = TestClock.UtcNow });
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqlState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reservation_transaction_foreign_keys_restrict_evidence_deletion(bool release)
    {
        var player = await FundedPlayer();
        var reserve = Request(player);
        var released = ReleaseRequest(reserve);
        await using var db = fixture.CreateDbContext();
        await Reserve(db, reserve);
        await Release(db, released);
        var id = release ? released.TransactionId.Value : reserve.TransactionId.Value;
        // Remove postings first inside the same SQL command so the reservation FK is the blocker.
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM blood_money_credit_postings WHERE \"TransactionId\" = {id}; DELETE FROM blood_money_credit_transactions WHERE \"Id\" = {id}"));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, error.SqlState);
        await Reconcile(player, 100, 1);
    }

    private async Task<PlayerId> FundedPlayer()
    {
        await using var db = fixture.CreateDbContext();
        var player = await PlayerSeeding.CreatePlayerAsync(db, "Reservation", TestClock.UtcNow);
        await new IssueBloodCreditsUseCase(new BloodCreditLedgerStore(db), new EfUnitOfWork(db), TestClock)
            .ExecuteAsync(new(BloodCreditTransactionId.New(), player, 100, "grant"), Ct);
        return player;
    }

    private async Task Reconcile(PlayerId player, long balance, int reservationCount)
    {
        await using var db = fixture.CreateDbContext();
        var account = (await new BloodCreditLedgerStore(db).FindAccountAsync(player, Ct))!;
        Assert.Equal(balance, account.AvailableBalance);
        var transactions = await db.Set<BloodCreditTransactionRow>().AsNoTracking().Include(row => row.Postings).Where(row => row.SubjectPlayerId == player.Value).ToListAsync();
        Assert.Equal(balance, transactions.Sum(row => Assert.Single(row.Postings, line => line.PostingOwner == "Player").Amount));
        Assert.All(transactions, row => { Assert.Equal(2, row.Postings.Count); Assert.Equal(0, row.Postings.Sum(line => line.Amount)); });
        var reservations = await db.Set<BloodCreditReservationRow>().AsNoTracking().Where(row => row.PlayerId == player.Value).ToListAsync();
        Assert.Equal(reservationCount, reservations.Count);
        foreach (var reservation in reservations)
        {
            var reserve = Assert.Single(transactions, row => row.Id == reservation.ReserveTransactionId);
            Assert.Equal("Reserve", reserve.Kind);
            Assert.Equal(-reservation.Amount, reserve.PlayerDelta);
            if (reservation.Status == "Reserved")
            {
                Assert.Null(reservation.ReleaseTransactionId); Assert.Null(reservation.ReleaseReason); Assert.Null(reservation.ReleasedAt);
            }
            else
            {
                var release = Assert.Single(transactions, row => row.Id == reservation.ReleaseTransactionId);
                Assert.Equal("Release", release.Kind); Assert.Equal(reservation.Amount, release.PlayerDelta);
                Assert.NotNull(reservation.ReleaseReason); Assert.NotNull(reservation.ReleasedAt);
            }
        }
        Assert.Equal(reservations.Count, transactions.Count(row => row.Kind == "Reserve"));
        Assert.Equal(reservations.Count(row => row.Status == "Released"), transactions.Count(row => row.Kind == "Release"));
        Assert.Equal(reservations.Count, reservations.Select(row => row.ReserveTransactionId).Distinct().Count());
        Assert.Equal(reservations.Count(row => row.ReleaseTransactionId.HasValue), reservations.Where(row => row.ReleaseTransactionId.HasValue).Select(row => row.ReleaseTransactionId).Distinct().Count());
    }

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
    private sealed class SaveBarrier
    {
        private int _arrivals;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Arrive() { if (Interlocked.Increment(ref _arrivals) == 2) _ready.TrySetResult(); await _ready.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
    }
    private sealed class BarrierUnitOfWork(Level5V2DbContext db, SaveBarrier barrier) : IUnitOfWork
    {
        public async Task SaveChangesAsync(CancellationToken ct) { await barrier.Arrive(); await new EfUnitOfWork(db).SaveChangesAsync(ct); }
    }
    private sealed class FailAfterCommand(string text) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains(text, StringComparison.Ordinal))
            { Fired = true; await result.DisposeAsync(); throw new InvalidOperationException("Injected failure after executing a financial command, before commit."); }
            return result;
        }
    }
    private sealed class PausedLedger(IBloodCreditLedgerStore inner) : IBloodCreditLedgerStore
    {
        public TaskCompletionSource Read { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<BloodCreditAccount?> FindAccountAsync(PlayerId player, CancellationToken ct)
        { var account = await inner.FindAccountAsync(player, ct); Read.TrySetResult(); await Resume.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); return account; }
        public Task<BloodCreditTransaction?> FindTransactionAsync(BloodCreditTransactionId id, CancellationToken ct) => inner.FindTransactionAsync(id, ct);
        public void AddAccount(BloodCreditAccount account) => inner.AddAccount(account);
        public void StageAccountUpdate(BloodCreditAccount account, long revision) => inner.StageAccountUpdate(account, revision);
        public void AddTransaction(BloodCreditTransaction transaction) => inner.AddTransaction(transaction);
    }
}
