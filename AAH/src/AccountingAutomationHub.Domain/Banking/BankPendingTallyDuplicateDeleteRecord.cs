namespace AccountingAutomationHub.Domain.Banking;

public sealed class BankPendingTallyDuplicateDeleteRecord
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public long RemovedTransactionId { get; set; }
    public long RetainedTransactionId { get; set; }
    public string PhysicalIdentityKey { get; set; } = string.Empty;
    public DateTime TransactionDate { get; set; }
    public long BankAccountId { get; set; }
    public string Mode { get; set; } = string.Empty;
    public string ReferenceNumber { get; set; } = string.Empty;
    public string Narration { get; set; } = string.Empty;
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal? RunningBalance { get; set; }
    public string RemovedSourceKey { get; set; } = string.Empty;
    public string RetainedSourceKey { get; set; } = string.Empty;
    public string KnownTallyMasterId { get; set; } = string.Empty;
    public string RetainedTallyMasterId { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public decimal Amount => Debit != 0m ? Math.Abs(Debit) : Math.Abs(Credit);
}
