using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Competition;
using Level5.Domain.Ids;

namespace Level5.Application.Competition;

public sealed record SeriesSummary(
    VersusSeriesId Id, PlayerId ChallengerId, PlayerId OpponentId, SeriesStatus Status,
    int CurrentGameNumber, int TotalGames, long Revision, DateTimeOffset CreatedAt);

/// <summary>
/// Every correspondence list use case takes the same shape (acting player plus an opaque page
/// request) and returns the same shape (a page of relational-only summaries) - each just points at
/// a different <see cref="IVersusSeriesStore"/> summary query, so there is nothing left to
/// deduplicate beyond this record.
/// </summary>
public sealed record ListSeriesPageRequest(PlayerId ActingPlayerId, int? Limit, string? Cursor);

public sealed class ListIncomingChallengesUseCase(IVersusSeriesStore seriesStore)
{
    public Task<PagedResult<SeriesSummary>> ExecuteAsync(ListSeriesPageRequest request, CancellationToken cancellationToken)
        => seriesStore.ListIncomingChallengeSummariesAsync(request.ActingPlayerId, request.Limit, request.Cursor, cancellationToken);
}

public sealed class ListOutgoingChallengesUseCase(IVersusSeriesStore seriesStore)
{
    public Task<PagedResult<SeriesSummary>> ExecuteAsync(ListSeriesPageRequest request, CancellationToken cancellationToken)
        => seriesStore.ListOutgoingChallengeSummariesAsync(request.ActingPlayerId, request.Limit, request.Cursor, cancellationToken);
}

public sealed class ListActiveSeriesUseCase(IVersusSeriesStore seriesStore)
{
    public Task<PagedResult<SeriesSummary>> ExecuteAsync(ListSeriesPageRequest request, CancellationToken cancellationToken)
        => seriesStore.ListActiveSeriesSummariesAsync(request.ActingPlayerId, request.Limit, request.Cursor, cancellationToken);
}

/// <summary>
/// Completed play history: series that reached <see cref="Domain.Competition.SeriesStatus.Completed"/>
/// only. <see cref="Domain.Competition.SeriesStatus.Declined"/>/<see cref="Domain.Competition.SeriesStatus.Cancelled"/>
/// series never played out and are deliberately excluded - issue #10 scopes "completed" to actual
/// finished play, not every way a challenge can stop being pending.
/// </summary>
public sealed class ListCompletedSeriesUseCase(IVersusSeriesStore seriesStore)
{
    public Task<PagedResult<SeriesSummary>> ExecuteAsync(ListSeriesPageRequest request, CancellationToken cancellationToken)
        => seriesStore.ListCompletedSeriesSummariesAsync(request.ActingPlayerId, request.Limit, request.Cursor, cancellationToken);
}
