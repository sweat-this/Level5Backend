using Level5.Api.Security;
using Level5.Application.Platform;
using Level5.Domain.Ids;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Level5.Api.Controllers;

public sealed record NotificationDto(
    Guid Id,
    string Source,
    string Kind,
    string Title,
    string? Body,
    string? ActionPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

public sealed record NotificationPageDto(IReadOnlyList<NotificationDto> Items, string? NextCursor);

public sealed record SetNotificationReadStateDto
{
    public required bool IsRead { get; init; }
}

[ApiController]
[Route("api/v2/platform/me/notifications")]
[Authorize]
public sealed class NotificationsController(
    ListNotificationsUseCase listNotifications,
    SetNotificationReadStateUseCase setReadState,
    ICurrentPlayerProvider currentPlayer) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<NotificationPageDto>> List(
        [FromQuery] int? limit,
        [FromQuery] string? cursor,
        CancellationToken cancellationToken)
    {
        var playerId = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var page = await listNotifications.ExecuteAsync(
            new ListNotificationsRequest(playerId, limit, cursor), cancellationToken);

        return Ok(new NotificationPageDto([.. page.Items.Select(ToDto)], page.NextCursor));
    }

    [HttpPatch("{notificationId:guid}")]
    public async Task<IActionResult> SetReadState(
        Guid notificationId,
        SetNotificationReadStateDto request,
        CancellationToken cancellationToken)
    {
        var playerId = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        await setReadState.ExecuteAsync(
            new SetNotificationReadStateRequest(playerId, new NotificationId(notificationId), request.IsRead),
            cancellationToken);
        return NoContent();
    }

    private static NotificationDto ToDto(NotificationView notification) => new(
        notification.Id.Value,
        notification.Source,
        notification.Kind,
        notification.Title,
        notification.Body,
        notification.ActionPath,
        notification.CreatedAt,
        notification.ReadAt);
}
