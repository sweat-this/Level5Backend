using System.Net;
using System.Reflection;
using System.Security.Claims;
using Level5Backend.Controllers;
using Level5Backend.Models;
using Level5Backend.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Level5Backend.Tests;

public class UserReportApiControllerTests
{
    [Fact]
    public async Task AnonymousRequestAttemptingToSpoofIdentity_PersistsNullAttribution()
    {
        await using var context = CreateContext();
        var controller = CreateController(context);
        var report = MakeReport();

        AssertCreated(await controller.PostUserReport(report));

        var persisted = await context.UserReports.SingleAsync();
        Assert.Null(persisted.Userid);
        Assert.Null(persisted.UserName);
    }

    [Fact]
    public async Task CompleteAuthenticatedClaims_OverrideSpoofedRequestIdentity()
    {
        await using var context = CreateContext();
        var principal = AuthenticatedPrincipal(
            new Claim("Userid", "42"),
            new Claim("username", "signed-user"));
        var controller = CreateController(context, principal: principal);

        AssertCreated(await controller.PostUserReport(MakeReport()));

        var persisted = await context.UserReports.SingleAsync();
        Assert.Equal(42, persisted.Userid);
        Assert.Equal("signed-user", persisted.UserName);
    }

    [Theory]
    [InlineData("Userid", "42")]
    [InlineData("username", "signed-user")]
    public async Task PartialAuthenticatedIdentity_NeverFallsBackToRequestIdentity(
        string claimType,
        string claimValue)
    {
        await using var context = CreateContext();
        var controller = CreateController(
            context,
            principal: AuthenticatedPrincipal(new Claim(claimType, claimValue)));

        AssertCreated(await controller.PostUserReport(MakeReport()));

        var persisted = await context.UserReports.SingleAsync();
        Assert.Null(persisted.Userid);
        Assert.Null(persisted.UserName);
    }

    [Fact]
    public async Task UnauthenticatedClaims_AreNotTrustedAsReporterIdentity()
    {
        await using var context = CreateContext();
        var claims = new[]
        {
            new Claim("Userid", "42"),
            new Claim("username", "unsigned-user")
        };
        var controller = CreateController(
            context,
            principal: new ClaimsPrincipal(new ClaimsIdentity(claims)));

        AssertCreated(await controller.PostUserReport(MakeReport()));

        var persisted = await context.UserReports.SingleAsync();
        Assert.Null(persisted.Userid);
        Assert.Null(persisted.UserName);
    }

    [Fact]
    public async Task IdenticalAnonymousReports_AreBothPermitted()
    {
        await using var context = CreateContext();
        var controller = CreateController(context);

        AssertCreated(await controller.PostUserReport(MakeReport("same text")));
        AssertCreated(await controller.PostUserReport(MakeReport("same text")));

        Assert.Equal(2, await context.UserReports.CountAsync());
    }

    [Fact]
    public async Task SameAuthenticatedUserSubmittingSameExactText_ReturnsConflict()
    {
        await using var context = CreateContext();
        var controller = CreateController(
            context,
            principal: AuthenticatedPrincipal(
                new Claim("Userid", "42"),
                new Claim("username", "signed-user")));

        AssertCreated(await controller.PostUserReport(MakeReport("same text")));
        var duplicate = await controller.PostUserReport(MakeReport("same text"));

        Assert.IsType<ConflictResult>(duplicate.Result);
        Assert.Single(await context.UserReports.ToListAsync());
    }

    [Fact]
    public async Task ClientSuppliedIp_IsReplacedByServerDerivedIp()
    {
        await using var context = CreateContext();
        var controller = CreateController(context, IPAddress.Parse("203.0.113.17"));
        var report = MakeReport();
        report.Ipaddress = "198.51.100.23";

        AssertCreated(await controller.PostUserReport(report));

        Assert.Equal("203.0.113.17", (await context.UserReports.SingleAsync()).Ipaddress);
    }

    [Fact]
    public async Task ClientSuppliedDate_IsReplacedByServerUtcTime()
    {
        await using var context = CreateContext();
        var report = MakeReport();
        report.Date = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime before = DateTime.UtcNow;

        AssertCreated(await CreateController(context).PostUserReport(report));
        DateTime after = DateTime.UtcNow;

        DateTime persistedDate = (await context.UserReports.SingleAsync()).Date;
        Assert.Equal(DateTimeKind.Utc, persistedDate.Kind);
        Assert.InRange(persistedDate, before, after);
    }

    [Fact]
    public async Task EmptyReport_IsRejected()
    {
        await AssertInvalidReport(string.Empty);
    }

    [Fact]
    public async Task NullReport_IsRejected()
    {
        await using var context = CreateContext();
        var report = MakeReport();
        report.Report = null!;

        var result = await CreateController(context).PostUserReport(report);

        Assert.IsType<BadRequestResult>(result.Result);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task WhitespaceOnlyReport_IsRejected()
    {
        await AssertInvalidReport(" \t\r\n");
    }

    [Fact]
    public async Task ReportLongerThanDatabaseLimit_IsRejected()
    {
        await AssertInvalidReport(new string('x', 256));
    }

    [Fact]
    public void PostOnly_HasDedicatedUserReportRateLimitPolicy()
    {
        MethodInfo post = typeof(UserReportApiController)
            .GetMethod(nameof(UserReportApiController.PostUserReport))!;
        var postAttribute = Assert.Single(post.GetCustomAttributes<EnableRateLimitingAttribute>());
        Assert.Equal(UserReportRateLimitPolicy.Name, postAttribute.PolicyName);

        MethodInfo get = typeof(UserReportApiController)
            .GetMethod(nameof(UserReportApiController.GetAllReports))!;
        Assert.Empty(get.GetCustomAttributes<EnableRateLimitingAttribute>());
    }

    [Fact]
    public void UserReportRateLimitPolicy_UsesRequiredFixedWindowSettingsAndIpPartition()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.17");

        Assert.Equal("203.0.113.17", UserReportRateLimitPolicy.GetPartitionKey(httpContext));

        var options = UserReportRateLimitPolicy.CreateOptions();
        Assert.Equal(5, options.PermitLimit);
        Assert.Equal(TimeSpan.FromMinutes(1), options.Window);
        Assert.Equal(0, options.QueueLimit);
    }

    private static Level5Context CreateContext()
    {
        var options = new DbContextOptionsBuilder<Level5Context>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new Level5Context(options);
    }

    private static UserReportApiController CreateController(
        Level5Context context,
        IPAddress? remoteIpAddress = null,
        ClaimsPrincipal? principal = null)
    {
        var httpContext = new DefaultHttpContext
        {
            User = principal ?? new ClaimsPrincipal(new ClaimsIdentity())
        };
        httpContext.Connection.RemoteIpAddress = remoteIpAddress;

        return new UserReportApiController(
            context,
            NullLogger<UserReportApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    private static ClaimsPrincipal AuthenticatedPrincipal(params Claim[] claims)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static UserReport MakeReport(string text = "a useful report")
    {
        return new UserReport
        {
            Report = text,
            Userid = 999,
            UserName = "spoofed-local-profile",
            Os = "Windows",
            Device = "Desktop",
            DeviceName = "Test device",
            Version = "1.0",
            Ipaddress = "198.51.100.23",
            Date = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
    }

    private static void AssertCreated(ActionResult<User> result)
    {
        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    private static async Task AssertInvalidReport(string text)
    {
        await using var context = CreateContext();
        var result = await CreateController(context).PostUserReport(MakeReport(text));

        Assert.IsType<BadRequestResult>(result.Result);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Empty(await context.UserReports.ToListAsync());
    }
}
