using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Level5.Application.BloodMoney;
using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;
using Level5.Infrastructure.BloodMoney;
using Level5.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Level5.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class BloodCreditsFlowTests(ApiFactory factory)
{
    private const string Path = "/api/v2/games/blood-money/me/credits";

    [Fact]
    public async Task Balance_requires_authentication()
        => Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(Path)).StatusCode);

    [Fact]
    public async Task Registration_and_missing_balance_read_do_not_create_credit_state()
    {
        var player = await factory.RegisterNewPlayerAsync("CreditZero");
        Assert.Equal(0, await Balance(player.Client));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
        Assert.False(await db.Set<BloodCreditAccountRow>().AnyAsync(row => row.PlayerId == player.PlayerId));
        Assert.False(await db.Set<BloodCreditTransactionRow>().AnyAsync(row => row.SubjectPlayerId == player.PlayerId));
    }

    [Fact]
    public async Task Funded_balance_is_exact_and_query_or_path_identity_cannot_expose_another_player()
    {
        var caller = await factory.RegisterNewPlayerAsync("CreditSelf");
        var other = await factory.RegisterNewPlayerAsync("CreditOther");
        await Fund(caller.PlayerId, 9007199254740993);
        await Fund(other.PlayerId, 42);
        Assert.Equal(9007199254740993, await Balance(caller.Client));
        Assert.Equal(42, await Balance(other.Client));
        Assert.Equal(9007199254740993, await Balance(caller.Client, $"?playerId={other.PlayerId}&accountId={other.PlayerId}"));
        Assert.Equal(HttpStatusCode.NotFound, (await caller.Client.GetAsync($"/api/v2/games/blood-money/players/{other.PlayerId}/credits")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await caller.Client.GetAsync($"{Path}/{other.PlayerId}")).StatusCode);
    }

    [Theory]
    [InlineData("POST", "")]
    [InlineData("PUT", "")]
    [InlineData("DELETE", "")]
    [InlineData("POST", "/issue")]
    [InlineData("POST", "/spend")]
    [InlineData("POST", "/correct")]
    [InlineData("PUT", "/correct")]
    [InlineData("DELETE", "/spend")]
    public async Task Player_credit_mutations_are_not_exposed(string method, string suffix)
    {
        var player = await factory.RegisterNewPlayerAsync("CreditReadOnly");
        using var request = new HttpRequestMessage(new HttpMethod(method), Path + suffix)
        {
            Content = JsonContent.Create(new { playerId = player.PlayerId, amount = 1000, delta = 1000 })
        };
        var response = await player.Client.SendAsync(request);
        Assert.Equal(suffix.Length == 0 ? HttpStatusCode.MethodNotAllowed : HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await Balance(player.Client));
    }

    [Fact]
    public async Task Openapi_contains_only_the_balance_read_and_its_minimal_integer_contract()
    {
        var response = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var path = Assert.Single(document.GetProperty("paths").EnumerateObject(), item => item.Name == Path);
        Assert.Equal(Path, path.Name);
        Assert.Equal("get", Assert.Single(path.Value.EnumerateObject()).Name);
        var schema = document.GetProperty("components").GetProperty("schemas").GetProperty("BloodCreditBalanceResponseDto");
        var property = Assert.Single(schema.GetProperty("properties").EnumerateObject());
        Assert.Equal("availableCredits", property.Name);
        Assert.Equal("integer", property.Value.GetProperty("type").GetString());
        Assert.Equal("int64", property.Value.GetProperty("format").GetString());
    }

    private async Task Fund(Guid playerId, long amount)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IssueBloodCreditsUseCase>().ExecuteAsync(
            new(BloodCreditTransactionId.New(), new PlayerId(playerId), amount, "api-test-grant"), CancellationToken.None);
    }

    private static async Task<long> Balance(HttpClient client, string query = "")
    {
        var response = await client.GetAsync(Path + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("availableCredits", Assert.Single(body.EnumerateObject()).Name);
        return body.GetProperty("availableCredits").GetInt64();
    }
}
