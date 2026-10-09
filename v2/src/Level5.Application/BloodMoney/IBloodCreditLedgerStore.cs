using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;

namespace Level5.Application.BloodMoney;

/// <summary>Stages module-owned persistence; the enclosing unit of work performs the only commit.</summary>
public interface IBloodCreditLedgerStore
{
    Task<BloodCreditAccount?> FindAccountAsync(PlayerId playerId, CancellationToken cancellationToken);
    Task<BloodCreditTransaction?> FindTransactionAsync(BloodCreditTransactionId transactionId, CancellationToken cancellationToken);
    void AddAccount(BloodCreditAccount account);
    void StageAccountUpdate(BloodCreditAccount account, long expectedRevision);
    void AddTransaction(BloodCreditTransaction transaction);
}
