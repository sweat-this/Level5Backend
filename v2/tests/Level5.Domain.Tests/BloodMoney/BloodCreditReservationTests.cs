using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;
using Xunit;

namespace Level5.Domain.Tests.BloodMoney;

public sealed class BloodCreditReservationTests
{
    private static readonly PlayerId Player = PlayerId.New();
    private static readonly BloodMoneyChallengeId Challenge = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static BloodCreditTransaction Reserve(long amount = 80) => BloodCreditTransaction.Reserve(BloodCreditTransactionId.New(), Player, amount, "reserve", Now);
    private static BloodCreditTransaction Release(long amount = 80) => BloodCreditTransaction.Release(BloodCreditTransactionId.New(), Player, amount, "release", Now.AddSeconds(1));

    [Fact]
    public void Reserved_and_released_snapshots_preserve_evidence_and_do_not_reopen()
    {
        var reserve = Reserve();
        var reservation = BloodCreditReservation.Reserve(Challenge, reserve);
        Assert.Equal(80, reservation.Amount);
        Assert.Equal(Player, reservation.PlayerId);
        Assert.Equal(Challenge, reservation.ChallengeId);
        Assert.Equal(reserve.TransactionId, reservation.ReserveTransactionId);
        Assert.Equal(Now, reservation.ReservedAt);
        Assert.Equal(1, reservation.Revision);
        Assert.Null(reservation.ReleaseTransactionId);
        var release = Release();
        var released = reservation.Release(release, BloodCreditReleaseReason.Cancelled);
        Assert.Equal(BloodCreditReservationStatus.Reserved, reservation.Status);
        Assert.Equal(BloodCreditReservationStatus.Released, released.Status);
        Assert.Equal(release.TransactionId, released.ReleaseTransactionId);
        Assert.Equal(release.CreatedAt, released.ReleasedAt);
        Assert.Equal(BloodCreditReleaseReason.Cancelled, released.ReleaseReason);
        Assert.Equal(2, released.Revision);
        var rehydrated = BloodCreditReservation.Rehydrate(released.ChallengeId, released.PlayerId, released.Amount,
            released.Status, released.ReserveTransactionId, released.ReleaseTransactionId, released.ReservedAt,
            released.ReleasedAt, released.ReleaseReason, released.Revision);
        Assert.Equal(released.ReleaseTransactionId, rehydrated.ReleaseTransactionId);
        Assert.Equal(released.ReleaseReason, rehydrated.ReleaseReason);
        Assert.Equal(released.ReleasedAt, rehydrated.ReleasedAt);
        Assert.Equal(released.Status, rehydrated.Status);
        Assert.Throws<InvalidBloodCreditsException>(() => released.Release(Release(), BloodCreditReleaseReason.Cancelled));
        Assert.Throws<InvalidBloodCreditsException>(() => reservation.Release(Release(79), BloodCreditReleaseReason.Cancelled));
        Assert.Throws<InvalidBloodCreditsException>(() => reservation.Release(
            BloodCreditTransaction.Release(BloodCreditTransactionId.New(), PlayerId.New(), 80, "release", Now), BloodCreditReleaseReason.Cancelled));
        Assert.Throws<InvalidBloodCreditsException>(() => BloodCreditReservation.Reserve(Challenge, Release()));
        Assert.Throws<InvalidBloodCreditsException>(() => reservation.Release(Reserve(), BloodCreditReleaseReason.Cancelled));
    }

    [Theory]
    [InlineData("challenge")]
    [InlineData("player")]
    [InlineData("reserve-id")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("revision")]
    [InlineData("status")]
    [InlineData("reserved-release-id")]
    [InlineData("reserved-release-time")]
    [InlineData("reserved-release-reason")]
    [InlineData("released-no-id")]
    [InlineData("released-empty-id")]
    [InlineData("released-same-id")]
    [InlineData("released-no-time")]
    [InlineData("released-early-time")]
    [InlineData("released-no-reason")]
    [InlineData("released-invalid-reason")]
    public void Rehydration_rejects_invalid_identity_and_state_coherence(string invalid)
    {
        var released = invalid.StartsWith("released", StringComparison.Ordinal);
        var reserveId = BloodCreditTransactionId.New();
        Assert.Throws<InvalidBloodCreditsException>(() => BloodCreditReservation.Rehydrate(
            invalid == "challenge" ? default : Challenge, invalid == "player" ? default : Player,
            invalid == "zero" ? 0 : invalid == "negative" ? -1 : 80,
            invalid == "status" ? (BloodCreditReservationStatus)99 : released ? BloodCreditReservationStatus.Released : BloodCreditReservationStatus.Reserved,
            invalid == "reserve-id" ? default : reserveId,
            invalid == "released-empty-id" ? default(BloodCreditTransactionId) : invalid == "released-same-id" ? reserveId :
                invalid == "released-no-id" ? null : released || invalid == "reserved-release-id" ? BloodCreditTransactionId.New() : null,
            Now, invalid == "released-no-time" ? null : invalid == "released-early-time" ? Now.AddSeconds(-1) :
                released || invalid == "reserved-release-time" ? Now : null,
            invalid == "released-no-reason" ? null : invalid == "released-invalid-reason" ? (BloodCreditReleaseReason)99 :
                released || invalid == "reserved-release-reason" ? BloodCreditReleaseReason.Declined : null,
            invalid == "revision" ? 0 : 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Transaction_signs_balancing_rehydration_and_overflow_are_enforced(bool release)
    {
        var transaction = release ? Release(long.MaxValue) : Reserve(long.MaxValue);
        Assert.Equal(release ? long.MaxValue : -long.MaxValue, transaction.PlayerDelta);
        Assert.Equal(0, checked(transaction.Postings[0].Amount + transaction.Postings[1].Amount));
        Assert.Throws<InvalidBloodCreditsException>(() => BloodCreditTransaction.Rehydrate(transaction.TransactionId, transaction.Kind,
            Player, -transaction.PlayerDelta, "bad-sign", Now, transaction.Postings));
        foreach (var amount in new[] { 0L, -1, long.MinValue })
            Assert.Throws<InvalidBloodCreditsException>(() => release ? Release(amount) : Reserve(amount));
        var account = BloodCreditAccount.Rehydrate(Player, release ? long.MaxValue : 0, 1);
        if (release) Assert.Throws<OverflowException>(() => account.Apply(Release(1)));
        else Assert.Throws<InsufficientCreditsException>(() => account.Apply(Reserve(1)));
        Assert.Throws<OverflowException>(() => BloodCreditAccount.Rehydrate(Player, 100, long.MaxValue).Apply(Reserve(1)));
        var reservation = BloodCreditReservation.Rehydrate(Challenge, Player, 80, BloodCreditReservationStatus.Reserved,
            BloodCreditTransactionId.New(), null, Now, null, null, long.MaxValue);
        Assert.Throws<OverflowException>(() => reservation.Release(Release(), BloodCreditReleaseReason.PendingExpired));
    }
}
