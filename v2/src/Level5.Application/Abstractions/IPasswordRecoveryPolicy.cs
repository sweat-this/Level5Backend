namespace Level5.Application.Abstractions;

public interface IPasswordRecoveryPolicy
{
    TimeSpan TokenLifetime { get; }
    TimeSpan RequestCooldown { get; }
}
