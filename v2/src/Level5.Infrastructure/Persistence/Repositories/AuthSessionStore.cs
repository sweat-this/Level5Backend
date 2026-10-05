using Level5.Application.Abstractions;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.Persistence.Repositories;

public sealed class AuthSessionStore(Level5V2DbContext db) : IAuthSessionStore
{
    public async Task<AuthSession?> FindByIdAsync(AuthSessionId id, CancellationToken cancellationToken)
    {
        var row = await db.AuthSessions.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id.Value, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task<AuthSession?> FindByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken)
    {
        var row = await db.AuthSessions.AsNoTracking().SingleOrDefaultAsync(r => r.RefreshTokenHash == refreshTokenHash, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task AddAsync(AuthSession session, CancellationToken cancellationToken)
        => await db.AuthSessions.AddAsync(ToRow(session), cancellationToken);

    public async Task<bool> TrySaveAsync(AuthSession session, long expectedRevision, CancellationToken cancellationToken)
    {
        // Conditional UPDATE ... WHERE id = @id AND revision = @expectedRevision, executed
        // directly against the database rather than through change tracking, so the affected-row
        // count is the concurrency check: 1 means our write (rotation or revocation) won, 0 means
        // someone else's write already moved the revision forward - see AuthSessionStore's
        // VersusSeriesStore counterpart for the same pattern.
        var affected = await db.AuthSessions
            .Where(r => r.Id == session.Id.Value && r.Revision == expectedRevision)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.RefreshTokenHash, session.RefreshTokenHash)
                .SetProperty(r => r.LastRefreshedAt, session.LastRefreshedAt)
                .SetProperty(r => r.ExpiresAt, session.ExpiresAt)
                .SetProperty(r => r.RevokedAt, session.RevokedAt)
                .SetProperty(r => r.Revision, session.Revision),
                cancellationToken);

        return affected == 1;
    }

    public async Task<bool> TryRotateForActiveGenerationAsync(
        AuthSession session, long expectedRevision, CancellationToken cancellationToken)
    {
        var affected = await db.AuthSessions
            .Where(r => r.Id == session.Id.Value &&
                        r.Revision == expectedRevision &&
                        r.RevokedAt == null &&
                        r.ExpiresAt > session.LastRefreshedAt &&
                        db.Accounts.Any(a => a.Id == r.AccountId &&
                                             a.Status == AccountStatus.Active.ToString() &&
                                             a.SessionGeneration == r.SessionGeneration))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.RefreshTokenHash, session.RefreshTokenHash)
                .SetProperty(r => r.LastRefreshedAt, session.LastRefreshedAt)
                .SetProperty(r => r.ExpiresAt, session.ExpiresAt)
                .SetProperty(r => r.Revision, session.Revision), cancellationToken);
        return affected == 1;
    }

    public Task<long?> FindActiveGenerationAsync(
        AccountId accountId,
        AuthSessionId sessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var activeStatus = AccountStatus.Active.ToString();
        return (
                from session in db.AuthSessions.AsNoTracking()
                join account in db.Accounts.AsNoTracking() on session.AccountId equals account.Id
                where session.Id == sessionId.Value &&
                      session.AccountId == accountId.Value &&
                      session.RevokedAt == null &&
                      session.ExpiresAt > now &&
                      session.SessionGeneration == account.SessionGeneration &&
                      account.Status == activeStatus
                select (long?)session.SessionGeneration)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuthSessionSummary>> ListActiveAsync(
        AccountId accountId,
        AuthSessionId currentSessionId,
        long sessionGeneration,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var activeStatus = AccountStatus.Active.ToString();
        var rows = await (
                from session in db.AuthSessions.AsNoTracking()
                join account in db.Accounts.AsNoTracking() on session.AccountId equals account.Id
                where session.AccountId == accountId.Value &&
                      session.RevokedAt == null &&
                      session.ExpiresAt > now &&
                      session.SessionGeneration == sessionGeneration &&
                      account.SessionGeneration == sessionGeneration &&
                      account.Status == activeStatus
                orderby session.LastRefreshedAt descending, session.CreatedAt descending, session.Id descending
                select new
                {
                    session.Id,
                    session.ClientKind,
                    session.CreatedAt,
                    session.LastRefreshedAt,
                    session.ExpiresAt
                })
            .ToListAsync(cancellationToken);

        return rows.Select(row => new AuthSessionSummary(
                new AuthSessionId(row.Id),
                ParseClientKind(row.ClientKind),
                row.CreatedAt,
                row.LastRefreshedAt,
                row.ExpiresAt,
                row.Id == currentSessionId.Value))
            .ToList();
    }

    public async Task RevokeActiveAsync(
        AccountId accountId,
        AuthSessionId sessionId,
        long sessionGeneration,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var activeStatus = AccountStatus.Active.ToString();
        await db.AuthSessions
            .Where(session => session.Id == sessionId.Value &&
                              session.AccountId == accountId.Value &&
                              session.SessionGeneration == sessionGeneration &&
                              session.RevokedAt == null &&
                              session.ExpiresAt > now &&
                              db.Accounts.Any(account => account.Id == session.AccountId &&
                                                         account.Status == activeStatus &&
                                                         account.SessionGeneration == sessionGeneration))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(session => session.RevokedAt, now)
                .SetProperty(session => session.Revision, session => session.Revision + 1),
                cancellationToken);
    }

    public async Task RevokeOtherActiveAsync(
        AccountId accountId,
        AuthSessionId currentSessionId,
        long sessionGeneration,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var activeStatus = AccountStatus.Active.ToString();
        await db.AuthSessions
            .Where(session => session.AccountId == accountId.Value &&
                              session.Id != currentSessionId.Value &&
                              session.SessionGeneration == sessionGeneration &&
                              session.RevokedAt == null &&
                              session.ExpiresAt > now &&
                              db.Accounts.Any(account => account.Id == session.AccountId &&
                                                         account.Status == activeStatus &&
                                                         account.SessionGeneration == sessionGeneration))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(session => session.RevokedAt, now)
                .SetProperty(session => session.Revision, session => session.Revision + 1),
                cancellationToken);
    }

    private static AuthSessionRow ToRow(AuthSession session) => new()
    {
        Id = session.Id.Value,
        AccountId = session.AccountId.Value,
        RefreshTokenHash = session.RefreshTokenHash,
        CreatedAt = session.CreatedAt,
        LastRefreshedAt = session.LastRefreshedAt,
        ExpiresAt = session.ExpiresAt,
        RevokedAt = session.RevokedAt,
        ClientKind = session.ClientKind.ToString(),
        Revision = session.Revision,
        SessionGeneration = session.SessionGeneration
    };

    private static AuthSession ToDomain(AuthSessionRow row)
        => AuthSession.Rehydrate(
            new AuthSessionId(row.Id),
            new AccountId(row.AccountId),
            row.RefreshTokenHash,
            row.CreatedAt,
            row.LastRefreshedAt,
            row.ExpiresAt,
            row.RevokedAt,
            ParseClientKind(row.ClientKind),
            row.Revision,
            row.SessionGeneration);

    private static ClientKind ParseClientKind(string value)
        => Enum.TryParse<ClientKind>(value, ignoreCase: false, out var parsed)
            ? parsed
            : ClientKind.Unknown;
}
