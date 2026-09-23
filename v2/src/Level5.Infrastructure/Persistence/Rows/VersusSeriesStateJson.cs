namespace Level5.Infrastructure.Persistence.Rows;

/// <summary>
/// Wire shape of <see cref="VersusSeriesRow.StateJson"/>. <see cref="SchemaVersion"/> is checked
/// on every read (see <see cref="Repositories.VersusSeriesStore"/>) so a row persisted under an
/// unsupported shape fails loudly instead of being silently misread. Version 2 is the Competition
/// Protocol V1 shape: it adds the frozen <see cref="Rules"/> snapshot and replaces each attempt's
/// single integer score with a named-metric <see cref="AttemptJson.Result"/> bag. Version 1 rows
/// (pre-issue-#9, no frozen rules at all) are rejected rather than migrated - see
/// v2/README.md's correspondence-persistence section for why that is safe.
/// </summary>
public sealed class VersusSeriesStateJson
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public required FrozenRulesJson Rules { get; set; }
    public List<GameRoundJson> Rounds { get; set; } = [];
}

public sealed class FrozenRulesJson
{
    public int CompetitionProtocolVersion { get; set; }
    public required string RulesetId { get; set; }
    public int RulesetVersion { get; set; }
    public int MinimumCompatibleVersion { get; set; }
    public required string ModeId { get; set; }
    public required string InformationPolicy { get; set; }
    public bool AlternatesFirstAttempt { get; set; }
    public List<ComparisonKeyJson> ComparisonKeys { get; set; } = [];
}

public sealed class ComparisonKeyJson
{
    public required string Metric { get; set; }
    public required string Direction { get; set; }
}

public sealed class GameRoundJson
{
    public int GameNumber { get; set; }
    public AttemptJson? ChallengerAttempt { get; set; }
    public AttemptJson? OpponentAttempt { get; set; }
}

public sealed class AttemptJson
{
    public required Guid Id { get; set; }
    public required Guid PlayerId { get; set; }
    public required string Status { get; set; }

    /// <summary>Named metric -> value, e.g. <c>{"Score": 90}</c>. Keyed by <see cref="Level5.Domain.Competition.ResultMetric"/> name, never an ordinal or a positional array.</summary>
    public Dictionary<string, double>? Result { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>Minimal shape used to read <see cref="VersusSeriesStateJson.SchemaVersion"/> before committing to a full, version-specific deserialize.</summary>
public sealed class SeriesSchemaVersionEnvelope
{
    public int SchemaVersion { get; set; }
}
