using Level5.Domain.Identity;
using Level5.Domain.Ids;

namespace Level5.Application.Abstractions;

public interface IAccountStore
{
    Task<Account?> FindByIdAsync(AccountId id, CancellationToken cancellationToken);

    Task<Account?> FindByUsernameAsync(Username username, CancellationToken cancellationToken);

    Task<bool> UsernameExistsAsync(Username username, CancellationToken cancellationToken);

    Task<bool> EmailExistsAsync(Email email, CancellationToken cancellationToken);

    Task AddAsync(Account account, CancellationToken cancellationToken);

    /// <summary>Writes back changes made to a previously-loaded account (e.g. a rehashed password).</summary>
    Task UpdateAsync(Account account, CancellationToken cancellationToken);
}
