using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Level5.Domain.Ids;
using Level5.Domain.Platform;
using Level5.Infrastructure.Persistence;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Level5.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class PlatformEntitlementsFlowTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Theory]
    [InlineData("/api/v2/platform/me/entitlements")]
    [InlineData("/api/v2/platform/me/entitlements/level5")]
    public async Task Entitlement_reads_require_authentication(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Exact_check_returns_the_active_entitlement_contract()
    {
        var player = await factory.RegisterNewPlayerAsync("Entitled");
        var expiresAt = DateTimeOffset.UtcNow.AddHours(2);
        await GrantAsync(player.PlayerId, "level5", EntitlementKind.Owned, expiresAt);

        var response = await player.Client.GetAsync("/api/v2/platform/me/entitlements/LEVEL5");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("level5", body.GetProperty("productId").GetString());
        Assert.True(body.GetProperty("hasAccess").GetBoolean());
        Assert.Equal("Owned", body.GetProperty("kind").GetString());
        var actualExpiry = body.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.InRange((actualExpiry - expiresAt).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        Assert.Equal(
            new HashSet<string> { "productid", "hasaccess", "kind", "expiresat" },
            body.EnumerateObject().Select(property => property.Name.ToLowerInvariant()).ToHashSet());
    }

    [Fact]
    public async Task Missing_expired_and_revoked_products_return_normal_no_access_answers()
    {
        var player = await factory.RegisterNewPlayerAsync("NoAccess");
        await GrantAsync(
            player.PlayerId,
            "expired-demo",
            EntitlementKind.Demo,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            grantedAt: DateTimeOffset.UtcNow.AddHours(-1));
        await GrantAsync(
            player.PlayerId,
            "revoked-beta",
            EntitlementKind.Beta,
            revokedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            grantedAt: DateTimeOffset.UtcNow.AddHours(-1));

        foreach (var productId in new[] { "missing", "expired-demo", "revoked-beta" })
        {
            var response = await player.Client.GetAsync($"/api/v2/platform/me/entitlements/{productId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal(productId, body.GetProperty("productId").GetString());
            Assert.False(body.GetProperty("hasAccess").GetBoolean());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("kind").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.GetProperty("expiresAt").ValueKind);
        }
    }

    [Fact]
    public async Task Active_list_excludes_expired_revoked_and_other_player_rows()
    {
        var player = await factory.RegisterNewPlayerAsync("ListOwner");
        var other = await factory.RegisterNewPlayerAsync("ListOther");
        await GrantAsync(player.PlayerId, "level5", EntitlementKind.Owned);
        await GrantAsync(
            player.PlayerId,
            "expired",
            EntitlementKind.Demo,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            grantedAt: DateTimeOffset.UtcNow.AddHours(-1));
        await GrantAsync(
            player.PlayerId,
            "revoked",
            EntitlementKind.Playtest,
            revokedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            grantedAt: DateTimeOffset.UtcNow.AddHours(-1));
        await GrantAsync(other.PlayerId, "other-only", EntitlementKind.Beta);

        var response = await player.Client.GetAsync("/api/v2/platform/me/entitlements");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var item = Assert.Single(body.EnumerateArray());
        Assert.Equal("level5", item.GetProperty("productId").GetString());
        Assert.Equal("Owned", item.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Player_identity_is_derived_from_the_token_not_query_input()
    {
        var caller = await factory.RegisterNewPlayerAsync("Caller");
        var other = await factory.RegisterNewPlayerAsync("OtherOwner");
        await GrantAsync(other.PlayerId, "other-product", EntitlementKind.Owned);

        var response = await caller.Client.GetAsync(
            $"/api/v2/platform/me/entitlements/other-product?playerId={other.PlayerId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.False(body.GetProperty("hasAccess").GetBoolean());
    }

    [Theory]
    [InlineData("not_valid")]
    [InlineData("double--hyphen")]
    public async Task Invalid_product_identifiers_return_validation_failure(string productId)
    {
        var player = await factory.RegisterNewPlayerAsync("InvalidProduct");

        var response = await player.Client.GetAsync($"/api/v2/platform/me/entitlements/{productId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("invalid_product_id", body.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("POST", "/api/v2/platform/me/entitlements")]
    [InlineData("DELETE", "/api/v2/platform/me/entitlements/level5")]
    public async Task No_player_facing_grant_or_revoke_route_exists(string method, string path)
    {
        var player = await factory.RegisterNewPlayerAsync("ReadOnly");
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        var response = await player.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private async Task GrantAsync(
        Guid playerId,
        string productId,
        EntitlementKind kind,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? revokedAt = null,
        DateTimeOffset? grantedAt = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
        var entitlement = ProductEntitlement.Grant(
            new PlayerId(playerId),
            ProductId.Create(productId),
            kind,
            grantedAt ?? DateTimeOffset.UtcNow,
            expiresAt);
        if (revokedAt is not null)
        {
            entitlement.Revoke(revokedAt.Value);
        }

        await new ProductEntitlementStore(db).AddAsync(entitlement, CancellationToken.None);
        await db.SaveChangesAsync();
    }
}
