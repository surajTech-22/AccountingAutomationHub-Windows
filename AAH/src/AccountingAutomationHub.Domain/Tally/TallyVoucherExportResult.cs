namespace AccountingAutomationHub.Domain.Tally;

public sealed class TallyVoucherExportResult
{
    public DateTime FromDate { get; init; }
    public DateTime ToDate { get; init; }
    public int Selected { get; set; }
    public int Ready { get; set; }
    public int ReadyCreates { get; set; }
    public int ReadyAlters { get; set; }
    public int Imported { get; set; }
    public int ExactDuplicatesSkipped { get; set; }
    public int ExistingSourceDuplicatesSkipped { get; set; }
    public int RepeatedSourceCandidatesSkipped { get; set; }
    public int ConflictsBlocked { get; set; }
    public int DeletedInTallySkipped { get; set; }
    public bool AutoRecreateBatchLimitReached { get; set; }
    public int GenerationRebindBlocked { get; set; }
    public List<string> ConflictDetails { get; } = [];
    public int MissingLedgers { get; set; }
    public int Unsupported { get; set; }
    public int PaymentReturnPairTransactionsSkipped { get; set; }
    public int TreatmentCorrected { get; set; }
    public int TreatmentRequired { get; set; }
    public List<string> TreatmentDetails { get; } = [];
    public int TallyCreated { get; set; }
    public int TallyAltered { get; set; }
    public int TallyDeleted { get; set; }
    public int TallyIgnored { get; set; }
    public int TallyErrors { get; set; }
    public int LiveReadErrors { get; set; }
    public List<string> LiveReadErrorDetails { get; } = [];
    public int UnverifiedAfterImport { get; set; }
    public string LineError { get; set; } = string.Empty;
    public string MasterResponsePath { get; set; } = string.Empty;
    public string ResponsePath { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}
