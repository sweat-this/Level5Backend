using Level5.Application.Abstractions;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.Persistence.Repositories;

public sealed class PasswordResetChallengeStore(Level5V2DbContext db) : IPasswordResetChallengeStore
{
    public async Task<PasswordResetChallenge?> FindByAccountIdAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        var row = await db.PasswordResetChallenges.SingleOrDefaultAsync(c => c.AccountId == accountId.Value, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task<PasswordResetChallenge?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken)
    {
        var row = await db.PasswordResetChallenges.SingleOrDefaultAsync(c => c.TokenHash == tokenHash, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public Task AddAsync(PasswordResetChallenge challenge, CancellationToken cancellationToken)
        => db.PasswordResetChallenges.AddAsync(ToRow(challenge), cancellationToken).AsTask();

    public async Task StageUpdateAsync(PasswordResetChallenge challenge, CancellationToken cancellationToken)
    {
        var row = await db.PasswordResetChallenges.SingleAsync(c => c.Id == challenge.Id.Value, cancellationToken);
        row.TargetEmail = challenge.TargetEmail.Value;
        row.TargetEmailCanonical = challenge.TargetEmail.Canonical;
        row.TokenHash = challenge.TokenHash;
        row.IssuedAt = challenge.IssuedAt;
        row.ExpiresAt = challenge.ExpiresAt;
        row.ConsumedAt = challenge.ConsumedAt;
        row.Revision = challenge.Revision;
    }

    private static PasswordResetChallengeRow ToRow(PasswordResetChallenge challenge) => new()
    {
        Id = challenge.Id.Value,
        AccountId = challenge.AccountId.Value,
        TargetEmail = challenge.TargetEmail.Value,
        TargetEmailCanonical = challenge.TargetEmail.Canonical,
        TokenHash = challenge.TokenHash,
        IssuedAt = challenge.IssuedAt,
        ExpiresAt = challenge.ExpiresAt,
        ConsumedAt = challenge.ConsumedAt,
        Revision = challenge.Revision
    };

    private static PasswordResetChallenge ToDomain(PasswordResetChallengeRow row)
        => PasswordResetChallenge.Rehydrate(
            new PasswordResetChallengeId(row.Id),
            new AccountId(row.AccountId),
            Email.Create(row.TargetEmail),
            row.TokenHash,
            row.IssuedAt,
            row.ExpiresAt,
            row.ConsumedAt,
            row.Revision);
}
