using Level5.Domain.Common;

namespace Level5.Domain.Competition;

/// <summary>
/// The immutable Protocol V1 rules snapshot a <see cref="VersusSeries"/> is created with and
/// carries for its entire lifetime (Competition Protocol V1 section 9). The server resolves and
/// freezes this once, at creation, from its own authoritative ruleset source - never from
/// client-supplied comparison keys, directions, mode data, or capabilities. A later change to
/// the live ruleset catalog must never retroactively alter an already-created series: the only
/// way to get a <see cref="FrozenRules"/> instance is <see cref="Create"/>, and nothing on this
/// type re-resolves against any external catalog afterward.
/// </summary>
public sealed class FrozenRules : IEquatable<FrozenRules>
{
    public int CompetitionProtocolVersion { get; }
    public string RulesetId { get; }
    public int RulesetVersion { get; }
    public int MinimumCompatibleVersion { get; }
    public string ModeId { get; }
    public InformationPolicy InformationPolicy { get; }
    public bool AlternatesFirstAttempt { get; }
    public IReadOnlyList<ComparisonKey> ComparisonKeys { get; }

    private FrozenRules(
        int competitionProtocolVersion, string rulesetId, int rulesetVersion, int minimumCompatibleVersion,
        string modeId, InformationPolicy informationPolicy, bool alternatesFirstAttempt, IReadOnlyList<ComparisonKey> comparisonKeys)
    {
        CompetitionProtocolVersion = competitionProtocolVersion;
        RulesetId = rulesetId;
        RulesetVersion = rulesetVersion;
        MinimumCompatibleVersion = minimumCompatibleVersion;
        ModeId = modeId;
        InformationPolicy = informationPolicy;
        AlternatesFirstAttempt = alternatesFirstAttempt;
        ComparisonKeys = comparisonKeys;
    }

    public static FrozenRules Create(
        int competitionProtocolVersion,
        string rulesetId,
        int rulesetVersion,
        int minimumCompatibleVersion,
        string modeId,
        InformationPolicy informationPolicy,
        bool alternatesFirstAttempt,
        IReadOnlyList<ComparisonKey> comparisonKeys)
    {
        if (string.IsNullOrWhiteSpace(rulesetId))
        {
            throw new InvalidFrozenRulesException("RulesetId cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(modeId))
        {
            throw new InvalidFrozenRulesException("ModeId cannot be empty.");
        }

        if (rulesetVersion < 1)
        {
            throw new InvalidFrozenRulesException("RulesetVersion must be at least 1.");
        }

        if (minimumCompatibleVersion < 1 || minimumCompatibleVersion > rulesetVersion)
        {
            throw new InvalidFrozenRulesException("MinimumCompatibleVersion must be between 1 and RulesetVersion.");
        }

        if (comparisonKeys is null || comparisonKeys.Count == 0)
        {
            throw new InvalidFrozenRulesException("At least one comparison key is required.");
        }

        if (comparisonKeys.Select(k => k.Metric).Distinct().Count() != comparisonKeys.Count)
        {
            throw new InvalidFrozenRulesException("ComparisonKeys must not repeat the same metric.");
        }

        return new FrozenRules(
            competitionProtocolVersion, rulesetId, rulesetVersion, minimumCompatibleVersion,
            modeId, informationPolicy, alternatesFirstAttempt, [.. comparisonKeys]);
    }

    public bool Equals(FrozenRules? other) =>
        other is not null
        && CompetitionProtocolVersion == other.CompetitionProtocolVersion
        && RulesetId == other.RulesetId
        && RulesetVersion == other.RulesetVersion
        && MinimumCompatibleVersion == other.MinimumCompatibleVersion
        && ModeId == other.ModeId
        && InformationPolicy == other.InformationPolicy
        && AlternatesFirstAttempt == other.AlternatesFirstAttempt
        && ComparisonKeys.SequenceEqual(other.ComparisonKeys);

    public override bool Equals(object? obj) => Equals(obj as FrozenRules);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(CompetitionProtocolVersion);
        hash.Add(RulesetId);
        hash.Add(RulesetVersion);
        hash.Add(MinimumCompatibleVersion);
        hash.Add(ModeId);
        hash.Add(InformationPolicy);
        hash.Add(AlternatesFirstAttempt);
        foreach (var key in ComparisonKeys)
        {
            hash.Add(key);
        }

        return hash.ToHashCode();
    }
}

public sealed class InvalidFrozenRulesException : DomainException
{
    public InvalidFrozenRulesException(string message) : base(message)
    {
    }
}
