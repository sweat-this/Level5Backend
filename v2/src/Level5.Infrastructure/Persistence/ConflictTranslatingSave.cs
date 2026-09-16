using Level5.Application.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Level5.Infrastructure.Persistence;

internal static class ConflictTranslatingSave
{
    /// <summary>
    /// Commits pending changes, translating a unique-constraint violation or an optimistic-
    /// concurrency conflict into the application-level <see cref="ConflictException"/>.
    ///
    /// Only <c>23505</c> (unique_violation) and <see cref="DbUpdateConcurrencyException"/> (a
    /// tracked entity's <c>IsConcurrencyToken()</c> column, e.g. FriendRequestRow.Revision, no
    /// longer matched what was loaded) are translated. Every other database failure - data
    /// truncation, a check/not-null violation, a deadlock, a statement timeout - is left to
    /// propagate untouched, because those are server-side faults that must surface (and be
    /// logged) as errors rather than being reported to the caller as a retryable conflict.
    /// </summary>
    public static async Task SaveChangesTranslatingConflictsAsync(this DbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException("The request conflicts with existing data. Please retry.");
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The request was concurrently modified. Please retry.");
        }
    }
}
