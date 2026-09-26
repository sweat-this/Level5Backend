using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Ids;
using Level5.Domain.Results;

namespace Level5.Application.Tests.Fakes;

/// <summary>Mimics the real store's (PlayerId, ClientResultId) uniqueness: a duplicate pair fails with ConflictException instead of overwriting.</summary>
public sealed class InMemoryMatchResultStore : IMatchResultStore
{
    private readonly Dictionary<Guid, MatchResult> _rows = [];

    public Task<MatchResult?> FindByClientResultIdAsync(PlayerId playerId, Guid clientResultId, CancellationToken cancellationToken)
    {
        var match = _rows.Values.SingleOrDefault(r => r.PlayerId == playerId && r.ClientResultId == clientResultId);
        return Task.FromResult(match);
    }

    public Task<MatchResult?> FindByIdAsync(MatchResultId id, CancellationToken cancellationToken)
        => Task.FromResult(_rows.GetValueOrDefault(id.Value));

    public Task AddAsync(MatchResult result, CancellationToken cancellationToken)
    {
        var duplicate = _rows.Values.Any(r => r.PlayerId == result.PlayerId && r.ClientResultId == result.ClientResultId);
        if (duplicate)
        {
            throw new ConflictException("The request conflicts with existing data. Please retry.");
        }

        _rows[result.Id.Value] = result;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Stands in for the genuine concurrent-duplicate-submission race without depending on real
/// thread timing: request A's lookup → none, request B's lookup → none, A's insert wins, B's
/// insert loses. The first <see cref="FindByClientResultIdAsync"/> call is forced to miss (as if
/// running before either request had committed anything), and <see cref="AddAsync"/> always
/// throws <see cref="ConflictException"/> (as the real store would on a unique-constraint
/// violation) - so <c>SubmitMatchResultUseCase</c>'s catch-and-reload path is exercised against
/// whatever the winner (seeded into <paramref name="inner"/> beforehand) actually committed.
/// </summary>
public sealed class RacingMatchResultStore(InMemoryMatchResultStore inner) : IMatchResultStore
{
    private bool _firstLookupDone;

    public Task<MatchResult?> FindByClientResultIdAsync(PlayerId playerId, Guid clientResultId, CancellationToken cancellationToken)
    {
        if (!_firstLookupDone)
        {
            _firstLookupDone = true;
            return Task.FromResult<MatchResult?>(null);
        }

        return inner.FindByClientResultIdAsync(playerId, clientResultId, cancellationToken);
    }

    public Task<MatchResult?> FindByIdAsync(MatchResultId id, CancellationToken cancellationToken)
        => inner.FindByIdAsync(id, cancellationToken);

    public Task AddAsync(MatchResult result, CancellationToken cancellationToken)
        => throw new ConflictException("The request conflicts with existing data. Please retry.");
}
