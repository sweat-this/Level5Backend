namespace Level5.Application.Abstractions;

public enum PasswordVerificationResult
{
    Failed,
    Success,
    SuccessRehashNeeded
}

/// <summary>Wraps the concrete password hashing algorithm behind a port the domain never sees.</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    PasswordVerificationResult Verify(string passwordHash, string suppliedPassword);
}
