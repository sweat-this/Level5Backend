using System.Diagnostics.Metrics;

namespace Level5.Api.Observability;

/// <summary>
/// The one Api-layer counter issue #22 calls for: unhandled 5xx responses. Static for the same
/// reason as <see cref="Level5.Application.Observability.ApplicationMetrics"/> - OpenTelemetry's
/// <c>MeterProvider</c> subscribes by meter name (see Level5.Api's telemetry registration), not
/// through DI.
/// </summary>
public static class ApiMetrics
{
    public const string MeterName = "Level5.Api";
    public const string CodeTag = "code";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Requests <see cref="Level5.Api.ErrorHandling.ApiExceptionHandler"/> reported as an unhandled
    /// 500. Tag <c>code</c> is the same small, fixed <c>ProblemDetails</c> "code" vocabulary already
    /// returned to the client (currently just <c>internal_error</c>) - never the exception's own
    /// message or type.
    /// </summary>
    public static readonly Counter<long> UnhandledServerErrors =
        Meter.CreateCounter<long>("http.server.5xx", description: "Requests that resulted in an unhandled 5xx response, by ProblemDetails code.");
}
