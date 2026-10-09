using System.Globalization;
using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;

namespace Level5.Application.BloodMoney;

public sealed record ReserveBloodCreditsRequest(BloodCreditTransactionId TransactionId, BloodMoneyChallengeId ChallengeId, PlayerId PlayerId, long Amount);
public sealed record ReleaseBloodCreditsRequest(BloodCreditTransactionId TransactionId, BloodMoneyChallengeId ChallengeId, PlayerId PlayerId, BloodCreditReleaseReason Reason);

/// <summary>Trusted financial staging only. The challenge owner validates eligibility and commits all transitions together.</summary>
public sealed class BloodCreditReservationMutator(IBloodCreditLedgerStore ledger, IBloodCreditReservationStore reservations, IClock clock)
{
    public async Task<BloodCreditMutationResult> StageReserveAsync(ReserveBloodCreditsRequest request, CancellationToken cancellationToken)
    {
        ValidateIdentity(request.ChallengeId, request.PlayerId, request.TransactionId);
        var transaction = BloodCreditTransaction.Reserve(request.TransactionId, request.PlayerId, request.Amount,
            Reference("reserve", request.ChallengeId, request.PlayerId, request.Amount), clock.UtcNow);
        // Account first, operation lookup last: a projection/reservation that includes a competing
        // commit must resolve its idempotency evidence before balance or lifecycle validation.
        var account = await ledger.FindAccountAsync(request.PlayerId, cancellationToken);
        var reservation = await reservations.FindAsync(request.ChallengeId, request.PlayerId, cancellationToken);
        if (await IsReplay(transaction, cancellationToken)) return new(request.TransactionId, true);
        if (reservation is not null) throw new ConflictException("This challenge/player reservation lifecycle already exists.");
        if (account is null) throw new InsufficientCreditsException();
        var updated = account.Apply(transaction);
        var reserved = BloodCreditReservation.Reserve(request.ChallengeId, transaction);
        ledger.StageAccountUpdate(updated, account.Revision);
        ledger.AddTransaction(transaction);
        reservations.Add(reserved);
        return new(request.TransactionId, false);
    }

    public async Task<BloodCreditMutationResult> StageReleaseAsync(ReleaseBloodCreditsRequest request, CancellationToken cancellationToken)
    {
        ValidateIdentity(request.ChallengeId, request.PlayerId, request.TransactionId);
        if (!Enum.IsDefined(request.Reason)) throw new InvalidBloodCreditsException("Invalid release reason.");
        var account = await ledger.FindAccountAsync(request.PlayerId, cancellationToken);
        var reservation = await reservations.FindAsync(request.ChallengeId, request.PlayerId, cancellationToken);
        if (reservation is null)
        {
            if (await ledger.FindTransactionAsync(request.TransactionId, cancellationToken) is not null)
                throw new ConflictException("Blood Credit transaction ID was already used for a different intent.");
            throw new InvalidBloodCreditsException("The reservation does not exist.");
        }
        var transaction = BloodCreditTransaction.Release(request.TransactionId, request.PlayerId, reservation.Amount,
            Reference("release", request.ChallengeId, request.PlayerId, reservation.Amount) + ":" + request.Reason, clock.UtcNow);
        if (await IsReplay(transaction, cancellationToken)) return new(request.TransactionId, true);
        if (reservation.Status != BloodCreditReservationStatus.Reserved)
            throw new ConflictException("This reservation was already released by another operation.");
        if (account is null) throw new InvalidBloodCreditsException("The reservation account is missing.");
        var updated = account.Apply(transaction);
        var released = reservation.Release(transaction, request.Reason);
        ledger.StageAccountUpdate(updated, account.Revision);
        ledger.AddTransaction(transaction);
        reservations.StageUpdate(released, reservation.Revision);
        return new(request.TransactionId, false);
    }

    private async Task<bool> IsReplay(BloodCreditTransaction transaction, CancellationToken cancellationToken)
    {
        var existing = await ledger.FindTransactionAsync(transaction.TransactionId, cancellationToken);
        if (existing is null) return false;
        if (!existing.HasSameIntent(transaction))
            throw new ConflictException("Blood Credit transaction ID was already used for a different intent.");
        return true;
    }

    private static string Reference(string operation, BloodMoneyChallengeId challengeId, PlayerId playerId, long amount)
        => string.Create(CultureInfo.InvariantCulture, $"{operation}:{challengeId.Value:N}:{playerId.Value:N}:{amount}");

    private static void ValidateIdentity(BloodMoneyChallengeId challengeId, PlayerId playerId, BloodCreditTransactionId transactionId)
    {
        if (challengeId.Value == Guid.Empty || playerId.Value == Guid.Empty || transactionId.Value == Guid.Empty)
            throw new InvalidBloodCreditsException("Reservation operation IDs must be nonempty.");
    }
}

public sealed class ReserveBloodCreditsUseCase(BloodCreditReservationMutator mutator, IUnitOfWork unitOfWork)
{
    public async Task<BloodCreditMutationResult> ExecuteAsync(ReserveBloodCreditsRequest request, CancellationToken cancellationToken)
    {
        var result = await mutator.StageReserveAsync(request, cancellationToken);
        if (!result.IsReplay) await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}

public sealed class ReleaseBloodCreditsUseCase(BloodCreditReservationMutator mutator, IUnitOfWork unitOfWork)
{
    public async Task<BloodCreditMutationResult> ExecuteAsync(ReleaseBloodCreditsRequest request, CancellationToken cancellationToken)
    {
        var result = await mutator.StageReleaseAsync(request, cancellationToken);
        if (!result.IsReplay) await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}
