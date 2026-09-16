namespace Level5.Application.Abstractions;

/// <summary>Source of the current UTC time, injected so use cases and tests can control it.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
