using Level5.Domain.Ids;

namespace Level5.Domain.Identity;

/// <summary>
/// The private authentication identity behind a player. Holds nothing about in-game identity
/// (display name, tag) - that lives on <see cref="Players.PlayerProfile"/>. Holds no email or
/// other PII; the password hash is an opaque string produced by an <c>IPasswordHasher</c> port,
/// never interpreted by the domain itself.
/// </summary>
public sealed class Account
{
    public AccountId Id { get; private set; }
    public Username Username { get; private set; }
    public string PasswordHash { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private Account(AccountId id, Username username, string passwordHash, DateTimeOffset createdAt)
    {
        Id = id;
        Username = username;
        PasswordHash = passwordHash;
        CreatedAt = createdAt;
    }

    public static Account Register(Username username, string passwordHash, DateTimeOffset now)
        => new(AccountId.New(), username, passwordHash, now);

    public static Account Rehydrate(AccountId id, Username username, string passwordHash, DateTimeOffset createdAt)
        => new(id, username, passwordHash, createdAt);

    public void ChangePasswordHash(string newPasswordHash) => PasswordHash = newPasswordHash;
}
