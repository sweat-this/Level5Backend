using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Players;

namespace Level5.Application.Players;

/// <summary>
/// Assigns a free <see cref="PlayerTag"/> for a new profile by deriving a base handle from a
/// display name and retrying random 4-digit discriminators until an unused tag is found. Shared by
/// <see cref="Identity.RegisterAccountUseCase"/> (fresh signups) and the legacy account migration
/// tool (imported accounts) so both paths allocate tags identically.
/// </summary>
public sealed class PlayerTagAllocator(IPlayerProfileStore playerProfileStore)
{
    private const int MaxTagAssignmentAttempts = 10;

    public async Task<PlayerTag> AssignAvailableTagAsync(string displayName, CancellationToken cancellationToken)
    {
        var baseHandle = new string([.. displayName.Where(char.IsLetterOrDigit)]);
        if (baseHandle.Length < 2)
        {
            baseHandle = "PLAYER";
        }
        else if (baseHandle.Length > 20)
        {
            baseHandle = baseHandle[..20];
        }

        for (var attempt = 0; attempt < MaxTagAssignmentAttempts; attempt++)
        {
            var discriminator = Random.Shared.Next(1, 9999).ToString("D4");
            var candidate = PlayerTag.Create($"{baseHandle}#{discriminator}");

            if (!await playerProfileStore.TagExistsAsync(candidate, cancellationToken))
            {
                return candidate;
            }
        }

        throw new ConflictException("Could not assign a unique player tag. Please try again.");
    }
}
