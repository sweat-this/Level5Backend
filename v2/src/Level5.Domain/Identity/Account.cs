using Level5.Domain.Ids;

namespace Level5.Domain.Identity;

/// <summary>
/// The private authentication identity behind a player. Holds nothing about in-game identity
/// (display name, tag) - that lives on <see cref="Players.PlayerProfile"/>. May hold a private,
/// normalized email address, never exposed through a public player-facing API. The password hash
/// is an opaque string produced by an <c>IPasswordHasher</c> port, never interpreted by the domain
/// itself.
/// </summary>
public sealed class Account
{
    public AccountId Id { get; private set; }
    public Username Username { get; private set; }
    public Email? Email { get; private set; }
    public AccountStatus Status { get; private set; }
    public string PasswordHash { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private Account(AccountId id, Username username, Email? email, AccountStatus status, string passwordHash, DateTimeOffset createdAt)
    {
        Id = id;
        Username = username;
        Email = email;
        Status = status;
        PasswordHash = passwordHash;
        CreatedAt = createdAt;
    }

    public static Account Register(Username username, string passwordHash, DateTimeOffset now, Email? email = null)
        => new(AccountId.New(), username, email, AccountStatus.Active, passwordHash, now);

    public static Account Rehydrate(AccountId id, Username username, Email? email, AccountStatus status, string passwordHash, DateTimeOffset createdAt)
        => new(id, username, email, status, passwordHash, createdAt);

    public void ChangePasswordHash(string newPasswordHash) => PasswordHash = newPasswordHash;
}
