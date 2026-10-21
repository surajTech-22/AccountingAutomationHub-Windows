namespace AccountingAutomationHub.Domain.Banking;

public sealed class BankTransactionDuplicateCleanupResult
{
    public int DuplicateGroupsFound { get; set; }
    public int TransactionsRemoved { get; set; }
    public int ProtectedGroupsSkipped { get; set; }
    public int ConflictingGroupsSkipped { get; set; }
    public int EmptyStatementsRemoved { get; set; }
}
