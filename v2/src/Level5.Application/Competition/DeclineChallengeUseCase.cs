using Level5.Application.Abstractions;
using Level5.Domain.Competition;
using Level5.Domain.Ids;

namespace Level5.Application.Competition;

public sealed record DeclineChallengeRequest(PlayerId ActingPlayerId, VersusSeriesId SeriesId);

public sealed class DeclineChallengeUseCase(IVersusSeriesStore seriesStore, IClock clock)
{
    public async Task<SeriesView> ExecuteAsync(DeclineChallengeRequest request, CancellationToken cancellationToken)
    {
        var series = await SeriesLookup.LoadForParticipantAsync(seriesStore, request.SeriesId, request.ActingPlayerId, cancellationToken);
        var expectedRevision = series.Revision;

        series.Decline(request.ActingPlayerId, clock.UtcNow);

        await SeriesLookup.SaveOrThrowAsync(seriesStore, series, expectedRevision, cancellationToken);
        return series.ToView(request.ActingPlayerId);
    }
}
