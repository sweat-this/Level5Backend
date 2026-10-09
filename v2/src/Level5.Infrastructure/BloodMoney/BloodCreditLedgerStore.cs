using Level5.Application.BloodMoney;
using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;
using Level5.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.BloodMoney;

public sealed class BloodCreditLedgerStore(Level5V2DbContext db) : IBloodCreditLedgerStore
{
    public async Task<BloodCreditAccount?> FindAccountAsync(PlayerId playerId, CancellationToken cancellationToken)
    {
        var row = await db.Set<BloodCreditAccountRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.PlayerId == playerId.Value, cancellationToken);
        return row is null ? null : BloodCreditAccount.Rehydrate(playerId, row.AvailableBalance, row.Revision);
    }

    public async Task<BloodCreditTransaction?> FindTransactionAsync(BloodCreditTransactionId transactionId, CancellationToken cancellationToken)
    {
        var row = await db.Set<BloodCreditTransactionRow>().AsNoTracking().Include(row => row.Postings)
            .SingleOrDefaultAsync(row => row.Id == transactionId.Value, cancellationToken);
        return row is null ? null : BloodCreditTransaction.Rehydrate(new(row.Id),
            Enum.Parse<BloodCreditTransactionKind>(row.Kind), new(row.SubjectPlayerId), row.PlayerDelta, row.ReferenceCode, row.CreatedAt,
            row.Postings.Select(line => new BloodCreditPosting(line.LineNumber, Enum.Parse<BloodCreditPostingOwner>(line.PostingOwner),
                line.PlayerId.HasValue ? new PlayerId(line.PlayerId.Value) : null, line.Amount)));
    }

    public void AddAccount(BloodCreditAccount account)
        => db.Set<BloodCreditAccountRow>().Add(ToRow(account));

    public void StageAccountUpdate(BloodCreditAccount account, long expectedRevision)
    {
        if (account.Revision != checked(expectedRevision + 1))
            throw new InvalidBloodCreditsException("An account update must advance exactly one revision.");

        var row = db.Set<BloodCreditAccountRow>().Local.SingleOrDefault(row => row.PlayerId == account.PlayerId.Value);
        if (row is null)
        {
            row = ToRow(account);
            db.Attach(row);
        }
        var entry = db.Entry(row);
        row.AvailableBalance = account.AvailableBalance;
        row.Revision = account.Revision;
        entry.Property(value => value.AvailableBalance).IsModified = true;
        entry.Property(value => value.Revision).IsModified = true;
        entry.Property(value => value.Revision).OriginalValue = expectedRevision;
    }

    public void AddTransaction(BloodCreditTransaction transaction)
        => db.Set<BloodCreditTransactionRow>().Add(new()
        {
            Id = transaction.TransactionId.Value,
            Kind = transaction.Kind.ToString(),
            SubjectPlayerId = transaction.SubjectPlayerId.Value,
            PlayerDelta = transaction.PlayerDelta,
            ReferenceCode = transaction.ReferenceCode,
            CreatedAt = transaction.CreatedAt,
            Postings = transaction.Postings.Select(line => new BloodCreditPostingRow
            {
                TransactionId = transaction.TransactionId.Value,
                LineNumber = line.LineNumber,
                PostingOwner = line.PostingOwner.ToString(),
                PlayerId = line.PlayerId?.Value,
                Amount = line.Amount
            }).ToList()
        });

    private static BloodCreditAccountRow ToRow(BloodCreditAccount account)
        => new() { PlayerId = account.PlayerId.Value, AvailableBalance = account.AvailableBalance, Revision = account.Revision };
}
