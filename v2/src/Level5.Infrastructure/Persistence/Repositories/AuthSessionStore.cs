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
                .SetProperty(r => r.ExpiresAt, session.ExpiresAt)
                .SetProperty(r => r.RevokedAt, session.RevokedAt)
                .SetProperty(r => r.Revision, session.Revision),
                cancellationToken);

        return affected == 1;
    }

    private static AuthSessionRow ToRow(AuthSession session) => new()
    {
        Id = session.Id.Value,
        AccountId = session.AccountId.Value,
        RefreshTokenHash = session.RefreshTokenHash,
        CreatedAt = session.CreatedAt,
        ExpiresAt = session.ExpiresAt,
        RevokedAt = session.RevokedAt,
        Revision = session.Revision
    };

    private static AuthSession ToDomain(AuthSessionRow row)
        => AuthSession.Rehydrate(
            new AuthSessionId(row.Id),
            new AccountId(row.AccountId),
            row.RefreshTokenHash,
            row.CreatedAt,
            row.ExpiresAt,
            row.RevokedAt,
            row.Revision);
}
