using Level5.Application.Abstractions;
using Level5.Domain.Competition;
using Level5.Domain.Ids;

namespace Level5.Application.Competition;

public sealed record CancelChallengeRequest(PlayerId ActingPlayerId, VersusSeriesId SeriesId);

/// <summary>Retry- and race-safe per <see cref="SeriesLookup.ApplyChallengeTransitionAsync"/> (issue #10).</summary>
public sealed class CancelChallengeUseCase(IVersusSeriesStore seriesStore, IClock clock)
{
    public Task<SeriesView> ExecuteAsync(CancelChallengeRequest request, CancellationToken cancellationToken)
        => SeriesLookup.ApplyChallengeTransitionAsync(
            seriesStore, request.SeriesId, request.ActingPlayerId, "cancel_challenge",
            series => series.Cancel(request.ActingPlayerId, clock.UtcNow), cancellationToken);
}
