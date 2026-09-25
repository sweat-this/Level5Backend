using Level5.Application.Abstractions;
using Level5.Domain.Ids;
using Level5.Domain.Migration;
using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.Persistence.Repositories;

public sealed class LegacyAccountLinkStore(Level5V2DbContext db) : ILegacyAccountLinkStore
{
    public async Task<LegacyAccountLink?> FindByLegacyUserIdAsync(int legacyUserId, CancellationToken cancellationToken)
    {
        var row = await db.LegacyAccountLinks.SingleOrDefaultAsync(l => l.LegacyUserId == legacyUserId, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public Task<bool> ExistsByLegacyUserIdAsync(int legacyUserId, CancellationToken cancellationToken)
        => db.LegacyAccountLinks.AnyAsync(l => l.LegacyUserId == legacyUserId, cancellationToken);

    public Task<bool> ExistsForAccountIdAsync(AccountId accountId, CancellationToken cancellationToken)
        => db.LegacyAccountLinks.AnyAsync(l => l.AccountId == accountId.Value, cancellationToken);

    public async Task AddAsync(LegacyAccountLink link, CancellationToken cancellationToken)
    {
        await db.LegacyAccountLinks.AddAsync(new LegacyAccountLinkRow
        {
            LegacyUserId = link.LegacyUserId,
            AccountId = link.AccountId.Value,
            PlayerId = link.PlayerId.Value,
            LegacyUsername = link.LegacyUsername,
            MigratedAt = link.MigratedAt
        }, cancellationToken);
    }

    private static LegacyAccountLink ToDomain(LegacyAccountLinkRow row)
        => new(row.LegacyUserId, new AccountId(row.AccountId), new PlayerId(row.PlayerId), row.LegacyUsername, row.MigratedAt);
}
