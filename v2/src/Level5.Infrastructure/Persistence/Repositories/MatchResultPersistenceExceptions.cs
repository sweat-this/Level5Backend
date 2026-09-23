namespace Level5.Infrastructure.Persistence.Repositories;

/// <summary>
/// A persisted <c>match_results</c> row's JSONB metrics or modifiers document contained a value
/// that does not map to a known domain identifier (e.g. a metric name this build's
/// <see cref="Level5.Domain.Results.MatchResultMetric"/> enum does not recognize). This indicates
/// corrupted or forward-incompatible data, not a client mistake, so it is intentionally not a
/// <see cref="Level5.Application.Common.AppException"/>/<see cref="Level5.Domain.Common.DomainException"/> -
/// it falls through to a plain 500 in <c>ApiExceptionHandler</c> and is logged there.
/// </summary>
public sealed class CorruptMatchResultStateException : Exception
{
    public CorruptMatchResultStateException(string message) : base(message)
    {
    }
}
