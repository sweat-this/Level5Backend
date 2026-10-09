namespace Level5.Infrastructure.BloodMoney;

public sealed class BloodCreditAccountRow
{
    public Guid PlayerId { get; set; }
    public long AvailableBalance { get; set; }
    public long Revision { get; set; }
}

public sealed class BloodCreditTransactionRow
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = null!;
    public Guid SubjectPlayerId { get; set; }
    public long PlayerDelta { get; set; }
    public string ReferenceCode { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public List<BloodCreditPostingRow> Postings { get; set; } = [];
}

public sealed class BloodCreditPostingRow
{
    public Guid TransactionId { get; set; }
    public int LineNumber { get; set; }
    public string PostingOwner { get; set; } = null!;
    public Guid? PlayerId { get; set; }
    public long Amount { get; set; }
}
