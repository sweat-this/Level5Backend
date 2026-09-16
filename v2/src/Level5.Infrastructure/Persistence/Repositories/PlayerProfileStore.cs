using Level5.Application.Abstractions;
using Level5.Domain.Ids;
using Level5.Domain.Players;
using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.Persistence.Repositories;

public sealed class PlayerProfileStore(Level5V2DbContext db) : IPlayerProfileStore
{
    public async Task<PlayerProfile?> FindByIdAsync(PlayerId id, CancellationToken cancellationToken)
    {
        var row = await db.PlayerProfiles.SingleOrDefaultAsync(p => p.Id == id.Value, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task<PlayerProfile?> FindByAccountIdAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        var row = await db.PlayerProfiles.SingleOrDefaultAsync(p => p.AccountId == accountId.Value, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task<PlayerProfile?> FindByTagAsync(PlayerTag tag, CancellationToken cancellationToken)
    {
        var row = await db.PlayerProfiles.SingleOrDefaultAsync(p => p.Tag == tag.Value, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyList<PlayerProfile>> FindByIdsAsync(IReadOnlyCollection<PlayerId> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var idValues = ids.Select(id => id.Value).ToArray();
        var rows = await db.PlayerProfiles.Where(p => idValues.Contains(p.Id)).ToListAsync(cancellationToken);
        return [.. rows.Select(ToDomain)];
    }

    public Task<bool> TagExistsAsync(PlayerTag tag, CancellationToken cancellationToken)
        => db.PlayerProfiles.AnyAsync(p => p.Tag == tag.Value, cancellationToken);

    public async Task AddAsync(PlayerProfile profile, CancellationToken cancellationToken)
    {
        await db.PlayerProfiles.AddAsync(new PlayerProfileRow
        {
            Id = profile.Id.Value,
            AccountId = profile.AccountId.Value,
            DisplayName = profile.DisplayName,
            Tag = profile.Tag.Value,
            CreatedAt = profile.CreatedAt
        }, cancellationToken);
    }

    private static PlayerProfile ToDomain(PlayerProfileRow row)
        => PlayerProfile.Rehydrate(new PlayerId(row.Id), new AccountId(row.AccountId), row.DisplayName, PlayerTag.Create(row.Tag), row.CreatedAt);
}
