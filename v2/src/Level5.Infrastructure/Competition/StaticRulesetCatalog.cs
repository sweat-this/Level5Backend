using Level5.Application.Abstractions;
using Level5.Application.Competition;
using Level5.Domain.Competition;

namespace Level5.Infrastructure.Competition;

/// <summary>
/// The server's authoritative ruleset catalog: hardcoded and in-memory for now, deliberately
/// minimal per issue #9's boundary with issue #10 (which owns how this catalog is actually
/// administered/populated - see v2/docs/competition-protocol/README.md section 22's "ruleset
/// content authority" open question). Every entry mirrors a real, already-shipped Unity ruleset
/// per the Competition Protocol V1 audit (issue #8), not a hypothetical example.
/// </summary>
public sealed class StaticRulesetCatalog : IRulesetCatalog
{
    private sealed record CatalogEntry(
        int CurrentVersion,
        int MinimumCompatibleVersion,
        string ModeId,
        InformationPolicy InformationPolicy,
        bool AlternatesFirstAttempt,
        IReadOnlyList<ComparisonKey> ComparisonKeys);

    private static readonly Dictionary<string, CatalogEntry> Entries = new()
    {
        ["score-only"] = new CatalogEntry(
            CurrentVersion: 1,
            MinimumCompatibleVersion: 1,
            ModeId: "mode-score-only",
            InformationPolicy: InformationPolicy.SealedAttempt,
            AlternatesFirstAttempt: false,
            ComparisonKeys: [new ComparisonKey(ResultMetric.Score, MetricDirection.HigherWins)]),

        // "score-only" above has no counterpart in the Unity client's own ruleset registry
        // (Assets/Scripts/versus/Level5Versus/DefaultCompetitiveRulesets.cs) - discovered during
        // issue #159 live Unity-client certification: RemoteAttemptDescriptorMapper.Map resolves a
        // remote attempt's ruleset by looking the server's RulesetId up in Unity's own
        // VersusCatalogs.Rulesets, which has never had a "score-only" entry, so no client build
        // could ever actually launch a match for the server's only advertised ruleset - contrary to
        // this class's own doc comment ("every entry mirrors a real, already-shipped Unity
        // ruleset"). "most-points" is that real, already-shipped Unity ruleset (id/comparison shape
        // match DefaultCompetitiveRulesets.Score("most-points", GameModeId.TotalPoints, ...)
        // exactly), added here rather than renaming "score-only" so the existing catalog entry and
        // everything that already depends on its id (tests, fixtures) is left untouched.
        ["most-points"] = new CatalogEntry(
            CurrentVersion: 1,
            MinimumCompatibleVersion: 1,
            ModeId: "mode-most-points",
            InformationPolicy: InformationPolicy.SealedAttempt,
            AlternatesFirstAttempt: false,
            ComparisonKeys: [new ComparisonKey(ResultMetric.Score, MetricDirection.HigherWins)])
    };

    public RulesetDefinition Resolve(string rulesetId, int? requestedVersion)
    {
        if (!Entries.TryGetValue(rulesetId, out var entry))
        {
            throw new UnknownRulesetException($"Ruleset '{rulesetId}' is not recognized.");
        }

        var version = requestedVersion ?? entry.CurrentVersion;
        if (version < entry.MinimumCompatibleVersion || version > entry.CurrentVersion)
        {
            throw new RulesetVersionUnsupportedException(
                $"Ruleset '{rulesetId}' version {version} is not supported (supported range: {entry.MinimumCompatibleVersion}-{entry.CurrentVersion}).");
        }

        return new RulesetDefinition(
            rulesetId, version, entry.MinimumCompatibleVersion, entry.ModeId,
            entry.InformationPolicy, entry.AlternatesFirstAttempt, entry.ComparisonKeys);
    }
}
