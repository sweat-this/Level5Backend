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
    public long SessionGeneration { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private Account(
        AccountId id,
        Username username,
        Email? email,
        DateTimeOffset? emailVerifiedAt,
        AccountStatus status,
        string passwordHash,
        DateTimeOffset createdAt,
        long sessionGeneration)
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
        SessionGeneration = sessionGeneration;
        CreatedAt = createdAt;
    }

    public static Account Register(Username username, string passwordHash, DateTimeOffset now, Email? email = null)
        => new(AccountId.New(), username, email, emailVerifiedAt: null, AccountStatus.Active, passwordHash, now, sessionGeneration: 0);

    public static Account Rehydrate(
        AccountId id,
        Username username,
        Email? email,
        AccountStatus status,
        string passwordHash,
        DateTimeOffset createdAt,
        DateTimeOffset? emailVerifiedAt = null,
        long sessionGeneration = 0)
        => new(id, username, email, emailVerifiedAt, status, passwordHash, createdAt, sessionGeneration);

    /// <summary>Updates only hash representation during a transparent login upgrade.</summary>
    public void MaintainPasswordHash(string newPasswordHash) => PasswordHash = newPasswordHash;

    /// <summary>Changes the user credential and invalidates every older refresh-session generation.</summary>
    public void ChangePassword(string newPasswordHash)
    {
        PasswordHash = newPasswordHash;
        AdvanceSessionGeneration();
    }

    /// <summary>Invalidates every refresh session stamped with the current generation.</summary>
    public void AdvanceSessionGeneration() => SessionGeneration = checked(SessionGeneration + 1);

    /// <summary>
    /// Installs an unverified recovery-email candidate. A verified address is immutable here:
    /// changing it uses the separate verified-email replacement flow.
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

    /// <summary>
    /// Promotes a separately verified replacement address. The existing verified address remains
    /// canonical until this operation runs.
    /// </summary>
    public void PromoteVerifiedEmail(Email replacement, DateTimeOffset verifiedAt)
    {
        if (Email is null || EmailVerifiedAt is null || Email.Equals(replacement))
        {
            throw new EmailChangePromotionInvalidException();
        }

        Email = replacement;
        EmailVerifiedAt = verifiedAt;
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

public sealed class EmailChangePromotionInvalidException : DomainException
{
    public EmailChangePromotionInvalidException()
        : base("The verified email replacement cannot be promoted from the account's current state.")
    {
    }
}
