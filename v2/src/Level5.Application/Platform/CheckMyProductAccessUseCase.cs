using Level5.Application.Abstractions;
using Level5.Domain.Ids;
using Level5.Domain.Platform;

namespace Level5.Application.Platform;

public sealed record CheckMyProductAccessRequest(PlayerId PlayerId, ProductId ProductId);

public sealed record ProductAccessResult(
    ProductId ProductId,
    bool HasAccess,
    EntitlementKind? Kind,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Checks the authenticated player's current product access. The API supplies PlayerId from its
/// server-side authentication boundary; no player identity is accepted from request data.
/// </summary>
public sealed class CheckMyProductAccessUseCase(IProductEntitlementStore store, IClock clock)
{
    public async Task<ProductAccessResult> ExecuteAsync(
        CheckMyProductAccessRequest request,
        CancellationToken cancellationToken)
    {
        var entitlement = await store.FindAsync(request.PlayerId, request.ProductId, cancellationToken);
        if (entitlement is null || !entitlement.HasAccess(clock.UtcNow))
        {
            return new ProductAccessResult(request.ProductId, HasAccess: false, Kind: null, ExpiresAt: null);
        }

        return new ProductAccessResult(
            entitlement.ProductId,
            HasAccess: true,
            entitlement.Kind,
            entitlement.ExpiresAt);
    }
}
