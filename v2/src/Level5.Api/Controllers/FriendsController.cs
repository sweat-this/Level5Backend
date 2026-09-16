using Level5.Api.Security;
using Level5.Application.Social;
using Level5.Domain.Ids;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Level5.Api.Controllers;

public sealed record SendFriendRequestDto(Guid ToPlayerId);

public sealed record FriendRequestResponseDto(Guid Id, Guid FromPlayerId, Guid ToPlayerId, string Status);

public sealed record FriendSummaryDto(Guid PlayerId, string DisplayName, string Tag, DateTimeOffset FriendsSince);

[ApiController]
[Route("api/v2/friends")]
[Authorize]
public sealed class FriendsController(
    SendFriendRequestUseCase sendFriendRequest,
    AcceptFriendRequestUseCase acceptFriendRequest,
    DeclineFriendRequestUseCase declineFriendRequest,
    CancelFriendRequestUseCase cancelFriendRequest,
    RemoveFriendUseCase removeFriend,
    ListFriendsUseCase listFriends,
    ListIncomingFriendRequestsUseCase listIncoming,
    ListOutgoingFriendRequestsUseCase listOutgoing,
    ICurrentPlayerProvider currentPlayer) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<FriendSummaryDto>>> ListFriends(CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var friends = await listFriends.ExecuteAsync(me, cancellationToken);
        return Ok(friends.Select(f => new FriendSummaryDto(f.PlayerId.Value, f.DisplayName, f.Tag, f.FriendsSince)));
    }

    [HttpDelete("{playerId:guid}")]
    public async Task<IActionResult> RemoveFriend(Guid playerId, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        await removeFriend.ExecuteAsync(new RemoveFriendRequest(me, new PlayerId(playerId)), cancellationToken);
        return NoContent();
    }

    [HttpPost("requests")]
    public async Task<ActionResult<FriendRequestResponseDto>> SendRequest(SendFriendRequestDto request, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var result = await sendFriendRequest.ExecuteAsync(
            new SendFriendRequestRequest(me, new PlayerId(request.ToPlayerId)), cancellationToken);

        return Ok(new FriendRequestResponseDto(result.Id.Value, result.FromPlayerId.Value, result.ToPlayerId.Value, result.Status.ToString()));
    }

    [HttpGet("requests/incoming")]
    public async Task<ActionResult<IReadOnlyList<FriendRequestResponseDto>>> ListIncoming(CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var requests = await listIncoming.ExecuteAsync(me, cancellationToken);
        return Ok(requests.Select(r => new FriendRequestResponseDto(r.Id.Value, r.FromPlayerId.Value, r.ToPlayerId.Value, r.Status.ToString())));
    }

    [HttpGet("requests/outgoing")]
    public async Task<ActionResult<IReadOnlyList<FriendRequestResponseDto>>> ListOutgoing(CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var requests = await listOutgoing.ExecuteAsync(me, cancellationToken);
        return Ok(requests.Select(r => new FriendRequestResponseDto(r.Id.Value, r.FromPlayerId.Value, r.ToPlayerId.Value, r.Status.ToString())));
    }

    [HttpPost("requests/{requestId:guid}/accept")]
    public async Task<IActionResult> Accept(Guid requestId, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        await acceptFriendRequest.ExecuteAsync(new AcceptFriendRequestRequest(me, new FriendRequestId(requestId)), cancellationToken);
        return NoContent();
    }

    [HttpPost("requests/{requestId:guid}/decline")]
    public async Task<IActionResult> Decline(Guid requestId, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        await declineFriendRequest.ExecuteAsync(new DeclineFriendRequestRequest(me, new FriendRequestId(requestId)), cancellationToken);
        return NoContent();
    }

    [HttpPost("requests/{requestId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid requestId, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        await cancelFriendRequest.ExecuteAsync(new CancelFriendRequestRequest(me, new FriendRequestId(requestId)), cancellationToken);
        return NoContent();
    }
}
