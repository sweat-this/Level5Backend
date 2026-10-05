namespace Level5.Infrastructure.Persistence.Rows;

public sealed class PasswordResetChallengeRow
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public required string TargetEmail { get; set; }
    public required string TargetEmailCanonical { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public long Revision { get; set; }
}
