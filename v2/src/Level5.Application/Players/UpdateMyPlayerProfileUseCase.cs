using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Ids;

namespace Level5.Application.Players;

public sealed record UpdateMyPlayerProfileRequest(AccountId AccountId, string DisplayName);

/// <summary>
/// Self-scoped: the profile being mutated is always the one owned by the authenticated
/// <see cref="AccountId"/>, never a target the client supplies - see
/// <see cref="Level5.Api.Controllers.PlayersController"/>. PlayerTag is deliberately not
/// accepted here; it is stable/immutable identity, not part of this update.
/// </summary>
public sealed class UpdateMyPlayerProfileUseCase(IPlayerProfileStore playerProfileStore, IUnitOfWork unitOfWork)
{
    public async Task<PublicPlayerProfile> ExecuteAsync(UpdateMyPlayerProfileRequest request, CancellationToken cancellationToken)
    {
        var profile = await playerProfileStore.FindByAccountIdAsync(request.AccountId, cancellationToken)
            ?? throw new NotFoundException("No player profile was found for the authenticated account.");

        profile.ChangeDisplayName(request.DisplayName);

        await playerProfileStore.UpdateAsync(profile, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new PublicPlayerProfile(profile.Id, profile.DisplayName, profile.Tag.Value);
    }
}
