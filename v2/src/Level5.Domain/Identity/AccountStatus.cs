namespace Level5.Domain.Identity;

/// <summary>Lifecycle state of an <see cref="Account"/>. New accounts always start <see cref="Active"/>.</summary>
public enum AccountStatus
{
    Active = 0,
    Disabled = 1
}
