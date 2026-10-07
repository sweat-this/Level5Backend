using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Application.Observability;
using Level5.Domain.Identity;
using Level5.Domain.Ids;

namespace Level5.Application.Identity;

public sealed record RequestEmailChangeRequest(AccountId AccountId, string CurrentPassword, string NewEmail);
public sealed record EmailChangeDispatchResult(string PendingEmail, DateTimeOffset ExpiresAt);

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Major Code Smell", "S107",
    Justification = "This security use case coordinates account/challenge/credential/policy/delivery/transaction ports without hiding them behind a facade.")]
public sealed class RequestEmailChangeUseCase(
    IAccountStore accountStore,
    IEmailVerificationChallengeStore challengeStore,
    IPasswordHasher passwordHasher,
    IEmailVerificationTokenGenerator tokenGenerator,
    IEmailVerificationPolicy policy,
    IEmailChangeDelivery delivery,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<EmailChangeDispatchResult> ExecuteAsync(
        RequestEmailChangeRequest request,
        CancellationToken cancellationToken)
    {
        var account = await LoadActiveVerifiedAccountAsync(accountStore, request.AccountId, cancellationToken);

        if (passwordHasher.Verify(account.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            throw new CurrentPasswordInvalidException();
        }

        var target = Email.Create(request.NewEmail);
        if (account.Email!.Equals(target))
        {
            throw new EmailAlreadyCurrentException();
        }

        if (await accountStore.EmailExistsAsync(target, cancellationToken))
        {
            throw new EmailUnavailableException();
        }

        var now = clock.UtcNow;
        await challengeStore.ReleaseExpiredReplacementReservationAsync(target, now, cancellationToken);

        var challenge = await challengeStore.FindByAccountIdAsync(account.Id, cancellationToken);
        if (challenge is not null &&
            (challenge.ConsumedAt is null || !challenge.TargetEmail.Equals(account.Email)) &&
            now < challenge.IssuedAt + policy.ResendCooldown)
        {
            throw new EmailChangeCooldownException();
        }

        var token = tokenGenerator.Generate();
        if (challenge is null)
        {
            challenge = EmailVerificationChallenge.Create(
                account.Id, target, token.Hash, now, policy.TokenLifetime);
            await challengeStore.AddAsync(challenge, cancellationToken);
        }
        else
        {
            challenge.Rotate(target, token.Hash, now, policy.TokenLifetime);
            await challengeStore.StageUpdateAsync(challenge, cancellationToken);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConflictException)
        {
            // Covers a concurrent challenge rotation and the canonical-target reservation losing
            // a race to another account. Do not reveal which account owns the candidate.
            throw new EmailUnavailableException();
        }

        await delivery.DeliverVerificationAsync(
            target, token.RawValue, challenge.ExpiresAt, cancellationToken);

        return new EmailChangeDispatchResult(target.Value, challenge.ExpiresAt);
    }

    internal static async Task<Account> LoadActiveVerifiedAccountAsync(
        IAccountStore accountStore,
        AccountId accountId,
        CancellationToken cancellationToken)
    {
        var account = await accountStore.FindByIdAsync(accountId, cancellationToken);
        if (account is null ||
            account.Status != AccountStatus.Active ||
            account.Email is null ||
            account.EmailVerifiedAt is null)
        {
            throw new EmailChangeNotAvailableException();
        }

        return account;
    }

    internal static bool IsPendingReplacement(Account account, EmailVerificationChallenge? challenge)
        => account.Email is not null &&
           account.EmailVerifiedAt is not null &&
           challenge is { ConsumedAt: null } &&
           !challenge.TargetEmail.Equals(account.Email);
}

public sealed class ResendEmailChangeUseCase(
    IAccountStore accountStore,
    IEmailVerificationChallengeStore challengeStore,
    IEmailVerificationTokenGenerator tokenGenerator,
    IEmailVerificationPolicy policy,
    IEmailChangeDelivery delivery,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<EmailChangeDispatchResult> ExecuteAsync(
        AccountId accountId,
        CancellationToken cancellationToken)
    {
        var account = await RequestEmailChangeUseCase.LoadActiveVerifiedAccountAsync(
            accountStore, accountId, cancellationToken);
        var challenge = await challengeStore.FindByAccountIdAsync(accountId, cancellationToken);
        if (!RequestEmailChangeUseCase.IsPendingReplacement(account, challenge))
        {
            throw new EmailChangeNotAvailableException();
        }

        var now = clock.UtcNow;
        if (now < challenge!.IssuedAt + policy.ResendCooldown)
        {
            throw new EmailChangeCooldownException();
        }

        var token = tokenGenerator.Generate();
        challenge.Rotate(challenge.TargetEmail, token.Hash, now, policy.TokenLifetime);
        await challengeStore.StageUpdateAsync(challenge, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConflictException)
        {
            throw new EmailChangeCooldownException();
        }

        await delivery.DeliverVerificationAsync(
            challenge.TargetEmail, token.RawValue, challenge.ExpiresAt, cancellationToken);

        return new EmailChangeDispatchResult(challenge.TargetEmail.Value, challenge.ExpiresAt);
    }
}

public sealed class CancelEmailChangeUseCase(
    IAccountStore accountStore,
    IEmailVerificationChallengeStore challengeStore,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task ExecuteAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        var account = await RequestEmailChangeUseCase.LoadActiveVerifiedAccountAsync(
            accountStore, accountId, cancellationToken);
        var challenge = await challengeStore.FindByAccountIdAsync(accountId, cancellationToken);
        if (!RequestEmailChangeUseCase.IsPendingReplacement(account, challenge))
        {
            return;
        }

        challenge!.Cancel(clock.UtcNow);
        await challengeStore.StageUpdateAsync(challenge, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

public sealed class CompleteEmailChangeUseCase(
    IEmailVerificationChallengeStore challengeStore,
    IEmailVerificationTokenGenerator tokenGenerator,
    IAccountStore accountStore,
    IEmailChangeDelivery delivery,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task ExecuteAsync(string rawToken, CancellationToken cancellationToken)
    {
        var challenge = await challengeStore.FindByTokenHashAsync(
            tokenGenerator.Hash(rawToken), cancellationToken);
        var now = clock.UtcNow;

        if (challenge is null || !challenge.CanComplete(now))
        {
            throw new InvalidEmailChangeException();
        }

        var account = await accountStore.FindByIdAsync(challenge.AccountId, cancellationToken);
        if (account is null ||
            account.Status != AccountStatus.Active ||
            !RequestEmailChangeUseCase.IsPendingReplacement(account, challenge))
        {
            throw new InvalidEmailChangeException();
        }

        var previousEmail = account.Email!;

        try
        {
            account.PromoteVerifiedEmail(challenge.TargetEmail, now);
            challenge.Consume(now);
            await accountStore.StageEmailUpdateAsync(account, cancellationToken);
            await challengeStore.StageUpdateAsync(challenge, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is EmailChangePromotionInvalidException
                                          or EmailVerificationChallengeInvalidException
                                          or ConflictException)
        {
            throw new InvalidEmailChangeException();
        }

        // Promotion is already committed. External notification failure cannot roll the account
        // backward or turn a successful mutation into an ambiguous client-visible 5xx.
        try
        {
            await delivery.NotifyPreviousAddressAsync(previousEmail, cancellationToken);
            ApplicationMetrics.EmailChangePreviousAddressNotificationOutcomes
                .Increment(ApplicationMetrics.OutcomeTag, "delivered");
        }
        catch (Exception)
        {
            ApplicationMetrics.EmailChangePreviousAddressNotificationOutcomes
                .Increment(ApplicationMetrics.OutcomeTag, "failed");
        }
    }
}
