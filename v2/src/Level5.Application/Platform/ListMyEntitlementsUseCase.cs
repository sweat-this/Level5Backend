using Level5.Application.Abstractions;
using Level5.Domain.Ids;
using Level5.Domain.Platform;

namespace Level5.Application.Platform;

public sealed record ListMyEntitlementsRequest(PlayerId PlayerId);

public sealed record ActiveEntitlementResult(
    ProductId ProductId,
    EntitlementKind Kind,
    DateTimeOffset? ExpiresAt);

/// <summary>Lists only the authenticated player's current, effective product access.</summary>
public sealed class ListMyEntitlementsUseCase(IProductEntitlementStore store, IClock clock)
{
    public async Task<IReadOnlyList<ActiveEntitlementResult>> ExecuteAsync(
        ListMyEntitlementsRequest request,
        CancellationToken cancellationToken)
    {
        var entitlements = await store.ListActiveAsync(request.PlayerId, clock.UtcNow, cancellationToken);
        return [.. entitlements.Select(entitlement => new ActiveEntitlementResult(
            entitlement.ProductId,
            entitlement.Kind,
            entitlement.ExpiresAt))];
    }
}
