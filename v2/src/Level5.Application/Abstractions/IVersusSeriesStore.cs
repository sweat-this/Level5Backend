using Level5.Domain.Competition;
using Level5.Domain.Ids;

namespace Level5.Application.Abstractions;

public interface IVersusSeriesStore
{
    Task<VersusSeries?> FindByIdAsync(VersusSeriesId id, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a newly created series. <paramref name="clientRequestId"/> is the challenger-scoped
    /// idempotency key the series was created under (see <see cref="FindByIdempotencyKeyAsync"/>) -
    /// <c>null</c> only for series seeded directly by tests that are not exercising retry-safety.
    /// A duplicate <c>(challengerId, clientRequestId)</c> pair (a create race, not a sequential
    /// retry - see <see cref="FindByIdempotencyKeyAsync"/> for the sequential-retry path) fails
    /// with <see cref="Level5.Application.Common.ConflictException"/> rather than creating a
    /// second series.
    /// </summary>
    Task AddAsync(VersusSeries series, Guid? clientRequestId, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the series previously created by this exact challenger under this exact idempotency
    /// key, if any - the retry-safe path for <c>CreateChallenge</c>: a lost response followed by
    /// an identical retry finds this and returns the original series instead of creating a
    /// duplicate. Scoped to <paramref name="challengerId"/> so one player's key can never collide
    /// with another's.
    /// </summary>
    Task<VersusSeries?> FindByIdempotencyKeyAsync(PlayerId challengerId, Guid clientRequestId, CancellationToken cancellationToken);

    Task<IReadOnlyList<VersusSeries>> ListIncomingChallengesAsync(PlayerId playerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<VersusSeries>> ListOutgoingChallengesAsync(PlayerId playerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<VersusSeries>> ListActiveSeriesAsync(PlayerId playerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<VersusSeries>> ListCompletedSeriesAsync(PlayerId playerId, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a mutated series iff the stored revision still equals
    /// <paramref name="expectedRevision"/> (the revision that was loaded before the domain
    /// mutation ran). Returns false on a concurrency conflict instead of throwing, so callers can
    /// decide whether to reload-and-retry or surface a 409 to the client.
    /// </summary>
    Task<bool> TrySaveAsync(VersusSeries series, long expectedRevision, CancellationToken cancellationToken);
}
