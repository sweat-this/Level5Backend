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
