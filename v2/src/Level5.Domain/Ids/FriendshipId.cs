namespace Level5.Domain.Ids;

public readonly record struct FriendshipId(Guid Value)
{
    public static FriendshipId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
