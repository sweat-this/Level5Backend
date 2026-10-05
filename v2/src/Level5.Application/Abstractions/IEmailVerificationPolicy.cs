namespace Level5.Application.Abstractions;

public interface IEmailVerificationPolicy
{
    TimeSpan TokenLifetime { get; }

    TimeSpan ResendCooldown { get; }
}
