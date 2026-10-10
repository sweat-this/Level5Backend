using System.Text.Json.Serialization;
using Level5.Api.Security;
using Level5.Application.BloodMoney;
using Level5.Domain.BloodMoney;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Level5.Api.Controllers;

public sealed record BloodMoneyChallengeParticipantDto(Guid PlayerId, int SeatIndex,
    [property: JsonConverter(typeof(JsonStringEnumConverter<BloodMoneyParticipantStatus>))] BloodMoneyParticipantStatus Status,
    DateTimeOffset? AcceptedAt);

public sealed record BloodMoneyChallengeDto(Guid ChallengeId, Guid CreatorPlayerId,
    [property: JsonConverter(typeof(JsonStringEnumConverter<BloodMoneyChallengeStatus>))] BloodMoneyChallengeStatus Status,
    long StakePerParticipant, string RulesetId, int RulesetVersion, DateTimeOffset CreatedAt,
    DateTimeOffset AcceptanceDeadlineAt, DateTimeOffset? ActivatedAt, DateTimeOffset? GameplayDeadlineAt,
    DateTimeOffset? TerminalAt, long Revision, IReadOnlyList<BloodMoneyChallengeParticipantDto> Participants);

[ApiController]
[Authorize]
[Route("api/v2/games/blood-money/challenges/{challengeId:guid}")]
public sealed class BloodMoneyChallengesController(ICurrentPlayerProvider currentPlayer,
    GetBloodMoneyChallengeUseCase get) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(BloodMoneyChallengeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(void), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<BloodMoneyChallengeDto>> Get(Guid challengeId, CancellationToken cancellationToken)
    {
        var actor = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var view = await get.ExecuteAsync(new(challengeId), actor, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return Ok(new BloodMoneyChallengeDto(view.ChallengeId.Value, view.CreatorPlayerId.Value, view.Status,
            view.StakePerParticipant, view.RulesetId, view.RulesetVersion, view.CreatedAt, view.AcceptanceDeadlineAt,
            view.ActivatedAt, view.GameplayDeadlineAt, view.TerminalAt, view.Revision,
            [.. view.Participants.Select(participant => new BloodMoneyChallengeParticipantDto(participant.PlayerId.Value,
                participant.SeatIndex, participant.Status, participant.AcceptedAt))]));
    }
}
