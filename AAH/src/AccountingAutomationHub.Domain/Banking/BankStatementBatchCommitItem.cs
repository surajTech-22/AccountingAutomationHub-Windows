namespace AccountingAutomationHub.Domain.Banking;

public sealed class BankStatementBatchCommitItem
{
    public required BankAccountRecord Account { get; init; }
    public required BankStatementRecord Statement { get; init; }
    public required IReadOnlyList<BankTransactionRecord> Transactions { get; init; }
    public required string SourcePath { get; init; }
}
