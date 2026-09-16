namespace Level5.Application.Common;

/// <summary>Base type for application-layer errors that map to a stable HTTP/ProblemDetails response.</summary>
public abstract class AppException : Exception
{
    public abstract string Code { get; }

    protected AppException(string message) : base(message)
    {
    }
}

public sealed class NotFoundException : AppException
{
    public override string Code => "not_found";

    public NotFoundException(string message) : base(message)
    {
    }
}

public sealed class ConflictException : AppException
{
    public override string Code => "conflict";

    public ConflictException(string message) : base(message)
    {
    }
}

public sealed class ValidationFailedException : AppException
{
    public override string Code => "validation_failed";

    public ValidationFailedException(string message) : base(message)
    {
    }
}

/// <summary>Thrown when an operation requires an accepted friendship that does not exist.</summary>
public sealed class FriendshipRequiredException : AppException
{
    public override string Code => "friendship_required";

    public FriendshipRequiredException(string message) : base(message)
    {
    }
}
