using Level5.Application.Abstractions;
using Level5.Domain.Identity;
using Level5.Domain.Ids;

namespace Level5.Application.Identity;

public sealed record RefreshSessionRequest(string RefreshToken);

public sealed record RefreshSessionResult(
    AccountId AccountId,
    PlayerId PlayerId,
    AccessToken AccessToken,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

/// <summary>
/// Exchanges a current refresh credential for a new access token and a rotated replacement
/// refresh credential. The old credential stops working the instant this succeeds - see
/// <see cref="AuthSession.Rotate"/> - and exactly one of several concurrent callers presenting the
/// same credential can win, via <c>IAuthSessionStore.TrySaveAsync</c>'s revision check. Every
/// failure mode (unknown, expired, revoked, already-rotated/replayed, or owned by a non-active
/// account) surfaces as the same <see cref="InvalidRefreshTokenException"/>, so a client can never
/// learn which one occurred.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107", Justification = "Each parameter is a single-purpose port this use case orchestrates; splitting them into a facade would just hide the same dependencies one level down.")]
public sealed class RefreshSessionUseCase(
    IAuthSessionStore authSessionStore,
    IAccountStore accountStore,
    IPlayerProfileStore playerProfileStore,
    IRefreshTokenGenerator refreshTokenGenerator,
    IAuthSessionPolicy sessionPolicy,
    ITokenIssuer tokenIssuer,
    IClock clock)
{
    public async Task<RefreshSessionResult> ExecuteAsync(RefreshSessionRequest request, CancellationToken cancellationToken)
    {
        var hash = refreshTokenGenerator.Hash(request.RefreshToken);
        var session = await authSessionStore.FindByRefreshTokenHashAsync(hash, cancellationToken)
            ?? throw new InvalidRefreshTokenException();

        var now = clock.UtcNow;
        if (!session.CanRefresh(now))
        {
            throw new InvalidRefreshTokenException();
        }

        var account = await accountStore.FindByIdAsync(session.AccountId, cancellationToken)
            ?? throw new InvalidRefreshTokenException();

        if (account.Status != AccountStatus.Active)
        {
            throw new InvalidRefreshTokenException();
        }

        var profile = await playerProfileStore.FindByAccountIdAsync(account.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Account {account.Id} has no player profile.");

        var expectedRevision = session.Revision;
        var newRefreshToken = refreshTokenGenerator.Generate();
        session.Rotate(newRefreshToken.Hash, now, sessionPolicy.RefreshTokenLifetime);

        var saved = await authSessionStore.TrySaveAsync(session, expectedRevision, cancellationToken);
        if (!saved)
        {
            // Someone else already rotated or revoked this exact session between our lookup and
            // our write - the credential we were just handed is no longer current, which is
            // exactly the replay case this method exists to reject, not a generic conflict.
            throw new InvalidRefreshTokenException();
        }

        var accessToken = tokenIssuer.IssueAccessToken(account.Id);
        return new RefreshSessionResult(account.Id, profile.Id, accessToken, newRefreshToken.RawValue, session.ExpiresAt);
    }
}
