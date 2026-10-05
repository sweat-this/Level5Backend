using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Level5.Api.BackgroundServices;
using Level5.Application.Abstractions;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Level5.Api.IntegrationTests;

public sealed class AuthRateLimitTests
{
    [Fact]
    public async Task Login_allows_five_production_requests_then_rate_limits_the_sixth()
    {
        await using var factory = new ProductionAuthRateLimitFactory();
        using var client = CreateClient(factory);

        await AssertAllowsFiveThenRateLimitsAsync(client, "/api/v2/auth/login");
    }

    [Fact]
    public async Task Exhausting_login_does_not_consume_the_independent_registration_bucket()
    {
        await using var factory = new ProductionAuthRateLimitFactory();
        using var client = CreateClient(factory);

        await AssertAllowsFiveThenRateLimitsAsync(client, "/api/v2/auth/login");
        await AssertAllowsFiveThenRateLimitsAsync(client, "/api/v2/auth/register");
    }

    [Fact]
    public async Task Exhausting_refresh_does_not_consume_the_independent_logout_bucket()
    {
        await using var factory = new ProductionAuthRateLimitFactory();
        using var client = CreateClient(factory);

        await AssertAllowsFiveThenRateLimitsAsync(client, "/api/v2/auth/refresh");
        await AssertAllowsFiveThenRateLimitsAsync(client, "/api/v2/auth/logout");
    }

    [Fact]
    public async Task Email_verification_operations_have_independent_buckets_from_each_other_and_login()
    {
        await using var factory = new ProductionAuthRateLimitFactory();
        using var client = CreateClient(factory);

        await AssertAllowsFiveThenRateLimitsAsync(client, "/api/v2/auth/login", HttpStatusCode.BadRequest);
        await AssertAllowsFiveThenRateLimitsAsync(
            client, "/api/v2/me/email/verification", HttpStatusCode.BadRequest);
        await AssertAllowsFiveThenRateLimitsAsync(
            client, "/api/v2/me/email/verification/resend", HttpStatusCode.NotFound);
        await AssertAllowsFiveThenRateLimitsAsync(
            client, "/api/v2/email-verification/complete", HttpStatusCode.BadRequest);
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

    private static async Task AssertAllowsFiveThenRateLimitsAsync(HttpClient client, string path)
        => await AssertAllowsFiveThenRateLimitsAsync(client, path, HttpStatusCode.BadRequest);

    private static async Task AssertAllowsFiveThenRateLimitsAsync(
        HttpClient client,
        string path,
        HttpStatusCode allowedStatus)
    {
        for (var requestNumber = 1; requestNumber <= 5; requestNumber++)
        {
            using var response = await client.PostAsJsonAsync(path, new { });
            Assert.Equal(allowedStatus, response.StatusCode);
        }

        using var rateLimited = await client.PostAsJsonAsync(path, new { });
        Assert.Equal(HttpStatusCode.TooManyRequests, rateLimited.StatusCode);
    }
}

/// <summary>
/// Runs the real middleware pipeline with Production's five-request auth limit while avoiding the
/// Postgres container used by business-flow tests. Empty request objects are rejected by model
/// validation before any auth use case or database dependency is resolved.
/// </summary>
internal sealed class ProductionAuthRateLimitFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused",
                ["Jwt:Key"] = "test-only-signing-key-not-for-production-use-32chars-min",
                ["Jwt:Issuer"] = "Level5BackendV2.RateLimitTests",
                ["Jwt:Audience"] = "Level5Client.RateLimitTests"
            });
        });

        builder.ConfigureServices(services =>
        {
            var expirySweep = services.SingleOrDefault(descriptor =>
                descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(ChallengeExpirySweepService));
            if (expirySweep is not null)
            {
                services.Remove(expirySweep);
            }

            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = RateLimitTestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = RateLimitTestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, RateLimitTestAuthHandler>(
                    RateLimitTestAuthHandler.SchemeName, _ => { });
            services.RemoveAll<IAccountStore>();
            services.AddSingleton<IAccountStore, MissingAccountStore>();
        });
    }
}

internal sealed class RateLimitTestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "RateLimitTest";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        Claim[] claims = [new("sub", Guid.Parse("11111111-1111-1111-1111-111111111111").ToString())];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

internal sealed class MissingAccountStore : IAccountStore
{
    public Task<Account?> FindByIdAsync(AccountId id, CancellationToken cancellationToken) => Task.FromResult<Account?>(null);
    public Task<Account?> FindByUsernameAsync(Username username, CancellationToken cancellationToken) => Task.FromResult<Account?>(null);
    public Task<bool> UsernameExistsAsync(Username username, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task AddAsync(Account account, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task UpdateAsync(Account account, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StageEmailUpdateAsync(Account account, CancellationToken cancellationToken) => Task.CompletedTask;
}
