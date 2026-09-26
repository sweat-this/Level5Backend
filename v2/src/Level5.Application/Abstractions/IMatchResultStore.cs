using Level5.Domain.Ids;
using Level5.Domain.Results;

namespace Level5.Application.Abstractions;

public interface IMatchResultStore
{
    /// <summary>
    /// Finds the result previously submitted by this exact player under this exact
    /// <see cref="MatchResult.ClientResultId"/>, if any - the retry-safe lookup path for
    /// <c>SubmitMatchResultUseCase</c>. Scoped to <paramref name="playerId"/> so one player's
    /// client result id can never collide with another's.
    /// </summary>
    Task<MatchResult?> FindByClientResultIdAsync(PlayerId playerId, Guid clientResultId, CancellationToken cancellationToken);

    /// <summary>Finds a result by its own id, regardless of owner - used by provenance verification (issue: historical score migration).</summary>
    Task<MatchResult?> FindByIdAsync(MatchResultId id, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a newly submitted result. A duplicate <c>(playerId, clientResultId)</c> pair (a
    /// submission race, not a sequential retry - see <see cref="FindByClientResultIdAsync"/> for
    /// the sequential-retry path) fails with <see cref="Level5.Application.Common.ConflictException"/>
    /// rather than creating a second row.
    /// </summary>
    Task AddAsync(MatchResult result, CancellationToken cancellationToken);
}
