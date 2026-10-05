using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Identity;
using Level5.Domain.Ids;

namespace Level5.Application.Identity;

public sealed record ResendEmailVerificationResult(DateTimeOffset ExpiresAt);

public sealed class ResendEmailVerificationUseCase(
    IAccountStore accountStore,
    IEmailVerificationChallengeStore challengeStore,
    IEmailVerificationTokenGenerator tokenGenerator,
    IEmailVerificationPolicy policy,
    IEmailVerificationDelivery delivery,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<ResendEmailVerificationResult> ExecuteAsync(
        AccountId accountId,
        CancellationToken cancellationToken)
    {
        var account = await accountStore.FindByIdAsync(accountId, cancellationToken)
            ?? throw new NotFoundException("Account not found.");

        if (account.Status != AccountStatus.Active)
        {
            throw new AccountSecurityActionForbiddenException();
        }

        if (account.Email is null || account.EmailVerifiedAt is not null)
        {
            throw new EmailVerificationNotAvailableException();
        }

        var now = clock.UtcNow;
        var challenge = await challengeStore.FindByAccountIdAsync(account.Id, cancellationToken);
        if (challenge is not null && now < challenge.IssuedAt + policy.ResendCooldown)
        {
            throw new EmailVerificationCooldownException();
        }

        var token = tokenGenerator.Generate();
        if (challenge is null)
        {
            challenge = EmailVerificationChallenge.Create(
                account.Id, account.Email, token.Hash, now, policy.TokenLifetime);
            await challengeStore.AddAsync(challenge, cancellationToken);
        }
        else
        {
            challenge.Rotate(account.Email, token.Hash, now, policy.TokenLifetime);
            await challengeStore.StageUpdateAsync(challenge, cancellationToken);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConflictException)
        {
            // A concurrent resend already rotated this account's one challenge.
            throw new EmailVerificationCooldownException();
        }

        await delivery.DeliverAsync(account.Email, token.RawValue, challenge.ExpiresAt, cancellationToken);
        return new ResendEmailVerificationResult(challenge.ExpiresAt);
    }
}
