using Level5.Domain.Identity;
using Level5.Domain.Ids;

namespace Level5.Application.Abstractions;

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
}
