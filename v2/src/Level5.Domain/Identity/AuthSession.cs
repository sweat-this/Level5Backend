using Level5.Domain.Common;
using Level5.Domain.Ids;

namespace Level5.Domain.Identity;

/// <summary>
/// A persistent, rotating refresh session backing long-lived login. Holds only a one-way hash of
/// the refresh credential (<see cref="RefreshTokenHash"/>) - the raw secret itself is never
/// stored, only ever handed to the client that receives it. Optimistic-concurrency via
/// <see cref="Revision"/>, the same <c>WHERE id = @id AND revision = @expected</c> pattern used by
/// <see cref="Competition.VersusSeries"/>, so a given refresh credential can be rotated by at most
/// one concurrent request - see <c>IAuthSessionStore.TrySaveAsync</c>.
/// </summary>
public sealed class AuthSession
{
    public AuthSessionId Id { get; private set; }
    public AccountId AccountId { get; private set; }
    public string RefreshTokenHash { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public long Revision { get; private set; }

    private AuthSession(
        AuthSessionId id,
        AccountId accountId,
        string refreshTokenHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        DateTimeOffset? revokedAt,
        long revision)
    {
        Id = id;
        AccountId = accountId;
        RefreshTokenHash = refreshTokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        RevokedAt = revokedAt;
        Revision = revision;
    }

    public static AuthSession Create(AccountId accountId, string refreshTokenHash, DateTimeOffset now, TimeSpan lifetime)
        => new(AuthSessionId.New(), accountId, refreshTokenHash, now, now + lifetime, revokedAt: null, revision: 0);

    public static AuthSession Rehydrate(
        AuthSessionId id,
        AccountId accountId,
        string refreshTokenHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        DateTimeOffset? revokedAt,
        long revision)
        => new(id, accountId, refreshTokenHash, createdAt, expiresAt, revokedAt, revision);

    /// <summary>Whether the current refresh credential on this session can still be exchanged for a new access token.</summary>
    public bool CanRefresh(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    /// <summary>
    /// Replaces the current refresh credential with a new one and extends the session's expiry,
    /// so the credential that hashed to the old <see cref="RefreshTokenHash"/> becomes unusable.
    /// Callers must persist the result conditioned on the revision that was loaded before calling
    /// this, so at most one of several concurrent rotations of the same credential can win (see
    /// <c>IAuthSessionStore.TrySaveAsync</c>) - this method itself only guards against rotating a
    /// session that is already revoked or expired, which a caller that checked
    /// <see cref="CanRefresh"/> first should never actually hit.
    /// </summary>
    public void Rotate(string newRefreshTokenHash, DateTimeOffset now, TimeSpan lifetime)
    {
        if (!CanRefresh(now))
        {
            throw new SessionNotRefreshableException("Session cannot be refreshed: it is revoked or expired.");
        }

        RefreshTokenHash = newRefreshTokenHash;
        ExpiresAt = now + lifetime;
        Revision++;
    }

    /// <summary>Idempotent - revoking an already-revoked session leaves it unchanged rather than throwing, so retried logout calls stay safe.</summary>
    public void Revoke(DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
        Revision++;
    }
}

public sealed class SessionNotRefreshableException : DomainException
{
    public SessionNotRefreshableException(string message) : base(message)
    {
    }
}
