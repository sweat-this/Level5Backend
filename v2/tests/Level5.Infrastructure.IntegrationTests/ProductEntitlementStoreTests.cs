using Level5.Domain.Platform;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ProductEntitlementStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Grant_regrant_and_revoke_persist_as_one_current_row()
    {
        await using var db = fixture.CreateDbContext();
        var playerId = await PlayerSeeding.CreatePlayerAsync(db, "entlife", Now);
        var productId = ProductId.Create("level5");
        var store = new ProductEntitlementStore(db);
        var entitlement = ProductEntitlement.Grant(
            playerId, productId, EntitlementKind.Demo, Now, Now.AddDays(1));

        await store.AddAsync(entitlement, CancellationToken.None);
        await db.SaveChangesAsync();

        var expectedRevision = entitlement.Revision;
        entitlement.Regrant(EntitlementKind.Owned, Now.AddHours(1));
        Assert.True(await store.TrySaveAsync(entitlement, expectedRevision, CancellationToken.None));

        expectedRevision = entitlement.Revision;
        entitlement.Revoke(Now.AddHours(2));
        Assert.True(await store.TrySaveAsync(entitlement, expectedRevision, CancellationToken.None));

        await using var verifyDb = fixture.CreateDbContext();
        var persisted = await new ProductEntitlementStore(verifyDb).FindAsync(
            playerId, productId, CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Equal(EntitlementKind.Owned, persisted.Kind);
        Assert.Equal(Now.AddHours(1), persisted.GrantedAt);
        Assert.Null(persisted.ExpiresAt);
        Assert.Equal(Now.AddHours(2), persisted.RevokedAt);
        Assert.Equal(2, persisted.Revision);
        Assert.Single(await verifyDb.ProductEntitlements
            .Where(row => row.PlayerId == playerId.Value && row.ProductId == productId.Value)
            .ToListAsync());
    }

    [Fact]
    public async Task Composite_key_rejects_a_second_row_for_the_same_player_and_product()
    {
        var productId = ProductId.Create("level5");
        Level5.Domain.Ids.PlayerId playerId;

        await using (var firstDb = fixture.CreateDbContext())
        {
            playerId = await PlayerSeeding.CreatePlayerAsync(firstDb, "entdupe", Now);
            await new ProductEntitlementStore(firstDb).AddAsync(
                ProductEntitlement.Grant(playerId, productId, EntitlementKind.Demo, Now),
                CancellationToken.None);
            await firstDb.SaveChangesAsync();
        }

        await using var secondDb = fixture.CreateDbContext();
        await new ProductEntitlementStore(secondDb).AddAsync(
            ProductEntitlement.Grant(playerId, productId, EntitlementKind.Owned, Now.AddMinutes(1)),
            CancellationToken.None);

        await Assert.ThrowsAsync<DbUpdateException>(() => secondDb.SaveChangesAsync());
    }

    [Fact]
    public async Task Player_profile_foreign_key_rejects_an_unknown_player()
    {
        await using var db = fixture.CreateDbContext();
        await new ProductEntitlementStore(db).AddAsync(
            ProductEntitlement.Grant(
                Level5.Domain.Ids.PlayerId.New(),
                ProductId.Create("level5"),
                EntitlementKind.Owned,
                Now),
            CancellationToken.None);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Competing_updates_cannot_silently_overwrite_each_other()
    {
        var productId = ProductId.Create("level5");
        Level5.Domain.Ids.PlayerId playerId;

        await using (var seedDb = fixture.CreateDbContext())
        {
            playerId = await PlayerSeeding.CreatePlayerAsync(seedDb, "entrace", Now);
            await new ProductEntitlementStore(seedDb).AddAsync(
                ProductEntitlement.Grant(playerId, productId, EntitlementKind.Demo, Now),
                CancellationToken.None);
            await seedDb.SaveChangesAsync();
        }

        await using var firstDb = fixture.CreateDbContext();
        await using var secondDb = fixture.CreateDbContext();
        var firstStore = new ProductEntitlementStore(firstDb);
        var secondStore = new ProductEntitlementStore(secondDb);
        var first = await firstStore.FindAsync(playerId, productId, CancellationToken.None);
        var second = await secondStore.FindAsync(playerId, productId, CancellationToken.None);
        Assert.NotNull(first);
        Assert.NotNull(second);

        first.Regrant(EntitlementKind.Owned, Now.AddMinutes(1));
        second.Regrant(EntitlementKind.Beta, Now.AddMinutes(1));

        Assert.True(await firstStore.TrySaveAsync(first, expectedRevision: 0, CancellationToken.None));
        Assert.False(await secondStore.TrySaveAsync(second, expectedRevision: 0, CancellationToken.None));

        await using var verifyDb = fixture.CreateDbContext();
        var persisted = await new ProductEntitlementStore(verifyDb).FindAsync(
            playerId, productId, CancellationToken.None);
        Assert.Equal(EntitlementKind.Owned, persisted!.Kind);
        Assert.Equal(1, persisted.Revision);
    }

    [Fact]
    public async Task Active_query_uses_exact_cutoff_and_never_leaks_another_players_entitlement()
    {
        await using var db = fixture.CreateDbContext();
        var playerId = await PlayerSeeding.CreatePlayerAsync(db, "entlist", Now);
        var otherPlayerId = await PlayerSeeding.CreatePlayerAsync(db, "entother", Now);
        var store = new ProductEntitlementStore(db);

        await store.AddAsync(
            ProductEntitlement.Grant(playerId, ProductId.Create("level5"), EntitlementKind.Owned, Now),
            CancellationToken.None);
        await store.AddAsync(
            ProductEntitlement.Grant(
                playerId, ProductId.Create("future-demo"), EntitlementKind.Demo, Now.AddHours(-1), Now.AddHours(1)),
            CancellationToken.None);
        await store.AddAsync(
            ProductEntitlement.Grant(
                playerId, ProductId.Create("exact-cutoff"), EntitlementKind.Beta, Now.AddHours(-1), Now),
            CancellationToken.None);
        var revoked = ProductEntitlement.Grant(
            playerId, ProductId.Create("revoked"), EntitlementKind.Playtest, Now.AddHours(-1));
        revoked.Revoke(Now.AddMinutes(-1));
        await store.AddAsync(revoked, CancellationToken.None);
        await store.AddAsync(
            ProductEntitlement.Grant(otherPlayerId, ProductId.Create("other-only"), EntitlementKind.Owned, Now),
            CancellationToken.None);
        await db.SaveChangesAsync();

        var active = await store.ListActiveAsync(playerId, Now, CancellationToken.None);

        Assert.Equal(["future-demo", "level5"], active.Select(item => item.ProductId.Value).ToArray());
        Assert.Null(await store.FindAsync(playerId, ProductId.Create("other-only"), CancellationToken.None));
    }
}
