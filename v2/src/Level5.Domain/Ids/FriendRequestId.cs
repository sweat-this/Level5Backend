namespace Level5.Domain.Ids;

public readonly record struct FriendRequestId(Guid Value)
{
    public static FriendRequestId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
