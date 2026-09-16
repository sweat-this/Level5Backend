using Level5.Application.Abstractions;
using Level5.Domain.Identity;
using Level5.Domain.Ids;

namespace Level5.Application.Identity;

public sealed record LoginRequest(string Username, string Password);

public sealed record LoginResult(
    AccountId AccountId,
    PlayerId PlayerId,
    AccessToken AccessToken,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107", Justification = "Each parameter is a single-purpose port this use case orchestrates in one atomic commit (account lookup/rehash, profile, session, token issuance); splitting them into a facade would just hide the same dependencies one level down.")]
public sealed class LoginUseCase(
    IAccountStore accountStore,
    IPlayerProfileStore playerProfileStore,
    IAuthSessionStore authSessionStore,
    IPasswordHasher passwordHasher,
    IRefreshTokenGenerator refreshTokenGenerator,
    IAuthSessionPolicy sessionPolicy,
    ITokenIssuer tokenIssuer,
    IUnitOfWork unitOfWork,
    IClock clock)
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

        // Checked only after a successful password verification (not before), so every account
        // that exists takes the same password-hashing cost regardless of status - a disabled
        // account does not become distinguishable from an active one by response timing.
        if (account.Status != AccountStatus.Active)
        {
            throw new InvalidCredentialsException();
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            account.ChangePasswordHash(passwordHasher.Hash(request.Password));
            await accountStore.UpdateAsync(account, cancellationToken);
        }

        var profile = await playerProfileStore.FindByAccountIdAsync(account.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Account {account.Id} has no player profile.");

        var now = clock.UtcNow;
        var refreshToken = refreshTokenGenerator.Generate();
        var session = AuthSession.Create(account.Id, refreshToken.Hash, now, sessionPolicy.RefreshTokenLifetime);
        await authSessionStore.AddAsync(session, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        var accessToken = tokenIssuer.IssueAccessToken(account.Id);
        return new LoginResult(account.Id, profile.Id, accessToken, refreshToken.RawValue, session.ExpiresAt);
    }
}
