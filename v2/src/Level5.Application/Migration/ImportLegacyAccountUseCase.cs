using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Application.Players;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Level5.Domain.Migration;
using Level5.Domain.Players;

namespace Level5.Application.Migration;

public sealed record ImportLegacyAccountRequest(
    int LegacyUserId,
    string LegacyUsername,
    LegacyCredential Credential,
    string DisplayNameCandidate,
    // V1's users.email is required, so this is normally present. Preserved where valid and
    // non-conflicting; never used to look up or auto-link an existing V2 account (see the
    // username-collision comment below - email carries the same account-takeover risk).
    string? LegacyEmail = null,
    // Explicit operator opt-in (mirrors --accept-legacy-plaintext): without it, an invalid or
    // canonically-conflicting email blocks the row rather than silently importing without it.
    bool OmitInvalidOrConflictingEmail = false);

/// <summary>
/// Either a value already in V2-compatible <c>PasswordHasher&lt;T&gt;</c> format (copied
/// verbatim) or raw legacy plaintext that still needs hashing. The caller (<c>MigrateCommand</c>)
/// decides which one it has via <see cref="LegacyCredentialClassifier"/>; the use case defers the
/// actual <c>IPasswordHasher.Hash</c> call until the moment it's about to create the account - see
/// the comment at that call site for why (avoiding PBKDF2 work on rows a rerun would just skip or
/// block anyway).
/// </summary>
public sealed record LegacyCredential(string Value, bool RequiresHashing)
{
    public static LegacyCredential AlreadyHashed(string hash) => new(hash, RequiresHashing: false);

    public static LegacyCredential Plaintext(string plaintext) => new(plaintext, RequiresHashing: true);
}

public enum ImportLegacyAccountOutcome
{
    Imported,
    AlreadyLinkedConsistent,
    Blocked
}

public sealed record ImportLegacyAccountResult(
    ImportLegacyAccountOutcome Outcome,
    AccountId? AccountId,
    PlayerId? PlayerId,
    string? BlockReason);

/// <summary>
/// Imports one legacy V1 user as a V2 Account + PlayerProfile + LegacyAccountLink, atomically,
/// without creating an AuthSession, refresh token, or access token - unlike
/// <see cref="Identity.RegisterAccountUseCase"/>, which a live registration always goes through, a
/// migration import must never look like an active login. Idempotent: re-running with the same
/// LegacyUserId either confirms the prior import is consistent and reports
/// <see cref="ImportLegacyAccountOutcome.AlreadyLinkedConsistent"/> (no writes), or throws
/// <see cref="LegacyMigrationInconsistentException"/> if the prior state is corrupt, or imports
/// fresh.
/// </summary>
public sealed class ImportLegacyAccountUseCase(
    IAccountStore accountStore,
    IPlayerProfileStore playerProfileStore,
    ILegacyAccountLinkStore legacyAccountLinkStore,
    PlayerTagAllocator playerTagAllocator,
    IUnitOfWork unitOfWork,
    IClock clock,
    IPasswordHasher passwordHasher)
{
    public async Task<ImportLegacyAccountResult> ExecuteAsync(ImportLegacyAccountRequest request, CancellationToken cancellationToken)
    {
        var existingLink = await legacyAccountLinkStore.FindByLegacyUserIdAsync(request.LegacyUserId, cancellationToken);
        if (existingLink is not null)
        {
            return await VerifyExistingLinkAsync(existingLink, cancellationToken);
        }

        Username username;
        try
        {
            username = Username.Create(request.LegacyUsername);
        }
        catch (InvalidUsernameException ex)
        {
            return Blocked($"Legacy username '{request.LegacyUsername}' is not a valid V2 username: {ex.Message}");
        }

        // Collision policy (non-negotiable): a V2 account with this canonical username is always
        // blocked, never auto-linked - we cannot know whether it's an unrelated fresh signup that
        // happens to share a name, or the same human re-registering. Email is never consulted for
        // auto-linking. The message distinguishes an unrelated V2-native account from one that was
        // itself migrated from a *different* legacy user - the latter is a real, expected
        // consequence of V1's case-sensitive username uniqueness colliding with V2's
        // case-insensitive uniqueness (e.g. two distinct V1 users "Patrick"/"patrick"), not a bug,
        // but the two situations need different operator follow-up.
        var existingAccount = await accountStore.FindByUsernameAsync(username, cancellationToken);
        if (existingAccount is not null)
        {
            var collidesWithMigratedAccount = await legacyAccountLinkStore.ExistsForAccountIdAsync(existingAccount.Id, cancellationToken);
            return Blocked(collidesWithMigratedAccount
                ? $"Username '{username.Value}' already exists in V2, migrated there from a different legacy user. Refusing to merge - " +
                  "this is likely a case-variant of the same username under V1's case-sensitive uniqueness (V2 usernames are case-insensitive)."
                : $"Username '{username.Value}' already exists in V2 with no legacy_account_links record for legacy user {request.LegacyUserId}. Refusing to auto-link.");
        }

        string displayName;
        try
        {
            // Validated by PlayerProfile.Create below too, but validated here first so a bad
            // display name is reported as a Blocked outcome, not an unhandled domain exception.
            displayName = ValidateDisplayNameCandidate(request.DisplayNameCandidate);
        }
        catch (InvalidDisplayNameException ex)
        {
            return Blocked(
                $"Legacy username '{request.LegacyUsername}' is not a valid V2 display name: {ex.Message} Never auto-truncated or renamed.");
        }

        var emailResolution = await ResolveEmailAsync(request, cancellationToken);
        if (emailResolution.BlockReason is not null)
        {
            return Blocked(emailResolution.BlockReason);
        }

        var now = clock.UtcNow;
        var email = emailResolution.Email;
        // Hashing is deferred to this exact point - after every check that could still Block the
        // row - rather than done eagerly by the caller. PBKDF2 is deliberately slow, and `migrate`
        // is explicitly safe/expected to be rerun (e.g. after resolving a few audit blockers); if
        // the caller hashed plaintext up front, every already-migrated or newly-blocked row would
        // pay that cost again on every rerun for no benefit, since none of those paths ever reach
        // here. Only a row that is actually about to be imported ever calls the hasher.
        var passwordHash = request.Credential.RequiresHashing ? passwordHasher.Hash(request.Credential.Value) : request.Credential.Value;
        var account = Account.Register(username, passwordHash, now, email);
        await accountStore.AddAsync(account, cancellationToken);

        var tag = await playerTagAllocator.AssignAvailableTagAsync(displayName, cancellationToken);
        var profile = PlayerProfile.Create(account.Id, displayName, tag, now);
        await playerProfileStore.AddAsync(profile, cancellationToken);

        var link = new LegacyAccountLink(request.LegacyUserId, account.Id, profile.Id, request.LegacyUsername, now);
        await legacyAccountLinkStore.AddAsync(link, cancellationToken);

        // Single SaveChangesAsync, exactly like RegisterAccountUseCase - Account, PlayerProfile,
        // and LegacyAccountLink either all commit together or none do. No AuthSession, no
        // refresh/access token: that is this use case's entire divergence from RegisterAccountUseCase.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new ImportLegacyAccountResult(ImportLegacyAccountOutcome.Imported, account.Id, profile.Id, null);
    }

    private async Task<ImportLegacyAccountResult> VerifyExistingLinkAsync(LegacyAccountLink link, CancellationToken cancellationToken)
    {
        var result = await LegacyLinkConsistencyChecker.CheckAsync(link, accountStore, playerProfileStore, cancellationToken);
        if (!result.IsConsistent)
        {
            throw new LegacyMigrationInconsistentException(
                result.Reason + " Refusing to proceed - this indicates manual/partial data tampering and must be investigated by hand.");
        }

        return new ImportLegacyAccountResult(ImportLegacyAccountOutcome.AlreadyLinkedConsistent, link.AccountId, link.PlayerId, null);
    }

    private async Task<EmailResolution> ResolveEmailAsync(ImportLegacyAccountRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.LegacyEmail))
        {
            return new EmailResolution(null, null);
        }

        Email candidateEmail;
        try
        {
            candidateEmail = Email.Create(request.LegacyEmail);
        }
        catch (InvalidEmailException ex)
        {
            return request.OmitInvalidOrConflictingEmail
                ? new EmailResolution(null, null)
                : new EmailResolution(null,
                    $"Legacy email for user {request.LegacyUserId} is not a valid V2 email: {ex.Message} " +
                    "Pass an explicit omit-invalid-email opt-in to import without preserving email.");
        }

        // Same reasoning as the username-collision check above: email is never used to look up or
        // auto-link an existing account - this only decides whether the *new* account may carry
        // the email at all (the DB's own unique index on EmailCanonical is the actual backstop
        // against a race with a concurrent insert).
        if (await accountStore.EmailExistsAsync(candidateEmail, cancellationToken))
        {
            return request.OmitInvalidOrConflictingEmail
                ? new EmailResolution(null, null)
                : new EmailResolution(null,
                    $"Legacy email for user {request.LegacyUserId} already exists in V2 (canonical conflict). " +
                    "Pass an explicit omit-invalid-email opt-in to import without preserving email.");
        }

        return new EmailResolution(candidateEmail, null);
    }

    private sealed record EmailResolution(Email? Email, string? BlockReason);

    private static string ValidateDisplayNameCandidate(string candidate)
    {
        var trimmed = (candidate ?? string.Empty).Trim();
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

    private static ImportLegacyAccountResult Blocked(string reason)
        => new(ImportLegacyAccountOutcome.Blocked, null, null, reason);
}

/// <summary>
/// Thrown when a legacy_account_links row exists but its Account/PlayerProfile are missing or
/// inconsistent - a state that must never be silently repaired or skipped past.
/// </summary>
public sealed class LegacyMigrationInconsistentException(string message) : AppException(message)
{
    public override string Code => "legacy_migration_inconsistent";
}
