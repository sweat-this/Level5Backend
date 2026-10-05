using Level5.Domain.Ids;
using Level5.Domain.Platform;
using Xunit;

namespace Level5.Domain.Tests.Platform;

public sealed class ProductEntitlementTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly ProductId Level5 = ProductId.Create("level5");
    private readonly PlayerId _playerId = PlayerId.New();

    [Fact]
    public void Permanent_grant_is_active_and_starts_at_revision_zero()
    {
        var entitlement = ProductEntitlement.Grant(_playerId, Level5, EntitlementKind.Owned, Now);

        Assert.True(entitlement.HasAccess(Now));
        Assert.Null(entitlement.ExpiresAt);
        Assert.Null(entitlement.RevokedAt);
        Assert.Equal(0, entitlement.Revision);
    }

    [Fact]
    public void Expiring_grant_is_active_before_its_expiry()
    {
        var entitlement = ProductEntitlement.Grant(
            _playerId, Level5, EntitlementKind.Demo, Now, Now.AddHours(1));

        Assert.True(entitlement.HasAccess(Now.AddMinutes(59)));
    }

    [Fact]
    public void Exact_expiry_cutoff_is_not_active()
    {
        var expiresAt = Now.AddHours(1);
        var entitlement = ProductEntitlement.Grant(
            _playerId, Level5, EntitlementKind.Beta, Now, expiresAt);

        Assert.False(entitlement.HasAccess(expiresAt));
    }

    [Fact]
    public void Revoked_grant_is_not_active()
    {
        var entitlement = ProductEntitlement.Grant(_playerId, Level5, EntitlementKind.Playtest, Now);

        entitlement.Revoke(Now.AddMinutes(1));

        Assert.False(entitlement.HasAccess(Now.AddMinutes(1)));
        Assert.Equal(1, entitlement.Revision);
    }

    [Fact]
    public void Revoke_is_idempotent()
    {
        var entitlement = ProductEntitlement.Grant(_playerId, Level5, EntitlementKind.Owned, Now);
        var revokedAt = Now.AddMinutes(1);

        entitlement.Revoke(revokedAt);
        entitlement.Revoke(revokedAt.AddMinutes(1));

        Assert.Equal(revokedAt, entitlement.RevokedAt);
        Assert.Equal(1, entitlement.Revision);
    }

    [Fact]
    public void Regrant_replaces_current_grant_clears_revocation_and_advances_revision()
    {
        var entitlement = ProductEntitlement.Grant(_playerId, Level5, EntitlementKind.Demo, Now);
        entitlement.Revoke(Now.AddMinutes(1));
        var regrantedAt = Now.AddMinutes(2);
        var expiresAt = Now.AddDays(1);

        entitlement.Regrant(EntitlementKind.Owned, regrantedAt, expiresAt);

        Assert.Equal(EntitlementKind.Owned, entitlement.Kind);
        Assert.Equal(regrantedAt, entitlement.GrantedAt);
        Assert.Equal(expiresAt, entitlement.ExpiresAt);
        Assert.Null(entitlement.RevokedAt);
        Assert.Equal(2, entitlement.Revision);
        Assert.True(entitlement.HasAccess(regrantedAt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Grant_rejects_expiry_at_or_before_grant_time(int expiryOffsetSeconds)
    {
        Assert.Throws<InvalidEntitlementExpiryException>(() => ProductEntitlement.Grant(
            _playerId,
            Level5,
            EntitlementKind.Demo,
            Now,
            Now.AddSeconds(expiryOffsetSeconds)));
    }

    [Fact]
    public void Rejected_regrant_does_not_change_current_state_or_revision()
    {
        var entitlement = ProductEntitlement.Grant(_playerId, Level5, EntitlementKind.Owned, Now);

        Assert.Throws<InvalidEntitlementExpiryException>(() => entitlement.Regrant(
            EntitlementKind.Demo,
            Now.AddHours(1),
            Now.AddMinutes(30)));

        Assert.Equal(EntitlementKind.Owned, entitlement.Kind);
        Assert.Equal(Now, entitlement.GrantedAt);
        Assert.Null(entitlement.ExpiresAt);
        Assert.Equal(0, entitlement.Revision);
    }
}
