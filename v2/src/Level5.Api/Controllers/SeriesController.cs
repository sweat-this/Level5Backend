using Level5.Api.Security;
using Level5.Application.Common;
using Level5.Application.Competition;
using Level5.Application.Players;
using Level5.Domain.Competition;
using Level5.Domain.Ids;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;

namespace Level5.Api.Controllers;

/// <summary>
/// <see cref="ClientRequestId"/> is mandatory (issue #10). <c>[JsonRequired]</c> makes an omitted
/// value fail model binding (400) and lists it as required in the generated contract; being
/// non-nullable rejects an explicit <c>null</c> the same way. An empty GUID binds and is rejected
/// by <c>CreateChallengeUseCase</c>. <c>[JsonRequired]</c> rather than <c>[Required]</c>, because
/// ASP.NET Core rejects validation attributes on a record's primary-constructor properties and
/// Swashbuckle ignores them on the parameter. Its default exists only so it can keep its position
/// after the optional parameters.
/// </summary>
public sealed record CreateChallengeDto(
    Guid OpponentId, int TotalGames, string RulesetId, int? RulesetVersion = null,
    string? InformationPolicy = null, [property: JsonRequired] Guid ClientRequestId = default);

public sealed record ComparisonKeyDto(string Metric, string Direction);

public sealed record FrozenRulesDto(
    int CompetitionProtocolVersion, string RulesetId, int RulesetVersion, int MinimumCompatibleVersion,
    string ModeId, string InformationPolicy, bool AlternatesFirstAttempt, IReadOnlyList<ComparisonKeyDto> ComparisonKeys);

public sealed record AttemptViewDto(Guid Id, string Status, IReadOnlyDictionary<string, double>? Result);

public sealed record GameRoundViewDto(int GameNumber, AttemptViewDto? YourAttempt, AttemptViewDto? OpponentAttempt);

public sealed record SeriesResponseDto(
    Guid Id, Guid ChallengerId, Guid OpponentId, string Status, int TotalGames, int GamesToWin,
    int CurrentGameNumber, long Revision, Guid? WinnerId, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
    FrozenRulesDto Rules, IReadOnlyList<GameRoundViewDto> Games);

/// <summary>
/// <see cref="SeriesResponseDto"/> plus both participants' public identity (issue #29), returned
/// only from <c>GET /api/v2/series/{seriesId}</c> - command endpoints (create/accept/decline/
/// cancel/complete) keep returning the plain <see cref="SeriesResponseDto"/> unchanged.
/// </summary>
public sealed record SeriesDetailResponseDto(
    Guid Id, Guid ChallengerId, Guid OpponentId, string Status, int TotalGames, int GamesToWin,
    int CurrentGameNumber, long Revision, Guid? WinnerId, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
    FrozenRulesDto Rules, IReadOnlyList<GameRoundViewDto> Games,
    PublicPlayerSummaryDto Challenger, PublicPlayerSummaryDto Opponent);

/// <summary>
/// <see cref="ChallengerId"/>/<see cref="OpponentId"/> are kept for backward compatibility (Unity's
/// existing Backend V2 client) alongside the additive <see cref="Challenger"/>/<see cref="Opponent"/>
/// public identity (issue #29), so a correspondence list is directly renderable without a per-row
/// player lookup.
/// </summary>
public sealed record SeriesSummaryDto(
    Guid Id, Guid ChallengerId, Guid OpponentId, string Status, int CurrentGameNumber, int TotalGames, long Revision, DateTimeOffset CreatedAt,
    PublicPlayerSummaryDto Challenger, PublicPlayerSummaryDto Opponent);

/// <summary>
/// The bounded-pagination envelope every correspondence list endpoint returns (issue #21) -
/// <see cref="NextCursor"/> is <c>null</c> once the caller has reached the end of the result set,
/// and must be passed back verbatim (never parsed) as the next request's <c>cursor</c>.
/// </summary>
public sealed record SeriesSummaryPageDto(IReadOnlyList<SeriesSummaryDto> Items, int Limit, string? NextCursor);

/// <summary>
/// The Protocol V1 named-metric result payload (Competition Protocol V1 section 10). Metric keys
/// must be one of the stable <see cref="ResultMetric"/> names, never a positional index - an
/// unrecognized key is a 400, not a silently ignored field.
/// </summary>
public sealed record CompleteAttemptDto(IReadOnlyDictionary<string, double> Metrics);

public sealed record ComparisonKeySummaryDto(string Metric, string Direction);

/// <summary>
/// The authoritative, participant-safe descriptor <c>StartAttempt</c> returns - everything Unity
/// needs to build its local <c>MatchConfiguration</c> for this attempt, derived only from the
/// series' persisted frozen state (Competition Protocol V1 section 14). Never a raw aggregate/JSON
/// dump.
/// </summary>
public sealed record AttemptDescriptorDto(
    Guid SeriesId, Guid AttemptId, int GameNumber, Guid PlayerId,
    int CompetitionProtocolVersion, string RulesetId, int RulesetVersion, int MinimumCompatibleVersion,
    string ModeId, string InformationPolicy, int TotalGames, int GamesToWin,
    IReadOnlyList<ComparisonKeySummaryDto> ComparisonKeys, IReadOnlyList<string> RequiredResultMetrics);

[ApiController]
[Route("api/v2/series")]
[Authorize]
public sealed class SeriesController(
    CreateChallengeUseCase createChallenge,
    AcceptChallengeUseCase acceptChallenge,
    DeclineChallengeUseCase declineChallenge,
    CancelChallengeUseCase cancelChallenge,
    ICurrentPlayerProvider currentPlayer) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<SeriesResponseDto>> CreateChallenge(CreateChallengeDto request, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var view = await createChallenge.ExecuteAsync(
            new CreateChallengeRequest(
                me, new PlayerId(request.OpponentId), request.RulesetId, request.RulesetVersion, request.TotalGames,
                request.InformationPolicy, request.ClientRequestId),
            cancellationToken);

        return Ok(SeriesDtoMapper.ToDto(view));
    }

    [HttpPost("{seriesId:guid}/accept")]
    public async Task<ActionResult<SeriesResponseDto>> Accept(Guid seriesId, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var view = await acceptChallenge.ExecuteAsync(new AcceptChallengeRequest(me, new VersusSeriesId(seriesId)), cancellationToken);
        return Ok(SeriesDtoMapper.ToDto(view));
    }

    [HttpPost("{seriesId:guid}/decline")]
    public async Task<ActionResult<SeriesResponseDto>> Decline(Guid seriesId, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var view = await declineChallenge.ExecuteAsync(new DeclineChallengeRequest(me, new VersusSeriesId(seriesId)), cancellationToken);
        return Ok(SeriesDtoMapper.ToDto(view));
    }

    [HttpPost("{seriesId:guid}/cancel")]
    public async Task<ActionResult<SeriesResponseDto>> Cancel(Guid seriesId, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var view = await cancelChallenge.ExecuteAsync(new CancelChallengeRequest(me, new VersusSeriesId(seriesId)), cancellationToken);
        return Ok(SeriesDtoMapper.ToDto(view));
    }
}

[ApiController]
[Route("api/v2/series")]
[Authorize]
public sealed class SeriesQueriesController(
    GetSeriesUseCase getSeries,
    ListIncomingChallengesUseCase listIncoming,
    ListOutgoingChallengesUseCase listOutgoing,
    ListActiveSeriesUseCase listActive,
    ListCompletedSeriesUseCase listCompleted,
    ListTerminalHistoryUseCase listHistory,
    ICurrentPlayerProvider currentPlayer) : ControllerBase
{
    [HttpGet("{seriesId:guid}")]
    public async Task<ActionResult<SeriesDetailResponseDto>> GetSeries(Guid seriesId, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var detail = await getSeries.ExecuteAsync(new GetSeriesRequest(me, new VersusSeriesId(seriesId)), cancellationToken);
        return Ok(SeriesDtoMapper.ToDto(detail));
    }

    [HttpGet("incoming")]
    public async Task<ActionResult<SeriesSummaryPageDto>> ListIncoming(int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var page = await listIncoming.ExecuteAsync(new ListSeriesPageRequest(me, limit, cursor), cancellationToken);
        return Ok(SeriesDtoMapper.ToDto(page, SeriesListPaging.ResolveLimit(limit)));
    }

    [HttpGet("outgoing")]
    public async Task<ActionResult<SeriesSummaryPageDto>> ListOutgoing(int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var page = await listOutgoing.ExecuteAsync(new ListSeriesPageRequest(me, limit, cursor), cancellationToken);
        return Ok(SeriesDtoMapper.ToDto(page, SeriesListPaging.ResolveLimit(limit)));
    }

    [HttpGet("active")]
    public async Task<ActionResult<SeriesSummaryPageDto>> ListActive(int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var page = await listActive.ExecuteAsync(new ListSeriesPageRequest(me, limit, cursor), cancellationToken);
        return Ok(SeriesDtoMapper.ToDto(page, SeriesListPaging.ResolveLimit(limit)));
    }

    [HttpGet("completed")]
    public async Task<ActionResult<SeriesSummaryPageDto>> ListCompleted(int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var page = await listCompleted.ExecuteAsync(new ListSeriesPageRequest(me, limit, cursor), cancellationToken);
        return Ok(SeriesDtoMapper.ToDto(page, SeriesListPaging.ResolveLimit(limit)));
    }

    /// <summary>
    /// Every terminal correspondence record either participant is in: Completed, Declined,
    /// Cancelled, and Expired. Unlike <see cref="ListCompleted"/>, which is scoped to series that
    /// actually finished play, this is the durable-history surface for every way a challenge
    /// stopped being active.
    /// </summary>
    [HttpGet("history")]
    public async Task<ActionResult<SeriesSummaryPageDto>> ListHistory(int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var page = await listHistory.ExecuteAsync(new ListSeriesPageRequest(me, limit, cursor), cancellationToken);
        return Ok(SeriesDtoMapper.ToDto(page, SeriesListPaging.ResolveLimit(limit)));
    }
}

[ApiController]
[Route("api/v2/series")]
[Authorize]
public sealed class SeriesAttemptsController(
    StartAttemptUseCase startAttempt,
    CompleteAttemptUseCase completeAttempt,
    ICurrentPlayerProvider currentPlayer) : ControllerBase
{
    private static readonly IReadOnlyDictionary<string, ResultMetric> ResultMetricsByName =
        Enum.GetValues<ResultMetric>().ToDictionary(metric => metric.ToString(), StringComparer.OrdinalIgnoreCase);

    [HttpPost("{seriesId:guid}/games/{gameNumber:int}/attempts/start")]
    public async Task<ActionResult<AttemptDescriptorDto>> StartAttempt(Guid seriesId, int gameNumber, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var descriptor = await startAttempt.ExecuteAsync(
            new StartAttemptRequest(me, new VersusSeriesId(seriesId), gameNumber), cancellationToken);

        return Ok(SeriesDtoMapper.ToDto(descriptor));
    }

    [HttpPost("{seriesId:guid}/games/{gameNumber:int}/attempts/{attemptId:guid}/complete")]
    public async Task<ActionResult<SeriesResponseDto>> CompleteAttempt(
        Guid seriesId, int gameNumber, Guid attemptId, CompleteAttemptDto request, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var result = ParseResult(request.Metrics);
        var view = await completeAttempt.ExecuteAsync(
            new CompleteAttemptRequest(me, new VersusSeriesId(seriesId), gameNumber, new AttemptId(attemptId), result), cancellationToken);

        return Ok(SeriesDtoMapper.ToDto(view));
    }

    /// <summary>
    /// Parses the wire's stable metric names into the canonical <see cref="AttemptResult"/> - an
    /// empty payload or a key that is not a recognized <see cref="ResultMetric"/> name is a 400,
    /// never silently dropped or coerced.
    /// </summary>
    private static AttemptResult ParseResult(IReadOnlyDictionary<string, double>? metrics)
    {
        if (metrics is null || metrics.Count == 0)
        {
            throw new ValidationFailedException("At least one result metric is required.");
        }

        var parsed = new Dictionary<ResultMetric, double>(metrics.Count);
        foreach (var (key, value) in metrics)
        {
            if (!ResultMetricsByName.TryGetValue(key, out var metric))
            {
                throw new ValidationFailedException($"Unknown result metric '{key}'.");
            }

            if (!parsed.TryAdd(metric, value))
            {
                throw new ValidationFailedException($"Result metric '{metric}' was supplied more than once.");
            }
        }

        return AttemptResult.Of(parsed);
    }
}

internal static class SeriesDtoMapper
{
    public static SeriesResponseDto ToDto(SeriesView view) => new(
        view.Id.Value, view.ChallengerId.Value, view.OpponentId.Value, view.Status.ToString(),
        view.TotalGames, view.GamesToWin, view.CurrentGameNumber, view.Revision, view.WinnerId?.Value,
        view.CreatedAt, view.CompletedAt, ToDto(view.Rules),
        [.. view.Games.Select(g => new GameRoundViewDto(g.GameNumber, ToDto(g.YourAttempt), ToDto(g.OpponentAttempt)))]);

    public static SeriesDetailResponseDto ToDto(SeriesDetailView detail) => new(
        detail.Series.Id.Value, detail.Series.ChallengerId.Value, detail.Series.OpponentId.Value, detail.Series.Status.ToString(),
        detail.Series.TotalGames, detail.Series.GamesToWin, detail.Series.CurrentGameNumber, detail.Series.Revision, detail.Series.WinnerId?.Value,
        detail.Series.CreatedAt, detail.Series.CompletedAt, ToDto(detail.Series.Rules),
        [.. detail.Series.Games.Select(g => new GameRoundViewDto(g.GameNumber, ToDto(g.YourAttempt), ToDto(g.OpponentAttempt)))],
        ToDto(detail.Challenger), ToDto(detail.Opponent));

    private static FrozenRulesDto ToDto(FrozenRules rules) => new(
        rules.CompetitionProtocolVersion, rules.RulesetId, rules.RulesetVersion, rules.MinimumCompatibleVersion,
        rules.ModeId, rules.InformationPolicy.ToString(), rules.AlternatesFirstAttempt,
        [.. rules.ComparisonKeys.Select(k => new ComparisonKeyDto(k.Metric.ToString(), k.Direction.ToString()))]);

    private static AttemptViewDto? ToDto(AttemptView? attempt) => attempt is null
        ? null
        : new AttemptViewDto(attempt.Id.Value, attempt.Status.ToString(), attempt.Result?.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value));

    public static SeriesSummaryDto ToDto(SeriesSummaryView summary) => new(
        summary.Id.Value, summary.ChallengerId.Value, summary.OpponentId.Value, summary.Status.ToString(),
        summary.CurrentGameNumber, summary.TotalGames, summary.Revision, summary.CreatedAt,
        ToDto(summary.Challenger), ToDto(summary.Opponent));

    public static SeriesSummaryPageDto ToDto(PagedResult<SeriesSummaryView> page, int resolvedLimit) => new(
        [.. page.Items.Select(ToDto)], resolvedLimit, page.NextCursor);

    private static PublicPlayerSummaryDto ToDto(PublicPlayerSummary summary) => new(summary.PlayerId.Value, summary.DisplayName, summary.Tag);

    public static AttemptDescriptorDto ToDto(AttemptDescriptor descriptor) => new(
        descriptor.SeriesId.Value, descriptor.AttemptId.Value, descriptor.GameNumber, descriptor.PlayerId.Value,
        descriptor.CompetitionProtocolVersion, descriptor.RulesetId, descriptor.RulesetVersion, descriptor.MinimumCompatibleVersion,
        descriptor.ModeId, descriptor.InformationPolicy.ToString(), descriptor.TotalGames, descriptor.GamesToWin,
        [.. descriptor.ComparisonKeys.Select(k => new ComparisonKeySummaryDto(k.Metric.ToString(), k.Direction.ToString()))],
        [.. descriptor.RequiredResultMetrics.Select(m => m.ToString())]);
}
