using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Level5.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class AuthFlowTests(ApiFactory factory)
{
    [Fact]
    public async Task Register_then_call_a_protected_endpoint_succeeds()
    {
        var player = await factory.RegisterNewPlayerAsync("Alice");

        var response = await player.Client.GetAsync("/api/v2/players/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_without_a_token_is_rejected()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v2/players/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Registering_a_duplicate_username_returns_a_conflict_problem_detail()
    {
        var client = factory.CreateClient();
        var username = $"dup{Guid.NewGuid():N}"[..20];
        var payload = new { username, password = "P@ssw0rd123!", displayName = "Dup" };

        var first = await client.PostAsJsonAsync("/api/v2/auth/register", payload);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/v2/auth/register", payload);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await second.Content.ReadFromJsonAsync<ProblemDetailsDto>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Equal("conflict", problem!.Code);
    }

    [Fact]
    public async Task Login_with_the_wrong_password_returns_unauthorized_not_a_stack_trace()
    {
        var client = factory.CreateClient();
        var username = $"wrongpw{Guid.NewGuid():N}"[..20];
        await client.PostAsJsonAsync("/api/v2/auth/register", new { username, password = "P@ssw0rd123!", displayName = "WrongPw" });

        var response = await client.PostAsJsonAsync("/api/v2/auth/login", new { username, password = "not-the-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", body); // no stack trace frames
    }

    [Fact]
    public async Task A_missing_required_field_returns_the_same_problem_details_shape_as_a_domain_validation_error()
    {
        var client = factory.CreateClient();

        // "username" is omitted entirely, so this is rejected by [ApiController]'s automatic
        // model-binding validation before RegisterAccountUseCase ever runs - a different code
        // path than the domain/application exceptions ApiExceptionHandler translates, which
        // previously produced a differently-shaped body with no "code" field.
        var response = await client.PostAsJsonAsync("/api/v2/auth/register", new { password = "P@ssw0rd123!", displayName = "NoUsername" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", problem.GetProperty("code").GetString());
    }

    private sealed record ProblemDetailsDto(string Code, string Title, int Status);
}
