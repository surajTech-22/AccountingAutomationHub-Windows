namespace AccountingAutomationHub.Domain.Banking;

/// <summary>
/// Non-blocking handoff result used while a Bank Statement is still staged.
/// Tally unavailability is informational: the importer must immediately finalize
/// the staged rows as ordinary Bank Statement rows rather than holding the import.
/// </summary>
public sealed class BankStatementPreImportReconciliationResult
{
    public bool WasAttempted { get; set; }
    public bool TallyConnected { get; set; }
    /// <summary>
    /// True only when the staging revision was durably reconciled/checkpointed against live Tally.
    /// The importer may advance the post-finalization Bank token only in this state.
    /// </summary>
    public bool CheckpointReady { get; set; }
    public int MatchedBeforeImport { get; set; }
    public string Status { get; set; } = string.Empty;
}
