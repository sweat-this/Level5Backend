namespace Level5.Infrastructure.Persistence.Rows;

public sealed class FriendRequestRow
{
    public Guid Id { get; set; }
    public Guid FromPlayerId { get; set; }
    public Guid ToPlayerId { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
}
