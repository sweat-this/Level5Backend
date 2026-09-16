namespace Level5.Infrastructure.Persistence.Rows;

/// <summary>
/// EF-mapped shape of an <see cref="Level5.Domain.Identity.AuthSession"/>. Stores only the
/// one-way <see cref="RefreshTokenHash"/> - the raw refresh secret is never persisted.
/// </summary>
public sealed class AuthSessionRow
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public required string RefreshTokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public long Revision { get; set; }
}
