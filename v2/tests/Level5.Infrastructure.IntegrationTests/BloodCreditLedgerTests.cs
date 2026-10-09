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
public sealed class BloodCreditLedgerTests(PostgresFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly Clock TestClock = new();

    [Fact]
    public async Task Mixed_ledger_reconciles_for_each_player_and_transaction_and_replays_without_writes()
    {
        var first = await CreatePlayer();
        var second = await CreatePlayer();
        foreach (var player in new[] { first, second })
        {
            await using var db = fixture.CreateDbContext();
            var store = new BloodCreditLedgerStore(db);
            var uow = new EfUnitOfWork(db);
            var issue = new IssueBloodCreditsUseCase(store, uow, TestClock);
            var spend = new SpendBloodCreditsUseCase(store, uow, TestClock);
            var correct = new CorrectBloodCreditsUseCase(store, uow, TestClock);
            Assert.Equal(0, await new GetMyBloodCreditBalanceUseCase(store).ExecuteAsync(player, Ct));
            var grant = new IssueBloodCreditsRequest(BloodCreditTransactionId.New(), player, 100, "grant-1");
            await issue.ExecuteAsync(grant, Ct);
            await issue.ExecuteAsync(new(BloodCreditTransactionId.New(), player, 50, "grant-2"), Ct);
            var debit = new SpendBloodCreditsRequest(BloodCreditTransactionId.New(), player, 80, "spend-1");
            await spend.ExecuteAsync(debit, Ct);
            var fix = new CorrectBloodCreditsRequest(BloodCreditTransactionId.New(), player, 7, "audit-1");
            await correct.ExecuteAsync(fix, Ct);
            await correct.ExecuteAsync(new(BloodCreditTransactionId.New(), player, -3, "audit-2"), Ct);
            Assert.True((await issue.ExecuteAsync(grant, Ct)).IsReplay);
            Assert.True((await spend.ExecuteAsync(debit, Ct)).IsReplay);
            Assert.True((await correct.ExecuteAsync(fix, Ct)).IsReplay);
            await Assert.ThrowsAsync<ConflictException>(() => issue.ExecuteAsync(grant with { Amount = 200 }, Ct));
            var account = await store.FindAccountAsync(player, Ct);
            Assert.Equal(74, account!.AvailableBalance);
            Assert.Equal(5, account.Revision);
            var transactions = await db.Set<BloodCreditTransactionRow>().AsNoTracking().Include(row => row.Postings)
                .Where(row => row.SubjectPlayerId == player.Value).ToListAsync();
            Assert.Equal(5, transactions.Count);
            foreach (var transaction in transactions)
            {
                Assert.Equal(2, transaction.Postings.Count);
                Assert.Equal(0, transaction.Postings.Sum(line => line.Amount));
                var playerLine = Assert.Single(transaction.Postings, line => line.PostingOwner == "Player");
                Assert.Equal(player.Value, playerLine.PlayerId);
                Assert.Equal(transaction.PlayerDelta, playerLine.Amount);
                Assert.Null(Assert.Single(transaction.Postings, line => line.PostingOwner == "Treasury").PlayerId);
            }
            var total = await db.Set<BloodCreditPostingRow>().Where(row => row.PlayerId == player.Value).SumAsync(row => row.Amount);
            Assert.Equal(account.AvailableBalance, total);
        }
    }

    [Fact]
    public async Task Concurrent_spends_from_the_same_revision_commit_once_and_retry_cannot_overdraw()
    {
        var player = await CreatePlayer();
        await Fund(player, 100);
        var barrier = new SaveBarrier();
        var first = new SpendBloodCreditsRequest(BloodCreditTransactionId.New(), player, 80, "first");
        var second = new SpendBloodCreditsRequest(BloodCreditTransactionId.New(), player, 80, "second");
        var outcomes = await Task.WhenAll(RaceSpend(first, barrier), RaceSpend(second, barrier));
        Assert.Single(outcomes, outcome => outcome is null);
        Assert.IsType<ConflictException>(Assert.Single(outcomes, outcome => outcome is not null));

        await using var retry = fixture.CreateDbContext();
        var store = new BloodCreditLedgerStore(retry);
        var loser = outcomes[0] is null ? second : first;
        await Assert.ThrowsAsync<InsufficientCreditsException>(() => new SpendBloodCreditsUseCase(store, new EfUnitOfWork(retry), TestClock).ExecuteAsync(loser, Ct));
        Assert.Equal(20, (await store.FindAccountAsync(player, Ct))!.AvailableBalance);
        Assert.Equal(2, await retry.Set<BloodCreditTransactionRow>().CountAsync(row => row.SubjectPlayerId == player.Value));
        Assert.Equal(2, await retry.Set<BloodCreditPostingRow>().CountAsync(row => row.TransactionId == first.TransactionId.Value || row.TransactionId == second.TransactionId.Value));
        Assert.Equal(20, await retry.Set<BloodCreditPostingRow>().Where(row => row.PlayerId == player.Value).SumAsync(row => row.Amount));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_duplicate_transaction_id_cannot_issue_twice_and_retry_is_replay(bool existingAccount)
    {
        var player = await CreatePlayer();
        if (existingAccount) await Fund(player, 100);
        var request = new IssueBloodCreditsRequest(BloodCreditTransactionId.New(), player, 25, "same-grant");
        var barrier = new SaveBarrier();
        async Task<Exception?> Run()
        {
            await using var db = fixture.CreateDbContext();
            return await Record.ExceptionAsync(() => new IssueBloodCreditsUseCase(new BloodCreditLedgerStore(db),
                new BarrierUnitOfWork(db, barrier), TestClock).ExecuteAsync(request, Ct));
        }
        var outcomes = await Task.WhenAll(Run(), Run());
        Assert.Single(outcomes, outcome => outcome is null);
        Assert.IsType<ConflictException>(Assert.Single(outcomes, outcome => outcome is not null));
        await using var retry = fixture.CreateDbContext();
        var store = new BloodCreditLedgerStore(retry);
        Assert.True((await new IssueBloodCreditsUseCase(store, new EfUnitOfWork(retry), TestClock).ExecuteAsync(request, Ct)).IsReplay);
        Assert.Equal(existingAccount ? 125 : 25, (await store.FindAccountAsync(player, Ct))!.AvailableBalance);
        Assert.Equal(1, await retry.Set<BloodCreditTransactionRow>().CountAsync(row => row.Id == request.TransactionId.Value));
        Assert.Equal(2, await retry.Set<BloodCreditPostingRow>().CountAsync(row => row.TransactionId == request.TransactionId.Value));
    }

    [Fact]
    public async Task Same_transaction_id_racing_across_different_players_is_globally_unique()
    {
        var first = await CreatePlayer();
        var second = await CreatePlayer();
        await Fund(first, 100);
        await Fund(second, 100);
        var id = BloodCreditTransactionId.New();
        var barrier = new SaveBarrier();
        async Task<Exception?> Run(PlayerId player)
        {
            await using var db = fixture.CreateDbContext();
            return await Record.ExceptionAsync(() => new IssueBloodCreditsUseCase(new BloodCreditLedgerStore(db),
                new BarrierUnitOfWork(db, barrier), TestClock).ExecuteAsync(new(id, player, 25, "same-id"), Ct));
        }
        var outcomes = await Task.WhenAll(Run(first), Run(second));
        Assert.Single(outcomes, outcome => outcome is null);
        Assert.IsType<ConflictException>(Assert.Single(outcomes, outcome => outcome is not null));
        await using var retry = fixture.CreateDbContext();
        var store = new BloodCreditLedgerStore(retry);
        var loser = outcomes[0] is null ? second : first;
        await Assert.ThrowsAsync<ConflictException>(() => new IssueBloodCreditsUseCase(store, new EfUnitOfWork(retry), TestClock)
            .ExecuteAsync(new(id, loser, 25, "same-id"), Ct));
        Assert.Equal(100, (await store.FindAccountAsync(loser, Ct))!.AvailableBalance);
        Assert.Equal(225, await retry.Set<BloodCreditAccountRow>().Where(row => row.PlayerId == first.Value || row.PlayerId == second.Value).SumAsync(row => row.AvailableBalance));
    }

    [Theory]
    [InlineData(BloodCreditTransactionKind.Spend, 100, 80, true)]
    [InlineData(BloodCreditTransactionKind.Spend, 100, 80, false)]
    [InlineData(BloodCreditTransactionKind.Issuance, long.MaxValue - 1, 1, true)]
    [InlineData(BloodCreditTransactionKind.Issuance, long.MaxValue - 1, 1, false)]
    [InlineData(BloodCreditTransactionKind.Correction, 100, -80, true)]
    [InlineData(BloodCreditTransactionKind.Correction, 100, -80, false)]
    [InlineData(BloodCreditTransactionKind.Correction, long.MaxValue - 1, 1, true)]
    [InlineData(BloodCreditTransactionKind.Correction, long.MaxValue - 1, 1, false)]
    public async Task Commit_between_reads_resolves_persisted_intent_before_balance_validation(
        BloodCreditTransactionKind kind, long initialBalance, long amountOrDelta, bool sameIntent)
    {
        var player = await CreatePlayer();
        await Fund(player, initialBalance);
        var id = BloodCreditTransactionId.New();
        await using var delayedDb = fixture.CreateDbContext();
        var delayedStore = new PauseAfterFirstReadStore(new BloodCreditLedgerStore(delayedDb));
        var delayedCall = Execute(delayedStore, new EfUnitOfWork(delayedDb), sameIntent ? "race" : "different-intent");
        try
        {
            await delayedStore.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await using var winnerDb = fixture.CreateDbContext();
            Assert.False((await Execute(new BloodCreditLedgerStore(winnerDb), new EfUnitOfWork(winnerDb), "race")).IsReplay);
        }
        finally
        {
            delayedStore.Resume.TrySetResult();
        }

        if (sameIntent)
            Assert.True((await delayedCall).IsReplay);
        else
            await Assert.ThrowsAsync<ConflictException>(() => delayedCall);
        Assert.Empty(delayedDb.ChangeTracker.Entries());

        await using var check = fixture.CreateDbContext();
        var account = (await new BloodCreditLedgerStore(check).FindAccountAsync(player, Ct))!;
        var delta = kind == BloodCreditTransactionKind.Spend ? checked(-amountOrDelta) : amountOrDelta;
        Assert.Equal(checked(initialBalance + delta), account.AvailableBalance);
        Assert.Equal(2, account.Revision);
        Assert.Equal(1, await check.Set<BloodCreditTransactionRow>().CountAsync(row => row.Id == id.Value));
        var postings = await check.Set<BloodCreditPostingRow>().Where(row => row.TransactionId == id.Value).ToListAsync();
        Assert.Equal(2, postings.Count);
        Assert.Equal(0, postings.Sum(row => row.Amount));
        Assert.Equal(account.AvailableBalance, await check.Set<BloodCreditPostingRow>().Where(row => row.PlayerId == player.Value).SumAsync(row => row.Amount));

        Task<BloodCreditMutationResult> Execute(IBloodCreditLedgerStore store, IUnitOfWork unitOfWork, string reference)
            => kind switch
            {
                BloodCreditTransactionKind.Issuance => new IssueBloodCreditsUseCase(store, unitOfWork, TestClock)
                    .ExecuteAsync(new(id, player, amountOrDelta, reference), Ct),
                BloodCreditTransactionKind.Spend => new SpendBloodCreditsUseCase(store, unitOfWork, TestClock)
                    .ExecuteAsync(new(id, player, amountOrDelta, reference), Ct),
                _ => new CorrectBloodCreditsUseCase(store, unitOfWork, TestClock)
                    .ExecuteAsync(new(id, player, amountOrDelta, reference), Ct)
            };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_after_a_posting_command_rolls_back_projection_transaction_and_both_postings(bool existingAccount)
    {
        var player = await CreatePlayer();
        if (existingAccount) await Fund(player, 100);
        var id = BloodCreditTransactionId.New();
        var fault = new FailAfterPosting();
        await using (var db = fixture.CreateDbContext(fault))
        {
            var store = new BloodCreditLedgerStore(db);
            var uow = new EfUnitOfWork(db);
            if (existingAccount)
                await Assert.ThrowsAsync<DbUpdateException>(() => new SpendBloodCreditsUseCase(store, uow, TestClock).ExecuteAsync(new(id, player, 80, "failed-spend"), Ct));
            else
                await Assert.ThrowsAsync<DbUpdateException>(() => new IssueBloodCreditsUseCase(store, uow, TestClock).ExecuteAsync(new(id, player, 80, "failed-grant"), Ct));
        }
        Assert.True(fault.Fired);
        await using var check = fixture.CreateDbContext();
        var account = await new BloodCreditLedgerStore(check).FindAccountAsync(player, Ct);
        if (existingAccount)
        {
            Assert.Equal(100, account!.AvailableBalance);
            Assert.Equal(1, account.Revision);
        }
        else Assert.Null(account);
        Assert.False(await check.Set<BloodCreditTransactionRow>().AnyAsync(row => row.Id == id.Value));
        Assert.False(await check.Set<BloodCreditPostingRow>().AnyAsync(row => row.TransactionId == id.Value));
        // A new unit of work can retry the uncommitted ID successfully.
        if (existingAccount)
            await new SpendBloodCreditsUseCase(new BloodCreditLedgerStore(check), new EfUnitOfWork(check), TestClock).ExecuteAsync(new(id, player, 80, "failed-spend"), Ct);
        else
            await new IssueBloodCreditsUseCase(new BloodCreditLedgerStore(check), new EfUnitOfWork(check), TestClock).ExecuteAsync(new(id, player, 80, "failed-grant"), Ct);
    }

    [Fact]
    public async Task Account_primary_key_and_player_foreign_key_are_enforced()
    {
        var player = await CreatePlayer();
        await Fund(player, 10);
        await using (var db = fixture.CreateDbContext())
        {
            db.Add(new BloodCreditAccountRow { PlayerId = player.Value, AvailableBalance = 0, Revision = 0 });
            AssertSqlState(PostgresErrorCodes.UniqueViolation, await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()));
        }
        await using (var db = fixture.CreateDbContext())
        {
            db.Add(new BloodCreditAccountRow { PlayerId = Guid.NewGuid(), AvailableBalance = 0, Revision = 0 });
            AssertSqlState(PostgresErrorCodes.ForeignKeyViolation, await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()));
        }
        await using (var db = fixture.CreateDbContext())
        {
            var profile = await db.PlayerProfiles.SingleAsync(row => row.Id == player.Value);
            db.Remove(profile);
            AssertSqlState(PostgresErrorCodes.RestrictViolation, await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()));
        }
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public async Task Nonnegative_account_constraints_are_enforced(long balance, long revision)
    {
        var player = await CreatePlayer();
        await using var db = fixture.CreateDbContext();
        db.Add(new BloodCreditAccountRow { PlayerId = player.Value, AvailableBalance = balance, Revision = revision });
        AssertSqlState(PostgresErrorCodes.CheckViolation, await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()));
    }

    [Theory]
    [InlineData("player-without-id")]
    [InlineData("treasury-with-id")]
    [InlineData("zero")]
    [InlineData("invalid-owner")]
    [InlineData("invalid-line")]
    [InlineData("unknown-player")]
    [InlineData("unknown-transaction")]
    public async Task Posting_constraints_and_foreign_keys_reject_invalid_evidence(string invalid)
    {
        var player = await CreatePlayer();
        await Fund(player, 10);
        await using var db = fixture.CreateDbContext();
        var transaction = await db.Set<BloodCreditTransactionRow>().SingleAsync(row => row.SubjectPlayerId == player.Value);
        var lineNumber = invalid == "treasury-with-id" ? 2 : 1;
        var row = await db.Set<BloodCreditPostingRow>().SingleAsync(row => row.TransactionId == transaction.Id && row.LineNumber == lineNumber);
        switch (invalid)
        {
            case "player-without-id": row.PlayerId = null; break;
            case "treasury-with-id": row.PlayerId = player.Value; break;
            case "zero": row.Amount = 0; break;
            case "invalid-owner": row.PostingOwner = "Other"; break;
            case "invalid-line":
                db.Entry(row).State = EntityState.Detached;
                db.Add(new BloodCreditPostingRow { TransactionId = transaction.Id, LineNumber = 3, PlayerId = player.Value, PostingOwner = "Player", Amount = 1 });
                break;
            case "unknown-player": row.PlayerId = Guid.NewGuid(); break;
            case "unknown-transaction":
                db.Entry(row).State = EntityState.Detached;
                db.Add(new BloodCreditPostingRow { TransactionId = Guid.NewGuid(), LineNumber = 1, PlayerId = player.Value, PostingOwner = "Player", Amount = 1 });
                break;
        }
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertSqlState(invalid.StartsWith("unknown-", StringComparison.Ordinal) ? PostgresErrorCodes.ForeignKeyViolation : PostgresErrorCodes.CheckViolation, error);
    }

    [Fact]
    public async Task Transaction_primary_key_and_subject_foreign_key_are_enforced()
    {
        var player = await CreatePlayer();
        var id = BloodCreditTransactionId.New();
        await using (var db = fixture.CreateDbContext())
            await new IssueBloodCreditsUseCase(new BloodCreditLedgerStore(db), new EfUnitOfWork(db), TestClock).ExecuteAsync(new(id, player, 10, "grant"), Ct);
        await using (var db = fixture.CreateDbContext())
        {
            new BloodCreditLedgerStore(db).AddTransaction(BloodCreditTransaction.Issue(id, player, 10, "grant", TestClock.UtcNow));
            AssertSqlState(PostgresErrorCodes.UniqueViolation, await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()));
        }
        await using (var db = fixture.CreateDbContext())
        {
            new BloodCreditLedgerStore(db).AddTransaction(BloodCreditTransaction.Issue(BloodCreditTransactionId.New(), PlayerId.New(), 10, "grant", TestClock.UtcNow));
            AssertSqlState(PostgresErrorCodes.ForeignKeyViolation, await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()));
        }
    }

    [Theory]
    [InlineData("zero-delta")]
    [InlineData("min-delta")]
    [InlineData("invalid-kind")]
    [InlineData("negative-issuance")]
    [InlineData("positive-spend")]
    [InlineData("blank-reference")]
    [InlineData("long-reference")]
    [InlineData("empty-id")]
    public async Task Transaction_constraints_reject_malformed_state(string invalid)
    {
        var player = await CreatePlayer();
        await using var db = fixture.CreateDbContext();
        var row = new BloodCreditTransactionRow
        {
            Id = Guid.NewGuid(), SubjectPlayerId = player.Value, Kind = "Issuance", PlayerDelta = 1,
            ReferenceCode = "grant", CreatedAt = TestClock.UtcNow
        };
        switch (invalid)
        {
            case "zero-delta": row.PlayerDelta = 0; break;
            case "min-delta": row.Kind = "Correction"; row.PlayerDelta = long.MinValue; break;
            case "invalid-kind": row.Kind = "Unknown"; break;
            case "negative-issuance": row.PlayerDelta = -1; break;
            case "positive-spend": row.Kind = "Spend"; break;
            case "blank-reference": row.ReferenceCode = "   "; break;
            case "long-reference": row.ReferenceCode = new string('a', 129); break;
            case "empty-id": row.Id = Guid.Empty; break;
        }
        db.Add(row);
        AssertSqlState(invalid == "long-reference" ? PostgresErrorCodes.StringDataRightTruncation : PostgresErrorCodes.CheckViolation,
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()));
    }

    private async Task<Exception?> RaceSpend(SpendBloodCreditsRequest request, SaveBarrier barrier)
    {
        await using var db = fixture.CreateDbContext();
        return await Record.ExceptionAsync(() => new SpendBloodCreditsUseCase(new BloodCreditLedgerStore(db),
            new BarrierUnitOfWork(db, barrier), TestClock).ExecuteAsync(request, Ct));
    }

    private async Task<PlayerId> CreatePlayer()
    {
        await using var db = fixture.CreateDbContext();
        return await PlayerSeeding.CreatePlayerAsync(db, "Credit", TestClock.UtcNow);
    }

    private async Task Fund(PlayerId player, long amount)
    {
        await using var db = fixture.CreateDbContext();
        await new IssueBloodCreditsUseCase(new BloodCreditLedgerStore(db), new EfUnitOfWork(db), TestClock)
            .ExecuteAsync(new(BloodCreditTransactionId.New(), player, amount, "grant"), Ct);
    }

    private static void AssertSqlState(string expected, DbUpdateException error)
        => Assert.Equal(expected, Assert.IsType<PostgresException>(error.InnerException).SqlState);

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

    // Pause after whichever read runs first, without prescribing the use case's query order.
    // A separate context commits the same ID before this operation performs its second read.
    private sealed class PauseAfterFirstReadStore(IBloodCreditLedgerStore inner) : IBloodCreditLedgerStore
    {
        private int _reads;
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BloodCreditAccount?> FindAccountAsync(PlayerId playerId, CancellationToken cancellationToken)
        {
            var account = await inner.FindAccountAsync(playerId, cancellationToken);
            await PauseFirstRead(cancellationToken);
            return account;
        }

        public async Task<BloodCreditTransaction?> FindTransactionAsync(BloodCreditTransactionId id, CancellationToken cancellationToken)
        {
            var transaction = await inner.FindTransactionAsync(id, cancellationToken);
            await PauseFirstRead(cancellationToken);
            return transaction;
        }

        public void AddAccount(BloodCreditAccount account) => inner.AddAccount(account);
        public void StageAccountUpdate(BloodCreditAccount account, long revision) => inner.StageAccountUpdate(account, revision);
        public void AddTransaction(BloodCreditTransaction transaction) => inner.AddTransaction(transaction);

        private async Task PauseFirstRead(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _reads) != 1) return;
            FirstRead.TrySetResult();
            await Resume.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
    }

    private sealed class SaveBarrier
    {
        private int _arrivals;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Arrive()
        {
            if (Interlocked.Increment(ref _arrivals) == 2) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private sealed class BarrierUnitOfWork(Level5V2DbContext db, SaveBarrier barrier) : IUnitOfWork
    {
        public async Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            await barrier.Arrive();
            await new EfUnitOfWork(db).SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class FailAfterPosting : DbCommandInterceptor
    {
        public bool Fired { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO blood_money_credit_postings", StringComparison.Ordinal))
            {
                Fired = true;
                await result.DisposeAsync();
                throw new InvalidOperationException("Simulated failure after executing a posting insert, before commit.");
            }
            return result;
        }
    }
}
