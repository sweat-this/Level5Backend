namespace Level5.Infrastructure.Persistence.Rows;

public sealed class BugReportRow
{
    public Guid Id { get; set; }
    public Guid ReporterPlayerId { get; set; }
    public Guid ClientSubmissionId { get; set; }
    public required string GameKey { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public string? Category { get; set; }
    public string? StepsToReproduce { get; set; }
    public string? GameVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
