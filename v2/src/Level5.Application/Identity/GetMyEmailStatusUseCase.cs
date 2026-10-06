using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Ids;

namespace Level5.Application.Identity;

public sealed record MyEmailStatus(
    string? Email,
    bool IsVerified,
    DateTimeOffset? VerifiedAt,
    string? PendingEmail,
    DateTimeOffset? PendingEmailChangeExpiresAt,
    bool PendingEmailChangeExpired);

public sealed class GetMyEmailStatusUseCase(
    IAccountStore accountStore,
    IEmailVerificationChallengeStore challengeStore,
    IClock clock)
{
    public async Task<MyEmailStatus> ExecuteAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        var account = await accountStore.FindByIdAsync(accountId, cancellationToken)
            ?? throw new NotFoundException("Account not found.");

        var challenge = await challengeStore.FindByAccountIdAsync(accountId, cancellationToken);
        var hasPendingReplacement =
            account.Email is not null &&
            account.EmailVerifiedAt is not null &&
            challenge is { ConsumedAt: null } &&
            !challenge.TargetEmail.Equals(account.Email);

        return new MyEmailStatus(
            account.Email?.Value,
            account.EmailVerifiedAt is not null,
            account.EmailVerifiedAt,
            hasPendingReplacement ? challenge!.TargetEmail.Value : null,
            hasPendingReplacement ? challenge!.ExpiresAt : null,
            hasPendingReplacement && clock.UtcNow >= challenge!.ExpiresAt);
    }
}
