using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Level5.Api.IntegrationTests;

public sealed record RegisteredPlayer(HttpClient Client, Guid PlayerId, string AccessToken, string RefreshToken);

public static class TestClientExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Registers a fresh, uniquely-named account and returns an HttpClient pre-authenticated as it.</summary>
    public static async Task<RegisteredPlayer> RegisterNewPlayerAsync(this ApiFactory factory, string displayName)
    {
        var client = factory.CreateClient();
        var username = $"{displayName}{Guid.NewGuid():N}"[..20];

        var response = await client.PostAsJsonAsync("/api/v2/auth/register", new
        {
            username,
            password = "P@ssw0rd123!",
            displayName
        });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<RegisterResponse>(JsonOptions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.AccessToken);

        return new RegisteredPlayer(client, body.PlayerId, body.AccessToken, body.RefreshToken);
    }

    private sealed record RegisterResponse(string AccessToken, DateTimeOffset ExpiresAt, Guid PlayerId, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);
}
