namespace Level5.Application.Abstractions;

/// <summary>Configuration for refresh-session lifetime, kept behind a port so Application depends only on a value, not on Infrastructure's configuration binding.</summary>
public interface IAuthSessionPolicy
{
    TimeSpan RefreshTokenLifetime { get; }
}
