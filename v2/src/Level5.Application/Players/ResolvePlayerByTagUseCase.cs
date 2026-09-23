using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Ids;
using Level5.Domain.Players;

namespace Level5.Application.Players;

public sealed record ResolvePlayerByTagRequest(string Tag);

public sealed record PublicPlayerProfile(PlayerId Id, string DisplayName, string Tag);

/// <summary>
/// Exact-match lookup only - deliberately does not support partial/prefix search, so a player
/// can only be found by someone who already knows their full tag.
/// </summary>
public sealed class ResolvePlayerByTagUseCase(IPlayerProfileStore playerProfileStore)
{
    public async Task<PublicPlayerProfile> ExecuteAsync(ResolvePlayerByTagRequest request, CancellationToken cancellationToken)
    {
        var tag = PlayerTag.Create(request.Tag);
        var profile = await playerProfileStore.FindByTagAsync(tag, cancellationToken)
            ?? throw new NotFoundException("No player with that tag was found.");

        return new PublicPlayerProfile(profile.Id, profile.DisplayName, profile.Tag.Value);
    }
}
