using Level5.Application.Abstractions;
using Level5.Application.Platform;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Ids;
using Level5.Domain.Platform;
using Xunit;

namespace Level5.Application.Tests.Platform;

public sealed class EntitlementUseCaseTests
{
    private static readonly ProductId Level5 = ProductId.Create("level5");
    private static readonly ProductId SecretRobot = ProductId.Create("secret-robot");
    private readonly PlayerId _playerId = PlayerId.New();
    private readonly FakeClock _clock = new();
    private readonly InMemoryProductEntitlementStore _store = new();

    [Fact]
    public async Task Check_returns_access_for_an_active_entitlement()
    {
        await _store.AddAsync(
            ProductEntitlement.Grant(_playerId, Level5, EntitlementKind.Owned, _clock.UtcNow),
            CancellationToken.None);

        var result = await new CheckMyProductAccessUseCase(_store, _clock).ExecuteAsync(
            new CheckMyProductAccessRequest(_playerId, Level5),
            CancellationToken.None);

        Assert.True(result.HasAccess);
        Assert.Equal(EntitlementKind.Owned, result.Kind);
        Assert.Null(result.ExpiresAt);
    }

    [Fact]
    public async Task Check_returns_no_access_for_a_missing_entitlement()
    {
        var result = await new CheckMyProductAccessUseCase(_store, _clock).ExecuteAsync(
            new CheckMyProductAccessRequest(_playerId, Level5),
            CancellationToken.None);

        Assert.False(result.HasAccess);
        Assert.Null(result.Kind);
        Assert.Null(result.ExpiresAt);
    }

    [Fact]
    public async Task Check_returns_no_access_at_the_exact_expiry_cutoff()
    {
        await _store.AddAsync(
            ProductEntitlement.Grant(
                _playerId,
                Level5,
                EntitlementKind.Demo,
                _clock.UtcNow.AddHours(-1),
                _clock.UtcNow),
            CancellationToken.None);

        var result = await new CheckMyProductAccessUseCase(_store, _clock).ExecuteAsync(
            new CheckMyProductAccessRequest(_playerId, Level5),
            CancellationToken.None);

        Assert.False(result.HasAccess);
        Assert.Null(result.Kind);
        Assert.Null(result.ExpiresAt);
    }

    [Fact]
    public async Task Check_returns_no_access_for_a_revoked_entitlement()
    {
        var entitlement = ProductEntitlement.Grant(
            _playerId, Level5, EntitlementKind.Beta, _clock.UtcNow.AddHours(-1));
        entitlement.Revoke(_clock.UtcNow.AddMinutes(-1));
        await _store.AddAsync(entitlement, CancellationToken.None);

        var result = await new CheckMyProductAccessUseCase(_store, _clock).ExecuteAsync(
            new CheckMyProductAccessRequest(_playerId, Level5),
            CancellationToken.None);

        Assert.False(result.HasAccess);
    }

    [Fact]
    public async Task List_returns_only_the_players_active_current_entitlements()
    {
        await _store.AddAsync(
            ProductEntitlement.Grant(_playerId, Level5, EntitlementKind.Owned, _clock.UtcNow),
            CancellationToken.None);
        await _store.AddAsync(
            ProductEntitlement.Grant(
                _playerId,
                SecretRobot,
                EntitlementKind.Demo,
                _clock.UtcNow.AddHours(-2),
                _clock.UtcNow.AddHours(-1)),
            CancellationToken.None);
        var revoked = ProductEntitlement.Grant(
            _playerId, ProductId.Create("zromp"), EntitlementKind.Playtest, _clock.UtcNow);
        revoked.Revoke(_clock.UtcNow);
        await _store.AddAsync(revoked, CancellationToken.None);
        await _store.AddAsync(
            ProductEntitlement.Grant(PlayerId.New(), SecretRobot, EntitlementKind.Beta, _clock.UtcNow),
            CancellationToken.None);

        var result = await new ListMyEntitlementsUseCase(_store, _clock).ExecuteAsync(
            new ListMyEntitlementsRequest(_playerId),
            CancellationToken.None);

        var entitlement = Assert.Single(result);
        Assert.Equal(Level5, entitlement.ProductId);
        Assert.Equal(EntitlementKind.Owned, entitlement.Kind);
    }
}

internal sealed class InMemoryProductEntitlementStore : IProductEntitlementStore
{
    private readonly Dictionary<(Guid PlayerId, string ProductId), ProductEntitlement> _rows = [];
    private readonly Dictionary<(Guid PlayerId, string ProductId), long> _revisions = [];

    public Task<ProductEntitlement?> FindAsync(
        PlayerId playerId,
        ProductId productId,
        CancellationToken cancellationToken)
    {
        var row = _rows.GetValueOrDefault((playerId.Value, productId.Value));
        return Task.FromResult(row is null ? null : Clone(row));
    }

    public Task<IReadOnlyList<ProductEntitlement>> ListActiveAsync(
        PlayerId playerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProductEntitlement> result = _rows.Values
            .Where(entitlement => entitlement.PlayerId == playerId && entitlement.HasAccess(now))
            .OrderBy(entitlement => entitlement.ProductId.Value)
            .Select(Clone)
            .ToList();
        return Task.FromResult(result);
    }

    public Task AddAsync(ProductEntitlement entitlement, CancellationToken cancellationToken)
    {
        var key = Key(entitlement);
        _rows.Add(key, Clone(entitlement));
        _revisions.Add(key, entitlement.Revision);
        return Task.CompletedTask;
    }

    public Task<bool> TrySaveAsync(
        ProductEntitlement entitlement,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var key = Key(entitlement);
        if (!_revisions.TryGetValue(key, out var revision) || revision != expectedRevision)
        {
            return Task.FromResult(false);
        }

        _rows[key] = Clone(entitlement);
        _revisions[key] = entitlement.Revision;
        return Task.FromResult(true);
    }

    private static (Guid PlayerId, string ProductId) Key(ProductEntitlement entitlement)
        => (entitlement.PlayerId.Value, entitlement.ProductId.Value);

    private static ProductEntitlement Clone(ProductEntitlement entitlement)
        => ProductEntitlement.Rehydrate(
            entitlement.PlayerId,
            ProductId.Create(entitlement.ProductId.Value),
            entitlement.Kind,
            entitlement.GrantedAt,
            entitlement.ExpiresAt,
            entitlement.RevokedAt,
            entitlement.Revision);
}
