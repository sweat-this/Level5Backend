using Level5.Domain.Common;
using Level5.Domain.Ids;

namespace Level5.Domain.BloodMoney;

public readonly record struct BloodCreditTransactionId(Guid Value)
{
    public static BloodCreditTransactionId New() => new(Guid.CreateVersion7());
}

public enum BloodCreditTransactionKind { Issuance, Spend, Correction, Reserve, Release }
public enum BloodCreditPostingOwner { Player, Treasury }

public sealed record BloodCreditPosting(int LineNumber, BloodCreditPostingOwner PostingOwner, PlayerId? PlayerId, long Amount);

public sealed class InvalidBloodCreditsException(string message) : DomainException(message)
{
    public override string Code => "invalid_blood_credits";
}

public sealed class InsufficientCreditsException() : DomainException("Insufficient available Blood Credits.")
{
    public override string Code => "insufficient_credits";
}

/// <summary>Immutable two-party ledger evidence. Treasury is a counterparty, never a player account.</summary>
public sealed class BloodCreditTransaction
{
    public const int ReferenceMaxLength = 128;
    public BloodCreditTransactionId TransactionId { get; }
    public BloodCreditTransactionKind Kind { get; }
    public PlayerId SubjectPlayerId { get; }
    public long PlayerDelta { get; }
    public string ReferenceCode { get; }
    public DateTimeOffset CreatedAt { get; }
    public IReadOnlyList<BloodCreditPosting> Postings { get; }

    private BloodCreditTransaction(BloodCreditTransactionId id, BloodCreditTransactionKind kind,
        PlayerId playerId, long delta, string reference, DateTimeOffset createdAt, IEnumerable<BloodCreditPosting> postings)
    {
        if (id.Value == Guid.Empty || playerId.Value == Guid.Empty || !Enum.IsDefined(kind) ||
            delta is 0 or long.MinValue ||
            (kind is BloodCreditTransactionKind.Issuance or BloodCreditTransactionKind.Release && delta < 0) ||
            (kind is BloodCreditTransactionKind.Spend or BloodCreditTransactionKind.Reserve && delta > 0) ||
            string.IsNullOrWhiteSpace(reference) || reference.Length > ReferenceMaxLength)
            throw new InvalidBloodCreditsException("Invalid transaction identity, kind, delta, or audit reference.");

        var lines = postings?.ToArray()
            ?? throw new InvalidBloodCreditsException("Transaction postings are required.");
        if (lines.Length != 2 ||
            !lines.Contains(new BloodCreditPosting(1, BloodCreditPostingOwner.Player, playerId, delta)) ||
            !lines.Contains(new BloodCreditPosting(2, BloodCreditPostingOwner.Treasury, null, checked(-delta))))
            throw new InvalidBloodCreditsException("A transaction requires exactly two equal-and-opposite owned postings.");

        TransactionId = id;
        Kind = kind;
        SubjectPlayerId = playerId;
        PlayerDelta = delta;
        ReferenceCode = reference;
        CreatedAt = createdAt;
        Postings = Array.AsReadOnly(lines);
    }

    public static BloodCreditTransaction Issue(BloodCreditTransactionId id, PlayerId player, long amount, string reference, DateTimeOffset now)
    {
        RequirePositive(amount);
        return Create(id, BloodCreditTransactionKind.Issuance, player, amount, reference, now);
    }

    public static BloodCreditTransaction Spend(BloodCreditTransactionId id, PlayerId player, long amount, string reference, DateTimeOffset now)
    {
        RequirePositive(amount);
        return Create(id, BloodCreditTransactionKind.Spend, player, checked(-amount), reference, now);
    }

    public static BloodCreditTransaction Correct(BloodCreditTransactionId id, PlayerId player, long delta, string reference, DateTimeOffset now)
        => Create(id, BloodCreditTransactionKind.Correction, player, delta, reference, now);

    public static BloodCreditTransaction Reserve(BloodCreditTransactionId id, PlayerId player, long amount, string reference, DateTimeOffset now)
    {
        RequirePositive(amount);
        return Create(id, BloodCreditTransactionKind.Reserve, player, checked(-amount), reference, now);
    }

    public static BloodCreditTransaction Release(BloodCreditTransactionId id, PlayerId player, long amount, string reference, DateTimeOffset now)
    {
        RequirePositive(amount);
        return Create(id, BloodCreditTransactionKind.Release, player, amount, reference, now);
    }

    public static BloodCreditTransaction Rehydrate(BloodCreditTransactionId id, BloodCreditTransactionKind kind,
        PlayerId player, long delta, string reference, DateTimeOffset createdAt, IEnumerable<BloodCreditPosting> postings)
        => new(id, kind, player, delta, reference, createdAt, postings);

    public bool HasSameIntent(BloodCreditTransaction other)
        => TransactionId == other.TransactionId && Kind == other.Kind && SubjectPlayerId == other.SubjectPlayerId &&
           PlayerDelta == other.PlayerDelta && ReferenceCode == other.ReferenceCode;

    private static BloodCreditTransaction Create(BloodCreditTransactionId id, BloodCreditTransactionKind kind,
        PlayerId player, long delta, string reference, DateTimeOffset now)
    {
        if (delta is 0 or long.MinValue)
            throw new InvalidBloodCreditsException("Delta must be nonzero and safely negatable.");
        return new(id, kind, player, delta, reference, now,
            [new(1, BloodCreditPostingOwner.Player, player, delta), new(2, BloodCreditPostingOwner.Treasury, null, checked(-delta))]);
    }

    private static void RequirePositive(long amount)
    {
        if (amount <= 0) throw new InvalidBloodCreditsException("Amount must be positive.");
    }
}
