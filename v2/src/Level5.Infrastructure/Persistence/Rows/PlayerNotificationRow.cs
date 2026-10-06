namespace Level5.Infrastructure.Persistence.Rows;

public sealed class PlayerNotificationRow
{
    public Guid Id { get; set; }
    public Guid RecipientPlayerId { get; set; }
    public required string Source { get; set; }
    public required string Kind { get; set; }
    public required string Title { get; set; }
    public string? Body { get; set; }
    public string? ActionPath { get; set; }
    public required string SourceEventKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}
