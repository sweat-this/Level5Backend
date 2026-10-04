using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Level5Backend.Tests;

[Collection("Legacy API integration")]
public sealed class UserReportRateLimitMiddlewareTests
{
    private static readonly IPAddress TrustedProxy = IPAddress.Parse("203.0.113.10");
    private static readonly IPAddress UntrustedProxy = IPAddress.Parse("203.0.113.11");

    [Fact]
    public async Task TrustedProxy_UsesForwardedClientAddressForIndependentRateLimitPartitions()
    {
        await using var factory = new UserReportApiFactory(TrustedProxy, TrustedProxy);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var response = await PostInvalidReport(client, "198.51.100.20");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var limited = await PostInvalidReport(client, "198.51.100.20");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        using var independentClient = await PostInvalidReport(client, "198.51.100.21");
        Assert.Equal(HttpStatusCode.BadRequest, independentClient.StatusCode);
    }

    [Fact]
    public async Task UntrustedProxy_CannotChooseRateLimitPartitionWithForwardedHeader()
    {
        await using var factory = new UserReportApiFactory(UntrustedProxy, TrustedProxy);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var response = await PostInvalidReport(client, $"198.51.100.{attempt + 30}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var limited = await PostInvalidReport(client, "198.51.100.99");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    private static Task<HttpResponseMessage> PostInvalidReport(HttpClient client, string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/userreport")
        {
            Content = JsonContent.Create(new { report = string.Empty })
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        return client.SendAsync(request);
    }

    private sealed class UserReportApiFactory(
        IPAddress remoteAddress,
        IPAddress configuredTrustedProxy) : WebApplicationFactory<Program>
    {
        private readonly Dictionary<string, string?> originalEnvironment = SetRequiredEnvironment();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] =
                        "Host=localhost;Database=unused;Username=unused;Password=unused",
                    ["Jwt:Key"] = "test-only-signing-key-at-least-32-characters",
                    ["Jwt:Issuer"] = "UserAuthenticationServer",
                    ["Jwt:Audience"] = "UserServiceClient",
                    ["ForwardedHeaders:KnownProxies:0"] = configuredTrustedProxy.ToString()
                });
            });
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new RemoteAddressStartupFilter(remoteAddress)));
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                base.Dispose(disposing);
            }
            finally
            {
                foreach (var variable in originalEnvironment)
                {
                    Environment.SetEnvironmentVariable(variable.Key, variable.Value);
                }
            }
        }

        private static Dictionary<string, string?> SetRequiredEnvironment()
        {
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings__DefaultConnection"] =
                    "Host=localhost;Database=unused;Username=unused;Password=unused",
                ["Jwt__Key"] = "test-only-signing-key-at-least-32-characters",
                ["Jwt__Issuer"] = "UserAuthenticationServer",
                ["Jwt__Audience"] = "UserServiceClient"
            };
            var originals = values.Keys.ToDictionary(
                variable => variable,
                Environment.GetEnvironmentVariable);

            foreach (var variable in values)
            {
                Environment.SetEnvironmentVariable(variable.Key, variable.Value);
            }

            return originals;
        }
    }

    private sealed class RemoteAddressStartupFilter(IPAddress remoteAddress) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, continuation) =>
                {
                    context.Connection.RemoteIpAddress = remoteAddress;
                    await continuation();
                });
                next(app);
            };
        }
    }
}

[CollectionDefinition("Legacy API integration", DisableParallelization = true)]
public sealed class LegacyApiIntegrationCollection;
