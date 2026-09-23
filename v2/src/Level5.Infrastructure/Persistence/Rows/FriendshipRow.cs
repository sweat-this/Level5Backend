namespace Level5.Infrastructure.Persistence.Rows;

public sealed class FriendshipRow
{
    public Guid Id { get; set; }
    public Guid LowerPlayerId { get; set; }
    public Guid UpperPlayerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
