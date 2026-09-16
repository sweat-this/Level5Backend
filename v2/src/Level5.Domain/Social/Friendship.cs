using Level5.Domain.Ids;

namespace Level5.Domain.Social;

/// <summary>
/// An accepted, symmetric friendship between two players. <see cref="LowerPlayerId"/> and
/// <see cref="UpperPlayerId"/> are canonically ordered (by underlying GUID) so a unique
/// (LowerPlayerId, UpperPlayerId) constraint at the persistence layer prevents a duplicate
/// friendship from being created in either direction.
/// </summary>
public sealed class Friendship
{
    public FriendshipId Id { get; private set; }
    public PlayerId LowerPlayerId { get; private set; }
    public PlayerId UpperPlayerId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private Friendship(FriendshipId id, PlayerId lower, PlayerId upper, DateTimeOffset createdAt)
    {
        LowerPlayerId = lower;
        UpperPlayerId = upper;
        Id = id;
        CreatedAt = createdAt;
    }

    public static Friendship Between(PlayerId playerA, PlayerId playerB, DateTimeOffset now)
    {
        if (playerA == playerB)
        {
            throw new InvalidOperationException("A player cannot be friends with themselves.");
        }

        var (lower, upper) = Order(playerA, playerB);
        return new Friendship(FriendshipId.New(), lower, upper, now);
    }

    public static Friendship Rehydrate(FriendshipId id, PlayerId lower, PlayerId upper, DateTimeOffset createdAt)
        => new(id, lower, upper, createdAt);

    public bool Involves(PlayerId playerId) => LowerPlayerId == playerId || UpperPlayerId == playerId;

    public PlayerId OtherPlayer(PlayerId playerId)
    {
        if (LowerPlayerId == playerId)
        {
            return UpperPlayerId;
        }

        if (UpperPlayerId == playerId)
        {
            return LowerPlayerId;
        }

        throw new InvalidOperationException("Player is not part of this friendship.");
    }

    public static (PlayerId Lower, PlayerId Upper) Order(PlayerId a, PlayerId b)
        => a.Value.CompareTo(b.Value) <= 0 ? (a, b) : (b, a);
}
