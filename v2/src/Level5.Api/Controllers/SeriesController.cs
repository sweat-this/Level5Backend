using Level5.Api.Security;
using Level5.Application.Competition;
using Level5.Domain.Competition;
using Level5.Domain.Ids;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Level5.Api.Controllers;

public sealed record CreateChallengeDto(Guid OpponentId, int TotalGames, string RulesetId, int? RulesetVersion = null);

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

public sealed record CompleteAttemptDto(int Score);

public sealed record AttemptStartedDto(Guid AttemptId, int GameNumber);

[ApiController]
[Route("api/v2/series")]
[Authorize]
public sealed class SeriesController(
    CreateChallengeUseCase createChallenge,
    AcceptChallengeUseCase acceptChallenge,
    DeclineChallengeUseCase declineChallenge,
    CancelChallengeUseCase cancelChallenge,
    GetSeriesUseCase getSeries,
    ListIncomingChallengesUseCase listIncoming,
    ListOutgoingChallengesUseCase listOutgoing,
    ListActiveSeriesUseCase listActive,
    StartAttemptUseCase startAttempt,
    CompleteAttemptUseCase completeAttempt,
    ICurrentPlayerProvider currentPlayer) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<SeriesResponseDto>> CreateChallenge(CreateChallengeDto request, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var view = await createChallenge.ExecuteAsync(
            new CreateChallengeRequest(me, new PlayerId(request.OpponentId), request.RulesetId, request.RulesetVersion, request.TotalGames), cancellationToken);

        return Ok(ToDto(view));
    }

    [HttpGet("{seriesId:guid}")]
    public async Task<ActionResult<SeriesResponseDto>> GetSeries(Guid seriesId, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var view = await getSeries.ExecuteAsync(new GetSeriesRequest(me, new VersusSeriesId(seriesId)), cancellationToken);
        return Ok(ToDto(view));
    }

    [HttpGet("incoming")]
    public async Task<ActionResult<IReadOnlyList<SeriesSummaryDto>>> ListIncoming(CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var series = await listIncoming.ExecuteAsync(me, cancellationToken);
        return Ok(series.Select(ToDto));
    }

    [HttpGet("outgoing")]
    public async Task<ActionResult<IReadOnlyList<SeriesSummaryDto>>> ListOutgoing(CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var series = await listOutgoing.ExecuteAsync(me, cancellationToken);
        return Ok(series.Select(ToDto));
    }

    [HttpGet("active")]
    public async Task<ActionResult<IReadOnlyList<SeriesSummaryDto>>> ListActive(CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var series = await listActive.ExecuteAsync(me, cancellationToken);
        return Ok(series.Select(ToDto));
    }

    [HttpPost("{seriesId:guid}/accept")]
    public async Task<ActionResult<SeriesResponseDto>> Accept(Guid seriesId, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var view = await acceptChallenge.ExecuteAsync(new AcceptChallengeRequest(me, new VersusSeriesId(seriesId)), cancellationToken);
        return Ok(ToDto(view));
    }

    [HttpPost("{seriesId:guid}/decline")]
    public async Task<ActionResult<SeriesResponseDto>> Decline(Guid seriesId, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var view = await declineChallenge.ExecuteAsync(new DeclineChallengeRequest(me, new VersusSeriesId(seriesId)), cancellationToken);
        return Ok(ToDto(view));
    }

    [HttpPost("{seriesId:guid}/cancel")]
    public async Task<ActionResult<SeriesResponseDto>> Cancel(Guid seriesId, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var view = await cancelChallenge.ExecuteAsync(new CancelChallengeRequest(me, new VersusSeriesId(seriesId)), cancellationToken);
        return Ok(ToDto(view));
    }

    [HttpPost("{seriesId:guid}/games/{gameNumber:int}/attempts/start")]
    public async Task<ActionResult<AttemptStartedDto>> StartAttempt(Guid seriesId, int gameNumber, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var result = await startAttempt.ExecuteAsync(
            new StartAttemptRequest(me, new VersusSeriesId(seriesId), gameNumber), cancellationToken);

        return Ok(new AttemptStartedDto(result.AttemptId.Value, result.GameNumber));
    }

    [HttpPost("{seriesId:guid}/games/{gameNumber:int}/attempts/complete")]
    public async Task<ActionResult<SeriesResponseDto>> CompleteAttempt(
        Guid seriesId, int gameNumber, CompleteAttemptDto request, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var view = await completeAttempt.ExecuteAsync(
            new CompleteAttemptRequest(me, new VersusSeriesId(seriesId), gameNumber, request.Score), cancellationToken);

        return Ok(ToDto(view));
    }

    private static SeriesResponseDto ToDto(SeriesView view) => new(
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

    private static SeriesSummaryDto ToDto(SeriesSummary summary) => new(
        summary.Id.Value, summary.ChallengerId.Value, summary.OpponentId.Value, summary.Status.ToString(),
        summary.CurrentGameNumber, summary.TotalGames, summary.Revision, summary.CreatedAt);
}
