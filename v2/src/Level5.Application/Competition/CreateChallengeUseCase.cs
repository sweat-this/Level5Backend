using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Competition;
using Level5.Domain.Ids;

namespace Level5.Application.Competition;

public sealed record CreateChallengeRequest(PlayerId ChallengerId, PlayerId OpponentId, string RulesetId, int? RulesetVersion, int TotalGames);

/// <summary>
/// Only accepted friends may challenge each other for the correspondence MVP - broader discovery
/// or matchmaking is explicitly out of scope. The requested ruleset is resolved against the
/// server's own authoritative <see cref="IRulesetCatalog"/> and frozen into the series - the
/// client names a ruleset (and, optionally, a version), it never supplies comparison keys,
/// metric directions, mode data, or an information policy directly (Competition Protocol V1
/// section 15). Issue #10 owns the finalized challenge endpoint (Best-of-{1,3,5,7} subset
/// enforcement, information-policy selection, forfeit, etc.) - this use case only adds the
/// minimum ruleset selection required so a created series carries valid frozen rules.
/// </summary>
public sealed class CreateChallengeUseCase(
    IVersusSeriesStore seriesStore,
    IFriendshipStore friendshipStore,
    IRulesetCatalog rulesetCatalog,
    IClock clock)
{
    public async Task<SeriesView> ExecuteAsync(CreateChallengeRequest request, CancellationToken cancellationToken)
    {
        if (!await friendshipStore.AreFriendsAsync(request.ChallengerId, request.OpponentId, cancellationToken))
        {
            throw new FriendshipRequiredException("You can only challenge an accepted friend.");
        }

        var format = SeriesFormat.BestOf(request.TotalGames);
        var ruleset = rulesetCatalog.Resolve(request.RulesetId, request.RulesetVersion);
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

        await seriesStore.AddAsync(series, cancellationToken);

        return series.ToView(request.ChallengerId);
    }
}
