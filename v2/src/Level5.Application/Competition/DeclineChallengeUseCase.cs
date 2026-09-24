using Level5.Application.Abstractions;
using Level5.Domain.Competition;
using Level5.Domain.Ids;

namespace Level5.Application.Competition;

public sealed record DeclineChallengeRequest(PlayerId ActingPlayerId, VersusSeriesId SeriesId);

/// <summary>Retry- and race-safe per <see cref="SeriesLookup.ApplyChallengeTransitionAsync"/> (issue #10).</summary>
public sealed class DeclineChallengeUseCase(IVersusSeriesStore seriesStore, IClock clock)
{
    public Task<SeriesView> ExecuteAsync(DeclineChallengeRequest request, CancellationToken cancellationToken)
        => SeriesLookup.ApplyChallengeTransitionAsync(
            seriesStore, request.SeriesId, request.ActingPlayerId, "decline_challenge",
            series => series.Decline(request.ActingPlayerId, clock.UtcNow), cancellationToken);
}
