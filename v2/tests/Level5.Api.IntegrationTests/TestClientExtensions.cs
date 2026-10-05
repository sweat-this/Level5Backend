using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Level5.Api.IntegrationTests;

public sealed record RegisteredPlayer(
    HttpClient Client,
    Guid PlayerId,
    string AccessToken,
    string RefreshToken,
    string Username);

public static class TestClientExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Registers a fresh, uniquely-named account and returns an HttpClient pre-authenticated as it.</summary>
    public static async Task<RegisteredPlayer> RegisterNewPlayerAsync(
        this ApiFactory factory,
        string displayName,
        string? clientKind = null)
    {
        var client = factory.CreateClient();
        var username = $"{displayName}{Guid.NewGuid():N}"[..20];

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/auth/register")
        {
            Content = JsonContent.Create(new
            {
                username,
                password = "P@ssw0rd123!",
                displayName
            })
        };
        if (clientKind is not null)
        {
            request.Headers.Add("X-SweatThis-Client", clientKind);
        }

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<RegisterResponse>(JsonOptions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.AccessToken);

        return new RegisteredPlayer(client, body.PlayerId, body.AccessToken, body.RefreshToken, username);
    }

    public static async Task<RegisteredPlayer> LoginAsync(
        this ApiFactory factory,
        string username,
        string? clientKind = null)
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/auth/login")
        {
            Content = JsonContent.Create(new { username, password = "P@ssw0rd123!" })
        };
        if (clientKind is not null)
        {
            request.Headers.Add("X-SweatThis-Client", clientKind);
        }

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<RegisterResponse>(JsonOptions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.AccessToken);
        return new RegisteredPlayer(client, body.PlayerId, body.AccessToken, body.RefreshToken, username);
    }

    private sealed record RegisterResponse(string AccessToken, DateTimeOffset ExpiresAt, Guid PlayerId, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);
}
