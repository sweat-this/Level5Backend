using Level5.Application.Abstractions;
using Level5.Domain.Competition;
using Level5.Domain.Ids;

namespace Level5.Application.Competition;

public sealed record AcceptChallengeRequest(PlayerId ActingPlayerId, VersusSeriesId SeriesId);

public sealed class AcceptChallengeUseCase(IVersusSeriesStore seriesStore, IClock clock)
{
    public async Task<SeriesView> ExecuteAsync(AcceptChallengeRequest request, CancellationToken cancellationToken)
    {
        var series = await SeriesLookup.LoadForParticipantAsync(seriesStore, request.SeriesId, request.ActingPlayerId, cancellationToken);
        var expectedRevision = series.Revision;

        series.Accept(request.ActingPlayerId, clock.UtcNow);

        await SeriesLookup.SaveOrThrowAsync(seriesStore, series, expectedRevision, cancellationToken);
        return series.ToView(request.ActingPlayerId);
    }
}
