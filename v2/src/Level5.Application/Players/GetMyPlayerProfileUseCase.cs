using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Ids;

namespace Level5.Application.Players;

public sealed record GetMyPlayerProfileRequest(AccountId AccountId);

/// <summary>
/// Self-scoped read: the profile returned is always the one owned by the authenticated
/// <see cref="AccountId"/>, never a target the client supplies - see
/// <see cref="Level5.Api.Controllers.PlayersController"/>. Additive to (not a replacement for)
/// GET /api/v2/players/me, which the Unity client already depends on for its bare-GUID contract.
/// </summary>
public sealed class GetMyPlayerProfileUseCase(IPlayerProfileStore playerProfileStore)
{
    public async Task<PublicPlayerProfile> ExecuteAsync(GetMyPlayerProfileRequest request, CancellationToken cancellationToken)
    {
        var profile = await playerProfileStore.FindByAccountIdAsync(request.AccountId, cancellationToken)
            ?? throw new NotFoundException("No player profile was found for the authenticated account.");

        return new PublicPlayerProfile(profile.Id, profile.DisplayName, profile.Tag.Value);
    }
}
