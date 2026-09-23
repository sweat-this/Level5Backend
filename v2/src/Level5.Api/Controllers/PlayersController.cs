using Level5.Api.Security;
using Level5.Application.Players;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Level5.Api.Controllers;

public sealed record PlayerProfileResponseDto(Guid PlayerId, string DisplayName, string Tag);

/// <summary>
/// Only DisplayName is bindable here - deliberately no PlayerId/AccountId/Tag fields, so there is
/// nothing a client can supply to redirect an update to another profile or change stable identity;
/// any such fields in the raw request body are silently ignored by model binding.
/// </summary>
public sealed record UpdatePlayerProfileRequestDto(string DisplayName);

[ApiController]
[Route("api/v2/players")]
[Authorize]
// Lookup/self-view/self-update are one cohesive players resource, consistent with the
// thin-controller/one-use-case-per-action pattern used throughout this API (see AuthController).
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S6960", Justification = "Lookup/self-view/self-update are one cohesive players resource, consistent with the thin-controller/one-use-case-per-action pattern used throughout this API.")]
public sealed class PlayersController(
    ResolvePlayerByTagUseCase resolvePlayerByTag,
    UpdateMyPlayerProfileUseCase updateMyPlayerProfile,
    GetMyPlayerProfileUseCase getMyPlayerProfile,
    ICurrentPlayerProvider currentPlayer,
    ICurrentAccountAccessor currentAccount) : ControllerBase
{
    [HttpGet("by-tag/{tag}")]
    public async Task<ActionResult<PlayerProfileResponseDto>> GetByTag(string tag, CancellationToken cancellationToken)
    {
        var profile = await resolvePlayerByTag.ExecuteAsync(new ResolvePlayerByTagRequest(tag), cancellationToken);
        return Ok(new PlayerProfileResponseDto(profile.Id.Value, profile.DisplayName, profile.Tag));
    }

    // Preserved unchanged - the Unity client already depends on this bare-GUID contract (see
    // PlayersApiClient.GetMe() => ApiResponse<Guid>). GetMyProfile below is the additive,
    // richer-shaped sibling for the web portal; this endpoint is deliberately untouched.
    [HttpGet("me")]
    public async Task<ActionResult<Guid>> GetMyPlayerId(CancellationToken cancellationToken)
    {
        var playerId = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        return Ok(playerId.Value);
    }

    // Additive self-profile read (issue #7): the web portal's /account/profile needs its own
    // Display Name and Tag alongside PlayerId, and GET /me above can't grow that shape without
    // breaking the Unity client's existing Guid contract.
    [HttpGet("me/profile")]
    public async Task<ActionResult<PlayerProfileResponseDto>> GetMyProfile(CancellationToken cancellationToken)
    {
        var accountId = currentAccount.GetCurrentAccountId();
        var profile = await getMyPlayerProfile.ExecuteAsync(new GetMyPlayerProfileRequest(accountId), cancellationToken);
        return Ok(new PlayerProfileResponseDto(profile.Id.Value, profile.DisplayName, profile.Tag));
    }

    // The profile updated is always the authenticated account's own - derived from the JWT `sub`
    // claim via ICurrentAccountAccessor, never from anything in the request body/route. See
    // UpdatePlayerProfileRequestDto for why overposting another player's identity isn't possible.
    [HttpPatch("me")]
    public async Task<ActionResult<PlayerProfileResponseDto>> UpdateMyProfile(UpdatePlayerProfileRequestDto request, CancellationToken cancellationToken)
    {
        var accountId = currentAccount.GetCurrentAccountId();
        var profile = await updateMyPlayerProfile.ExecuteAsync(
            new UpdateMyPlayerProfileRequest(accountId, request.DisplayName), cancellationToken);

        return Ok(new PlayerProfileResponseDto(profile.Id.Value, profile.DisplayName, profile.Tag));
    }
}
