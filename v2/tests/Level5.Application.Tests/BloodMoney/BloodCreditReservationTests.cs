using Level5.Application.Abstractions;
using Level5.Application.BloodMoney;
using Level5.Application.Common;
using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;
using Xunit;

namespace Level5.Application.Tests.BloodMoney;

public sealed class BloodCreditReservationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly PlayerId _player = PlayerId.New();
    private readonly BloodMoneyChallengeId _challenge = new(Guid.NewGuid());
    private readonly Stores _stores = new();
    private BloodCreditReservationMutator Mutator => new(_stores, _stores, new Clock());
    private ReserveBloodCreditsRequest Request(long amount = 80) => new(BloodCreditTransactionId.New(), _challenge, _player, amount);
    private ReleaseBloodCreditsRequest ReleaseRequest() => new(BloodCreditTransactionId.New(), _challenge, _player, BloodCreditReleaseReason.Cancelled);
    private void Fund(long amount = 100) => _stores.Account = BloodCreditAccount.Rehydrate(_player, amount, 1);

    [Fact]
    public async Task Staging_composes_reserve_and_release_without_committing_and_replays_without_mutation()
    {
        Fund();
        var reserve = Request();
        Assert.False((await Mutator.StageReserveAsync(reserve, Ct)).IsReplay);
        Assert.Equal(20, _stores.Account!.AvailableBalance);
        Assert.Equal(BloodCreditTransactionKind.Reserve, _stores.Transactions[reserve.TransactionId].Kind);
        Assert.True((await Mutator.StageReserveAsync(reserve, Ct)).IsReplay);
        var release = ReleaseRequest();
        Assert.False((await Mutator.StageReleaseAsync(release, Ct)).IsReplay);
        Assert.Equal(100, _stores.Account.AvailableBalance);
        Assert.Equal(80, _stores.Transactions[release.TransactionId].PlayerDelta);
        Assert.Equal(BloodCreditTransactionKind.Release, _stores.Transactions[release.TransactionId].Kind);
        Assert.Equal(BloodCreditReservationStatus.Released, _stores.Reservations[(_challenge, _player)].Status);
        Assert.True((await Mutator.StageReleaseAsync(release, Ct)).IsReplay);
        Assert.True((await Mutator.StageReserveAsync(reserve, Ct)).IsReplay);
        Assert.Equal(0, _stores.Saves);
        Assert.Equal(2, _stores.Transactions.Count);
    }

    [Fact]
    public async Task Standalone_wrappers_commit_once_each_and_never_save_replays()
    {
        Fund();
        var reserve = Request(100);
        var useCase = new ReserveBloodCreditsUseCase(Mutator, _stores);
        await useCase.ExecuteAsync(reserve, Ct);
        Assert.True((await useCase.ExecuteAsync(reserve, Ct)).IsReplay);
        Assert.Equal(1, _stores.Saves);
        var release = ReleaseRequest();
        var releaseUseCase = new ReleaseBloodCreditsUseCase(Mutator, _stores);
        await releaseUseCase.ExecuteAsync(release, Ct);
        Assert.True((await releaseUseCase.ExecuteAsync(release, Ct)).IsReplay);
        Assert.Equal(2, _stores.Saves);
    }

    [Fact]
    public async Task Changed_reserve_fingerprints_and_second_lifecycles_conflict()
    {
        Fund();
        var reserve = Request();
        await Mutator.StageReserveAsync(reserve, Ct);
        foreach (var changed in new[] { reserve with { Amount = 81 }, reserve with { PlayerId = PlayerId.New() },
                     reserve with { ChallengeId = new(Guid.NewGuid()) }, reserve with { TransactionId = BloodCreditTransactionId.New() } })
            await Assert.ThrowsAsync<ConflictException>(() => Mutator.StageReserveAsync(changed, Ct));
        await Mutator.StageReleaseAsync(ReleaseRequest(), Ct);
        await Assert.ThrowsAsync<ConflictException>(() => Mutator.StageReserveAsync(Request(), Ct));
        Assert.Equal(2, _stores.Transactions.Count);
    }

    [Fact]
    public async Task Changed_release_intents_and_other_operations_using_same_id_conflict()
    {
        Fund();
        var reserve = Request();
        await Mutator.StageReserveAsync(reserve, Ct);
        var release = ReleaseRequest();
        await Assert.ThrowsAsync<ConflictException>(() => Mutator.StageReleaseAsync(release with { TransactionId = reserve.TransactionId }, Ct));
        await Mutator.StageReleaseAsync(release, Ct);
        foreach (var changed in new[] { release with { Reason = BloodCreditReleaseReason.Declined },
                     release with { PlayerId = PlayerId.New() }, release with { ChallengeId = new(Guid.NewGuid()) },
                     release with { TransactionId = BloodCreditTransactionId.New() } })
            await Assert.ThrowsAsync<ConflictException>(() => Mutator.StageReleaseAsync(changed, Ct));
        Assert.Equal(100, _stores.Account!.AvailableBalance);
        Assert.Equal(2, _stores.Transactions.Count);
    }

    [Fact]
    public async Task Missing_or_insufficient_account_does_not_fund_or_stage_reservations()
    {
        await Assert.ThrowsAsync<InsufficientCreditsException>(() => Mutator.StageReserveAsync(Request(), Ct));
        Assert.Null(_stores.Account);
        Fund(79);
        await Assert.ThrowsAsync<InsufficientCreditsException>(() => Mutator.StageReserveAsync(Request(), Ct));
        await Assert.ThrowsAsync<InvalidBloodCreditsException>(() => Mutator.StageReleaseAsync(ReleaseRequest(), Ct));
        Assert.Empty(_stores.Reservations);
        Assert.Empty(_stores.Transactions);
    }

    [Fact]
    public async Task Invalid_ids_reason_and_amount_and_overflow_fail_before_staging()
    {
        Fund();
        foreach (var invalid in new[] { Request(0), Request(-1), Request() with { ChallengeId = default },
                     Request() with { PlayerId = default }, Request() with { TransactionId = default } })
            await Assert.ThrowsAsync<InvalidBloodCreditsException>(() => Mutator.StageReserveAsync(invalid, Ct));
        await Assert.ThrowsAsync<InvalidBloodCreditsException>(() => Mutator.StageReleaseAsync(ReleaseRequest() with { Reason = (BloodCreditReleaseReason)99 }, Ct));
        var request = Request();
        await Mutator.StageReserveAsync(request, Ct);
        _stores.Account = BloodCreditAccount.Rehydrate(_player, long.MaxValue, 2);
        await Assert.ThrowsAsync<OverflowException>(() => Mutator.StageReleaseAsync(ReleaseRequest(), Ct));
        Assert.Single(_stores.Transactions);
        Assert.Equal(BloodCreditReservationStatus.Reserved, _stores.Reservations[(_challenge, _player)].Status);
    }

    [Fact]
    public async Task Release_requires_the_existing_account_and_never_recreates_it()
    {
        Fund();
        await Mutator.StageReserveAsync(Request(), Ct);
        _stores.Account = null;
        await Assert.ThrowsAsync<InvalidBloodCreditsException>(() => Mutator.StageReleaseAsync(ReleaseRequest(), Ct));
        Assert.Null(_stores.Account);
        Assert.Single(_stores.Transactions);
        Assert.Equal(BloodCreditReservationStatus.Reserved, _stores.Reservations[(_challenge, _player)].Status);
    }

    [Fact]
    public async Task References_encode_every_intent_field_invariantly_within_existing_bound()
    {
        Fund(long.MaxValue);
        var reserve = Request(long.MaxValue);
        await Mutator.StageReserveAsync(reserve, Ct);
        var release = ReleaseRequest() with { Reason = BloodCreditReleaseReason.PendingExpired };
        await Mutator.StageReleaseAsync(release, Ct);
        foreach (var transaction in _stores.Transactions.Values)
        {
            Assert.InRange(transaction.ReferenceCode.Length, 1, BloodCreditTransaction.ReferenceMaxLength);
            Assert.Contains(_challenge.Value.ToString("N"), transaction.ReferenceCode);
            Assert.Contains(_player.Value.ToString("N"), transaction.ReferenceCode);
            Assert.Contains("9223372036854775807", transaction.ReferenceCode);
        }
        Assert.EndsWith(":PendingExpired", _stores.Transactions[release.TransactionId].ReferenceCode);
    }

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
    private sealed class Stores : IBloodCreditLedgerStore, IBloodCreditReservationStore, IUnitOfWork
    {
        public BloodCreditAccount? Account { get; set; }
        public Dictionary<BloodCreditTransactionId, BloodCreditTransaction> Transactions { get; } = [];
        public Dictionary<(BloodMoneyChallengeId, PlayerId), BloodCreditReservation> Reservations { get; } = [];
        public int Saves { get; private set; }
        public Task<BloodCreditAccount?> FindAccountAsync(PlayerId playerId, CancellationToken ct) => Task.FromResult(Account?.PlayerId == playerId ? Account : null);
        public Task<BloodCreditTransaction?> FindTransactionAsync(BloodCreditTransactionId id, CancellationToken ct) => Task.FromResult(Transactions.GetValueOrDefault(id));
        public Task<BloodCreditReservation?> FindAsync(BloodMoneyChallengeId challengeId, PlayerId playerId, CancellationToken ct) => Task.FromResult(Reservations.GetValueOrDefault((challengeId, playerId)));
        public void AddAccount(BloodCreditAccount account) => throw new Xunit.Sdk.XunitException("Reservations must never create an account.");
        public void StageAccountUpdate(BloodCreditAccount account, long revision) { Assert.Equal(Account!.Revision, revision); Account = account; }
        public void AddTransaction(BloodCreditTransaction transaction) => Transactions.Add(transaction.TransactionId, transaction);
        public void Add(BloodCreditReservation reservation) => Reservations.Add((reservation.ChallengeId, reservation.PlayerId), reservation);
        public void StageUpdate(BloodCreditReservation reservation, long revision)
        {
            Assert.Equal(Reservations[(reservation.ChallengeId, reservation.PlayerId)].Revision, revision);
            Reservations[(reservation.ChallengeId, reservation.PlayerId)] = reservation;
        }
        public Task SaveChangesAsync(CancellationToken ct) { Saves++; return Task.CompletedTask; }
    }
}
