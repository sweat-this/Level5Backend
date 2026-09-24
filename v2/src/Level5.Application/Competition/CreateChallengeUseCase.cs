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
            return Replay(existing, request, requestedInformationPolicy, afterRace: false);
        }

        if (!SupportedTotalGames.Contains(request.TotalGames))
        {
            throw new ValidationFailedException(
                $"Unsupported series format: best-of-{request.TotalGames}. Remote challenges support best-of-1, 3, 5, or 7 only.");
        }

        if (!await friendshipStore.AreFriendsAsync(request.ChallengerId, request.OpponentId, cancellationToken))
        {
            throw new FriendshipRequiredException("You can only challenge an accepted friend.");
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

        // A concurrent duplicate may lose an insert race under this (challenger, clientRequestId)
        // key; the helper below reloads whoever actually won it for Replay to compare against.
        var raced = await IdempotentInsertRecovery.TryInsertAsync(
            ct => seriesStore.AddAsync(series, clientRequestId, ct),
            ct => seriesStore.FindByIdempotencyKeyAsync(request.ChallengerId, clientRequestId, ct),
            cancellationToken);

        if (raced is not null)
        {
            return Replay(raced, request, requestedInformationPolicy, afterRace: true);
        }

        ApplicationMetrics.ChallengeCreateOutcomes.Increment(ApplicationMetrics.OutcomeTag, "created");
        return series.ToView(request.ChallengerId);
    }

    /// <summary>
    /// The single replay path for a reused idempotency key, shared by the lookup-before-insert
    /// retry and the reload-after-insert-race case so both classify and compare identically.
    /// <paramref name="afterRace"/> only steers which outcome tag is recorded - a normal duplicate
    /// request versus one that only resolved after losing a real insert race, which is a
    /// operationally distinct signal worth telling apart in <see cref="ApplicationMetrics.ChallengeCreateOutcomes"/>.
    /// </summary>
    private static SeriesView Replay(VersusSeries existing, CreateChallengeRequest request, InformationPolicy? requestedInformationPolicy, bool afterRace)
    {
        EnsureMatchesExistingRequest(existing, request, requestedInformationPolicy);
        ApplicationMetrics.ChallengeCreateOutcomes.Increment(ApplicationMetrics.OutcomeTag, afterRace ? "idempotent_replay_after_race" : "idempotent_replay");
        return existing.ToView(request.ChallengerId);
    }

    /// <summary>
    /// The semantic fingerprint check for a reused idempotency key, delegated to the aggregate's
    /// own <see cref="VersusSeries.MatchesRequest"/> for the fields it owns. Also re-checks
    /// <see cref="SupportedTotalGames"/> membership against the persisted value rather than
    /// trusting it was already validated at creation - that invariant holds today, but this keeps
    /// a replay from silently succeeding against it if it is ever violated (e.g. by a future
    /// admin/backfill path).
    /// </summary>
    private static void EnsureMatchesExistingRequest(VersusSeries existing, CreateChallengeRequest request, InformationPolicy? requestedInformationPolicy)
    {
        var sameRequest =
            SupportedTotalGames.Contains(existing.Format.TotalGames) &&
            existing.MatchesRequest(request.OpponentId, request.TotalGames, request.RulesetId, request.RulesetVersion, requestedInformationPolicy);

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
