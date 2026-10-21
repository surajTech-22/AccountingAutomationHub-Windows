namespace AccountingAutomationHub.Domain.Banking;

public sealed class CashEntrySummary
{
    public int TotalEntries { get; set; }
    public int ReadyEntries { get; set; }
    public int AttentionEntries { get; set; }
    public int PaymentEntries { get; set; }
    public int ReceiptEntries { get; set; }
    public int JournalEntries { get; set; }
    public decimal TotalPayments { get; set; }
    public decimal TotalReceipts { get; set; }
}
