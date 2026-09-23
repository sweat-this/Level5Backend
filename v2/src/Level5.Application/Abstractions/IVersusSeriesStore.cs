using Level5.Application.Common;
using Level5.Application.Competition;
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

    /// <summary>
    /// Projects directly from relational <c>competitive_series</c> columns - never deserializes
    /// <c>state_json</c> or reconstructs a <see cref="VersusSeries"/> aggregate (issue #21), so a
    /// malformed or unsupported-schema-version document on an unrelated row cannot fail this
    /// player's whole list. <paramref name="cursor"/> is the opaque token from a previous page's
    /// <see cref="PagedResult{T}.NextCursor"/>, or <c>null</c> for the first page; an invalid
    /// cursor is rejected with <see cref="Level5.Application.Common.ValidationFailedException"/>.
    /// <paramref name="limit"/> is resolved against <see cref="SeriesListPaging"/> bounds
    /// (a null/non-positive value defaults, anything above the max is clamped to it).
    /// </summary>
    Task<PagedResult<SeriesSummary>> ListIncomingChallengeSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken);

    /// <summary>See <see cref="ListIncomingChallengeSummariesAsync"/> - same projection/pagination contract, scoped to the challenger's own pending outgoing challenges.</summary>
    Task<PagedResult<SeriesSummary>> ListOutgoingChallengeSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken);

    /// <summary>See <see cref="ListIncomingChallengeSummariesAsync"/> - same projection/pagination contract, scoped to active series either participant is in.</summary>
    Task<PagedResult<SeriesSummary>> ListActiveSeriesSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken);

    /// <summary>See <see cref="ListIncomingChallengeSummariesAsync"/> - same projection/pagination contract, scoped to completed series either participant is in, ordered by completion time.</summary>
    Task<PagedResult<SeriesSummary>> ListCompletedSeriesSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a mutated series iff the stored revision still equals
    /// <paramref name="expectedRevision"/> (the revision that was loaded before the domain
    /// mutation ran). Returns false on a concurrency conflict instead of throwing, so callers can
    /// decide whether to reload-and-retry or surface a 409 to the client.
    /// </summary>
    Task<bool> TrySaveAsync(VersusSeries series, long expectedRevision, CancellationToken cancellationToken);
}
