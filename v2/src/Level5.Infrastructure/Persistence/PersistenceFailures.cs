using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Level5.Infrastructure.Persistence;

/// <summary>
/// Lets the Api's exception handler recognise "the database is temporarily unavailable" without
/// the Api itself referencing EF Core or Npgsql types (see the architecture tests).
/// </summary>
public static class PersistenceFailures
{
    /// <summary>
    /// True when <paramref name="exception"/> (or anything it wraps) is a transient database
    /// failure - one that survived the bounded retry budget in <see cref="PostgresConfiguration"/>,
    /// or a timeout, which that strategy deliberately does not retry - i.e. the caller should get a
    /// retryable "service unavailable", not a generic 500. Constraint violations and other
    /// deterministic database errors are not transient and return false.
    /// </summary>
    public static bool IsTransientUnavailability(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is RetryLimitExceededException or NpgsqlException { IsTransient: true })
            {
                return true;
            }
        }

        return false;
    }
}
