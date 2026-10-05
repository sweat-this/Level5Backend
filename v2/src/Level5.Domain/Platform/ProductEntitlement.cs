using Level5.Domain.Common;
using Level5.Domain.Ids;

namespace Level5.Domain.Platform;

/// <summary>
/// The current access grant for one player and product. Revocation is lifecycle state rather
/// than an entitlement kind, and <see cref="Revision"/> is the optimistic-concurrency authority.
/// </summary>
public sealed class ProductEntitlement
{
    public PlayerId PlayerId { get; private set; }
    public ProductId ProductId { get; private set; }
    public EntitlementKind Kind { get; private set; }
    public DateTimeOffset GrantedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public long Revision { get; private set; }

    private ProductEntitlement(
        PlayerId playerId,
        ProductId productId,
        EntitlementKind kind,
        DateTimeOffset grantedAt,
        DateTimeOffset? expiresAt,
        DateTimeOffset? revokedAt,
        long revision)
    {
        PlayerId = playerId;
        ProductId = productId;
        Kind = kind;
        GrantedAt = grantedAt;
        ExpiresAt = expiresAt;
        RevokedAt = revokedAt;
        Revision = revision;
    }

    public static ProductEntitlement Grant(
        PlayerId playerId,
        ProductId productId,
        EntitlementKind kind,
        DateTimeOffset now,
        DateTimeOffset? expiresAt = null)
    {
        ValidateKind(kind);
        ValidateExpiry(now, expiresAt);
        return new ProductEntitlement(playerId, productId, kind, now, expiresAt, revokedAt: null, revision: 0);
    }

    public static ProductEntitlement Rehydrate(
        PlayerId playerId,
        ProductId productId,
        EntitlementKind kind,
        DateTimeOffset grantedAt,
        DateTimeOffset? expiresAt,
        DateTimeOffset? revokedAt,
        long revision)
    {
        ValidateKind(kind);
        return new ProductEntitlement(playerId, productId, kind, grantedAt, expiresAt, revokedAt, revision);
    }

    public bool HasAccess(DateTimeOffset now)
        => RevokedAt is null && (ExpiresAt is null || now < ExpiresAt.Value);

    /// <summary>Replaces the current grant deliberately; no entitlement-kind precedence exists.</summary>
    public void Regrant(EntitlementKind kind, DateTimeOffset now, DateTimeOffset? expiresAt = null)
    {
        ValidateKind(kind);
        ValidateExpiry(now, expiresAt);
        Kind = kind;
        GrantedAt = now;
        ExpiresAt = expiresAt;
        RevokedAt = null;
        Revision++;
    }

    /// <summary>Idempotent: a repeated revocation does not alter its timestamp or revision.</summary>
    public void Revoke(DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
        Revision++;
    }

    private static void ValidateKind(EntitlementKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new InvalidEntitlementKindException("Entitlement kind is not recognized.");
        }
    }

    private static void ValidateExpiry(DateTimeOffset grantedAt, DateTimeOffset? expiresAt)
    {
        if (expiresAt is not null && expiresAt <= grantedAt)
        {
            throw new InvalidEntitlementExpiryException("Entitlement expiry must be later than its grant time.");
        }
    }
}

public sealed class InvalidEntitlementKindException : DomainException
{
    public override string Code => "invalid_entitlement_kind";

    public InvalidEntitlementKindException(string message) : base(message)
    {
    }
}

public sealed class InvalidEntitlementExpiryException : DomainException
{
    public override string Code => "invalid_entitlement_expiry";

    public InvalidEntitlementExpiryException(string message) : base(message)
    {
    }
}
