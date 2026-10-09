using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;

namespace Level5.Application.BloodMoney;

public sealed class GetMyBloodCreditBalanceUseCase(IBloodCreditLedgerStore store)
{
    public async Task<long> ExecuteAsync(PlayerId playerId, CancellationToken cancellationToken)
        => (await store.FindAccountAsync(playerId, cancellationToken))?.AvailableBalance ?? 0;
}

public sealed record IssueBloodCreditsRequest(BloodCreditTransactionId TransactionId, PlayerId PlayerId, long Amount, string ReferenceCode);
public sealed record SpendBloodCreditsRequest(BloodCreditTransactionId TransactionId, PlayerId PlayerId, long Amount, string ReferenceCode);
public sealed record CorrectBloodCreditsRequest(BloodCreditTransactionId TransactionId, PlayerId PlayerId, long Delta, string ReferenceCode);
public sealed record BloodCreditMutationResult(BloodCreditTransactionId TransactionId, bool IsReplay);

/// <summary>Trusted integration seam; ordinary authenticated players cannot invoke issuance through HTTP.</summary>
public sealed class IssueBloodCreditsUseCase(IBloodCreditLedgerStore store, IUnitOfWork unitOfWork, IClock clock)
{
    public Task<BloodCreditMutationResult> ExecuteAsync(IssueBloodCreditsRequest request, CancellationToken cancellationToken)
        => BloodCreditMutation.ExecuteAsync(store, unitOfWork,
            BloodCreditTransaction.Issue(request.TransactionId, request.PlayerId, request.Amount, request.ReferenceCode, clock.UtcNow), cancellationToken);
}

public sealed class SpendBloodCreditsUseCase(IBloodCreditLedgerStore store, IUnitOfWork unitOfWork, IClock clock)
{
    public Task<BloodCreditMutationResult> ExecuteAsync(SpendBloodCreditsRequest request, CancellationToken cancellationToken)
        => BloodCreditMutation.ExecuteAsync(store, unitOfWork,
            BloodCreditTransaction.Spend(request.TransactionId, request.PlayerId, request.Amount, request.ReferenceCode, clock.UtcNow), cancellationToken);
}

public sealed class CorrectBloodCreditsUseCase(IBloodCreditLedgerStore store, IUnitOfWork unitOfWork, IClock clock)
{
    public Task<BloodCreditMutationResult> ExecuteAsync(CorrectBloodCreditsRequest request, CancellationToken cancellationToken)
        => BloodCreditMutation.ExecuteAsync(store, unitOfWork,
            BloodCreditTransaction.Correct(request.TransactionId, request.PlayerId, request.Delta, request.ReferenceCode, clock.UtcNow), cancellationToken);
}

internal static class BloodCreditMutation
{
    internal static async Task<BloodCreditMutationResult> ExecuteAsync(IBloodCreditLedgerStore store, IUnitOfWork unitOfWork,
        BloodCreditTransaction transaction, CancellationToken cancellationToken)
    {
        // Read the projection first: if it includes a concurrent commit, the subsequent
        // transaction lookup can resolve that commit before validating the balance again.
        var account = await store.FindAccountAsync(transaction.SubjectPlayerId, cancellationToken);
        var existing = await store.FindTransactionAsync(transaction.TransactionId, cancellationToken);
        if (existing is not null)
        {
            if (!existing.HasSameIntent(transaction))
                throw new ConflictException("Blood Credit transaction ID was already used for a different intent.");
            return new(transaction.TransactionId, true);
        }

        if (account is null)
        {
            if (transaction.Kind != BloodCreditTransactionKind.Issuance)
            {
                if (transaction.PlayerDelta < 0) throw new InsufficientCreditsException();
                throw new InvalidBloodCreditsException("A trusted issuance must open the account before correction.");
            }
            store.AddAccount(BloodCreditAccount.FromFirstIssuance(transaction));
        }
        else
        {
            var updated = account.Apply(transaction);
            store.StageAccountUpdate(updated, account.Revision);
        }

        store.AddTransaction(transaction);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new(transaction.TransactionId, false);
    }
}
