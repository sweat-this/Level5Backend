using Level5.Domain.Ids;
using Level5.Domain.Players;

namespace Level5.Application.Abstractions;

public interface IPlayerProfileStore
{
    Task<PlayerProfile?> FindByIdAsync(PlayerId id, CancellationToken cancellationToken);

    /// <summary>Batch lookup so callers resolving a list of players (e.g. a friends list) don't issue one query per id.</summary>
    Task<IReadOnlyList<PlayerProfile>> FindByIdsAsync(IReadOnlyCollection<PlayerId> ids, CancellationToken cancellationToken);

    Task<PlayerProfile?> FindByAccountIdAsync(AccountId accountId, CancellationToken cancellationToken);

    Task<PlayerProfile?> FindByTagAsync(PlayerTag tag, CancellationToken cancellationToken);

    Task<bool> TagExistsAsync(PlayerTag tag, CancellationToken cancellationToken);

    Task AddAsync(PlayerProfile profile, CancellationToken cancellationToken);
}
