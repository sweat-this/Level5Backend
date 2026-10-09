using Level5.Domain.Ids;

namespace Level5.Domain.BloodMoney;

/// <summary>Available-credit projection. Every transition requires immutable ledger evidence.</summary>
public sealed class BloodCreditAccount
{
    public PlayerId PlayerId { get; }
    public long AvailableBalance { get; }
    public long Revision { get; }

    private BloodCreditAccount(PlayerId playerId, long availableBalance, long revision)
    {
        if (playerId.Value == Guid.Empty || availableBalance < 0 || revision < 0)
            throw new InvalidBloodCreditsException("Invalid credit account identity, balance, or revision.");
        PlayerId = playerId;
        AvailableBalance = availableBalance;
        Revision = revision;
    }

    public static BloodCreditAccount Rehydrate(PlayerId playerId, long availableBalance, long revision)
        => new(playerId, availableBalance, revision);

    public static BloodCreditAccount FromFirstIssuance(BloodCreditTransaction transaction)
    {
        if (transaction.Kind != BloodCreditTransactionKind.Issuance)
            throw new InvalidBloodCreditsException("Only trusted issuance can open a credit account.");
        return new(transaction.SubjectPlayerId, transaction.PlayerDelta, 1);
    }

    public BloodCreditAccount Apply(BloodCreditTransaction transaction)
    {
        if (transaction.SubjectPlayerId != PlayerId)
            throw new InvalidBloodCreditsException("The transaction belongs to a different player.");
        var balance = checked(AvailableBalance + transaction.PlayerDelta);
        if (balance < 0) throw new InsufficientCreditsException();
        return new(PlayerId, balance, checked(Revision + 1));
    }
}
