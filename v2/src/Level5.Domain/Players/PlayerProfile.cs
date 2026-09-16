using Level5.Domain.Common;
using Level5.Domain.Ids;

namespace Level5.Domain.Players;

/// <summary>
/// The public, in-game identity of an account. Deliberately holds nothing from the private
/// account/authentication identity (no password hash, email, or IP address) - only fields
/// that are safe to reveal to other players.
/// </summary>
public sealed class PlayerProfile
{
    public PlayerId Id { get; private set; }
    public AccountId AccountId { get; private set; }
    public string DisplayName { get; private set; }
    public PlayerTag Tag { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private PlayerProfile(PlayerId id, AccountId accountId, string displayName, PlayerTag tag, DateTimeOffset createdAt)
    {
        Id = id;
        AccountId = accountId;
        DisplayName = displayName;
        Tag = tag;
        CreatedAt = createdAt;
    }

    public static PlayerProfile Create(AccountId accountId, string displayName, PlayerTag tag, DateTimeOffset now)
    {
        var trimmed = (displayName ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            throw new InvalidDisplayNameException("Display name cannot be empty.");
        }

        if (trimmed.Length > 32)
        {
            throw new InvalidDisplayNameException("Display name cannot exceed 32 characters.");
        }

        return new PlayerProfile(PlayerId.New(), accountId, trimmed, tag, now);
    }

    /// <summary>Reconstitutes a profile from persisted state. Infrastructure only.</summary>
    public static PlayerProfile Rehydrate(PlayerId id, AccountId accountId, string displayName, PlayerTag tag, DateTimeOffset createdAt)
        => new(id, accountId, displayName, tag, createdAt);
}

public sealed class InvalidDisplayNameException : DomainException
{
    public InvalidDisplayNameException(string message) : base(message)
    {
    }
}
