using Level5.Application.Abstractions;

namespace Level5.Application.Identity;

public sealed record LogoutRequest(string RefreshToken);

/// <summary>
/// Revokes the session backing a refresh credential, so it can no longer be exchanged for future
/// access tokens (see <c>Program.cs</c> / the V2 README for why already-issued access tokens are
/// deliberately unaffected). Safe to retry: an unknown, already-rotated, or already-revoked
/// credential is treated as an idempotent no-op rather than an error, since the caller's intent -
/// "this credential should not work anymore" - is already satisfied in every one of those cases.
/// A genuine infrastructure failure (e.g. the database is unreachable) still propagates as an
/// exception; it is never swallowed to force a success response.
/// </summary>
public sealed class LogoutUseCase(IAuthSessionStore authSessionStore, IRefreshTokenGenerator refreshTokenGenerator, IClock clock)
{
    public async Task ExecuteAsync(LogoutRequest request, CancellationToken cancellationToken)
    {
        var hash = refreshTokenGenerator.Hash(request.RefreshToken);
        var session = await authSessionStore.FindByRefreshTokenHashAsync(hash, cancellationToken);
        if (session is null || session.RevokedAt is not null)
        {
            return;
        }

        var expectedRevision = session.Revision;
        session.Revoke(clock.UtcNow);

        // A lost concurrency race here (another request rotated or revoked the same session
        // first) is not a genuine failure to surface: either outcome already leaves the
        // credential the caller presented unusable, which is all logout promises.
        await authSessionStore.TrySaveAsync(session, expectedRevision, cancellationToken);
    }
}
