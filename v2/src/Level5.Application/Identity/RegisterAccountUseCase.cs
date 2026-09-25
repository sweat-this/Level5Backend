using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Application.Players;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Level5.Domain.Players;

namespace Level5.Application.Identity;

public sealed record RegisterAccountRequest(string Username, string Password, string DisplayName);

public sealed record RegisterAccountResult(
    AccountId AccountId,
    PlayerId PlayerId,
    string PlayerTag,
    AccessToken AccessToken,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

/// <summary>
/// Creates a new account, its initial public player profile, and its first refresh session in
/// one atomic operation. Nothing is returned to the caller until every one of those has committed
/// - see the single <see cref="IUnitOfWork.SaveChangesAsync"/> call at the end - so a failure
/// partway through never hands out credentials for state that was not actually persisted.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107", Justification = "Each parameter is a single-purpose port this use case orchestrates in one atomic commit (account, profile, session, password policy, token issuance); splitting them into a facade would just hide the same dependencies one level down.")]
public sealed class RegisterAccountUseCase(
    IAccountStore accountStore,
    IPlayerProfileStore playerProfileStore,
    IAuthSessionStore authSessionStore,
    IPasswordHasher passwordHasher,
    IPasswordPolicy passwordPolicy,
    IRefreshTokenGenerator refreshTokenGenerator,
    IAuthSessionPolicy sessionPolicy,
    ITokenIssuer tokenIssuer,
    IUnitOfWork unitOfWork,
    IClock clock,
    PlayerTagAllocator playerTagAllocator)
{
    public async Task<RegisterAccountResult> ExecuteAsync(RegisterAccountRequest request, CancellationToken cancellationToken)
    {
        var username = Username.Create(request.Username);
        passwordPolicy.Validate(request.Password);

        if (await accountStore.UsernameExistsAsync(username, cancellationToken))
        {
            throw new ConflictException("Username is already taken.");
        }

        var now = clock.UtcNow;
        var account = Account.Register(username, passwordHasher.Hash(request.Password), now);
        await accountStore.AddAsync(account, cancellationToken);

        var tag = await playerTagAllocator.AssignAvailableTagAsync(request.DisplayName, cancellationToken);
        var profile = PlayerProfile.Create(account.Id, request.DisplayName, tag, now);
        await playerProfileStore.AddAsync(profile, cancellationToken);

        var refreshToken = refreshTokenGenerator.Generate();
        var session = AuthSession.Create(account.Id, refreshToken.Hash, now, sessionPolicy.RefreshTokenLifetime);
        await authSessionStore.AddAsync(session, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        var accessToken = tokenIssuer.IssueAccessToken(account.Id);
        return new RegisterAccountResult(account.Id, profile.Id, tag.Value, accessToken, refreshToken.RawValue, session.ExpiresAt);
    }
}
