using Level5.Domain.Ids;
using Level5.Domain.Platform;

namespace Level5.Application.Abstractions;

public interface IProductEntitlementStore
{
    Task<ProductEntitlement?> FindAsync(
        PlayerId playerId,
        ProductId productId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductEntitlement>> ListActiveAsync(
        PlayerId playerId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task AddAsync(ProductEntitlement entitlement, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a mutated current entitlement only when its stored revision still equals the
    /// revision that was loaded before the domain transition.
    /// </summary>
    Task<bool> TrySaveAsync(
        ProductEntitlement entitlement,
        long expectedRevision,
        CancellationToken cancellationToken);
}
