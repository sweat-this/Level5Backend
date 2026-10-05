using Level5.Domain.Identity;
using Level5.Domain.Ids;

namespace Level5.Application.Abstractions;

public interface IAccountStore
{
    Task<Account?> FindByIdAsync(AccountId id, CancellationToken cancellationToken);

    Task<Account?> FindByUsernameAsync(Username username, CancellationToken cancellationToken);

    Task<bool> UsernameExistsAsync(Username username, CancellationToken cancellationToken);

    Task AddAsync(Account account, CancellationToken cancellationToken);

    /// <summary>Stages only password/status changes made to a previously-loaded account.</summary>
    Task UpdateAsync(Account account, CancellationToken cancellationToken);

    /// <summary>Stages only private email and verification fields for the next unit-of-work commit.</summary>
    Task StageEmailUpdateAsync(Account account, CancellationToken cancellationToken);
}
