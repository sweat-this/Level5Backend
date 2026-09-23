namespace Level5.Infrastructure.Persistence.Rows;

public sealed class PlayerProfileRow
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public required string DisplayName { get; set; }
    public required string Tag { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
