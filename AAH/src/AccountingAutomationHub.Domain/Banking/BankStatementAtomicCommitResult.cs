namespace AccountingAutomationHub.Domain.Banking;

public sealed class BankStatementAtomicCommitResult
{
    public required BankAccountRecord Account { get; init; }
    public long StatementId { get; init; }
    public int TransactionsInserted { get; init; }
    public int ReplacedStatements { get; init; }
}
