namespace AccountingAutomationHub.Domain.Banking;

public sealed class BankStatementBatchCommitResult
{
    public List<BankStatementAtomicCommitResult> Items { get; } = [];
    public int TransactionsInserted => Items.Sum(item => item.TransactionsInserted);
    public int ReplacedStatements => Items.Sum(item => item.ReplacedStatements);
}
