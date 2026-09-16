using Level5.Application.Common;
using Level5.Domain.Competition;
using Level5.Domain.Common;
using Level5.Domain.Social;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Level5.Api.ErrorHandling;

/// <summary>
/// Central place where every exception a controller lets escape becomes a ProblemDetails
/// response. Nothing about this depends on which controller/action threw - domain and
/// application exceptions already carry everything needed to pick a status code and a stable
/// machine-readable <c>code</c>. Anything unrecognised becomes a bare 500 with no exception
/// detail, so a bug never leaks an internal message or stack trace to a client.
/// </summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code) = Classify(exception);

        if (status == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception processing {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }

        var problemDetails = new ProblemDetails
        {
            Status = status,
            Title = status == StatusCodes.Status500InternalServerError ? "An unexpected error occurred." : exception.Message,
            Type = $"https://level5.game/errors/{code}",
        };
        problemDetails.Extensions["code"] = code;
        problemDetails.Extensions["traceId"] = httpContext.TraceIdentifier;

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);
        return true;
    }

    // Persistence-specific failures are not listed here on purpose: the infrastructure layer
    // translates a unique-constraint violation into ConflictException before it gets this far
    // (see ConflictTranslatingSave), so EF/Npgsql exception types never leak into the HTTP layer.
    // Any database failure that is *not* a recognised conflict deliberately falls through to the
    // 500 branch below, where it gets logged rather than being mislabelled as a client conflict.
    private static (int Status, string Code) Classify(Exception exception) => exception switch
    {
        NotFoundException e => (StatusCodes.Status404NotFound, e.Code),
        Level5.Application.Identity.InvalidCredentialsException e => (StatusCodes.Status401Unauthorized, e.Code),
        Level5.Application.Identity.InvalidRefreshTokenException e => (StatusCodes.Status401Unauthorized, e.Code),
        FriendshipRequiredException e => (StatusCodes.Status403Forbidden, e.Code),
        ConflictException e => (StatusCodes.Status409Conflict, e.Code),
        ValidationFailedException e => (StatusCodes.Status400BadRequest, e.Code),
        AppException e => (StatusCodes.Status400BadRequest, e.Code),

        SeriesAuthorizationException e => (StatusCodes.Status403Forbidden, e.Code),
        FriendRequestAuthorizationException e => (StatusCodes.Status403Forbidden, e.Code),
        IllegalSeriesTransitionException e => (StatusCodes.Status409Conflict, e.Code),
        IllegalFriendRequestTransitionException e => (StatusCodes.Status409Conflict, e.Code),
        AttemptNotStartedException e => (StatusCodes.Status400BadRequest, e.Code),
        DomainException e => (StatusCodes.Status400BadRequest, e.Code),

        _ => (StatusCodes.Status500InternalServerError, "internal_error")
    };
}
