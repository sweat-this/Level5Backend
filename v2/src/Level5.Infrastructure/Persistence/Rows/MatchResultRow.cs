namespace Level5.Infrastructure.Persistence.Rows;

/// <summary>
/// Hybrid relational/document row for a <see cref="Level5.Domain.Results.MatchResult"/>.
/// Identity/filter metadata is relational; the named metric and modifier sets - which have no
/// query patterns of their own in this slice - are stored as JSONB blobs.
/// </summary>
public sealed class MatchResultRow
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public Guid ClientResultId { get; set; }
    public int ModeId { get; set; }
    public int LevelId { get; set; }
    public required string CharacterId { get; set; }
    public required string ClientVersion { get; set; }
    public required string Platform { get; set; }
    public required string MetricsJson { get; set; }
    public required string ModifiersJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
