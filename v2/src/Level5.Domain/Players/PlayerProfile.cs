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
        => new(PlayerId.New(), accountId, ValidateDisplayName(displayName), tag, now);

    /// <summary>
    /// Changes the public display name. PlayerId, AccountId, Tag, and CreatedAt are never touched -
    /// this is the only mutable field on a profile in this slice.
    /// </summary>
    public void ChangeDisplayName(string displayName)
    {
        DisplayName = ValidateDisplayName(displayName);
    }

    private static string ValidateDisplayName(string displayName)
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

        return trimmed;
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
