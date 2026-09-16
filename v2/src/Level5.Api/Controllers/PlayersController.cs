using Level5.Api.Security;
using Level5.Application.Players;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Level5.Api.Controllers;

public sealed record PlayerProfileResponseDto(Guid PlayerId, string DisplayName, string Tag);

[ApiController]
[Route("api/v2/players")]
[Authorize]
public sealed class PlayersController(ResolvePlayerByTagUseCase resolvePlayerByTag, ICurrentPlayerProvider currentPlayer) : ControllerBase
{
    [HttpGet("by-tag/{tag}")]
    public async Task<ActionResult<PlayerProfileResponseDto>> GetByTag(string tag, CancellationToken cancellationToken)
    {
        var profile = await resolvePlayerByTag.ExecuteAsync(new ResolvePlayerByTagRequest(tag), cancellationToken);
        return Ok(new PlayerProfileResponseDto(profile.Id.Value, profile.DisplayName, profile.Tag));
    }

    [HttpGet("me")]
    public async Task<ActionResult<Guid>> GetMyPlayerId(CancellationToken cancellationToken)
    {
        var playerId = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        return Ok(playerId.Value);
    }
}
