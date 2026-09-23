namespace Level5.Infrastructure.Persistence.Repositories;

/// <summary>
/// A persisted <c>competitive_series</c> row's JSONB state carries a
/// <c>schemaVersion</c> this build does not support (see
/// <see cref="Rows.VersusSeriesStateJson.CurrentSchemaVersion"/>). This is a genuine
/// server-side data problem, not a client mistake, so it is intentionally not a
/// <see cref="Level5.Application.Common.AppException"/>/<see cref="Level5.Domain.Common.DomainException"/> -
/// it falls through to a plain 500 in <c>ApiExceptionHandler</c> and is logged there.
/// </summary>
public sealed class UnsupportedSeriesSchemaVersionException : Exception
{
    public UnsupportedSeriesSchemaVersionException(string message) : base(message)
    {
    }
}

/// <summary>
/// A persisted <c>competitive_series</c> row's JSONB state matched a supported schema version
/// but contained a value that does not map to a known domain identifier (e.g. a metric name this
/// build's <see cref="Level5.Domain.Competition.ResultMetric"/> enum does not recognize). Like
/// <see cref="UnsupportedSeriesSchemaVersionException"/>, this indicates corrupted or
/// forward-incompatible data, not a client mistake.
/// </summary>
public sealed class CorruptSeriesStateException : Exception
{
    public CorruptSeriesStateException(string message) : base(message)
    {
    }
}
