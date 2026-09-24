using Level5.Application.Abstractions;
using Level5.Domain.Competition;
using Level5.Domain.Ids;

namespace Level5.Application.Competition;

public sealed record AcceptChallengeRequest(PlayerId ActingPlayerId, VersusSeriesId SeriesId);

/// <summary>Retry- and race-safe per <see cref="SeriesLookup.ApplyChallengeTransitionAsync"/> (issue #10).</summary>
public sealed class AcceptChallengeUseCase(IVersusSeriesStore seriesStore, IClock clock)
{
    public Task<SeriesView> ExecuteAsync(AcceptChallengeRequest request, CancellationToken cancellationToken)
        => SeriesLookup.ApplyChallengeTransitionAsync(
            seriesStore, request.SeriesId, request.ActingPlayerId, "accept_challenge",
            series => series.Accept(request.ActingPlayerId, clock.UtcNow), cancellationToken);
}
