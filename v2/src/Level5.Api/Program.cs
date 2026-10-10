using System.Text;
using Level5.Api.BackgroundServices;
using Level5.Api.ErrorHandling;
using Level5.Api.Security;
using Level5.Api.Telemetry;
using Level5.Application.Abstractions;
using Level5.Application.Competition;
using Level5.Application.Identity;
using Level5.Application.Leaderboards;
using Level5.Application.Players;
using Level5.Application.Platform;
using Level5.Application.Results;
using Level5.Application.Social;
using Level5.Infrastructure.DependencyInjection;
using Level5.Infrastructure.Identity;
using Level5.Infrastructure.BloodMoney;
using Level5.Application.BloodMoney;
using Level5.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using System.Net;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Jwt configuration (presence, key length) is validated at startup by JwtOptions' data
// annotations - see AddLevel5Infrastructure's ValidateDataAnnotations().ValidateOnStart().

builder.Services.AddControllers().ConfigureApiBehaviorOptions(options =>
{
    var fallback = options.InvalidModelStateResponseFactory;
    options.InvalidModelStateResponseFactory = context =>
    {
        if (context.ActionDescriptor.RouteValues["controller"] != "BloodMoneyChat") return fallback(context);
        var action = context.ActionDescriptor.RouteValues["action"];
        var code = action switch
        {
            "AdvanceRead" => "invalid_chat_read_position",
            "SetMuted" => "validation_failed",
            "Report" => "invalid_chat_report",
            "List" => "invalid_chat_limit",
            _ => "invalid_chat_message"
        };
        // Avoid reflecting raw input or serializer messages into chat errors.
        var problem = new ProblemDetails { Status = 400, Title = "Chat request is invalid.", Type = $"https://level5.game/errors/{code}" };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        return new BadRequestObjectResult(problem);
    };
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // Without this, Swashbuckle ignores this project's <Nullable>enable</Nullable> annotations
    // entirely and emits every property as optional/nullable - e.g. AccessTokenResponseDto's
    // AccessToken would be indistinguishable in the OpenAPI document from an actually-optional
    // field. This makes the generated contract (issue #4) match what the DTOs actually
    // guarantee, with no change to runtime (de)serialization behavior.
    options.SupportNonNullableReferenceTypes();
    options.SchemaFilter<BloodMoneyChatSchemaFilter>();
    options.OperationFilter<BloodMoneyChatOperationFilter>();
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
builder.Services.AddBloodMoneyChat(new BloodMoneyChatRatePolicy(5, TimeSpan.FromSeconds(10), 30, TimeSpan.FromMinutes(1)),
    new BloodMoneyChatNotificationPolicy("v1", TimeSpan.FromMinutes(5)));
builder.Services.AddScoped<BloodMoneyChatEligibilityFilter>();
builder.Services.AddLevel5Telemetry(builder.Configuration);

// Use cases - thin, stateless, one per operation. Registered here (the composition root)
// rather than via a DI extension inside Application, so Application stays free of any
// dependency-injection framework reference.
builder.Services.AddScoped<RegisterAccountUseCase>();
builder.Services.AddScoped<LoginUseCase>();
builder.Services.AddScoped<RefreshSessionUseCase>();
builder.Services.AddScoped<LogoutUseCase>();
builder.Services.AddScoped<GetCurrentAccountUseCase>();
builder.Services.AddScoped<GetMyEmailStatusUseCase>();
builder.Services.AddScoped<RequestEmailVerificationUseCase>();
builder.Services.AddScoped<ResendEmailVerificationUseCase>();
builder.Services.AddScoped<CompleteEmailVerificationUseCase>();
builder.Services.AddScoped<RequestEmailChangeUseCase>();
builder.Services.AddScoped<ResendEmailChangeUseCase>();
builder.Services.AddScoped<CancelEmailChangeUseCase>();
builder.Services.AddScoped<CompleteEmailChangeUseCase>();
builder.Services.AddScoped<RequestPasswordResetUseCase>();
builder.Services.AddScoped<CompletePasswordResetUseCase>();
builder.Services.AddScoped<ChangePasswordUseCase>();
builder.Services.AddScoped<AccountSecuritySessionGuard>();
builder.Services.AddScoped<ListAccountSessionsUseCase>();
builder.Services.AddScoped<RevokeAccountSessionUseCase>();
builder.Services.AddScoped<RevokeOtherAccountSessionsUseCase>();
builder.Services.AddScoped<RevokeAllAccountSessionsUseCase>();
builder.Services.AddScoped<ResolvePlayerByTagUseCase>();
builder.Services.AddScoped<UpdateMyPlayerProfileUseCase>();
builder.Services.AddScoped<GetMyPlayerProfileUseCase>();
builder.Services.AddScoped<Level5.Application.BloodMoney.GetMyBloodCreditBalanceUseCase>();
builder.Services.AddScoped<Level5.Application.BloodMoney.IssueBloodCreditsUseCase>();
builder.Services.AddScoped<Level5.Application.BloodMoney.SpendBloodCreditsUseCase>();
builder.Services.AddScoped<Level5.Application.BloodMoney.CorrectBloodCreditsUseCase>();
builder.Services.AddScoped<Level5.Application.BloodMoney.BloodCreditReservationMutator>();
builder.Services.AddScoped<Level5.Application.BloodMoney.ReserveBloodCreditsUseCase>();
builder.Services.AddScoped<Level5.Application.BloodMoney.ReleaseBloodCreditsUseCase>();
builder.Services.AddScoped<CheckMyProductAccessUseCase>();
builder.Services.AddScoped<ListMyEntitlementsUseCase>();
builder.Services.AddScoped<GrantProductEntitlementUseCase>();
builder.Services.AddScoped<RevokeProductEntitlementUseCase>();
builder.Services.AddScoped<INotificationWriter, NotificationWriter>();
builder.Services.AddScoped<ListNotificationsUseCase>();
builder.Services.AddScoped<SetNotificationReadStateUseCase>();
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
builder.Services.AddScoped<ListTerminalHistoryUseCase>();
builder.Services.AddScoped<StartAttemptUseCase>();
builder.Services.AddScoped<CompleteAttemptUseCase>();
builder.Services.AddScoped<SubmitMatchResultUseCase>();
builder.Services.AddScoped<GetLeaderboardUseCase>();
builder.Services.AddScoped<ExpireStalePendingChallengesUseCase>();
builder.Services.AddHostedService<ChallengeExpirySweepService>();

builder.Services.AddScoped<ICurrentAccountAccessor, CurrentAccountAccessor>();
builder.Services.AddScoped<ICurrentAuthSessionAccessor, CurrentAuthSessionAccessor>();
builder.Services.AddScoped<IClientKindAccessor, ClientKindAccessor>();
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
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"]!)),
        // Access-token expiry is the documented upper bound for bounded revocation. The
        // framework default permits a token for five minutes beyond exp, which would silently
        // extend the default disabled-account access window from 15 to roughly 20 minutes.
        ClockSkew = TimeSpan.Zero
    };
});

builder.Services.AddAuthorization();

// Each authentication/email-verification operation has its own per-IP budget so traffic to one
// endpoint cannot starve the others. There is no second aggregate limiter. Non-Production stays
// relaxed so local development and integration tests sharing TestServer's partition key do not
// interfere with one another.
var securityRequestLimit = builder.Environment.IsProduction() ? 5 : 1000;
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthRateLimitPolicyNames.Register,
        httpContext => CreateSecurityRateLimitPartition(httpContext, securityRequestLimit));
    options.AddPolicy(AuthRateLimitPolicyNames.Login,
        httpContext => CreateSecurityRateLimitPartition(httpContext, securityRequestLimit));
    options.AddPolicy(AuthRateLimitPolicyNames.Refresh,
        httpContext => CreateSecurityRateLimitPartition(httpContext, securityRequestLimit));
    options.AddPolicy(AuthRateLimitPolicyNames.Logout,
        httpContext => CreateSecurityRateLimitPartition(httpContext, securityRequestLimit));
    options.AddPolicy(EmailVerificationRateLimitPolicyNames.Request,
        httpContext => CreateSecurityRateLimitPartition(httpContext, securityRequestLimit));
    options.AddPolicy(EmailVerificationRateLimitPolicyNames.Resend,
        httpContext => CreateSecurityRateLimitPartition(httpContext, securityRequestLimit));
    options.AddPolicy(EmailVerificationRateLimitPolicyNames.Complete,
        httpContext => CreateSecurityRateLimitPartition(httpContext, securityRequestLimit));
    options.AddPolicy(EmailChangeRateLimitPolicyNames.Request,
        httpContext => CreateSecurityRateLimitPartition(httpContext, securityRequestLimit));
    options.AddPolicy(EmailChangeRateLimitPolicyNames.Resend,
        httpContext => CreateSecurityRateLimitPartition(httpContext, securityRequestLimit));
    options.AddPolicy(EmailChangeRateLimitPolicyNames.Complete,
        httpContext => CreateSecurityRateLimitPartition(httpContext, securityRequestLimit));
    options.AddPolicy(PasswordSecurityRateLimitPolicyNames.ResetRequest,
        httpContext => CreateSecurityRateLimitPartition(httpContext, securityRequestLimit));
    options.AddPolicy(PasswordSecurityRateLimitPolicyNames.ResetComplete,
        httpContext => CreateSecurityRateLimitPartition(httpContext, securityRequestLimit));
    options.AddPolicy(PasswordSecurityRateLimitPolicyNames.Change,
        httpContext => CreateSecurityRateLimitPartition(httpContext, securityRequestLimit));
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

static RateLimitPartition<string> CreateSecurityRateLimitPartition(HttpContext httpContext, int permitLimit) =>
    RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
