namespace AccountingAutomationHub.Domain.Tally;

public sealed class TallyBankImportResult
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public int SelectedTransactions { get; set; }
    public int ReadyTransactions { get; set; }
    public int ReadyCreateTransactions { get; set; }
    public int ReadyAlterTransactions { get; set; }
    public int ImportedTransactions { get; set; }
    public int ExactDuplicatesSkipped { get; set; }
    public int ExistingSourceDuplicatesSkipped { get; set; }
    public int RepeatedSourceCandidatesSkipped { get; set; }
    public int ConflictsBlocked { get; set; }
    public int DeletedInTallySkipped { get; set; }
    public int GenerationRebindBlocked { get; set; }
    public List<string> ConflictDetails { get; } = [];
    public int MissingLedgerTransactions { get; set; }
    public int UnsupportedTransactions { get; set; }
    public int PaymentReturnPairTransactionsSkipped { get; set; }
    public int TreatmentCorrectedTransactions { get; set; }
    public int TreatmentRequiredTransactions { get; set; }
    public List<string> TreatmentDetails { get; } = [];
    public int TallyCreated { get; set; }
    public int TallyAltered { get; set; }
    public int TallyDeleted { get; set; }
    public int LocalDuplicateTransactionsRemoved { get; set; }
    public int QueuedTallyDuplicateDeletesResolved { get; set; }
    public int QueuedTallyDuplicateDeletesPending { get; set; }
    public int TallyIgnored { get; set; }
    public int TallyErrors { get; set; }
    public int UnverifiedAfterImport { get; set; }
    public string LiveVerificationError { get; set; } = string.Empty;
    public string LineError { get; set; } = string.Empty;
    public string GroupResponsePath { get; set; } = string.Empty;
    public string DuplicateDeleteResponsePath { get; set; } = string.Empty;
    public string MasterResponsePath { get; set; } = string.Empty;
    public string MetadataResponsePath { get; set; } = string.Empty;
    public string ResponsePath { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}
