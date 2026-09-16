using Level5.Domain.Common;
using Level5.Domain.Ids;

namespace Level5.Domain.Social;

/// <summary>
/// A pending (or resolved) invitation from one player to another to become friends.
/// Only the recipient may accept/decline; only the sender may cancel while it is pending.
/// </summary>
public sealed class FriendRequest
{
    public FriendRequestId Id { get; private set; }
    public PlayerId FromPlayerId { get; private set; }
    public PlayerId ToPlayerId { get; private set; }
    public FriendRequestStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RespondedAt { get; private set; }

    private FriendRequest(
        FriendRequestId id,
        PlayerId fromPlayerId,
        PlayerId toPlayerId,
        FriendRequestStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset? respondedAt)
    {
        Id = id;
        FromPlayerId = fromPlayerId;
        ToPlayerId = toPlayerId;
        Status = status;
        CreatedAt = createdAt;
        RespondedAt = respondedAt;
    }

    public static FriendRequest Create(PlayerId fromPlayerId, PlayerId toPlayerId, DateTimeOffset now)
    {
        if (fromPlayerId == toPlayerId)
        {
            throw new InvalidFriendRequestException("A player cannot send a friend request to themselves.");
        }

        return new FriendRequest(FriendRequestId.New(), fromPlayerId, toPlayerId, FriendRequestStatus.Pending, now, null);
    }

    public static FriendRequest Rehydrate(
        FriendRequestId id,
        PlayerId fromPlayerId,
        PlayerId toPlayerId,
        FriendRequestStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset? respondedAt)
        => new(id, fromPlayerId, toPlayerId, status, createdAt, respondedAt);

    /// <summary>Accepts the request. Returns the new <see cref="Friendship"/> to be persisted alongside it.</summary>
    public Friendship Accept(PlayerId actingPlayerId, DateTimeOffset now)
    {
        if (actingPlayerId != ToPlayerId)
        {
            throw new FriendRequestAuthorizationException("Only the recipient can accept a friend request.");
        }

        EnsurePending();

        Status = FriendRequestStatus.Accepted;
        RespondedAt = now;
        return Friendship.Between(FromPlayerId, ToPlayerId, now);
    }

    public void Decline(PlayerId actingPlayerId, DateTimeOffset now)
    {
        if (actingPlayerId != ToPlayerId)
        {
            throw new FriendRequestAuthorizationException("Only the recipient can decline a friend request.");
        }

        EnsurePending();

        Status = FriendRequestStatus.Declined;
        RespondedAt = now;
    }

    public void Cancel(PlayerId actingPlayerId, DateTimeOffset now)
    {
        if (actingPlayerId != FromPlayerId)
        {
            throw new FriendRequestAuthorizationException("Only the sender can cancel a friend request.");
        }

        EnsurePending();

        Status = FriendRequestStatus.Cancelled;
        RespondedAt = now;
    }

    private void EnsurePending()
    {
        if (Status != FriendRequestStatus.Pending)
        {
            throw new IllegalFriendRequestTransitionException(
                $"Friend request is already {Status} and cannot be changed.");
        }
    }
}

public sealed class InvalidFriendRequestException : DomainException
{
    public InvalidFriendRequestException(string message) : base(message)
    {
    }
}

public sealed class FriendRequestAuthorizationException : DomainException
{
    public FriendRequestAuthorizationException(string message) : base(message)
    {
    }
}

public sealed class IllegalFriendRequestTransitionException : DomainException
{
    public IllegalFriendRequestTransitionException(string message) : base(message)
    {
    }
}
