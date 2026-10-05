using System.Security.Cryptography;
using System.Text;
using Level5.Application.Abstractions;
using Level5.Application.Competition;
using Level5.Domain.Competition;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Level5.Domain.Leaderboards;
using Level5.Domain.Results;

namespace Level5.Application.Tests.Fakes;

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}

public sealed class FakeChallengeExpiryPolicy : IChallengeExpiryPolicy
{
    public TimeSpan PendingAcceptanceTimeout { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);
    public int SweepBatchSize { get; set; } = 100;
}

/// <summary>Mirrors the single "score-only" entry of the real Infrastructure catalog (<c>StaticRulesetCatalog</c>) so use-case tests exercise the same resolution/rejection rules without depending on Infrastructure.</summary>
public sealed class FakeRulesetCatalog : IRulesetCatalog
{
    public const int CurrentVersion = 1;
    public const int MinimumCompatibleVersion = 1;

    public RulesetDefinition Resolve(string rulesetId, int? requestedVersion)
    {
        if (rulesetId != "score-only")
        {
            throw new UnknownRulesetException($"Ruleset '{rulesetId}' is not recognized.");
        }

        var version = requestedVersion ?? CurrentVersion;
        if (version < MinimumCompatibleVersion || version > CurrentVersion)
        {
            throw new RulesetVersionUnsupportedException(
                $"Ruleset '{rulesetId}' version {version} is not supported (supported range: {MinimumCompatibleVersion}-{CurrentVersion}).");
        }

        return new RulesetDefinition(
            rulesetId, version, MinimumCompatibleVersion, "mode-score-only",
            InformationPolicy.SealedAttempt, AlternatesFirstAttempt: false,
            [new ComparisonKey(ResultMetric.Score, MetricDirection.HigherWins)]);
    }
}

/// <summary>Mirrors the real Infrastructure catalog (<c>StaticLeaderboardPolicyCatalog</c>)'s mode-1 entry so use-case tests exercise the same required-metric rule without depending on Infrastructure.</summary>
public sealed class FakeLeaderboardPolicyCatalog : ILeaderboardPolicyCatalog
{
    public LeaderboardPolicy? TryResolve(int modeId) =>
        modeId == 1 ? new LeaderboardPolicy(1, MatchResultMetric.TotalPoints, RankingDirection.HigherWins) : null;
}

/// <summary>Not a real hash - deterministic and reversible so tests can assert on it directly.</summary>
public sealed class FakePasswordHasher : IPasswordHasher
{
    public string Hash(string password) => $"HASHED:{password}";

    public PasswordVerificationResult Verify(string passwordHash, string suppliedPassword)
        => passwordHash == Hash(suppliedPassword) ? PasswordVerificationResult.Success : PasswordVerificationResult.Failed;
}

public sealed class FakeTokenIssuer : ITokenIssuer
{
    public AccessToken IssueAccessToken(AccountId accountId)
        => new($"token-for-{accountId}", DateTimeOffset.UtcNow.AddMinutes(15));
}

public sealed class NoOpUnitOfWork : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A real SHA-256-based hash (not faked) so replay/lookup tests exercise the same derivation logic production uses; just backed by a predictable RNG source is not needed since raw values are never asserted against a fixed value.</summary>
public sealed class FakeRefreshTokenGenerator : IRefreshTokenGenerator
{
    public GeneratedRefreshToken Generate()
    {
        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        return new GeneratedRefreshToken(raw, Hash(raw));
    }

    public string Hash(string rawValue)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawValue)));
}

public sealed class FakeAuthSessionPolicy : IAuthSessionPolicy
{
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);
}

public sealed class FakeEmailVerificationPolicy : IEmailVerificationPolicy
{
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan ResendCooldown { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed class FakeEmailVerificationTokenGenerator : IEmailVerificationTokenGenerator
{
    private int _sequence;

    public GeneratedEmailVerificationToken Generate()
    {
        var raw = $"email-token-{++_sequence}";
        return new GeneratedEmailVerificationToken(raw, Hash(raw));
    }

    public string Hash(string rawValue)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawValue)));
}

public sealed class FakeEmailVerificationDelivery : IEmailVerificationDelivery
{
    public List<(Email Destination, string RawToken, DateTimeOffset ExpiresAt)> Deliveries { get; } = [];
    public bool ThrowOnDelivery { get; set; }
    public Action? OnDelivery { get; set; }

    public Task DeliverAsync(
        Email destination,
        string rawToken,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        OnDelivery?.Invoke();
        if (ThrowOnDelivery)
        {
            throw new Level5.Application.Identity.EmailVerificationDeliveryUnavailableException();
        }

        Deliveries.Add((destination, rawToken, expiresAt));
        return Task.CompletedTask;
    }
}

public sealed class FakePasswordRecoveryPolicy : IPasswordRecoveryPolicy
{
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan RequestCooldown { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed class FakePasswordResetTokenGenerator : IPasswordResetTokenGenerator
{
    private int _sequence;
    public GeneratedPasswordResetToken Generate()
    {
        var raw = $"reset-token-{++_sequence}";
        return new(raw, Hash(raw));
    }
    public string Hash(string rawValue) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawValue)));
}

public sealed class FakePasswordRecoveryDelivery : IPasswordRecoveryDelivery
{
    public PasswordRecoveryDeliveryOutcome Outcome { get; set; } = PasswordRecoveryDeliveryOutcome.Delivered;
    public List<(Email Destination, string RawToken)> Deliveries { get; } = [];
    public Task<PasswordRecoveryDeliveryOutcome> DeliverAsync(Email destination, string rawToken, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        Deliveries.Add((destination, rawToken));
        return Task.FromResult(Outcome);
    }
}

public sealed class InMemoryPasswordResetChallengeStore : IPasswordResetChallengeStore
{
    private PasswordResetChallenge? _challenge;
    public Task<PasswordResetChallenge?> FindByAccountIdAsync(AccountId accountId, CancellationToken cancellationToken)
        => Task.FromResult(_challenge?.AccountId == accountId ? Clone(_challenge) : null);
    public Task<PasswordResetChallenge?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken)
        => Task.FromResult(_challenge?.TokenHash == tokenHash ? Clone(_challenge) : null);
    public Task AddAsync(PasswordResetChallenge challenge, CancellationToken cancellationToken)
    {
        _challenge = Clone(challenge); return Task.CompletedTask;
    }
    public Task StageUpdateAsync(PasswordResetChallenge challenge, CancellationToken cancellationToken)
    {
        _challenge = Clone(challenge); return Task.CompletedTask;
    }
    private static PasswordResetChallenge Clone(PasswordResetChallenge source) => PasswordResetChallenge.Rehydrate(
        source.Id, source.AccountId, source.TargetEmail, source.TokenHash, source.IssuedAt,
        source.ExpiresAt, source.ConsumedAt, source.Revision);
}

public sealed class TrackingUnitOfWork : IUnitOfWork
{
    public bool Saved { get; private set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        Saved = true;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryEmailVerificationChallengeStore : IEmailVerificationChallengeStore
{
    private EmailVerificationChallenge? _challenge;

    public Task<EmailVerificationChallenge?> FindByAccountIdAsync(AccountId accountId, CancellationToken cancellationToken)
        => Task.FromResult(_challenge?.AccountId == accountId ? Clone(_challenge) : null);

    public Task<EmailVerificationChallenge?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken)
        => Task.FromResult(_challenge?.TokenHash == tokenHash ? Clone(_challenge) : null);

    public Task AddAsync(EmailVerificationChallenge challenge, CancellationToken cancellationToken)
    {
        _challenge = Clone(challenge);
        return Task.CompletedTask;
    }

    public Task StageUpdateAsync(EmailVerificationChallenge challenge, CancellationToken cancellationToken)
    {
        _challenge = Clone(challenge);
        return Task.CompletedTask;
    }

    private static EmailVerificationChallenge Clone(EmailVerificationChallenge source)
        => EmailVerificationChallenge.Rehydrate(
            source.Id,
            source.AccountId,
            source.TargetEmail,
            source.TokenHash,
            source.IssuedAt,
            source.ExpiresAt,
            source.ConsumedAt,
            source.Revision);
}

/// <summary>Accepts anything non-empty - the real policy is covered by its own Infrastructure tests; use-case tests only need to know the port is invoked in the right place.</summary>
public sealed class FakePasswordPolicy : IPasswordPolicy
{
    public void Validate(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new Level5.Application.Identity.PasswordPolicyViolationException("Password cannot be empty.");
        }
    }
}

/// <summary>Mirrors InMemoryVersusSeriesStore: clones on every read and tracks the stored revision separately from any caller's mutated instance, so mutating a loaded session can't secretly move the "stored" revision out from under TrySaveAsync's own check.</summary>
public sealed class InMemoryAuthSessionStore : IAuthSessionStore
{
    private readonly Dictionary<Guid, AuthSession> _rows = [];
    private readonly Dictionary<Guid, long> _storedRevisions = [];

    public Task<AuthSession?> FindByIdAsync(AuthSessionId id, CancellationToken cancellationToken)
    {
        var stored = _rows.GetValueOrDefault(id.Value);
        return Task.FromResult(stored is null ? null : Clone(stored));
    }

    public Task<AuthSession?> FindByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken)
    {
        var stored = _rows.Values.SingleOrDefault(s => s.RefreshTokenHash == refreshTokenHash);
        return Task.FromResult(stored is null ? null : Clone(stored));
    }

    public Task AddAsync(AuthSession session, CancellationToken cancellationToken)
    {
        _rows[session.Id.Value] = session;
        _storedRevisions[session.Id.Value] = session.Revision;
        return Task.CompletedTask;
    }

    public Task<bool> TrySaveAsync(AuthSession session, long expectedRevision, CancellationToken cancellationToken)
    {
        if (!_storedRevisions.TryGetValue(session.Id.Value, out var storedRevision) || storedRevision != expectedRevision)
        {
            return Task.FromResult(false);
        }

        _rows[session.Id.Value] = session;
        _storedRevisions[session.Id.Value] = session.Revision;
        return Task.FromResult(true);
    }

    public Task<bool> TryRotateForActiveGenerationAsync(AuthSession session, long expectedRevision, CancellationToken cancellationToken)
        => TrySaveAsync(session, expectedRevision, cancellationToken);

    private static AuthSession Clone(AuthSession source) => AuthSession.Rehydrate(
        source.Id, source.AccountId, source.RefreshTokenHash, source.CreatedAt, source.ExpiresAt, source.RevokedAt, source.Revision, source.SessionGeneration);
}

/// <summary>Simulates a genuine infrastructure failure (e.g. the database is unreachable) on every lookup, to prove RefreshSessionUseCase/LogoutUseCase let it propagate rather than mistranslating it into an auth failure.</summary>
public sealed class ThrowingAuthSessionStore : IAuthSessionStore
{
    public Task<AuthSession?> FindByIdAsync(AuthSessionId id, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Simulated infrastructure failure.");

    public Task<AuthSession?> FindByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Simulated infrastructure failure.");

    public Task AddAsync(AuthSession session, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Simulated infrastructure failure.");

    public Task<bool> TrySaveAsync(AuthSession session, long expectedRevision, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Simulated infrastructure failure.");

    public Task<bool> TryRotateForActiveGenerationAsync(AuthSession session, long expectedRevision, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Simulated infrastructure failure.");
}

/// <summary>Delegates reads to a real store but fails the write - simulates the database going down between loading a session and persisting its rotation/revocation.</summary>
public sealed class ThrowingOnSaveAuthSessionStore(IAuthSessionStore inner) : IAuthSessionStore
{
    public Task<AuthSession?> FindByIdAsync(AuthSessionId id, CancellationToken cancellationToken)
        => inner.FindByIdAsync(id, cancellationToken);

    public Task<AuthSession?> FindByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken)
        => inner.FindByRefreshTokenHashAsync(refreshTokenHash, cancellationToken);

    public Task AddAsync(AuthSession session, CancellationToken cancellationToken)
        => inner.AddAsync(session, cancellationToken);

    public Task<bool> TrySaveAsync(AuthSession session, long expectedRevision, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Simulated infrastructure failure.");

    public Task<bool> TryRotateForActiveGenerationAsync(AuthSession session, long expectedRevision, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Simulated infrastructure failure.");
}

public sealed class InMemoryAccountStore : IAccountStore
{
    private readonly Dictionary<Guid, Account> _accounts = [];

    public Task<Account?> FindByIdAsync(AccountId id, CancellationToken cancellationToken)
        => Task.FromResult(_accounts.GetValueOrDefault(id.Value));

    public Task<Account?> FindByUsernameAsync(Username username, CancellationToken cancellationToken)
        => Task.FromResult(_accounts.Values.SingleOrDefault(a => a.Username.Canonical == username.Canonical));

    public Task<Account?> FindByVerifiedEmailAsync(Email email, CancellationToken cancellationToken)
        => Task.FromResult(_accounts.Values.SingleOrDefault(a => a.EmailVerifiedAt is not null && a.Email?.Canonical == email.Canonical));

    public Task<bool> UsernameExistsAsync(Username username, CancellationToken cancellationToken)
        => Task.FromResult(_accounts.Values.Any(a => a.Username.Canonical == username.Canonical));

    public Task AddAsync(Account account, CancellationToken cancellationToken)
    {
        _accounts[account.Id.Value] = account;
        return Task.CompletedTask;
    }

    public Task StageCredentialUpdateAsync(Account account, long expectedSessionGeneration, CancellationToken cancellationToken)
    {
        _accounts[account.Id.Value] = account;
        return Task.CompletedTask;
    }

    public Task StageEmailUpdateAsync(Account account, CancellationToken cancellationToken)
    {
        _accounts[account.Id.Value] = account;
        return Task.CompletedTask;
    }
}
