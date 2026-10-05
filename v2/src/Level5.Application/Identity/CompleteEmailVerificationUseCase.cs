using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Identity;

namespace Level5.Application.Identity;

public sealed class CompleteEmailVerificationUseCase(
    IEmailVerificationChallengeStore challengeStore,
    IEmailVerificationTokenGenerator tokenGenerator,
    IAccountStore accountStore,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task ExecuteAsync(string rawToken, CancellationToken cancellationToken)
    {
        var tokenHash = tokenGenerator.Hash(rawToken);
        var challenge = await challengeStore.FindByTokenHashAsync(tokenHash, cancellationToken);
        var now = clock.UtcNow;

        if (challenge is null || !challenge.CanComplete(now))
        {
            throw new InvalidEmailVerificationException();
        }

        var account = await accountStore.FindByIdAsync(challenge.AccountId, cancellationToken);
        if (account is null || account.Status != AccountStatus.Active)
        {
            throw new InvalidEmailVerificationException();
        }

        try
        {
            account.VerifyEmail(challenge.TargetEmail, now);
            challenge.Consume(now);
            await accountStore.StageEmailUpdateAsync(account, cancellationToken);
            await challengeStore.StageUpdateAsync(challenge, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is EmailVerificationTargetMismatchException
                                          or EmailVerificationChallengeInvalidException
                                          or ConflictException)
        {
            throw new InvalidEmailVerificationException();
        }
    }
}
