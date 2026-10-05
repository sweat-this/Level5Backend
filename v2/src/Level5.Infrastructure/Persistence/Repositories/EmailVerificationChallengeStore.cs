using Level5.Application.Abstractions;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.Persistence.Repositories;

public sealed class EmailVerificationChallengeStore(Level5V2DbContext db) : IEmailVerificationChallengeStore
{
    public async Task<EmailVerificationChallenge?> FindByAccountIdAsync(
        AccountId accountId,
        CancellationToken cancellationToken)
    {
        var row = await db.EmailVerificationChallenges
            .SingleOrDefaultAsync(c => c.AccountId == accountId.Value, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task<EmailVerificationChallenge?> FindByTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken)
    {
        var row = await db.EmailVerificationChallenges
            .SingleOrDefaultAsync(c => c.TokenHash == tokenHash, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task AddAsync(EmailVerificationChallenge challenge, CancellationToken cancellationToken)
        => await db.EmailVerificationChallenges.AddAsync(ToRow(challenge), cancellationToken);

    public async Task StageUpdateAsync(EmailVerificationChallenge challenge, CancellationToken cancellationToken)
    {
        var row = await db.EmailVerificationChallenges
            .SingleAsync(c => c.Id == challenge.Id.Value, cancellationToken);
        row.TargetEmail = challenge.TargetEmail.Value;
        row.TargetEmailCanonical = challenge.TargetEmail.Canonical;
        row.TokenHash = challenge.TokenHash;
        row.IssuedAt = challenge.IssuedAt;
        row.ExpiresAt = challenge.ExpiresAt;
        row.ConsumedAt = challenge.ConsumedAt;
        row.Revision = challenge.Revision;
    }

    private static EmailVerificationChallengeRow ToRow(EmailVerificationChallenge challenge) => new()
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

    private static EmailVerificationChallenge ToDomain(EmailVerificationChallengeRow row)
        => EmailVerificationChallenge.Rehydrate(
            new EmailVerificationChallengeId(row.Id),
            new AccountId(row.AccountId),
            Email.Create(row.TargetEmail),
            row.TokenHash,
            row.IssuedAt,
            row.ExpiresAt,
            row.ConsumedAt,
            row.Revision);
}
