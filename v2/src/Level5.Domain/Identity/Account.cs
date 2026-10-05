using Level5.Domain.Common;
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
    public DateTimeOffset? EmailVerifiedAt { get; private set; }
    public AccountStatus Status { get; private set; }
    public string PasswordHash { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private Account(
        AccountId id,
        Username username,
        Email? email,
        DateTimeOffset? emailVerifiedAt,
        AccountStatus status,
        string passwordHash,
        DateTimeOffset createdAt)
    {
        if (email is null && emailVerifiedAt is not null)
        {
            throw new ArgumentException("An account without an email cannot have verification state.", nameof(emailVerifiedAt));
        }

        Id = id;
        Username = username;
        Email = email;
        EmailVerifiedAt = emailVerifiedAt;
        Status = status;
        PasswordHash = passwordHash;
        CreatedAt = createdAt;
    }

    public static Account Register(Username username, string passwordHash, DateTimeOffset now, Email? email = null)
        => new(AccountId.New(), username, email, emailVerifiedAt: null, AccountStatus.Active, passwordHash, now);

    public static Account Rehydrate(
        AccountId id,
        Username username,
        Email? email,
        AccountStatus status,
        string passwordHash,
        DateTimeOffset createdAt,
        DateTimeOffset? emailVerifiedAt = null)
        => new(id, username, email, emailVerifiedAt, status, passwordHash, createdAt);

    public void ChangePasswordHash(string newPasswordHash) => PasswordHash = newPasswordHash;

    /// <summary>
    /// Installs an unverified recovery-email candidate. A verified address is immutable here:
    /// changing it is a separate account-recovery operation owned by issue #64.
    /// </summary>
    /// <returns><see langword="false"/> when the same already-verified address was requested.</returns>
    public bool AttachOrReplaceUnverifiedEmail(Email email)
    {
        if (EmailVerifiedAt is not null)
        {
            if (Email is not null && Email.Equals(email))
            {
                return false;
            }

            throw new VerifiedEmailChangeNotAllowedException();
        }

        Email = email;
        EmailVerifiedAt = null;
        return true;
    }

    /// <summary>Marks only the account's currently attached canonical address as verified.</summary>
    public void VerifyEmail(Email targetEmail, DateTimeOffset verifiedAt)
    {
        if (Email is null || !Email.Equals(targetEmail))
        {
            throw new EmailVerificationTargetMismatchException();
        }

        EmailVerifiedAt ??= verifiedAt;
    }
}

public sealed class VerifiedEmailChangeNotAllowedException : DomainException
{
    public VerifiedEmailChangeNotAllowedException()
        : base("Changing a verified email address is not supported by this operation.")
    {
    }
}

public sealed class EmailVerificationTargetMismatchException : DomainException
{
    public EmailVerificationTargetMismatchException()
        : base("The verification target does not match the account's current email.")
    {
    }
}
