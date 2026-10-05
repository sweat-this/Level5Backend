namespace Level5.Application.Abstractions;

public sealed record GeneratedEmailVerificationToken(string RawValue, string Hash);

public interface IEmailVerificationTokenGenerator
{
    GeneratedEmailVerificationToken Generate();

    string Hash(string rawValue);
}
