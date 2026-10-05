using Level5.Domain.Identity;
using Level5.Domain.Ids;

namespace Level5.Application.Abstractions;

public interface IPasswordResetChallengeStore
{
    Task<PasswordResetChallenge?> FindByAccountIdAsync(AccountId accountId, CancellationToken cancellationToken);
    Task<PasswordResetChallenge?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);
    Task AddAsync(PasswordResetChallenge challenge, CancellationToken cancellationToken);
    Task StageUpdateAsync(PasswordResetChallenge challenge, CancellationToken cancellationToken);
}
