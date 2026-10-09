using Level5.Application.Abstractions;
using Level5.Application.BloodMoney;
using Level5.Application.Common;
using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;
using Xunit;

namespace Level5.Application.Tests.BloodMoney;

public sealed class BloodCreditUseCaseTests
{
    private readonly PlayerId _player = PlayerId.New();
    private readonly Ledger _ledger = new();
    private readonly Clock _clock = new();
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Missing_account_reads_zero_without_staging_or_saving()
    {
        Assert.Equal(0, await new GetMyBloodCreditBalanceUseCase(_ledger).ExecuteAsync(_player, Ct));
        Assert.Null(_ledger.Account);
        Assert.Empty(_ledger.Transactions);
        Assert.Equal(0, _ledger.Saves);
    }

    [Fact]
    public async Task First_issuance_stages_account_and_balanced_evidence_in_one_save_and_replays_without_save()
    {
        var request = new IssueBloodCreditsRequest(BloodCreditTransactionId.New(), _player, 100, "grant-1");
        Assert.False((await Issue.ExecuteAsync(request, Ct)).IsReplay);
        Assert.Equal(100, _ledger.Account!.AvailableBalance);
        Assert.Equal(1, _ledger.Saves);
        Assert.Equal(0, Assert.Single(_ledger.Transactions.Values).Postings.Sum(line => line.Amount));
        _clock.UtcNow = _clock.UtcNow.AddHours(1);
        Assert.True((await Issue.ExecuteAsync(request, Ct)).IsReplay);
        Assert.Equal(1, _ledger.Saves);
        Assert.Equal(100, _ledger.Account.AvailableBalance);
    }

    [Fact]
    public async Task Reused_transaction_id_rejects_every_changed_intent_field()
    {
        var request = new IssueBloodCreditsRequest(BloodCreditTransactionId.New(), _player, 10, "grant");
        await Issue.ExecuteAsync(request, Ct);
        await Assert.ThrowsAsync<ConflictException>(() => Issue.ExecuteAsync(request with { PlayerId = PlayerId.New() }, Ct));
        await Assert.ThrowsAsync<ConflictException>(() => Issue.ExecuteAsync(request with { Amount = 11 }, Ct));
        await Assert.ThrowsAsync<ConflictException>(() => Issue.ExecuteAsync(request with { ReferenceCode = "other" }, Ct));
        await Assert.ThrowsAsync<ConflictException>(() => Correct.ExecuteAsync(new(request.TransactionId, _player, 10, "grant"), Ct));
        await Assert.ThrowsAsync<ConflictException>(() => Spend.ExecuteAsync(new(request.TransactionId, _player, 10, "grant"), Ct));
        Assert.Equal(1, _ledger.Saves);
        Assert.Equal(10, _ledger.Account!.AvailableBalance);
    }

    [Fact]
    public async Task Insufficient_spend_changes_nothing_and_exact_spend_can_replay_at_zero()
    {
        await Assert.ThrowsAsync<InsufficientCreditsException>(() => Spend.ExecuteAsync(new(BloodCreditTransactionId.New(), _player, 1, "spend"), Ct));
        Assert.Null(_ledger.Account);
        await Fund(100);
        await Assert.ThrowsAsync<InsufficientCreditsException>(() => Spend.ExecuteAsync(new(BloodCreditTransactionId.New(), _player, 101, "spend"), Ct));
        Assert.Equal(1, _ledger.Saves);
        Assert.Single(_ledger.Transactions);
        Assert.Equal(100, _ledger.Account!.AvailableBalance);
        var request = new SpendBloodCreditsRequest(BloodCreditTransactionId.New(), _player, 100, "spend");
        await Spend.ExecuteAsync(request, Ct);
        Assert.True((await Spend.ExecuteAsync(request, Ct)).IsReplay);
        Assert.Equal(0, _ledger.Account.AvailableBalance);
        Assert.Equal(2, _ledger.Saves);
    }

    [Fact]
    public async Task Corrections_are_audited_replayable_and_cannot_overdraw_or_open_accounts()
    {
        await Assert.ThrowsAsync<InvalidBloodCreditsException>(() => Correct.ExecuteAsync(new(BloodCreditTransactionId.New(), _player, 5, "fix"), Ct));
        await Assert.ThrowsAsync<InsufficientCreditsException>(() => Correct.ExecuteAsync(new(BloodCreditTransactionId.New(), _player, -5, "fix"), Ct));
        Assert.Null(_ledger.Account);
        await Fund(10);
        var positive = new CorrectBloodCreditsRequest(BloodCreditTransactionId.New(), _player, 5, "audit-1");
        var negative = new CorrectBloodCreditsRequest(BloodCreditTransactionId.New(), _player, -3, "audit-2");
        await Correct.ExecuteAsync(positive, Ct);
        await Correct.ExecuteAsync(negative, Ct);
        Assert.True((await Correct.ExecuteAsync(positive, Ct)).IsReplay);
        Assert.True((await Correct.ExecuteAsync(negative, Ct)).IsReplay);
        await Assert.ThrowsAsync<InsufficientCreditsException>(() => Correct.ExecuteAsync(negative with { TransactionId = BloodCreditTransactionId.New(), Delta = -13 }, Ct));
        Assert.Equal(12, _ledger.Account!.AvailableBalance);
        Assert.Equal(3, _ledger.Saves);
        Assert.Equal(3, _ledger.Transactions.Count);
    }

    [Fact]
    public async Task Overflow_leaves_projection_and_ledger_unstaged()
    {
        await Fund(long.MaxValue);
        await Assert.ThrowsAsync<OverflowException>(() => Issue.ExecuteAsync(new(BloodCreditTransactionId.New(), _player, 1, "too-much"), Ct));
        await Assert.ThrowsAsync<OverflowException>(() => Correct.ExecuteAsync(new(BloodCreditTransactionId.New(), _player, 1, "too-much"), Ct));
        Assert.Equal(long.MaxValue, _ledger.Account!.AvailableBalance);
        Assert.Equal(1, _ledger.Saves);
        Assert.Single(_ledger.Transactions);
    }

    private IssueBloodCreditsUseCase Issue => new(_ledger, _ledger, _clock);
    private SpendBloodCreditsUseCase Spend => new(_ledger, _ledger, _clock);
    private CorrectBloodCreditsUseCase Correct => new(_ledger, _ledger, _clock);
    private Task<BloodCreditMutationResult> Fund(long amount) => Issue.ExecuteAsync(new(BloodCreditTransactionId.New(), _player, amount, "grant"), Ct);

    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow; }

    // Records staging/save order; atomicity and rollback are proven separately against PostgreSQL.
    private sealed class Ledger : IBloodCreditLedgerStore, IUnitOfWork
    {
        public BloodCreditAccount? Account { get; private set; }
        public Dictionary<BloodCreditTransactionId, BloodCreditTransaction> Transactions { get; } = [];
        public int Saves { get; private set; }
        public Task<BloodCreditAccount?> FindAccountAsync(PlayerId playerId, CancellationToken cancellationToken) => Task.FromResult(Account);
        public Task<BloodCreditTransaction?> FindTransactionAsync(BloodCreditTransactionId id, CancellationToken cancellationToken)
            => Task.FromResult(Transactions.GetValueOrDefault(id));
        public void AddAccount(BloodCreditAccount account) => Account = account;
        public void StageAccountUpdate(BloodCreditAccount account, long expectedRevision)
        {
            Assert.Equal(Account!.Revision, expectedRevision);
            Account = account;
        }
        public void AddTransaction(BloodCreditTransaction transaction) => Transactions.Add(transaction.TransactionId, transaction);
        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            Assert.Equal(Account!.Revision, Transactions.Count);
            Saves++;
            return Task.CompletedTask;
        }
    }
}
