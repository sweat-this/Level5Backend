using Level5.Application.Abstractions;
using Level5.Domain.Competition;
using Level5.Domain.Ids;

namespace Level5.Application.Competition;

public sealed record GetSeriesRequest(PlayerId ActingPlayerId, VersusSeriesId SeriesId);

public sealed class GetSeriesUseCase(IVersusSeriesStore seriesStore)
{
    public async Task<SeriesView> ExecuteAsync(GetSeriesRequest request, CancellationToken cancellationToken)
    {
        var series = await SeriesLookup.LoadForParticipantAsync(seriesStore, request.SeriesId, request.ActingPlayerId, cancellationToken);
        return series.ToView(request.ActingPlayerId);
    }
}
