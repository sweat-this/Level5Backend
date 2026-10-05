using Level5.Domain.Identity;
using Level5.Domain.Ids;

namespace Level5.Application.Abstractions;

public sealed record AuthSessionSummary(
    AuthSessionId SessionId,
    ClientKind ClientKind,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastRefreshedAt,
    DateTimeOffset ExpiresAt,
    bool IsCurrent);

public interface IAuthSessionStore
{
    Task<AuthSession?> FindByIdAsync(AuthSessionId id, CancellationToken cancellationToken);

    /// <summary>Looks up the session currently owning this refresh credential's hash - the only way a refresh/logout request identifies which session it means.</summary>
    Task<AuthSession?> FindByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken);

    Task AddAsync(AuthSession session, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a mutated session iff the stored revision still equals
    /// <paramref name="expectedRevision"/> (the revision that was loaded before the domain
    /// mutation ran). Returns false on a concurrency conflict instead of throwing - e.g. two
    /// concurrent refresh requests racing to rotate the same credential - so exactly one caller's
    /// rotation/revocation wins.
    /// </summary>
    Task<bool> TrySaveAsync(AuthSession session, long expectedRevision, CancellationToken cancellationToken);

    /// <summary>Rotates only if the session revision and owning active account generation still match.</summary>
    Task<bool> TryRotateForActiveGenerationAsync(AuthSession session, long expectedRevision, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the caller session's generation only when the signed account/session pair is
    /// currently active. This is the account-security surface's deliberately scoped stateful guard.
    /// </summary>
    Task<long?> FindActiveGenerationAsync(
        AccountId accountId,
        AuthSessionId sessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AuthSessionSummary>> ListActiveAsync(
        AccountId accountId,
        AuthSessionId currentSessionId,
        long sessionGeneration,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task RevokeActiveAsync(
        AccountId accountId,
        AuthSessionId sessionId,
        long sessionGeneration,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task RevokeOtherActiveAsync(
        AccountId accountId,
        AuthSessionId currentSessionId,
        long sessionGeneration,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
