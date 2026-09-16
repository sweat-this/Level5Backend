using Level5.Application.Abstractions;
using Level5.Domain.Identity;
using Level5.Domain.Ids;

namespace Level5.Application.Identity;

public sealed record LoginRequest(string Username, string Password);

public sealed record LoginResult(AccountId AccountId, PlayerId PlayerId, AccessToken AccessToken);

public sealed class LoginUseCase(
    IAccountStore accountStore,
    IPlayerProfileStore playerProfileStore,
    IPasswordHasher passwordHasher,
    ITokenIssuer tokenIssuer,
    IUnitOfWork unitOfWork)
{
    public async Task<LoginResult> ExecuteAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        // Validate the format first so a malformed username can't even reach the store - but
        // report it as "invalid credentials" rather than a validation error, so this endpoint
        // never distinguishes "bad format" from "wrong password" for an attacker probing it.
        Username username;
        try
        {
            username = Username.Create(request.Username);
        }
        catch (InvalidUsernameException)
        {
            throw new InvalidCredentialsException();
        }

        var account = await accountStore.FindByUsernameAsync(username, cancellationToken)
            ?? throw new InvalidCredentialsException();

        var verification = passwordHasher.Verify(account.PasswordHash, request.Password);
        if (verification == PasswordVerificationResult.Failed)
        {
            throw new InvalidCredentialsException();
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            account.ChangePasswordHash(passwordHasher.Hash(request.Password));
            await accountStore.UpdateAsync(account, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var profile = await playerProfileStore.FindByAccountIdAsync(account.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Account {account.Id} has no player profile.");

        var accessToken = tokenIssuer.IssueAccessToken(account.Id);
        return new LoginResult(account.Id, profile.Id, accessToken);
    }
}
