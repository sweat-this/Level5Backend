using Level5.Domain.Ids;
using Level5.Domain.Migration;

namespace Level5.Application.Abstractions;

/// <summary>Append-only provenance store for the legacy account migration tool. No Update/Delete: a link is never edited once created.</summary>
public interface ILegacyAccountLinkStore
{
    Task<LegacyAccountLink?> FindByLegacyUserIdAsync(int legacyUserId, CancellationToken cancellationToken);

    Task<bool> ExistsByLegacyUserIdAsync(int legacyUserId, CancellationToken cancellationToken);

    /// <summary>Whether the given account was itself created by this migration tool - used to give a more precise reason when a different legacy row collides with it.</summary>
    Task<bool> ExistsForAccountIdAsync(AccountId accountId, CancellationToken cancellationToken);

    Task AddAsync(LegacyAccountLink link, CancellationToken cancellationToken);
}
