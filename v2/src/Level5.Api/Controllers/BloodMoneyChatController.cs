using System.Text.Json.Serialization;
using Level5.Api.Security;
using Level5.Application.BloodMoney;
using Level5.Domain.BloodMoney;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Level5.Api.Controllers;

public sealed record BloodMoneyChatMessageDto(Guid MessageId, Guid ChallengeId, long Sequence, Guid SenderPlayerId,
    Guid ClientMessageId, string? Body, DateTimeOffset CreatedAt,
    [property: JsonConverter(typeof(JsonStringEnumConverter<BloodMoneyChatVisibility>))] BloodMoneyChatVisibility Visibility);
public sealed record BloodMoneyChatPageDto(IReadOnlyList<BloodMoneyChatMessageDto> Items, string? OlderCursor,
    string ResumeCursor, long LatestSequence, long LastReadSequence, bool IsReadOnly, bool NotificationsMuted);
public sealed record SendBloodMoneyChatMessageDto
{
    public required Guid ClientMessageId { get; init; }
    public required string Body { get; init; }
}
public sealed record BloodMoneyChatReadPositionDto
{
    public required long LastReadSequence { get; init; }
}
public sealed record BloodMoneyChatNotificationsMutedDto
{
    public required bool NotificationsMuted { get; init; }
}
public sealed record ReportBloodMoneyChatMessageDto
{
    public required Guid MessageId { get; init; }
    [JsonConverter(typeof(BloodMoneyChatReportReasonConverter))]
    public required BloodMoneyChatReportReason Reason { get; init; }
}
public sealed record BloodMoneyChatReportAcceptanceDto(Guid ReportId);

[ApiController]
[Authorize]
[ServiceFilter(typeof(BloodMoneyChatEligibilityFilter))]
[Route("api/v2/games/blood-money/challenges/{challengeId:guid}")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
public sealed class BloodMoneyChatController(ICurrentPlayerProvider currentPlayer,
    ListBloodMoneyChallengeMessagesUseCase list, SendBloodMoneyChallengeMessageUseCase send,
    AdvanceBloodMoneyChatReadPositionUseCase advanceRead, SetBloodMoneyChatNotificationsMutedUseCase setMuted,
    ReportBloodMoneyChatMessageUseCase report) : ControllerBase
{
    [HttpGet("messages")]
    [ProducesResponseType(typeof(BloodMoneyChatPageDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<BloodMoneyChatPageDto>> List(Guid challengeId, [FromQuery] string? limit,
        [FromQuery] string? cursor, CancellationToken cancellationToken)
    {
        var actor = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        // Preserve empty/repeated query inputs for validation after resource authorization.
        if (Request.Query.ContainsKey("limit") && (limit is null || Request.Query["limit"].Count != 1)) limit = "";
        if (Request.Query.ContainsKey("cursor") && (cursor is null || Request.Query["cursor"].Count != 1)) cursor = "";
        var page = await list.ExecuteAsync(new(new(challengeId), actor, limit, cursor), cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return Ok(new BloodMoneyChatPageDto([.. page.Items.Select(ToDto)], page.OlderCursor, page.ResumeCursor,
            page.LatestSequence, page.LastReadSequence, page.IsReadOnly, page.NotificationsMuted));
    }

    [HttpPost("messages")]
    [ProducesResponseType(typeof(BloodMoneyChatMessageDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(BloodMoneyChatMessageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<BloodMoneyChatMessageDto>> Send(Guid challengeId, SendBloodMoneyChatMessageDto request,
        CancellationToken cancellationToken)
    {
        var actor = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var result = await send.ExecuteAsync(new(new(challengeId), actor, request.ClientMessageId, request.Body), cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return StatusCode(result.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK, ToDto(result.Message));
    }

    [HttpPut("read-position")]
    [ProducesResponseType(typeof(BloodMoneyChatReadPositionDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<BloodMoneyChatReadPositionDto>> AdvanceRead(Guid challengeId,
        BloodMoneyChatReadPositionDto request, CancellationToken cancellationToken)
    {
        var actor = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var state = await advanceRead.ExecuteAsync(new(challengeId), actor, request.LastReadSequence, cancellationToken);
        return Ok(new BloodMoneyChatReadPositionDto { LastReadSequence = state.LastReadSequence });
    }

    [HttpPut("notifications-muted")]
    [ProducesResponseType(typeof(BloodMoneyChatNotificationsMutedDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<BloodMoneyChatNotificationsMutedDto>> SetMuted(Guid challengeId,
        BloodMoneyChatNotificationsMutedDto request, CancellationToken cancellationToken)
    {
        var actor = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var state = await setMuted.ExecuteAsync(new(challengeId), actor, request.NotificationsMuted, cancellationToken);
        return Ok(new BloodMoneyChatNotificationsMutedDto { NotificationsMuted = state.NotificationsMuted });
    }

    [HttpPost("message-reports")]
    [ProducesResponseType(typeof(BloodMoneyChatReportAcceptanceDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BloodMoneyChatReportAcceptanceDto>> Report(Guid challengeId,
        ReportBloodMoneyChatMessageDto request, CancellationToken cancellationToken)
    {
        var actor = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var result = await report.ExecuteAsync(new(new(challengeId), actor, request.MessageId, request.Reason), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, new BloodMoneyChatReportAcceptanceDto(result.ReportId));
    }

    private static BloodMoneyChatMessageDto ToDto(BloodMoneyChatMessageView message) => new(message.MessageId,
        message.ChallengeId.Value, message.Sequence, message.SenderPlayerId.Value, message.ClientMessageId,
        message.Body, message.CreatedAt, message.Visibility);
}
