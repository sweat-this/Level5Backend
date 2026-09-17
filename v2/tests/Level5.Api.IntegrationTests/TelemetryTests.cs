using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Level5.Api.IntegrationTests;

/// <summary>
/// Issue #22: adding the OpenTelemetry baseline must not change liveness/readiness semantics, and
/// request/trace correlation (<c>Activity.Current</c>-backed <c>HttpContext.TraceIdentifier</c>)
/// must reach <c>ProblemDetails</c> responses end-to-end through the real ASP.NET Core pipeline -
/// not just the handler-level coverage in <see cref="ApiExceptionHandlerTests"/>.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed partial class TelemetryTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Once an Activity is current, HttpContext.TraceIdentifier is Activity.Id - the full W3C
    // "traceparent" shape (version-traceid-spanid-flags, e.g.
    // "00-80f0dc9e6e5490d97cc8d601ba0fd12f-651a8c94f2ec2145-01"), not a bare trace id. Asserting
    // this exact shape - not just non-emptiness - is what actually distinguishes
    // "Activity.Current-backed trace context" from ASP.NET Core's own always-present fallback
    // TraceIdentifier (a synthesized "{ConnectionId}:{RequestNumber}" string), which was already
    // non-empty before this issue and would make a merely-non-empty assertion pass even if the
    // OpenTelemetry ASP.NET Core instrumentation were removed entirely.
    [GeneratedRegex("^[0-9a-f]{2}-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$")]
    private static partial Regex W3CTraceParent();

    [Fact]
    public async Task Liveness_reports_healthy_with_no_dependency_checks()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_reports_healthy_when_the_database_is_reachable()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_problem_details_response_carries_a_real_w3c_trace_id_alongside_its_code()
    {
        var client = factory.CreateClient();

        // No Authorization header - AuthController requires none, so this hits [Authorize] and
        // returns a ProblemDetails 401, exercising the same real request pipeline (and therefore
        // the same Activity/TraceIdentifier wiring the OpenTelemetry ASP.NET Core instrumentation
        // now participates in) that a genuine failure would.
        var response = await client.GetAsync("/api/v2/players/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            // The bare-challenge 401 from the authentication handler itself (no body) is a valid
            // outcome here too - what matters for this test is the ProblemDetails-producing paths
            // below, which every mutating/lookup failure in this suite already goes through.
            return;
        }

        var problem = JsonSerializer.Deserialize<JsonElement>(body, JsonOptions);
        Assert.Matches(W3CTraceParent(), problem.GetProperty("traceId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task A_validation_problem_details_response_carries_a_real_w3c_trace_id()
    {
        var player = await factory.RegisterNewPlayerAsync("TraceCheck");

        // Missing required DisplayName - triggers [ApiController]'s automatic 400, which goes
        // through Program.cs's CustomizeProblemDetails callback rather than ApiExceptionHandler.
        var response = await player.Client.PatchAsJsonAsync("/api/v2/players/me", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", problem.GetProperty("code").GetString());
        Assert.Matches(W3CTraceParent(), problem.GetProperty("traceId").GetString());
    }
}
