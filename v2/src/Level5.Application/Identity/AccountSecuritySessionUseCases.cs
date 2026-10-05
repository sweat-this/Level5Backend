using Level5.Application.Abstractions;
using Level5.Domain.Ids;

namespace Level5.Application.Identity;

public sealed record AccountSecuritySessionContext(
    AccountId AccountId,
    AuthSessionId SessionId,
    long SessionGeneration,
    DateTimeOffset ValidatedAt);

/// <summary>
/// Deliberately scoped stateful guard for account-session administration. Ordinary bearer APIs
/// remain stateless and continue to authorize from the signed subject claim alone.
/// </summary>
public sealed class AccountSecuritySessionGuard(IAuthSessionStore sessions, IClock clock)
{
    public async Task<AccountSecuritySessionContext> RequireActiveAsync(
        AccountId accountId,
        AuthSessionId sessionId,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var generation = await sessions.FindActiveGenerationAsync(
            accountId, sessionId, now, cancellationToken);

        return generation is long activeGeneration
            ? new(accountId, sessionId, activeGeneration, now)
            : throw new ReauthenticationRequiredException();
    }
}

public sealed class ListAccountSessionsUseCase(
    AccountSecuritySessionGuard guard,
    IAuthSessionStore sessions)
{
    public async Task<IReadOnlyList<AuthSessionSummary>> ExecuteAsync(
        AccountId accountId,
        AuthSessionId currentSessionId,
        CancellationToken cancellationToken)
    {
        var context = await guard.RequireActiveAsync(accountId, currentSessionId, cancellationToken);
        return await sessions.ListActiveAsync(
            context.AccountId,
            context.SessionId,
            context.SessionGeneration,
            context.ValidatedAt,
            cancellationToken);
    }
}

public sealed class RevokeAccountSessionUseCase(
    AccountSecuritySessionGuard guard,
    IAuthSessionStore sessions)
{
    public async Task ExecuteAsync(
        AccountId accountId,
        AuthSessionId currentSessionId,
        AuthSessionId targetSessionId,
        CancellationToken cancellationToken)
    {
        var context = await guard.RequireActiveAsync(accountId, currentSessionId, cancellationToken);
        await sessions.RevokeActiveAsync(
            context.AccountId,
            targetSessionId,
            context.SessionGeneration,
            context.ValidatedAt,
            cancellationToken);
    }
}

public sealed class RevokeOtherAccountSessionsUseCase(
    AccountSecuritySessionGuard guard,
    IAuthSessionStore sessions)
{
    public async Task ExecuteAsync(
        AccountId accountId,
        AuthSessionId currentSessionId,
        CancellationToken cancellationToken)
    {
        var context = await guard.RequireActiveAsync(accountId, currentSessionId, cancellationToken);
        await sessions.RevokeOtherActiveAsync(
            context.AccountId,
            context.SessionId,
            context.SessionGeneration,
            context.ValidatedAt,
            cancellationToken);
    }
}

public sealed class RevokeAllAccountSessionsUseCase(
    AccountSecuritySessionGuard guard,
    IAccountStore accounts)
{
    public async Task ExecuteAsync(
        AccountId accountId,
        AuthSessionId currentSessionId,
        CancellationToken cancellationToken)
    {
        var context = await guard.RequireActiveAsync(accountId, currentSessionId, cancellationToken);

        // A concurrent password change/reset/revoke-all that wins this CAS has already established
        // the same or stronger global-invalidity postcondition, so a zero-row result is success.
        await accounts.TryAdvanceSessionGenerationAsync(
            context.AccountId,
            context.SessionGeneration,
            cancellationToken);
    }
}
