using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Application.Leaderboards;
using Level5.Domain.Ids;
using Level5.Domain.Results;

namespace Level5.Application.Results;

public sealed record SubmitMatchResultRequest(
    PlayerId PlayerId,
    Guid ClientResultId,
    int ModeId,
    int LevelId,
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
public sealed class SubmitMatchResultUseCase(IMatchResultStore store, ILeaderboardPolicyCatalog leaderboardPolicyCatalog, IClock clock)
{
    public async Task<MatchResult> ExecuteAsync(SubmitMatchResultRequest request, CancellationToken cancellationToken)
    {
        var existing = await store.FindByClientResultIdAsync(request.PlayerId, request.ClientResultId, cancellationToken);
        if (existing is not null)
        {
            return EnsureMatchesExistingRequest(existing, request);
        }

        EnsureSatisfiesLeaderboardPolicy(request);

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

    /// <summary>
    /// A mode with a server-owned <see cref="Level5.Domain.Leaderboards.LeaderboardPolicy"/> must
    /// carry the metric that policy ranks by - validated up front so a leaderboard-eligible result
    /// can never end up unrankable later. A mode with no policy is unaffected: it may still be
    /// persisted as a raw, unranked <see cref="MatchResult"/> (issue: leaderboard reads).
    /// </summary>
    private void EnsureSatisfiesLeaderboardPolicy(SubmitMatchResultRequest request)
    {
        var policy = leaderboardPolicyCatalog.TryResolve(request.ModeId);
        if (policy is null)
        {
            return;
        }

        if (request.Metrics.ValueOf(policy.RankingMetric) is null)
        {
            throw new RequiredLeaderboardMetricMissingException(
                $"Mode {request.ModeId} requires the '{policy.RankingMetric}' metric to be included in the result.");
        }
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
