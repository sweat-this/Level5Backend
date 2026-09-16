using Level5.Application.Abstractions;
using Level5.Domain.Ids;

namespace Level5.Application.Competition;

public sealed record StartAttemptRequest(PlayerId ActingPlayerId, VersusSeriesId SeriesId, int GameNumber);

public sealed record AttemptStarted(AttemptId AttemptId, int GameNumber);

/// <summary>
/// Idempotent by construction: calling this again for the same player/game before completion
/// returns the same <see cref="AttemptId"/> instead of allocating a new one, so a client that
/// retries after a dropped response does not orphan an attempt.
/// </summary>
public sealed class StartAttemptUseCase(IVersusSeriesStore seriesStore, IClock clock)
{
    public async Task<AttemptStarted> ExecuteAsync(StartAttemptRequest request, CancellationToken cancellationToken)
    {
        var series = await SeriesLookup.LoadForParticipantAsync(seriesStore, request.SeriesId, request.ActingPlayerId, cancellationToken);
        var expectedRevision = series.Revision;

        var attempt = series.StartAttempt(request.ActingPlayerId, request.GameNumber, clock.UtcNow);

        await SeriesLookup.SaveOrThrowAsync(seriesStore, series, expectedRevision, cancellationToken);
        return new AttemptStarted(attempt.Id, request.GameNumber);
    }
}
