namespace Level5.Infrastructure.Persistence.Rows;

/// <summary>
/// EF-mapped shape of an <see cref="Level5.Domain.Identity.Account"/>. Kept separate from the
/// domain type so the domain never has to expose a parameterless constructor or public setters
/// just to satisfy the ORM; <c>AccountStore</c> maps between the two explicitly.
/// </summary>
public sealed class AccountRow
{
    public Guid Id { get; set; }
    public required string Username { get; set; }
    public required string UsernameCanonical { get; set; }
    public required string PasswordHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
