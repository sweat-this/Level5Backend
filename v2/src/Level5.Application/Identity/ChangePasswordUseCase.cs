using Level5.Application.Abstractions;
using Level5.Domain.Identity;
using Level5.Domain.Ids;

namespace Level5.Application.Identity;

public sealed record ChangePasswordRequest(AccountId AccountId, string CurrentPassword, string NewPassword);

public sealed class ChangePasswordUseCase(
    IAccountStore accountStore, IPasswordHasher passwordHasher, IPasswordPolicy passwordPolicy, IUnitOfWork unitOfWork)
{
    public async Task ExecuteAsync(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var account = await accountStore.FindByIdAsync(request.AccountId, cancellationToken);
        if (account is null || account.Status != AccountStatus.Active) throw new AccountSecurityActionForbiddenException();
        if (passwordHasher.Verify(account.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
            throw new CurrentPasswordInvalidException();

        passwordPolicy.Validate(request.NewPassword);
        var expectedGeneration = account.SessionGeneration;
        account.ChangePassword(passwordHasher.Hash(request.NewPassword));
        await accountStore.StageCredentialUpdateAsync(account, expectedGeneration, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
