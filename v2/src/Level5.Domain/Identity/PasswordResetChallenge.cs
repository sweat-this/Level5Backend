using Level5.Domain.Common;
using Level5.Domain.Ids;

namespace Level5.Domain.Identity;

/// <summary>A single-use credential bound to one account and its verified canonical email.</summary>
public sealed class PasswordResetChallenge
{
    public PasswordResetChallengeId Id { get; private set; }
    public AccountId AccountId { get; private set; }
    public Email TargetEmail { get; private set; }
    public string TokenHash { get; private set; }
    public DateTimeOffset IssuedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }
    public long Revision { get; private set; }

    private PasswordResetChallenge(
        PasswordResetChallengeId id, AccountId accountId, Email targetEmail, string tokenHash,
        DateTimeOffset issuedAt, DateTimeOffset expiresAt, DateTimeOffset? consumedAt, long revision)
    {
        Id = id;
        AccountId = accountId;
        TargetEmail = targetEmail;
        TokenHash = tokenHash;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
        ConsumedAt = consumedAt;
        Revision = revision;
    }

    public static PasswordResetChallenge Create(
        AccountId accountId, Email targetEmail, string tokenHash, DateTimeOffset now, TimeSpan lifetime)
    {
        ValidateCredential(tokenHash, lifetime);
        return new(PasswordResetChallengeId.New(), accountId, targetEmail, tokenHash, now, now + lifetime, null, 0);
    }

    public static PasswordResetChallenge Rehydrate(
        PasswordResetChallengeId id, AccountId accountId, Email targetEmail, string tokenHash,
        DateTimeOffset issuedAt, DateTimeOffset expiresAt, DateTimeOffset? consumedAt, long revision)
        => new(id, accountId, targetEmail, tokenHash, issuedAt, expiresAt, consumedAt, revision);

    public bool CanComplete(DateTimeOffset now) => ConsumedAt is null && now < ExpiresAt;

    public void Rotate(Email targetEmail, string tokenHash, DateTimeOffset now, TimeSpan lifetime)
    {
        ValidateCredential(tokenHash, lifetime);
        TargetEmail = targetEmail;
        TokenHash = tokenHash;
        IssuedAt = now;
        ExpiresAt = now + lifetime;
        ConsumedAt = null;
        Revision++;
    }

    public void Consume(DateTimeOffset now)
    {
        if (!CanComplete(now)) throw new PasswordResetChallengeInvalidException();
        ConsumedAt = now;
        Revision++;
    }

    private static void ValidateCredential(string tokenHash, TimeSpan lifetime)
    {
        if (string.IsNullOrWhiteSpace(tokenHash)) throw new ArgumentException("A token hash is required.", nameof(tokenHash));
        if (lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
    }
}

public sealed class PasswordResetChallengeInvalidException : DomainException
{
    public PasswordResetChallengeInvalidException() : base("The password-reset credential is invalid or no longer usable.") { }
}
