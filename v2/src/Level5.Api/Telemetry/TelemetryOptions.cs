namespace Level5.Api.Telemetry;

/// <summary>
/// Bound from the <c>Telemetry</c> configuration section. Every field has a safe default so the
/// app starts, with tracing/metrics enabled but no exporter, when nothing is configured at all -
/// local dev and integration tests never need an observability backend (issue #22).
/// </summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    public string ServiceName { get; set; } = "Level5.Api.V2";

    public string ServiceVersion { get; set; } = "1.0.0";

    public OtlpOptions Otlp { get; set; } = new();

    public sealed class OtlpOptions
    {
        /// <summary>
        /// OTLP collector endpoint (e.g. <c>http://localhost:4317</c>). Null/empty (the default)
        /// means "no exporter" - traces/metrics are still recorded in-process, just never shipped
        /// anywhere, which is exactly what local dev and the test suite need.
        /// </summary>
        public string? Endpoint { get; set; }
    }
}
