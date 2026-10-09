using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;
using Xunit;

namespace Level5.Domain.Tests.BloodMoney;

public sealed class BloodCreditsTests
{
    private static readonly PlayerId Player = PlayerId.New();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Theory]
    [InlineData(BloodCreditTransactionKind.Issuance, long.MaxValue)]
    [InlineData(BloodCreditTransactionKind.Spend, long.MaxValue)]
    [InlineData(BloodCreditTransactionKind.Correction, long.MaxValue)]
    [InlineData(BloodCreditTransactionKind.Correction, -long.MaxValue)]
    public void Factories_produce_immutable_balanced_owned_postings(BloodCreditTransactionKind kind, long amount)
    {
        var transaction = Create(kind, amount);
        Assert.Equal(2, transaction.Postings.Count);
        Assert.Equal(0, checked(transaction.Postings[0].Amount + transaction.Postings[1].Amount));
        Assert.Equal(Player, transaction.Postings[0].PlayerId);
        Assert.Null(transaction.Postings[1].PlayerId);
        Assert.Equal(BloodCreditPostingOwner.Treasury, transaction.Postings[1].PostingOwner);
        Assert.Throws<NotSupportedException>(() => ((IList<BloodCreditPosting>)transaction.Postings).Clear());
        Assert.True(transaction.HasSameIntent(BloodCreditTransaction.Rehydrate(transaction.TransactionId, kind, Player,
            transaction.PlayerDelta, transaction.ReferenceCode, Now, transaction.Postings)));
    }

    [Theory]
    [InlineData(BloodCreditTransactionKind.Issuance, 0)]
    [InlineData(BloodCreditTransactionKind.Issuance, -1)]
    [InlineData(BloodCreditTransactionKind.Spend, 0)]
    [InlineData(BloodCreditTransactionKind.Spend, -1)]
    [InlineData(BloodCreditTransactionKind.Correction, 0)]
    [InlineData(BloodCreditTransactionKind.Correction, long.MinValue)]
    public void Invalid_amounts_are_rejected(BloodCreditTransactionKind kind, long amount)
        => Assert.Throws<InvalidBloodCreditsException>(() => Create(kind, amount));

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    public void Mandatory_reference_is_validated(string reference)
        => Assert.Throws<InvalidBloodCreditsException>(() => BloodCreditTransaction.Correct(
            BloodCreditTransactionId.New(), Player, 1, reference, Now));

    [Fact]
    public void Identity_and_reference_bounds_are_validated()
    {
        Assert.Throws<InvalidBloodCreditsException>(() => BloodCreditTransaction.Issue(default, Player, 1, "ref", Now));
        Assert.Throws<InvalidBloodCreditsException>(() => BloodCreditTransaction.Issue(BloodCreditTransactionId.New(), default, 1, "ref", Now));
        Assert.Throws<InvalidBloodCreditsException>(() => BloodCreditTransaction.Correct(BloodCreditTransactionId.New(), Player, 1, new string('a', 129), Now));
        Assert.Equal(128, BloodCreditTransaction.Correct(BloodCreditTransactionId.New(), Player, 1, new string('a', 128), Now).ReferenceCode.Length);
    }

    [Fact]
    public void Rehydration_rejects_malformed_ledger_evidence()
    {
        var valid = Create(BloodCreditTransactionKind.Issuance, 10);
        var invalidLines = new IReadOnlyList<BloodCreditPosting>[]
        {
            [], [valid.Postings[0]], [valid.Postings[0], valid.Postings[0]],
            [valid.Postings[0], valid.Postings[1] with { Amount = -9 }],
            [valid.Postings[0] with { PlayerId = PlayerId.New() }, valid.Postings[1]],
            [valid.Postings[0], valid.Postings[1] with { PlayerId = Player }],
            [valid.Postings[0], valid.Postings[1], valid.Postings[1]]
        };
        foreach (var lines in invalidLines)
            Assert.Throws<InvalidBloodCreditsException>(() => BloodCreditTransaction.Rehydrate(valid.TransactionId,
                valid.Kind, Player, valid.PlayerDelta, "ref", Now, lines));
        Assert.Throws<InvalidBloodCreditsException>(() => BloodCreditTransaction.Rehydrate(valid.TransactionId,
            (BloodCreditTransactionKind)99, Player, 10, "ref", Now, valid.Postings));
        Assert.Throws<InvalidBloodCreditsException>(() => BloodCreditTransaction.Rehydrate(valid.TransactionId,
            BloodCreditTransactionKind.Correction, Player, long.MinValue, "ref", Now, valid.Postings));
    }

    [Fact]
    public void Accounts_require_issuance_and_preserve_nonnegative_checked_state()
    {
        var account = BloodCreditAccount.FromFirstIssuance(Create(BloodCreditTransactionKind.Issuance, 100));
        Assert.Equal(1, account.Revision);
        var zero = account.Apply(Create(BloodCreditTransactionKind.Spend, 100));
        Assert.Equal(0, zero.AvailableBalance);
        Assert.Equal(2, zero.Revision);
        Assert.Throws<InsufficientCreditsException>(() => zero.Apply(Create(BloodCreditTransactionKind.Spend, 1)));
        Assert.Throws<InvalidBloodCreditsException>(() => BloodCreditAccount.FromFirstIssuance(Create(BloodCreditTransactionKind.Correction, 10)));
        Assert.Throws<InvalidBloodCreditsException>(() => account.Apply(BloodCreditTransaction.Issue(BloodCreditTransactionId.New(), PlayerId.New(), 1, "ref", Now)));
        Assert.Throws<OverflowException>(() => BloodCreditAccount.Rehydrate(Player, long.MaxValue, 0).Apply(Create(BloodCreditTransactionKind.Issuance, 1)));
        Assert.Throws<OverflowException>(() => BloodCreditAccount.Rehydrate(Player, 0, long.MaxValue).Apply(Create(BloodCreditTransactionKind.Issuance, 1)));
        Assert.Equal(100, account.AvailableBalance);
        Assert.Equal(1, account.Revision);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void Account_rehydration_rejects_invalid_projections(long balance, long revision)
        => Assert.Throws<InvalidBloodCreditsException>(() => BloodCreditAccount.Rehydrate(Player, balance, revision));

    private static BloodCreditTransaction Create(BloodCreditTransactionKind kind, long amount) => kind switch
    {
        BloodCreditTransactionKind.Issuance => BloodCreditTransaction.Issue(BloodCreditTransactionId.New(), Player, amount, "ref", Now),
        BloodCreditTransactionKind.Spend => BloodCreditTransaction.Spend(BloodCreditTransactionId.New(), Player, amount, "ref", Now),
        _ => BloodCreditTransaction.Correct(BloodCreditTransactionId.New(), Player, amount, "ref", Now)
    };
}
