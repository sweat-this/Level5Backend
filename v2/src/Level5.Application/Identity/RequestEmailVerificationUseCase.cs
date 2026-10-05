using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Identity;
using Level5.Domain.Ids;

namespace Level5.Application.Identity;

public sealed record RequestEmailVerificationRequest(AccountId AccountId, string CurrentPassword, string Email);

public sealed record RequestEmailVerificationResult(bool AlreadyVerified, DateTimeOffset? ExpiresAt);

[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107", Justification = "This use case coordinates the narrow account, challenge, credential, policy, delivery, clock, and atomic persistence ports required by one security operation.")]
public sealed class RequestEmailVerificationUseCase(
    IAccountStore accountStore,
    IEmailVerificationChallengeStore challengeStore,
    IPasswordHasher passwordHasher,
    IEmailVerificationTokenGenerator tokenGenerator,
    IEmailVerificationPolicy policy,
    IEmailVerificationDelivery delivery,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<RequestEmailVerificationResult> ExecuteAsync(
        RequestEmailVerificationRequest request,
        CancellationToken cancellationToken)
    {
        var account = await accountStore.FindByIdAsync(request.AccountId, cancellationToken)
            ?? throw new NotFoundException("Account not found.");

        if (account.Status != AccountStatus.Active)
        {
            throw new AccountSecurityActionForbiddenException();
        }

        if (passwordHasher.Verify(account.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            throw new CurrentPasswordInvalidException();
        }

        var targetEmail = Email.Create(request.Email);
        if (!account.AttachOrReplaceUnverifiedEmail(targetEmail))
        {
            return new RequestEmailVerificationResult(AlreadyVerified: true, ExpiresAt: null);
        }

        var now = clock.UtcNow;
        var token = tokenGenerator.Generate();
        var challenge = await challengeStore.FindByAccountIdAsync(account.Id, cancellationToken);
        if (challenge is null)
        {
            challenge = EmailVerificationChallenge.Create(account.Id, targetEmail, token.Hash, now, policy.TokenLifetime);
            await challengeStore.AddAsync(challenge, cancellationToken);
        }
        else
        {
            challenge.Rotate(targetEmail, token.Hash, now, policy.TokenLifetime);
            await challengeStore.StageUpdateAsync(challenge, cancellationToken);
        }

        await accountStore.StageEmailUpdateAsync(account, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConflictException)
        {
            throw new EmailUnavailableException();
        }

        await delivery.DeliverAsync(targetEmail, token.RawValue, challenge.ExpiresAt, cancellationToken);
        return new RequestEmailVerificationResult(AlreadyVerified: false, challenge.ExpiresAt);
    }
}
