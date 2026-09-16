using Level5.Application.Abstractions;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.Persistence.Repositories;

public sealed class AccountStore(Level5V2DbContext db) : IAccountStore
{
    public async Task<Account?> FindByIdAsync(AccountId id, CancellationToken cancellationToken)
    {
        var row = await db.Accounts.SingleOrDefaultAsync(a => a.Id == id.Value, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task<Account?> FindByUsernameAsync(Username username, CancellationToken cancellationToken)
    {
        var row = await db.Accounts.SingleOrDefaultAsync(a => a.UsernameCanonical == username.Canonical, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public Task<bool> UsernameExistsAsync(Username username, CancellationToken cancellationToken)
        => db.Accounts.AnyAsync(a => a.UsernameCanonical == username.Canonical, cancellationToken);

    public async Task AddAsync(Account account, CancellationToken cancellationToken)
    {
        await db.Accounts.AddAsync(new AccountRow
        {
            Id = account.Id.Value,
            Username = account.Username.Value,
            UsernameCanonical = account.Username.Canonical,
            PasswordHash = account.PasswordHash,
            CreatedAt = account.CreatedAt
        }, cancellationToken);
    }

    public async Task UpdateAsync(Account account, CancellationToken cancellationToken)
    {
        var row = await db.Accounts.SingleAsync(a => a.Id == account.Id.Value, cancellationToken);
        row.PasswordHash = account.PasswordHash;
    }

    private static Account ToDomain(AccountRow row)
        => Account.Rehydrate(new AccountId(row.Id), Username.Create(row.Username), row.PasswordHash, row.CreatedAt);
}
