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
