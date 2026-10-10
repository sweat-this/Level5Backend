using System.Globalization;
using Level5.Domain.BloodMoney;

namespace Level5.Application.BloodMoney;

/// <summary>Immutable producer policy; buckets depend only on the committed message's server timestamp.</summary>
public sealed record BloodMoneyChatNotificationPolicy
{
    public string Version { get; }
    public TimeSpan Window { get; }

    public BloodMoneyChatNotificationPolicy(string version, TimeSpan window)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 32 ||
            version.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("Policy version must contain 1–32 ASCII letters, digits or hyphens.", nameof(version));
        if (window <= TimeSpan.Zero || window.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentException("Notification window must be a positive whole number of seconds.", nameof(window));
        Version = version;
        Window = window;
    }

    public string SourceEventKey(BloodMoneyChallengeId challengeId, DateTimeOffset createdAt)
    {
        var seconds = createdAt.ToUnixTimeSeconds();
        var windowSeconds = Window.Ticks / TimeSpan.TicksPerSecond;
        var bucket = Math.DivRem(seconds, windowSeconds, out var remainder);
        if (remainder < 0) bucket--; // Floor also for instants before the Unix epoch.
        return string.Create(CultureInfo.InvariantCulture, $"chat-{Version}:{challengeId.Value:N}:{bucket * windowSeconds}");
    }
}
