using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Application.Observability;
using Level5.Domain.Competition;
using Level5.Domain.Ids;

namespace Level5.Application.Competition;

public sealed record CreateChallengeRequest(
    PlayerId ChallengerId,
    PlayerId OpponentId,
    string RulesetId,
    int? RulesetVersion,
    int TotalGames,
    string? InformationPolicy,
    Guid ClientRequestId);

/// <summary>
/// Only accepted friends may challenge each other for the correspondence MVP - broader discovery
/// or matchmaking is explicitly out of scope. The requested ruleset is resolved against the
/// server's own authoritative <see cref="IRulesetCatalog"/> and frozen into the series - the
/// client names a ruleset (and, optionally, a version), it never supplies comparison keys,
/// metric directions, mode data, or an information policy directly (Competition Protocol V1
/// section 15). Remote challenge creation is further restricted to the Unity-supported
/// best-of-{1,3,5,7} subset (Competition Protocol V1 section 8) even though the underlying
/// <see cref="SeriesFormat"/> domain type accepts any odd value - that restriction belongs here,
/// at the remote boundary, not in the general-purpose domain validator. Retry-safe: a request
/// carrying a <see cref="CreateChallengeRequest.ClientRequestId"/> already used by this same
/// challenger for the same semantic request returns the originally created series instead of
/// creating a duplicate; the same key reused for a different request is a 409 conflict (issue
/// #10). A concurrent duplicate (both requests miss the initial lookup, then race on insert) is
/// resolved the same way: the loser's unique-constraint conflict is caught, the winner is
/// reloaded by key, and the identical replay/conflict check runs against it.
/// </summary>
public sealed class CreateChallengeUseCase(
    IVersusSeriesStore seriesStore,
    IFriendshipStore friendshipStore,
    IRulesetCatalog rulesetCatalog,
    IClock clock)
{
    private static readonly HashSet<int> SupportedTotalGames = [1, 3, 5, 7];

    public async Task<SeriesView> ExecuteAsync(CreateChallengeRequest request, CancellationToken cancellationToken)
    {
        if (request.ClientRequestId == Guid.Empty)
        {
            throw new ValidationFailedException("A non-empty clientRequestId is required to create a challenge.");
        }

        var clientRequestId = request.ClientRequestId;
        var requestedInformationPolicy = ParseInformationPolicy(request.InformationPolicy);

        var existing = await seriesStore.FindByIdempotencyKeyAsync(request.ChallengerId, clientRequestId, cancellationToken);
        if (existing is not null)
        {
            return Replay(existing, request, requestedInformationPolicy);
        }

        if (!await friendshipStore.AreFriendsAsync(request.ChallengerId, request.OpponentId, cancellationToken))
        {
            throw new FriendshipRequiredException("You can only challenge an accepted friend.");
        }

        if (!SupportedTotalGames.Contains(request.TotalGames))
        {
            throw new ValidationFailedException(
                $"Unsupported series format: best-of-{request.TotalGames}. Remote challenges support best-of-1, 3, 5, or 7 only.");
        }

        var format = SeriesFormat.BestOf(request.TotalGames);
        var ruleset = rulesetCatalog.Resolve(request.RulesetId, request.RulesetVersion);

        if (requestedInformationPolicy is { } policy && policy != ruleset.InformationPolicy)
        {
            throw new ValidationFailedException(
                $"Ruleset '{request.RulesetId}' uses information policy '{ruleset.InformationPolicy}', not '{policy}'.");
        }

        var rules = FrozenRules.Create(
            CompetitionProtocol.CurrentVersion,
            ruleset.RulesetId,
            ruleset.RulesetVersion,
            ruleset.MinimumCompatibleVersion,
            ruleset.ModeId,
            ruleset.InformationPolicy,
            ruleset.AlternatesFirstAttempt,
            ruleset.ComparisonKeys);

        var series = VersusSeries.CreateChallenge(request.ChallengerId, request.OpponentId, format, rules, clock.UtcNow);

        try
        {
            await seriesStore.AddAsync(series, clientRequestId, cancellationToken);
        }
        catch (ConflictException)
        {
            // Possibly lost a concurrent insert race under this (challenger, clientRequestId) key.
            // Only a row actually holding that key proves it; otherwise the conflict is something
            // else and must surface unchanged rather than be reported as a replay.
            var raced = await seriesStore.FindByIdempotencyKeyAsync(request.ChallengerId, clientRequestId, cancellationToken);
            if (raced is null)
            {
                throw;
            }

            return Replay(raced, request, requestedInformationPolicy);
        }

        ApplicationMetrics.ChallengeCreateOutcomes.Increment(ApplicationMetrics.OutcomeTag, "created");
        return series.ToView(request.ChallengerId);
    }

    /// <summary>
    /// The single replay path for a reused idempotency key, shared by the lookup-before-insert
    /// retry and the reload-after-insert-race case so both classify and compare identically.
    /// </summary>
    private static SeriesView Replay(VersusSeries existing, CreateChallengeRequest request, InformationPolicy? requestedInformationPolicy)
    {
        EnsureMatchesExistingRequest(existing, request, requestedInformationPolicy);
        ApplicationMetrics.ChallengeCreateOutcomes.Increment(ApplicationMetrics.OutcomeTag, "idempotent_replay");
        return existing.ToView(request.ChallengerId);
    }

    /// <summary>
    /// The semantic fingerprint check for a reused idempotency key: every field the caller
    /// supplied must match what the original request actually produced. Compared against the
    /// already-persisted series' own fields rather than a separately stored fingerprint, since
    /// everything needed is already part of the aggregate - no extra persistence is required just
    /// to detect a conflicting reuse.
    /// </summary>
    private static void EnsureMatchesExistingRequest(VersusSeries existing, CreateChallengeRequest request, InformationPolicy? requestedInformationPolicy)
    {
        var sameRequest =
            existing.OpponentId == request.OpponentId &&
            existing.Format.TotalGames == request.TotalGames &&
            existing.Rules.RulesetId == request.RulesetId &&
            (request.RulesetVersion is null || existing.Rules.RulesetVersion == request.RulesetVersion) &&
            (requestedInformationPolicy is null || existing.Rules.InformationPolicy == requestedInformationPolicy);

        if (!sameRequest)
        {
            ApplicationMetrics.ChallengeCreateOutcomes.Increment(ApplicationMetrics.OutcomeTag, "conflict");
            throw new ConflictException("This clientRequestId was already used to create a different challenge request.");
        }
    }

    private static InformationPolicy? ParseInformationPolicy(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (!Enum.TryParse<InformationPolicy>(value, ignoreCase: true, out var parsed))
        {
            throw new ValidationFailedException($"Unknown information policy '{value}'.");
        }

        return parsed;
    }
}
