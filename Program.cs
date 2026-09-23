using Level5Backend.Models;
using Level5Backend.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Threading.RateLimiting;


var MyAllowSpecificOrigins = "ApiCors";

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Connection string comes from configuration (appsettings.json "ConnectionStrings:DefaultConnection"),
// which is overridden in production via the ConnectionStrings__DefaultConnection environment variable -
// it must never be a literal in source, since source is committed to git.
string _connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured. Set it via user-secrets (local dev) or the ConnectionStrings__DefaultConnection environment variable (production).");

// TokenController signs JWTs with this at request time; failing fast here surfaces a missing key at
// startup instead of as a cryptic 500 on the first login attempt.
if (string.IsNullOrEmpty(builder.Configuration["Jwt:Key"]))
{
    throw new InvalidOperationException("Jwt:Key is not configured. Set it via user-secrets (local dev) or the Jwt__Key environment variable (production).");
}

// add CORS
// No live site to target yet - allow any localhost/127.0.0.1 origin regardless of port so this
// works against whatever port a local frontend dev server happens to be running on. Once there's
// a real deployed frontend, replace this with WithOrigins("https://<real-domain>").
builder.Services.AddCors(options =>
{
    options.AddPolicy(name: MyAllowSpecificOrigins,
                      policy =>
                      {
                          policy.SetIsOriginAllowed(origin =>
                                    Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback).
                                              AllowAnyHeader().
                                              AllowAnyMethod();
                      });
});

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Without this, an unhandled exception anywhere returns the bare framework default (a raw stack
// trace in dev, an empty 500 in prod) instead of a consistent ProblemDetails body - and nothing
// about it gets logged unless a controller happens to catch and log it itself.
builder.Services.AddProblemDetails();

builder.Services.AddDbContext<Level5Context>(options =>
options.UseNpgsql(_connectionString));

// Recomputes ServerStats periodically off the request path (see Services/ServerStatsService.cs) -
// this used to run synchronously inline on every highscore POST.
builder.Services.AddScoped<IServerStatsService, ServerStatsService>();
builder.Services.AddHostedService<ServerStatsBackgroundService>();

// For container/orchestrator liveness-readiness probes - there was previously no way to tell from
// outside the process whether the app could actually reach Postgres.
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

// TokenController issues JWTs signed with Jwt:Key/Issuer/Audience; without registering a matching
// authentication scheme here, every [Authorize]-protected endpoint 500s instead of 401ing, since
// there's no DefaultChallengeScheme for the authorization middleware to fall back on.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
    };
});

// Used by the remaining admin-only endpoints (UserReportApiController.GetAllReports,
// ApplicationController.PostApplicationVersion). There's no roles system in this app, but User.Isdev
// already exists for exactly this purpose - TokenController puts it on the JWT as a claim.
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequireDev", policy => policy.RequireClaim("IsDev", "true"));
});

// TokenController.Post checks a submitted password against a stored hash with no other guard
// against brute-forcing - this is the only throttle on login attempts. Partitioned per client IP
// so one attacker can't exhaust the limit for everyone else.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("LoginPolicy", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    // UsersApiController.PostUser hashes every submitted password with the same deliberately-expensive
    // PBKDF2 call as login, and was the only mutating unauthenticated endpoint with no rate limit at
    // all - a flood of signups is the same resource-exhaustion shape login is already guarded against.
    // A separate policy (rather than reusing LoginPolicy) keeps the two counters independent per IP,
    // so hammering registration doesn't also lock that IP out of logging in, and vice versa.
    options.AddPolicy("RegisterPolicy", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

var app = builder.Build();
// First so it can catch exceptions thrown by everything downstream, including other middleware.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    // Full schema/route disclosure - fine for local dev, not for anyone who can reach a
    // production deployment.
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    // Tells browsers to only ever talk to this host over HTTPS, even if a link/bookmark points at
    // plain http:// - skipped in dev since local HTTPS certs are often self-signed/untrusted.
    app.UseHsts();
}

app.UseCors(MyAllowSpecificOrigins);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapHealthChecks("/health");
app.Run();
