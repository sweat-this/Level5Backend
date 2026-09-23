namespace Level5.Domain.Common;

/// <summary>
/// Base type for all errors that represent a violated domain invariant or an illegal state
/// transition, as opposed to an infrastructure failure. Application handlers catch this type
/// to translate it into a stable, participant-safe error response.
/// </summary>
public abstract class DomainException : Exception
{
    /// <summary>Stable, machine-readable identifier for API error responses. Defaults to the concrete exception type name.</summary>
    public virtual string Code => GetType().Name;

    protected DomainException(string message) : base(message)
    {
    }
}
