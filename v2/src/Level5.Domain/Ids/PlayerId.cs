namespace Level5.Domain.Ids;

/// <summary>Opaque identity for a public in-game <see cref="Players.PlayerProfile"/>.</summary>
public readonly record struct PlayerId(Guid Value)
{
    public static PlayerId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
