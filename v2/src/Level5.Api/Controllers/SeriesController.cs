using Level5.Api.Security;
using Level5.Application.Common;
using Level5.Application.Competition;
using Level5.Domain.Competition;
using Level5.Domain.Ids;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Level5.Api.Controllers;

public sealed record CreateChallengeDto(
    Guid OpponentId, int TotalGames, string RulesetId, int? RulesetVersion = null,
    string? InformationPolicy = null, Guid? ClientRequestId = null);

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

public sealed record SeriesSummaryDto(
    Guid Id, Guid ChallengerId, Guid OpponentId, string Status, int CurrentGameNumber, int TotalGames, long Revision, DateTimeOffset CreatedAt);

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
    ICurrentPlayerProvider currentPlayer) : ControllerBase
{
    [HttpGet("{seriesId:guid}")]
    public async Task<ActionResult<SeriesResponseDto>> GetSeries(Guid seriesId, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var view = await getSeries.ExecuteAsync(new GetSeriesRequest(me, new VersusSeriesId(seriesId)), cancellationToken);
        return Ok(SeriesDtoMapper.ToDto(view));
    }

    [HttpGet("incoming")]
    public async Task<ActionResult<IReadOnlyList<SeriesSummaryDto>>> ListIncoming(CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var series = await listIncoming.ExecuteAsync(me, cancellationToken);
        return Ok(series.Select(SeriesDtoMapper.ToDto));
    }

    [HttpGet("outgoing")]
    public async Task<ActionResult<IReadOnlyList<SeriesSummaryDto>>> ListOutgoing(CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var series = await listOutgoing.ExecuteAsync(me, cancellationToken);
        return Ok(series.Select(SeriesDtoMapper.ToDto));
    }

    [HttpGet("active")]
    public async Task<ActionResult<IReadOnlyList<SeriesSummaryDto>>> ListActive(CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var series = await listActive.ExecuteAsync(me, cancellationToken);
        return Ok(series.Select(SeriesDtoMapper.ToDto));
    }

    [HttpGet("completed")]
    public async Task<ActionResult<IReadOnlyList<SeriesSummaryDto>>> ListCompleted(CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var series = await listCompleted.ExecuteAsync(me, cancellationToken);
        return Ok(series.Select(SeriesDtoMapper.ToDto));
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

    private static FrozenRulesDto ToDto(FrozenRules rules) => new(
        rules.CompetitionProtocolVersion, rules.RulesetId, rules.RulesetVersion, rules.MinimumCompatibleVersion,
        rules.ModeId, rules.InformationPolicy.ToString(), rules.AlternatesFirstAttempt,
        [.. rules.ComparisonKeys.Select(k => new ComparisonKeyDto(k.Metric.ToString(), k.Direction.ToString()))]);

    private static AttemptViewDto? ToDto(AttemptView? attempt) => attempt is null
        ? null
        : new AttemptViewDto(attempt.Id.Value, attempt.Status.ToString(), attempt.Result?.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value));

    public static SeriesSummaryDto ToDto(SeriesSummary summary) => new(
        summary.Id.Value, summary.ChallengerId.Value, summary.OpponentId.Value, summary.Status.ToString(),
        summary.CurrentGameNumber, summary.TotalGames, summary.Revision, summary.CreatedAt);

    public static AttemptDescriptorDto ToDto(AttemptDescriptor descriptor) => new(
        descriptor.SeriesId.Value, descriptor.AttemptId.Value, descriptor.GameNumber, descriptor.PlayerId.Value,
        descriptor.CompetitionProtocolVersion, descriptor.RulesetId, descriptor.RulesetVersion, descriptor.MinimumCompatibleVersion,
        descriptor.ModeId, descriptor.InformationPolicy.ToString(), descriptor.TotalGames, descriptor.GamesToWin,
        [.. descriptor.ComparisonKeys.Select(k => new ComparisonKeySummaryDto(k.Metric.ToString(), k.Direction.ToString()))],
        [.. descriptor.RequiredResultMetrics.Select(m => m.ToString())]);
}
