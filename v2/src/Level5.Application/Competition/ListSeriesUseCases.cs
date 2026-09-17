using Level5.Application.Abstractions;
using Level5.Domain.Competition;
using Level5.Domain.Ids;

namespace Level5.Application.Competition;

public sealed record SeriesSummary(
    VersusSeriesId Id, PlayerId ChallengerId, PlayerId OpponentId, SeriesStatus Status,
    int CurrentGameNumber, int TotalGames, long Revision, DateTimeOffset CreatedAt);

public sealed class ListIncomingChallengesUseCase(IVersusSeriesStore seriesStore)
{
    public async Task<IReadOnlyList<SeriesSummary>> ExecuteAsync(PlayerId actingPlayerId, CancellationToken cancellationToken)
    {
        var series = await seriesStore.ListIncomingChallengesAsync(actingPlayerId, cancellationToken);
        return [.. series.Select(ToSummary)];
    }

    internal static SeriesSummary ToSummary(VersusSeries s)
        => new(s.Id, s.ChallengerId, s.OpponentId, s.Status, s.CurrentGameNumber, s.Format.TotalGames, s.Revision, s.CreatedAt);
}

public sealed class ListOutgoingChallengesUseCase(IVersusSeriesStore seriesStore)
{
    public async Task<IReadOnlyList<SeriesSummary>> ExecuteAsync(PlayerId actingPlayerId, CancellationToken cancellationToken)
    {
        var series = await seriesStore.ListOutgoingChallengesAsync(actingPlayerId, cancellationToken);
        return [.. series.Select(ListIncomingChallengesUseCase.ToSummary)];
    }
}

public sealed class ListActiveSeriesUseCase(IVersusSeriesStore seriesStore)
{
    public async Task<IReadOnlyList<SeriesSummary>> ExecuteAsync(PlayerId actingPlayerId, CancellationToken cancellationToken)
    {
        var series = await seriesStore.ListActiveSeriesAsync(actingPlayerId, cancellationToken);
        return [.. series.Select(ListIncomingChallengesUseCase.ToSummary)];
    }
}

/// <summary>
/// Completed play history: series that reached <see cref="Domain.Competition.SeriesStatus.Completed"/>
/// only. <see cref="Domain.Competition.SeriesStatus.Declined"/>/<see cref="Domain.Competition.SeriesStatus.Cancelled"/>
/// series never played out and are deliberately excluded - issue #10 scopes "completed" to actual
/// finished play, not every way a challenge can stop being pending.
/// </summary>
public sealed class ListCompletedSeriesUseCase(IVersusSeriesStore seriesStore)
{
    public async Task<IReadOnlyList<SeriesSummary>> ExecuteAsync(PlayerId actingPlayerId, CancellationToken cancellationToken)
    {
        var series = await seriesStore.ListCompletedSeriesAsync(actingPlayerId, cancellationToken);
        return [.. series.Select(ListIncomingChallengesUseCase.ToSummary)];
    }
}
