using Level5.Application.Abstractions;
using Level5.Domain.Ids;
using Level5.Domain.Players;

namespace Level5.LegacyAccountMigration.IntegrationTests;

/// <summary>Forces PlayerTagAllocator to exhaust every attempt deterministically, regardless of its random discriminator - used to test tag-collision/exhaustion handling without seeding thousands of rows.</summary>
internal sealed class AlwaysCollidingPlayerProfileStore : IPlayerProfileStore
{
    public Task<bool> TagExistsAsync(PlayerTag tag, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<PlayerProfile?> FindByIdAsync(PlayerId id, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<PlayerProfile>> FindByIdsAsync(IReadOnlyCollection<PlayerId> ids, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<PlayerProfile?> FindByAccountIdAsync(AccountId accountId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<PlayerProfile?> FindByTagAsync(PlayerTag tag, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task AddAsync(PlayerProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task UpdateAsync(PlayerProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
}
