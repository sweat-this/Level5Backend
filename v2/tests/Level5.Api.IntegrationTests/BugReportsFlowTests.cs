using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Level5.Infrastructure.Persistence;
using Level5.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Level5.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class BugReportsFlowTests(ApiFactory factory)
{
    private const string Path = "/api/v2/platform/bug-reports";
    private static object Body(Guid key, string game = "level5", string title = "title")
        => new { clientSubmissionId = key, gameKey = game, title, description = "private-description", category = "technical",
            stepsToReproduce = "private-steps", gameVersion = "1.0" };

    [Fact]
    public async Task Unauthenticated_reports_are_rejected_and_no_retrieval_endpoint_exists()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Path, Body(Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync(Path)).StatusCode);
    }

    [Theory]
    [InlineData("level5")]
    [InlineData("zromp")]
    [InlineData("secret-robot")]
    [InlineData("blood-money")]
    public async Task Supported_game_is_persisted_without_purchase_with_server_identity_and_time(string game)
    {
        var player = await factory.RegisterNewPlayerAsync("BugGame");
        var key = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow;
        var response = await player.Client.PostAsJsonAsync(Path, Body(key, game));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<Level5V2DbContext>().BugReports.SingleAsync(r => r.Id == id);
        Assert.Equal(player.PlayerId, row.ReporterPlayerId);
        Assert.Equal(key, row.ClientSubmissionId);
        Assert.Equal(game, row.GameKey);
        Assert.InRange(row.CreatedAt, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Matching_replay_conflict_and_distinct_identical_text()
    {
        var player = await factory.RegisterNewPlayerAsync("BugRetry");
        var key = Guid.NewGuid();
        var created = await player.Client.PostAsJsonAsync(Path, Body(key, title: " title "));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var replay = await player.Client.PostAsJsonAsync(Path, Body(key));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(await created.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await player.Client.PostAsJsonAsync(Path, Body(key, title: "different"))).StatusCode);
        var distinct = await player.Client.PostAsJsonAsync(Path, Body(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Created, distinct.StatusCode);
        Assert.NotEqual(await created.Content.ReadAsStringAsync(), await distinct.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"clientSubmissionId\":\"not-a-uuid\",\"description\":\"private-description\"}")]
    [InlineData("{\"clientSubmissionId\":\"00000000-0000-0000-0000-000000000000\",\"gameKey\":\"level5\",\"title\":\"title\",\"description\":\"private-description\"}")]
    [InlineData("{\"clientSubmissionId\":\"10000000-0000-4000-8000-000000000001\",\"gameKey\":\"invalid\",\"title\":\"title\",\"description\":\"private-description\"}")]
    [InlineData("{\"clientSubmissionId\":\"10000000-0000-4000-8000-000000000001\",\"gameKey\":\"level5\",\"title\":\"title\",\"description\":\"private-description\",\"reporterPlayerId\":\"forged\"}")]
    [InlineData("{\"clientSubmissionId\":\"10000000-0000-4000-8000-000000000001\",\"gameKey\":\"level5\",\"title\":\"title\",\"description\":\"private-description\",\"createdAt\":\"2000-01-01\"}")]
    public async Task Invalid_models_are_safe_errors(string json)
    {
        var player = await factory.RegisterNewPlayerAsync("BugInvalid");
        var response = await player.Client.PostAsync(Path, new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-description", text);
        Assert.DoesNotContain("forged", text);
        Assert.Contains("traceId", text);
        Assert.Contains("code", text);
    }

    [Fact]
    public async Task Limits_optional_fields_whitespace_and_category_are_validated()
    {
        var player = await factory.RegisterNewPlayerAsync("BugLimits");
        var key = Guid.NewGuid();
        var max = new { clientSubmissionId = key, gameKey = "level5", title = new string('t', 160), description = new string('d', 8000),
            stepsToReproduce = new string('s', 8000), gameVersion = new string('v', 64) };
        Assert.Equal(HttpStatusCode.Created, (await player.Client.PostAsJsonAsync(Path, max)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await player.Client.PostAsJsonAsync(Path, max with { title = new string('t', 161) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await player.Client.PostAsJsonAsync(Path, Body(Guid.NewGuid(), title: " \t\r\n "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await player.Client.PostAsJsonAsync(Path,
            new { clientSubmissionId = Guid.NewGuid(), gameKey = "level5", title = "title", description = "description", category = "unknown" })).StatusCode);
    }

    [Fact]
    public async Task Reporting_rate_limit_is_bounded_and_independent_per_account()
    {
        var player = await factory.RegisterNewPlayerAsync("BugRate");
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await player.Client.PostAsJsonAsync(Path, new { })).StatusCode);
        var limited = await player.Client.PostAsJsonAsync(Path, Body(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Contains("bug_report_rate_limited", await limited.Content.ReadAsStringAsync());
        var other = await factory.RegisterNewPlayerAsync("BugRateOther");
        Assert.Equal(HttpStatusCode.Created, (await other.Client.PostAsJsonAsync(Path, Body(Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task Commit_failure_returns_safe_server_error_and_does_not_persist()
    {
        var player = await factory.RegisterNewPlayerAsync("BugFailure");
        await using var failing = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IUnitOfWork>();
            services.AddScoped<IUnitOfWork, FailedCommit>();
        }));
        using var client = failing.CreateClient();
        client.DefaultRequestHeaders.Authorization = player.Client.DefaultRequestHeaders.Authorization;
        var key = Guid.NewGuid();
        var response = await client.PostAsJsonAsync(Path, Body(key));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-description", text);
        Assert.DoesNotContain("private-steps", text);
        Assert.DoesNotContain("simulated", text);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<Level5V2DbContext>().BugReports.AnyAsync(r => r.ClientSubmissionId == key));
    }

    private sealed class FailedCommit : IUnitOfWork
    {
        public Task SaveChangesAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("simulated database commit failure");
    }
}
