using System.Text;
using Level5.Api.ErrorHandling;
using Level5.Api.Security;
using Level5.Api.Telemetry;
using Level5.Application.Competition;
using Level5.Application.Identity;
using Level5.Application.Leaderboards;
using Level5.Application.Players;
using Level5.Application.Results;
using Level5.Application.Social;
using Level5.Infrastructure.DependencyInjection;
using Level5.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using System.Net;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Jwt configuration (presence, key length) is validated at startup by JwtOptions' data
// annotations - see AddLevel5Infrastructure's ValidateDataAnnotations().ValidateOnStart().

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // Without this, Swashbuckle ignores this project's <Nullable>enable</Nullable> annotations
    // entirely and emits every property as optional/nullable - e.g. AccessTokenResponseDto's
    // AccessToken would be indistinguishable in the OpenAPI document from an actually-optional
    // field. This makes the generated contract (issue #4) match what the DTOs actually
    // guarantee, with no change to runtime (de)serialization behavior.
    options.SupportNonNullableReferenceTypes();
});
builder.Services.AddHttpContextAccessor();

builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails(options =>
{
    // ApiExceptionHandler stamps its own "code"/"traceId" extensions on every exception it
    // translates, but built-in failures that never reach it - e.g. [ApiController]'s automatic
    // 400 for a missing required field on model binding - go through this pipeline instead and
    // would otherwise come back in a differently-shaped body with no "code" at all.
    options.CustomizeProblemDetails = context =>
    {
        // Derived from the status rather than hardcoded: this callback also runs for any other
        // built-in ProblemDetails response (e.g. if UseStatusCodePages is added later), and
        // labelling a 404 or 405 "validation_failed" would be worse than having no code at all.
        context.ProblemDetails.Extensions.TryAdd("code",
            context.ProblemDetails.Status == StatusCodes.Status400BadRequest ? "validation_failed" : "error");
        context.ProblemDetails.Extensions.TryAdd("traceId", context.HttpContext.TraceIdentifier);
    };
});

// X-Forwarded-For/-Proto are only honoured from addresses listed in KnownProxies/KnownNetworks,
// which are bound from configuration so putting this service behind a reverse proxy is a
// deployment setting rather than a code change. Configure nothing (the default) and the
// framework's loopback-only defaults apply, so forwarded headers from arbitrary clients are
// ignored and the auth rate limiter below keeps partitioning on the real connection address.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    foreach (var proxy in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
    {
        options.KnownProxies.Add(IPAddress.Parse(proxy));
    }

    foreach (var network in builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
    {
        // Parsed with the BCL CIDR parser (so a malformed value fails loudly at startup), then
        // converted to the type ForwardedHeadersOptions expects.
        var cidr = System.Net.IPNetwork.Parse(network);
        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(cidr.BaseAddress, cidr.PrefixLength));
    }
});

builder.Services.AddLevel5Infrastructure(builder.Configuration);
builder.Services.AddLevel5Telemetry(builder.Configuration);

// Use cases - thin, stateless, one per operation. Registered here (the composition root)
// rather than via a DI extension inside Application, so Application stays free of any
// dependency-injection framework reference.
builder.Services.AddScoped<RegisterAccountUseCase>();
builder.Services.AddScoped<LoginUseCase>();
builder.Services.AddScoped<RefreshSessionUseCase>();
builder.Services.AddScoped<LogoutUseCase>();
builder.Services.AddScoped<GetCurrentAccountUseCase>();
builder.Services.AddScoped<ResolvePlayerByTagUseCase>();
builder.Services.AddScoped<UpdateMyPlayerProfileUseCase>();
builder.Services.AddScoped<SendFriendRequestUseCase>();
builder.Services.AddScoped<AcceptFriendRequestUseCase>();
builder.Services.AddScoped<DeclineFriendRequestUseCase>();
builder.Services.AddScoped<CancelFriendRequestUseCase>();
builder.Services.AddScoped<RemoveFriendUseCase>();
builder.Services.AddScoped<ListFriendsUseCase>();
builder.Services.AddScoped<ListIncomingFriendRequestsUseCase>();
builder.Services.AddScoped<ListOutgoingFriendRequestsUseCase>();
builder.Services.AddScoped<CreateChallengeUseCase>();
builder.Services.AddScoped<AcceptChallengeUseCase>();
builder.Services.AddScoped<DeclineChallengeUseCase>();
builder.Services.AddScoped<CancelChallengeUseCase>();
builder.Services.AddScoped<GetSeriesUseCase>();
builder.Services.AddScoped<ListIncomingChallengesUseCase>();
builder.Services.AddScoped<ListOutgoingChallengesUseCase>();
builder.Services.AddScoped<ListActiveSeriesUseCase>();
builder.Services.AddScoped<ListCompletedSeriesUseCase>();
builder.Services.AddScoped<StartAttemptUseCase>();
builder.Services.AddScoped<CompleteAttemptUseCase>();
builder.Services.AddScoped<SubmitMatchResultUseCase>();
builder.Services.AddScoped<GetLeaderboardUseCase>();

builder.Services.AddScoped<ICurrentAccountAccessor, CurrentAccountAccessor>();
builder.Services.AddScoped<ICurrentPlayerProvider, CurrentPlayerProvider>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("ApiCors", policy =>
    {
        policy.SetIsOriginAllowed(origin => Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    // Without this, the handler silently remaps "sub" to the legacy
    // ClaimTypes.NameIdentifier URI, so reading JwtRegisteredClaimNames.Sub back out of
    // ClaimsPrincipal later (CurrentPlayerProvider) would find nothing.
    options.MapInboundClaims = false;

    var jwt = builder.Configuration.GetSection(JwtOptions.SectionName);
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidIssuer = jwt["Issuer"],
        ValidAudience = jwt["Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"]!))
    };
});

builder.Services.AddAuthorization();

// Shared by register/login - both are brute-force/enumeration surfaces. Kept tight in
// Production; relaxed elsewhere so local dev and the integration test suite (which share one
// partition key under TestServer, since it has no real remote IP) aren't rate-limited against
// each other.
var authRequestLimit = builder.Environment.IsProduction() ? 5 : 1000;
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("AuthPolicy", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = authRequestLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

var app = builder.Build();

app.UseExceptionHandler(_ => { });
app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHsts();
}

app.UseCors("ApiCors");
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();

// Liveness: the process is up and can respond - no dependency checks, so a slow/unreachable
// database never takes the container out of rotation for something a restart won't fix.
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});

// Readiness: can this instance actually serve traffic right now (i.e. can it reach Postgres).
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.Run();

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
