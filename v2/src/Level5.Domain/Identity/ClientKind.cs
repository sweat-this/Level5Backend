namespace Level5.Domain.Identity;

/// <summary>Coarse, caller-declared display metadata. Never use this value for authorization.</summary>
public enum ClientKind
{
    Unknown = 0,
    Web = 1,
    Unity = 2
}
