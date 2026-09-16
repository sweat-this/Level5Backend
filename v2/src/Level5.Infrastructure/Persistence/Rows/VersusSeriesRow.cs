namespace Level5.Infrastructure.Persistence.Rows;

/// <summary>
/// Hybrid relational/document row for a <see cref="Level5.Domain.Competition.VersusSeries"/>.
/// Searchable/indexed metadata (participants, status, revision) is relational; the nested
/// per-game attempt state - which has no query patterns of its own and would otherwise require
/// several normalized child tables - is stored as a JSONB blob in <see cref="StateJson"/>.
/// </summary>
public sealed class VersusSeriesRow
{
    public Guid Id { get; set; }
    public Guid ChallengerId { get; set; }
    public Guid OpponentId { get; set; }
    public required string Status { get; set; }
    public int TotalGames { get; set; }
    public int CurrentGameNumber { get; set; }
    public Guid? WinnerId { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public required string StateJson { get; set; }
}
