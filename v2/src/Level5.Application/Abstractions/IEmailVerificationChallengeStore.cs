using Level5.Domain.Identity;
using Level5.Domain.Ids;

namespace Level5.Application.Abstractions;

public interface IEmailVerificationChallengeStore
{
    /// <summary>
    /// Terminates an expired replacement-email challenge reserving <paramref name="targetEmail"/>.
    /// Initial-verification challenges remain untouched because their target is still the owning
    /// account's attached canonical email.
    /// </summary>
    Task ReleaseExpiredReplacementReservationAsync(
        Email targetEmail,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<EmailVerificationChallenge?> FindByAccountIdAsync(AccountId accountId, CancellationToken cancellationToken);

    Task<EmailVerificationChallenge?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    Task AddAsync(EmailVerificationChallenge challenge, CancellationToken cancellationToken);

    Task StageUpdateAsync(EmailVerificationChallenge challenge, CancellationToken cancellationToken);
}
