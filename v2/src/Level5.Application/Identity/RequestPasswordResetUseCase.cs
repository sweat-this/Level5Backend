using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Application.Observability;
using Level5.Domain.Identity;

namespace Level5.Application.Identity;

public sealed record RequestPasswordResetRequest(string Email);

[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107", Justification = "The use case coordinates narrow recovery ports and one atomic persistence boundary.")]
public sealed class RequestPasswordResetUseCase(
    IAccountStore accountStore,
    IPasswordResetChallengeStore challengeStore,
    IPasswordResetTokenGenerator tokenGenerator,
    IPasswordRecoveryPolicy policy,
    IPasswordRecoveryDelivery delivery,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task ExecuteAsync(RequestPasswordResetRequest request, CancellationToken cancellationToken)
    {
        var email = Email.Create(request.Email);
        var account = await accountStore.FindByVerifiedEmailAsync(email, cancellationToken);
        if (account is null || account.Status != AccountStatus.Active) return;
        var targetEmail = account.Email!;

        var now = clock.UtcNow;
        var challenge = await challengeStore.FindByAccountIdAsync(account.Id, cancellationToken);
        if (challenge is not null && now < challenge.IssuedAt + policy.RequestCooldown) return;

        var token = tokenGenerator.Generate();
        if (challenge is null)
        {
            challenge = PasswordResetChallenge.Create(account.Id, targetEmail, token.Hash, now, policy.TokenLifetime);
            await challengeStore.AddAsync(challenge, cancellationToken);
        }
        else
        {
            challenge.Rotate(targetEmail, token.Hash, now, policy.TokenLifetime);
            await challengeStore.StageUpdateAsync(challenge, cancellationToken);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConflictException)
        {
            return; // Concurrent request/cooldown is deliberately indistinguishable.
        }

        PasswordRecoveryDeliveryOutcome outcome;
        try
        {
            outcome = await delivery.DeliverAsync(targetEmail, token.RawValue, challenge.ExpiresAt, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A delivery-provider fault must not distinguish an eligible account from an unknown
            // one at this anonymous boundary. Do not attach email/token/exception text to metrics.
            ApplicationMetrics.PasswordRecoveryDeliveryOutcomes.Increment(ApplicationMetrics.OutcomeTag, "faulted");
            return;
        }

        ApplicationMetrics.PasswordRecoveryDeliveryOutcomes.Increment(
            ApplicationMetrics.OutcomeTag,
            outcome == PasswordRecoveryDeliveryOutcome.Delivered ? "delivered" : "unavailable");
    }
}
