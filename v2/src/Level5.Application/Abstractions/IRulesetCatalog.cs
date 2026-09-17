using Level5.Domain.Competition;

namespace Level5.Application.Abstractions;

/// <summary>
/// Server-authoritative source of ruleset content. A client may name a ruleset id (and,
/// optionally, a version it was built against) but never dictates comparison keys, metric
/// directions, mode identity, or capabilities - this port resolves those from the server's own
/// catalog and rejects a request naming an unknown ruleset or a version outside what the server
/// still supports (Competition Protocol V1 section 15). Issue #10 owns how this catalog itself
/// is administered/populated; this seam exists only so issue #9 can persist a valid frozen
/// Protocol V1 aggregate instead of a rules-less one.
/// </summary>
public interface IRulesetCatalog
{
    /// <exception cref="Level5.Application.Competition.UnknownRulesetException">The ruleset id is not recognized.</exception>
    /// <exception cref="Level5.Application.Competition.RulesetVersionUnsupportedException">The requested (or current) version is outside the range this build can still play.</exception>
    RulesetDefinition Resolve(string rulesetId, int? requestedVersion);
}

public sealed record RulesetDefinition(
    string RulesetId,
    int RulesetVersion,
    int MinimumCompatibleVersion,
    string ModeId,
    InformationPolicy InformationPolicy,
    bool AlternatesFirstAttempt,
    IReadOnlyList<ComparisonKey> ComparisonKeys);
