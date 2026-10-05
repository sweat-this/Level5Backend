using Level5.Application.Abstractions;
using Level5.Domain.Ids;
using Level5.Domain.Platform;
using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.Persistence.Repositories;

public sealed class ProductEntitlementStore(Level5V2DbContext db) : IProductEntitlementStore
{
    public async Task<ProductEntitlement?> FindAsync(
        PlayerId playerId,
        ProductId productId,
        CancellationToken cancellationToken)
    {
        var row = await db.ProductEntitlements
            .AsNoTracking()
            .SingleOrDefaultAsync(
                entitlement => entitlement.PlayerId == playerId.Value &&
                               entitlement.ProductId == productId.Value,
                cancellationToken);

        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyList<ProductEntitlement>> ListActiveAsync(
        PlayerId playerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var rows = await db.ProductEntitlements
            .AsNoTracking()
            .Where(entitlement => entitlement.PlayerId == playerId.Value &&
                                  entitlement.RevokedAt == null &&
                                  (entitlement.ExpiresAt == null || entitlement.ExpiresAt > now))
            .OrderBy(entitlement => entitlement.ProductId)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(ToDomain)];
    }

    public async Task AddAsync(ProductEntitlement entitlement, CancellationToken cancellationToken)
        => await db.ProductEntitlements.AddAsync(ToRow(entitlement), cancellationToken);

    public async Task<bool> TrySaveAsync(
        ProductEntitlement entitlement,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var affected = await db.ProductEntitlements
            .Where(row => row.PlayerId == entitlement.PlayerId.Value &&
                          row.ProductId == entitlement.ProductId.Value &&
                          row.Revision == expectedRevision)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Kind, entitlement.Kind.ToString())
                .SetProperty(row => row.GrantedAt, entitlement.GrantedAt)
                .SetProperty(row => row.ExpiresAt, entitlement.ExpiresAt)
                .SetProperty(row => row.RevokedAt, entitlement.RevokedAt)
                .SetProperty(row => row.Revision, entitlement.Revision),
                cancellationToken);

        return affected == 1;
    }

    private static ProductEntitlementRow ToRow(ProductEntitlement entitlement) => new()
    {
        PlayerId = entitlement.PlayerId.Value,
        ProductId = entitlement.ProductId.Value,
        Kind = entitlement.Kind.ToString(),
        GrantedAt = entitlement.GrantedAt,
        ExpiresAt = entitlement.ExpiresAt,
        RevokedAt = entitlement.RevokedAt,
        Revision = entitlement.Revision
    };

    private static ProductEntitlement ToDomain(ProductEntitlementRow row)
        => ProductEntitlement.Rehydrate(
            new PlayerId(row.PlayerId),
            ProductId.Create(row.ProductId),
            Enum.Parse<EntitlementKind>(row.Kind),
            row.GrantedAt,
            row.ExpiresAt,
            row.RevokedAt,
            row.Revision);
}
