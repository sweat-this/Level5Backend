namespace Level5.Application.Abstractions;

public sealed record GeneratedPasswordResetToken(string RawValue, string Hash);

public interface IPasswordResetTokenGenerator
{
    GeneratedPasswordResetToken Generate();
    string Hash(string rawValue);
}
