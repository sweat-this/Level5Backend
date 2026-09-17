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

    /// <summary>
    /// The challenger-scoped create idempotency key (issue #10) - null for rows created without
    /// one (e.g. seeded directly by tests). Unique together with <see cref="ChallengerId"/> so a
    /// retried create for the same challenger and key finds this row instead of inserting a
    /// duplicate series; a concurrent duplicate insert of the same pair fails the unique
    /// constraint and is translated to <see cref="Level5.Application.Common.ConflictException"/>.
    /// </summary>
    public Guid? ClientRequestId { get; set; }
}
