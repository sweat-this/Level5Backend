namespace Level5.Infrastructure.Persistence.Rows;

/// <summary>
/// Wire shape of <see cref="VersusSeriesRow.StateJson"/>. <see cref="SchemaVersion"/> lets a
/// future change to this shape be detected and migrated explicitly in code instead of silently
/// misreading older rows.
/// </summary>
public sealed class VersusSeriesStateJson
{
    public int SchemaVersion { get; set; } = 1;
    public List<GameRoundJson> Rounds { get; set; } = [];
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
    public int? Score { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
