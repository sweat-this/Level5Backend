using Level5.Domain.Competition;
using Level5.Domain.Ids;

namespace Level5.Application.Abstractions;

public interface IVersusSeriesStore
{
    Task<VersusSeries?> FindByIdAsync(VersusSeriesId id, CancellationToken cancellationToken);

    Task AddAsync(VersusSeries series, CancellationToken cancellationToken);

    Task<IReadOnlyList<VersusSeries>> ListIncomingChallengesAsync(PlayerId playerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<VersusSeries>> ListOutgoingChallengesAsync(PlayerId playerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<VersusSeries>> ListActiveSeriesAsync(PlayerId playerId, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a mutated series iff the stored revision still equals
    /// <paramref name="expectedRevision"/> (the revision that was loaded before the domain
    /// mutation ran). Returns false on a concurrency conflict instead of throwing, so callers can
    /// decide whether to reload-and-retry or surface a 409 to the client.
    /// </summary>
    Task<bool> TrySaveAsync(VersusSeries series, long expectedRevision, CancellationToken cancellationToken);
}
