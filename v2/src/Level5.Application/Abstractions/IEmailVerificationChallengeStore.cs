using Level5.Domain.Identity;
using Level5.Domain.Ids;

namespace Level5.Application.Abstractions;

public interface IEmailVerificationChallengeStore
{
    Task<EmailVerificationChallenge?> FindByAccountIdAsync(AccountId accountId, CancellationToken cancellationToken);

    Task<EmailVerificationChallenge?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    Task AddAsync(EmailVerificationChallenge challenge, CancellationToken cancellationToken);

    Task StageUpdateAsync(EmailVerificationChallenge challenge, CancellationToken cancellationToken);
}
