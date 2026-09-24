using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Level5.Api.IntegrationTests;

/// <summary>
/// Postgres unavailable end to end: the process stays live, readiness goes unhealthy, and a
/// database-backed request fails clearly with a retryable 503 once the bounded retry budget is
/// spent - never fake success, never a leaked Npgsql message. Uses its own host pointed at a port
/// nothing listens on rather than the shared <see cref="ApiFactory"/> container.
/// </summary>
public sealed class DatabaseOutageTests(DatabaseOutageTests.UnreachableDatabaseApiFactory factory)
    : IClassFixture<DatabaseOutageTests.UnreachableDatabaseApiFactory>
{
    [Fact]
    public async Task Liveness_stays_healthy_while_readiness_reports_unhealthy()
    {
        var client = factory.CreateClient();

        var live = await client.GetAsync("/health/live");
        var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    [Fact]
    public async Task A_database_backed_request_returns_a_safe_retryable_503_problem_details()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v2/auth/login", new { username = "someone", password = "not-a-real-password" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.Equal("service_unavailable", body.GetProperty("code").GetString());
        Assert.Equal("The service is temporarily unavailable. Please retry.", body.GetProperty("title").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("Npgsql", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("127.0.0.1", raw, StringComparison.Ordinal);
    }

    public sealed class UnreachableDatabaseApiFactory : WebApplicationFactory<Program>
    {
        private readonly int _closedPort = GetClosedPort();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = $"Host=127.0.0.1;Port={_closedPort};Database=unreachable;Username=unused;Password=unused;Timeout=3",
                    ["Jwt:Key"] = "test-only-signing-key-not-for-production-use-32chars-min",
                    ["Jwt:Issuer"] = "Level5BackendV2.Tests",
                    ["Jwt:Audience"] = "Level5Client.Tests"
                });
            });
        }

        private static int GetClosedPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
