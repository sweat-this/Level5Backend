using Level5.Api.Observability;
using Level5.Application.Observability;
using Level5.Infrastructure.Persistence;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Level5.Api.Telemetry;

/// <summary>
/// The standard OpenTelemetry baseline for issue #22: ASP.NET Core request tracing/metrics,
/// database spans (via <see cref="PersistenceTelemetry.DatabaseActivitySourceName"/> - Npgsql's
/// own native tracing support, no extra EF Core/Npgsql instrumentation package needed), .NET
/// runtime/process metrics, and this app's own small set of service-level counters
/// (<see cref="ApplicationMetrics"/>/<see cref="ApiMetrics"/>). An OTLP exporter is wired in only
/// when <see cref="TelemetryOptions.OtlpOptions.Endpoint"/> is configured - with it empty (the
/// default), everything above still runs, just with nowhere to ship to, so local dev and
/// integration tests never need a collector/backend.
/// </summary>
public static class TelemetryServiceCollectionExtensions
{
    public static IServiceCollection AddLevel5Telemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>() ?? new TelemetryOptions();
        var otlpEndpoint = options.Otlp.Endpoint;

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(options.ServiceName, serviceVersion: options.ServiceVersion))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation();
                tracing.AddSource(PersistenceTelemetry.DatabaseActivitySourceName);

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpEndpoint));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation();
                metrics.AddRuntimeInstrumentation();
                metrics.AddMeter(ApplicationMetrics.MeterName, ApiMetrics.MeterName);

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    metrics.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpEndpoint));
                }
            });

        return services;
    }
}
