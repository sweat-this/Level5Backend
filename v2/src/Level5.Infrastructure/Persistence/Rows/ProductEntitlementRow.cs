namespace Level5.Infrastructure.Persistence.Rows;

/// <summary>EF-mapped current entitlement keyed by the player/product pair.</summary>
public sealed class ProductEntitlementRow
{
    public Guid PlayerId { get; set; }
    public required string ProductId { get; set; }
    public required string Kind { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public long Revision { get; set; }
}
