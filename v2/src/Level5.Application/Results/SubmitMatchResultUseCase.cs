using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Ids;
using Level5.Domain.Results;

namespace Level5.Application.Results;

public sealed record SubmitMatchResultRequest(
    PlayerId PlayerId,
    Guid ClientResultId,
    string ModeId,
    string LevelId,
    string CharacterId,
    string ClientVersion,
    string Platform,
    MatchResultMetrics Metrics,
    MatchResultModifiers Modifiers);

/// <summary>
/// Records an ordinary completed match as an immutable, retry-safe result. <see cref="PlayerId"/>
/// is taken from the authenticated application/API boundary, never from request data - the
/// request itself supplies only client-reported gameplay/context fields.
///
/// Idempotent on <c>(PlayerId, ClientResultId)</c>: an identical replay returns the originally
/// persisted result; a replay carrying a materially different payload for the same key is a 409
/// conflict rather than a silent overwrite (issue: general match-result ingestion). A concurrent
/// duplicate submission (both requests miss the initial lookup, then race on insert) is resolved
/// the same way - the loser's unique-constraint violation is translated into
/// <see cref="ConflictException"/> by the store, caught here, and resolved by reloading and
/// re-running the same replay/conflict comparison against whichever request actually won.
/// </summary>
public sealed class SubmitMatchResultUseCase(IMatchResultStore store, IClock clock)
{
    public async Task<MatchResult> ExecuteAsync(SubmitMatchResultRequest request, CancellationToken cancellationToken)
    {
        var existing = await store.FindByClientResultIdAsync(request.PlayerId, request.ClientResultId, cancellationToken);
        if (existing is not null)
        {
            return EnsureMatchesExistingRequest(existing, request);
        }

        var result = MatchResult.Submit(
            request.PlayerId, request.ClientResultId, request.ModeId, request.LevelId, request.CharacterId,
            request.ClientVersion, request.Platform, request.Metrics, request.Modifiers, clock.UtcNow);

        try
        {
            await store.AddAsync(result, cancellationToken);
        }
        catch (ConflictException)
        {
            // Lost a concurrent insert race under the same (PlayerId, ClientResultId) idempotency
            // key: someone else's request (or an earlier attempt of this same one) committed first.
            // Reload and resolve exactly like a sequential retry would, against whichever request
            // actually won the race.
            var raced = await store.FindByClientResultIdAsync(request.PlayerId, request.ClientResultId, cancellationToken)
                ?? throw new ConflictException("The request conflicts with existing data. Please retry.");
            return EnsureMatchesExistingRequest(raced, request);
        }

        return result;
    }

    private static MatchResult EnsureMatchesExistingRequest(MatchResult existing, SubmitMatchResultRequest request)
    {
        var sameRequest = existing.MatchesRequest(
            request.ModeId, request.LevelId, request.CharacterId, request.ClientVersion, request.Platform,
            request.Metrics, request.Modifiers);

        if (!sameRequest)
        {
            throw new ConflictException("This clientResultId was already used to submit a different match result.");
        }

        return existing;
    }
}
