using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Level5.Api.IntegrationTests;

/// <summary>
/// Issue #22 requires the app to start whether or not an OTLP collector is actually reachable -
/// the exporter only needs to be configured, never connected to, at startup. This deliberately does
/// not join <see cref="ApiCollection"/>/<see cref="ApiFactory"/> (which boots a real Postgres
/// container): liveness never touches the database, so a syntactically valid but never-dialled
/// connection string is enough to prove the OpenTelemetry OTLP exporter registration itself doesn't
/// block host startup, without paying for another container per test run.
/// </summary>
public sealed class TelemetryOtlpStartupTests : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused",
                ["Jwt:Key"] = "test-only-signing-key-not-for-production-use-32chars-min",
                ["Jwt:Issuer"] = "Level5BackendV2.Tests",
                ["Jwt:Audience"] = "Level5Client.Tests",
                // A well-formed but unreachable OTLP endpoint - nothing needs to be listening on
                // it for the exporter to register successfully; export failures happen later, in
                // the background, and never surface as a startup failure.
                ["Telemetry:Otlp:Endpoint"] = "http://127.0.0.1:4319",
            });
        });
    }

    [Fact]
    public async Task The_app_starts_and_reports_live_with_an_otlp_endpoint_configured_but_unreachable()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
