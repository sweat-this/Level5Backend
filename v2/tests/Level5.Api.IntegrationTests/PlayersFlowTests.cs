using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Level5.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Level5.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class PlayersFlowTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Updating_the_display_name_without_a_token_is_rejected()
    {
        var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync("/api/v2/players/me", new { displayName = "New Name" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_player_can_update_their_own_display_name()
    {
        var player = await factory.RegisterNewPlayerAsync("Alice");

        var response = await player.Client.PatchAsJsonAsync("/api/v2/players/me", new { displayName = "Alice Updated" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(player.PlayerId, body.GetProperty("playerId").GetGuid());
        Assert.Equal("Alice Updated", body.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Updating_one_players_profile_does_not_affect_another()
    {
        var alice = await factory.RegisterNewPlayerAsync("AliceTwo");
        var bob = await factory.RegisterNewPlayerAsync("BobTwo");
        var bobNameBefore = await GetDisplayNameAsync(bob.PlayerId);
        var bobTagBefore = await GetTagAsync(bob.PlayerId);

        var response = await alice.Client.PatchAsJsonAsync("/api/v2/players/me", new { displayName = "Alice Changed" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(bobNameBefore, await GetDisplayNameAsync(bob.PlayerId));
        Assert.Equal(bobTagBefore, await GetTagAsync(bob.PlayerId));
    }

    [Fact]
    public async Task Client_supplied_playerId_accountId_and_tag_cannot_redirect_or_alter_identity()
    {
        var alice = await factory.RegisterNewPlayerAsync("Carol");
        var bob = await factory.RegisterNewPlayerAsync("Dave");
        var bobNameBefore = await GetDisplayNameAsync(bob.PlayerId);
        var bobTagBefore = await GetTagAsync(bob.PlayerId);
        var aliceTagBefore = await GetTagAsync(alice.PlayerId);

        var response = await alice.Client.PatchAsJsonAsync("/api/v2/players/me", new
        {
            displayName = "Carol Overpost",
            playerId = bob.PlayerId,
            accountId = Guid.NewGuid(),
            tag = "OTHER#1234"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        // The update landed on Alice's own profile - not Bob's, and the tag wasn't touched.
        Assert.Equal(alice.PlayerId, body.GetProperty("playerId").GetGuid());
        Assert.Equal("Carol Overpost", body.GetProperty("displayName").GetString());
        Assert.Equal(aliceTagBefore, body.GetProperty("tag").GetString());
        Assert.NotEqual("OTHER#1234", body.GetProperty("tag").GetString());

        Assert.Equal(bobNameBefore, await GetDisplayNameAsync(bob.PlayerId));
        Assert.Equal(bobTagBefore, await GetTagAsync(bob.PlayerId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_or_whitespace_only_display_name_is_rejected(string invalid)
    {
        var player = await factory.RegisterNewPlayerAsync("Eve");

        var response = await player.Client.PatchAsJsonAsync("/api/v2/players/me", new { displayName = invalid });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_display_name_over_32_characters_is_rejected()
    {
        var player = await factory.RegisterNewPlayerAsync("Frank");

        var response = await player.Client.PatchAsJsonAsync("/api/v2/players/me", new { displayName = new string('A', 33) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_request_body_missing_the_display_name_field_entirely_returns_a_validation_problem_detail()
    {
        var player = await factory.RegisterNewPlayerAsync("FrankTwo");

        // "displayName" is omitted entirely, so this is rejected by [ApiController]'s automatic
        // model-binding validation before UpdateMyPlayerProfileUseCase ever runs - a different
        // code path than the empty/whitespace/over-length cases above, which fail domain
        // validation instead. Mirrors AuthFlowTests's equivalent check for RegisterRequestDto.
        var response = await player.Client.PatchAsJsonAsync("/api/v2/players/me", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("validation_failed", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Updated_display_name_is_visible_through_exact_tag_lookup()
    {
        var player = await factory.RegisterNewPlayerAsync("Gail");
        var tag = await GetTagAsync(player.PlayerId);
        await player.Client.PatchAsJsonAsync("/api/v2/players/me", new { displayName = "Gail Updated" });

        var response = await player.Client.GetAsync($"/api/v2/players/by-tag/{Uri.EscapeDataString(tag)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("Gail Updated", body.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task By_tag_lookup_is_case_insensitive()
    {
        var player = await factory.RegisterNewPlayerAsync("Hank");
        var tag = await GetTagAsync(player.PlayerId);

        var response = await player.Client.GetAsync($"/api/v2/players/by-tag/{Uri.EscapeDataString(tag.ToLowerInvariant())}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(player.PlayerId, body.GetProperty("playerId").GetGuid());
    }

    [Fact]
    public async Task By_tag_lookup_url_encodes_the_hash_delimiter_end_to_end()
    {
        var player = await factory.RegisterNewPlayerAsync("Ivan");
        var tag = await GetTagAsync(player.PlayerId);
        Assert.Contains('#', tag);

        // Exercises the real ASP.NET routing/binding stack, not just the use case: the raw '#'
        // must be percent-encoded (%23) or it would be parsed as a URL fragment delimiter and
        // never reach the server as part of the path at all.
        var response = await player.Client.GetAsync($"/api/v2/players/by-tag/{Uri.EscapeDataString(tag)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(tag, body.GetProperty("tag").GetString());
    }

    [Fact]
    public async Task A_wellformed_unknown_tag_returns_not_found()
    {
        var player = await factory.RegisterNewPlayerAsync("Jill");

        var response = await player.Client.GetAsync($"/api/v2/players/by-tag/{Uri.EscapeDataString("NOBODY#9999")}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_malformed_tag_returns_a_validation_failure()
    {
        var player = await factory.RegisterNewPlayerAsync("Kyle");

        var response = await player.Client.GetAsync("/api/v2/players/by-tag/not-a-tag");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_public_player_response_exposes_only_public_fields()
    {
        var player = await factory.RegisterNewPlayerAsync("Liam");
        var tag = await GetTagAsync(player.PlayerId);

        var response = await player.Client.GetAsync($"/api/v2/players/by-tag/{Uri.EscapeDataString(tag)}");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var propertyNames = body.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToHashSet();

        Assert.Equal(new HashSet<string> { "playerid", "displayname", "tag" }, propertyNames);
    }

    private async Task<string> GetTagAsync(Guid playerId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
        var row = await db.PlayerProfiles.SingleAsync(p => p.Id == playerId);
        return row.Tag;
    }

    private async Task<string> GetDisplayNameAsync(Guid playerId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
        var row = await db.PlayerProfiles.SingleAsync(p => p.Id == playerId);
        return row.DisplayName;
    }
}
