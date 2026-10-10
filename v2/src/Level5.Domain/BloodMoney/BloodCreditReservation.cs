using Level5.Domain.Ids;

namespace Level5.Domain.BloodMoney;

public readonly record struct BloodMoneyChallengeId(Guid Value);
public enum BloodCreditReservationStatus { Reserved, Released }
public enum BloodCreditReleaseReason { Declined, Cancelled, PendingExpired }

/// <summary>One immutable financial lifecycle per challenge/player; challenge eligibility is owned by its caller.</summary>
public sealed class BloodCreditReservation
{
    public BloodMoneyChallengeId ChallengeId { get; }
    public PlayerId PlayerId { get; }
    public long Amount { get; }
    public BloodCreditReservationStatus Status { get; }
    public BloodCreditTransactionId ReserveTransactionId { get; }
    public BloodCreditTransactionId? ReleaseTransactionId { get; }
    public DateTimeOffset ReservedAt { get; }
    public DateTimeOffset? ReleasedAt { get; }
    public BloodCreditReleaseReason? ReleaseReason { get; }
    public long Revision { get; }

    private BloodCreditReservation(BloodMoneyChallengeId challengeId, PlayerId playerId, long amount,
        BloodCreditReservationStatus status, BloodCreditTransactionId reserveTransactionId,
        BloodCreditTransactionId? releaseTransactionId, DateTimeOffset reservedAt, DateTimeOffset? releasedAt,
        BloodCreditReleaseReason? releaseReason, long revision)
    {
        if (challengeId.Value == Guid.Empty || playerId.Value == Guid.Empty || amount <= 0 || revision <= 0 ||
            reserveTransactionId.Value == Guid.Empty || !Enum.IsDefined(status))
            throw new InvalidBloodCreditsException("Invalid reservation identity, amount, status, or revision.");
        if (status == BloodCreditReservationStatus.Reserved &&
            (releaseTransactionId is not null || releasedAt is not null || releaseReason is not null))
            throw new InvalidBloodCreditsException("A reserved stake cannot contain release evidence.");
        if (status == BloodCreditReservationStatus.Released &&
            (releaseTransactionId is null || releaseTransactionId.Value.Value == Guid.Empty ||
             releaseTransactionId == reserveTransactionId || releasedAt is null || releasedAt < reservedAt ||
             releaseReason is null || !Enum.IsDefined(releaseReason.Value)))
            throw new InvalidBloodCreditsException("A released stake requires coherent release evidence.");
        ChallengeId = challengeId;
        PlayerId = playerId;
        Amount = amount;
        Status = status;
        ReserveTransactionId = reserveTransactionId;
        ReleaseTransactionId = releaseTransactionId;
        ReservedAt = reservedAt;
        ReleasedAt = releasedAt;
        ReleaseReason = releaseReason;
        Revision = revision;
    }

    public static BloodCreditReservation Reserve(BloodMoneyChallengeId challengeId, BloodCreditTransaction transaction)
    {
        if (transaction.Kind != BloodCreditTransactionKind.Reserve)
            throw new InvalidBloodCreditsException("A reservation requires Reserve ledger evidence.");
        return new(challengeId, transaction.SubjectPlayerId, checked(-transaction.PlayerDelta),
            BloodCreditReservationStatus.Reserved, transaction.TransactionId, null, transaction.CreatedAt, null, null, 1);
    }

    public BloodCreditReservation Release(BloodCreditTransaction transaction, BloodCreditReleaseReason reason)
    {
        if (Status != BloodCreditReservationStatus.Reserved || transaction.Kind != BloodCreditTransactionKind.Release ||
            transaction.SubjectPlayerId != PlayerId || transaction.PlayerDelta != Amount)
            throw new InvalidBloodCreditsException("Only an active reservation can release its exact stake to its owner.");
        return new(ChallengeId, PlayerId, Amount, BloodCreditReservationStatus.Released, ReserveTransactionId,
            transaction.TransactionId, ReservedAt, transaction.CreatedAt, reason, checked(Revision + 1));
    }

    public static BloodCreditReservation Rehydrate(BloodMoneyChallengeId challengeId, PlayerId playerId, long amount,
        BloodCreditReservationStatus status, BloodCreditTransactionId reserveTransactionId,
        BloodCreditTransactionId? releaseTransactionId, DateTimeOffset reservedAt, DateTimeOffset? releasedAt,
        BloodCreditReleaseReason? releaseReason, long revision)
        => new(challengeId, playerId, amount, status, reserveTransactionId, releaseTransactionId,
            reservedAt, releasedAt, releaseReason, revision);
}
