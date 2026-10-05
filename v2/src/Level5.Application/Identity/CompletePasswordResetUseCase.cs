using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Identity;

namespace Level5.Application.Identity;

public sealed record CompletePasswordResetRequest(string ResetToken, string NewPassword);

public sealed class CompletePasswordResetUseCase(
    IPasswordResetChallengeStore challengeStore,
    IPasswordResetTokenGenerator tokenGenerator,
    IAccountStore accountStore,
    IPasswordHasher passwordHasher,
    IPasswordPolicy passwordPolicy,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task ExecuteAsync(CompletePasswordResetRequest request, CancellationToken cancellationToken)
    {
        passwordPolicy.Validate(request.NewPassword);
        var challenge = await challengeStore.FindByTokenHashAsync(tokenGenerator.Hash(request.ResetToken), cancellationToken);
        var now = clock.UtcNow;
        if (challenge is null || !challenge.CanComplete(now)) throw new InvalidPasswordResetException();

        var account = await accountStore.FindByIdAsync(challenge.AccountId, cancellationToken);
        if (account is null || account.Status != AccountStatus.Active || account.EmailVerifiedAt is null ||
            account.Email is null || !account.Email.Equals(challenge.TargetEmail))
            throw new InvalidPasswordResetException();

        var expectedGeneration = account.SessionGeneration;
        try
        {
            account.ChangePassword(passwordHasher.Hash(request.NewPassword));
            challenge.Consume(now);
            await accountStore.StageCredentialUpdateAsync(account, expectedGeneration, cancellationToken);
            await challengeStore.StageUpdateAsync(challenge, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is PasswordResetChallengeInvalidException or ConflictException)
        {
            throw new InvalidPasswordResetException();
        }
    }
}
