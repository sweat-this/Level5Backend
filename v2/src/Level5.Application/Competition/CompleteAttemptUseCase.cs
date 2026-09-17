using Level5.Application.Abstractions;
using Level5.Domain.Competition;
using Level5.Domain.Ids;

namespace Level5.Application.Competition;

public sealed record CompleteAttemptRequest(PlayerId ActingPlayerId, VersusSeriesId SeriesId, int GameNumber, int Score);

/// <summary>
/// Idempotent: if this attempt was already completed (e.g. the client lost the response to an
/// earlier successful call and retried), this returns the originally accepted result instead of
/// re-applying the completion or re-resolving an already-resolved game.
/// </summary>
public sealed class CompleteAttemptUseCase(IVersusSeriesStore seriesStore, IClock clock)
{
    public async Task<SeriesView> ExecuteAsync(CompleteAttemptRequest request, CancellationToken cancellationToken)
    {
        var series = await SeriesLookup.LoadForParticipantAsync(seriesStore, request.SeriesId, request.ActingPlayerId, cancellationToken);
        var expectedRevision = series.Revision;

        series.CompleteAttempt(request.ActingPlayerId, request.GameNumber, AttemptResult.OfScore(request.Score), clock.UtcNow);

        await SeriesLookup.SaveOrThrowAsync(seriesStore, series, expectedRevision, cancellationToken);
        return series.ToView(request.ActingPlayerId);
    }
}
