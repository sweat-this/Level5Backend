namespace Level5.Domain.Ids;

/// <summary>Opaque identity for a private authentication/security account.</summary>
public readonly record struct AccountId(Guid Value)
{
    public static AccountId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
