using Level5.Application.Abstractions;
using Level5.Domain.Ids;
using Level5.Domain.Players;

namespace Level5.Application.Tests.Fakes;

public sealed class InMemoryPlayerProfileStore : IPlayerProfileStore
{
    private readonly Dictionary<Guid, PlayerProfile> _profiles = [];

    public Task<PlayerProfile?> FindByIdAsync(PlayerId id, CancellationToken cancellationToken)
        => Task.FromResult(_profiles.GetValueOrDefault(id.Value));

    public Task<PlayerProfile?> FindByAccountIdAsync(AccountId accountId, CancellationToken cancellationToken)
        => Task.FromResult(_profiles.Values.SingleOrDefault(p => p.AccountId == accountId));

    public Task<PlayerProfile?> FindByTagAsync(PlayerTag tag, CancellationToken cancellationToken)
        => Task.FromResult(_profiles.Values.SingleOrDefault(p => p.Tag.Equals(tag)));

    public Task<IReadOnlyList<PlayerProfile>> FindByIdsAsync(IReadOnlyCollection<PlayerId> ids, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<PlayerProfile>>([.. _profiles.Values.Where(p => ids.Contains(p.Id))]);

    public Task<bool> TagExistsAsync(PlayerTag tag, CancellationToken cancellationToken)
        => Task.FromResult(_profiles.Values.Any(p => p.Tag.Equals(tag)));

    public Task AddAsync(PlayerProfile profile, CancellationToken cancellationToken)
    {
        _profiles[profile.Id.Value] = profile;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(PlayerProfile profile, CancellationToken cancellationToken)
    {
        _profiles[profile.Id.Value] = profile;
        return Task.CompletedTask;
    }
}
