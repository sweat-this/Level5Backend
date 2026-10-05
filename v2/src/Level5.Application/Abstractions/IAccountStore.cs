using Level5.Domain.Identity;
using Level5.Domain.Ids;

namespace Level5.Application.Abstractions;

public interface IAccountStore
{
    Task<Account?> FindByIdAsync(AccountId id, CancellationToken cancellationToken);

    Task<Account?> FindByUsernameAsync(Username username, CancellationToken cancellationToken);

    /// <summary>Private recovery lookup; returns only an account whose canonical email is verified.</summary>
    Task<Account?> FindByVerifiedEmailAsync(Email email, CancellationToken cancellationToken);

    Task<bool> UsernameExistsAsync(Username username, CancellationToken cancellationToken);

    Task AddAsync(Account account, CancellationToken cancellationToken);

    /// <summary>Stages only password hash and session generation, conditionally on the loaded generation.</summary>
    Task StageCredentialUpdateAsync(Account account, long expectedSessionGeneration, CancellationToken cancellationToken);

    /// <summary>Stages only private email and verification fields for the next unit-of-work commit.</summary>
    Task StageEmailUpdateAsync(Account account, CancellationToken cancellationToken);
}
