namespace Level5.Application.Common;

/// <summary>
/// The insert-race half of an idempotent-create use case: attempt the insert, and if it lost a
/// concurrent race under the same idempotency key (the store translates the unique-constraint
/// violation into <see cref="ConflictException"/>), reload by that same key and hand the caller
/// whatever committed - the caller then runs its normal replay/conflict comparison against it,
/// exactly as it would for a sequential retry. If the conflict was not actually a collision on
/// this key (the reload finds nothing), the original exception is rethrown unchanged rather than
/// masked, since that means the conflict was something else entirely.
/// </summary>
public static class IdempotentInsertRecovery
{
    /// <returns>
    /// <see langword="null"/> if <paramref name="insert"/> succeeded outright; otherwise the
    /// already-committed row belonging to whoever won the race, for the caller to replay against.
    /// </returns>
    public static async Task<T?> TryInsertAsync<T>(
        Func<CancellationToken, Task> insert,
        Func<CancellationToken, Task<T?>> reloadByKey,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            await insert(cancellationToken);
            return null;
        }
        catch (ConflictException)
        {
            var raced = await reloadByKey(cancellationToken);
            if (raced is null)
            {
                throw;
            }

            return raced;
        }
    }
}
