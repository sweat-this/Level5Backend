using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Application.Players;
using Level5.Domain.Competition;
using Level5.Domain.Ids;

namespace Level5.Application.Competition;

public sealed record SeriesSummary(
    VersusSeriesId Id, PlayerId ChallengerId, PlayerId OpponentId, SeriesStatus Status,
    int CurrentGameNumber, int TotalGames, long Revision, DateTimeOffset CreatedAt);

/// <summary>
/// A <see cref="SeriesSummary"/> plus the public identity (issue #29) of both participants, so a
/// list response is directly renderable without a per-row player lookup. Carries every
/// <see cref="SeriesSummary"/> field flattened alongside <see cref="Challenger"/>/<see cref="Opponent"/>
/// rather than nesting the summary, so existing field access (<c>.Id</c>, <c>.Status</c>, ...) keeps working.
/// </summary>
public sealed record SeriesSummaryView(
    VersusSeriesId Id, PlayerId ChallengerId, PlayerId OpponentId, SeriesStatus Status,
    int CurrentGameNumber, int TotalGames, long Revision, DateTimeOffset CreatedAt,
    PublicPlayerSummary Challenger, PublicPlayerSummary Opponent);

/// <summary>
/// Every correspondence list use case takes the same shape (acting player plus an opaque page
/// request) and returns the same shape (a page of relational-only summaries) - each just points at
/// a different <see cref="IVersusSeriesStore"/> summary query, so there is nothing left to
/// deduplicate beyond this record.
/// </summary>
public sealed record ListSeriesPageRequest(PlayerId ActingPlayerId, int? Limit, string? Cursor);

/// <summary>
/// Enriches an already-paged <see cref="SeriesSummary"/> result with public participant identity
/// in exactly one additional batch query (issue #29) - pagination always runs first, against the
/// relational-only store query, so player presentation can never influence cursor boundaries. A
/// row whose challenger/opponent profile unexpectedly fails to resolve is dropped rather than
/// failing the whole page - matching <see cref="Level5.Application.Social.ListFriendsUseCase"/>'s
/// established orphan-safe pattern, so one inconsistent row can never hide every other row on a
/// player's list.
/// </summary>
internal static class SeriesSummaryEnrichment
{
    public static async Task<PagedResult<SeriesSummaryView>> EnrichAsync(
        IPlayerProfileStore playerProfileStore, PagedResult<SeriesSummary> page, CancellationToken cancellationToken)
    {
        if (page.Items.Count == 0)
        {
            return new PagedResult<SeriesSummaryView>([], page.NextCursor);
        }

        var playerIds = page.Items.SelectMany(s => new[] { s.ChallengerId, s.OpponentId }).ToArray();
        var profiles = await PublicPlayerSummaries.ResolveAsync(playerProfileStore, playerIds, cancellationToken);

        var items = new List<SeriesSummaryView>(page.Items.Count);
        foreach (var s in page.Items)
        {
            if (!profiles.TryGetValue(s.ChallengerId, out var challenger) || !profiles.TryGetValue(s.OpponentId, out var opponent))
            {
                continue;
            }

            items.Add(new SeriesSummaryView(
                s.Id, s.ChallengerId, s.OpponentId, s.Status, s.CurrentGameNumber, s.TotalGames, s.Revision, s.CreatedAt,
                challenger, opponent));
        }

        return new PagedResult<SeriesSummaryView>(items, page.NextCursor);
    }
}

public sealed class ListIncomingChallengesUseCase(IVersusSeriesStore seriesStore, IPlayerProfileStore playerProfileStore)
{
    public async Task<PagedResult<SeriesSummaryView>> ExecuteAsync(ListSeriesPageRequest request, CancellationToken cancellationToken)
    {
        var page = await seriesStore.ListIncomingChallengeSummariesAsync(request.ActingPlayerId, request.Limit, request.Cursor, cancellationToken);
        return await SeriesSummaryEnrichment.EnrichAsync(playerProfileStore, page, cancellationToken);
    }
}

public sealed class ListOutgoingChallengesUseCase(IVersusSeriesStore seriesStore, IPlayerProfileStore playerProfileStore)
{
    public async Task<PagedResult<SeriesSummaryView>> ExecuteAsync(ListSeriesPageRequest request, CancellationToken cancellationToken)
    {
        var page = await seriesStore.ListOutgoingChallengeSummariesAsync(request.ActingPlayerId, request.Limit, request.Cursor, cancellationToken);
        return await SeriesSummaryEnrichment.EnrichAsync(playerProfileStore, page, cancellationToken);
    }
}

public sealed class ListActiveSeriesUseCase(IVersusSeriesStore seriesStore, IPlayerProfileStore playerProfileStore)
{
    public async Task<PagedResult<SeriesSummaryView>> ExecuteAsync(ListSeriesPageRequest request, CancellationToken cancellationToken)
    {
        var page = await seriesStore.ListActiveSeriesSummariesAsync(request.ActingPlayerId, request.Limit, request.Cursor, cancellationToken);
        return await SeriesSummaryEnrichment.EnrichAsync(playerProfileStore, page, cancellationToken);
    }
}

/// <summary>
/// Completed play history: series that reached <see cref="Domain.Competition.SeriesStatus.Completed"/>
/// only. <see cref="Domain.Competition.SeriesStatus.Declined"/>/<see cref="Domain.Competition.SeriesStatus.Cancelled"/>
/// series never played out and are deliberately excluded - issue #10 scopes "completed" to actual
/// finished play, not every way a challenge can stop being pending.
/// </summary>
public sealed class ListCompletedSeriesUseCase(IVersusSeriesStore seriesStore, IPlayerProfileStore playerProfileStore)
{
    public async Task<PagedResult<SeriesSummaryView>> ExecuteAsync(ListSeriesPageRequest request, CancellationToken cancellationToken)
    {
        var page = await seriesStore.ListCompletedSeriesSummariesAsync(request.ActingPlayerId, request.Limit, request.Cursor, cancellationToken);
        return await SeriesSummaryEnrichment.EnrichAsync(playerProfileStore, page, cancellationToken);
    }
}

/// <summary>
/// Full durable correspondence history: every series that reached any terminal status -
/// <see cref="Domain.Competition.SeriesStatus.Completed"/>, <see cref="Domain.Competition.SeriesStatus.Declined"/>,
/// <see cref="Domain.Competition.SeriesStatus.Cancelled"/>, or <see cref="Domain.Competition.SeriesStatus.Expired"/>.
/// Unlike <see cref="ListCompletedSeriesUseCase"/> (deliberately scoped to actual finished play,
/// issue #10), this is the query a client uses to render a "History" view of every way a
/// challenge stopped being active - terminal records are retained indefinitely and are never
/// deleted by normal application behavior, so this always reflects the complete history.
/// </summary>
public sealed class ListTerminalHistoryUseCase(IVersusSeriesStore seriesStore, IPlayerProfileStore playerProfileStore)
{
    public async Task<PagedResult<SeriesSummaryView>> ExecuteAsync(ListSeriesPageRequest request, CancellationToken cancellationToken)
    {
        var page = await seriesStore.ListTerminalHistorySummariesAsync(request.ActingPlayerId, request.Limit, request.Cursor, cancellationToken);
        return await SeriesSummaryEnrichment.EnrichAsync(playerProfileStore, page, cancellationToken);
    }
}
