namespace Level5.Domain.Ids;

/// <summary>Opaque identity for a persistent <see cref="Identity.AuthSession"/>.</summary>
public readonly record struct AuthSessionId(Guid Value)
{
    public static AuthSessionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
