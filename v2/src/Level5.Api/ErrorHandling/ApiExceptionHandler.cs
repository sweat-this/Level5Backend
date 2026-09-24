using Level5.Api.Observability;
using Level5.Application.Common;
using Level5.Domain.Competition;
using Level5.Domain.Common;
using Level5.Domain.Social;
using Level5.Infrastructure.Persistence;
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
            ApiMetrics.UnhandledServerErrors.Add(1, new KeyValuePair<string, object?>(ApiMetrics.CodeTag, code));
        }
        else if (status == StatusCodes.Status503ServiceUnavailable)
        {
            logger.LogError(exception, "Database unavailable processing {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
            ApiMetrics.UnhandledServerErrors.Add(1, new KeyValuePair<string, object?>(ApiMetrics.CodeTag, code));
        }

        var problemDetails = new ProblemDetails
        {
            Status = status,
            Title = status switch
            {
                StatusCodes.Status500InternalServerError => "An unexpected error occurred.",
                StatusCodes.Status503ServiceUnavailable => "The service is temporarily unavailable. Please retry.",
                _ => exception.Message
            },
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
    // The one other database outcome recognised here is a transient failure (connection loss or
    // server shutdown that outlived Infrastructure's bounded retry budget, or a timeout, which is
    // never retried) - a retryable 503, classified by PersistenceFailures so no Npgsql type is
    // referenced from this layer. Any
    // other database failure deliberately falls through to the 500 branch below, where it gets
    // logged rather than being mislabelled as a client conflict or a transient outage.
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
        AttemptIdentityMismatchException e => (StatusCodes.Status400BadRequest, e.Code),
        ConflictingAttemptResultException e => (StatusCodes.Status409Conflict, e.Code),
        OpenTargetIssuanceOrderException e => (StatusCodes.Status409Conflict, e.Code),
        MissingRequiredMetricException e => (StatusCodes.Status400BadRequest, e.Code),
        DomainException e => (StatusCodes.Status400BadRequest, e.Code),

        _ when PersistenceFailures.IsTransientUnavailability(exception) => (StatusCodes.Status503ServiceUnavailable, "service_unavailable"),
        _ => (StatusCodes.Status500InternalServerError, "internal_error")
    };
}
