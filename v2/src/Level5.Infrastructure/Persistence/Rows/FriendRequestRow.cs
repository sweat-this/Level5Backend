namespace Level5.Infrastructure.Persistence.Rows;

public sealed class FriendRequestRow
{
    public Guid Id { get; set; }
    public Guid FromPlayerId { get; set; }
    public Guid ToPlayerId { get; set; }

    // Canonical (order-independent) pair, derived from FromPlayerId/ToPlayerId at insert time via
    // Friendship.Order - direction still lives in FromPlayerId/ToPlayerId above; these two columns
    // exist only so a unique partial index can block a Pending request in the *other* direction
    // too (A->B and B->A must conflict), which a unique index on (FromPlayerId, ToPlayerId) alone
    // cannot express.
    public Guid LowerPlayerId { get; set; }
    public Guid UpperPlayerId { get; set; }

    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }

    // Optimistic-concurrency token, used exactly like VersusSeriesRow.Revision /
    // AuthSessionRow.Revision - see FriendRequest.Revision.
    public long Revision { get; set; }
}
