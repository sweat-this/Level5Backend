using System.Security.Cryptography;
using System.Text;
using Level5.Application.Abstractions;
using Level5.Domain.Identity;
using Level5.Domain.Ids;

namespace Level5.Application.Tests.Fakes;

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
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

    private static AuthSession Clone(AuthSession source) => AuthSession.Rehydrate(
        source.Id, source.AccountId, source.RefreshTokenHash, source.CreatedAt, source.ExpiresAt, source.RevokedAt, source.Revision);
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
}

public sealed class InMemoryAccountStore : IAccountStore
{
    private readonly Dictionary<Guid, Account> _accounts = [];

    public Task<Account?> FindByIdAsync(AccountId id, CancellationToken cancellationToken)
        => Task.FromResult(_accounts.GetValueOrDefault(id.Value));

    public Task<Account?> FindByUsernameAsync(Username username, CancellationToken cancellationToken)
        => Task.FromResult(_accounts.Values.SingleOrDefault(a => a.Username.Canonical == username.Canonical));

    public Task<bool> UsernameExistsAsync(Username username, CancellationToken cancellationToken)
        => Task.FromResult(_accounts.Values.Any(a => a.Username.Canonical == username.Canonical));

    public Task AddAsync(Account account, CancellationToken cancellationToken)
    {
        _accounts[account.Id.Value] = account;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Account account, CancellationToken cancellationToken)
    {
        _accounts[account.Id.Value] = account;
        return Task.CompletedTask;
    }
}
