namespace AccountingAutomationHub.Domain.Banking;

public sealed class BankStatementImportRequest
{
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public IReadOnlyList<string> FilePaths { get; set; } = [];
    public bool ClearExisting { get; set; }
    public IReadOnlyDictionary<string, string> Passwords { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public int Attempt { get; set; } = 1;
    public bool RecoveryMode { get; set; }
    public bool StrictMetadata { get; set; } = true;
    /// <summary>
    /// Auto Import only: when the same linked source file changes, allow the importer
    /// to pass the identical-period guard and persist only transaction fingerprints
    /// that are not already in the client/FY. Normal manual imports keep the original
    /// statement-period duplicate protection.
    /// </summary>
    public bool AllowSourceDelta { get; set; }
    /// <summary>
    /// Explicit/manual rebuild authority. When true, an unchanged content hash may still
    /// replace the whole linked source file. Normal background Auto Import keeps hash
    /// duplicate protection.
    /// </summary>
    public bool ForceSourceReplacement { get; set; }
    /// <summary>
    /// Auto Import fallback only. Use this when the source file is NOT already present in
    /// durable Auto Import history. Incoming rows are then checked against existing Client/FY
    /// Bank transactions by transaction identity (date/narration/debit/credit plus reference
    /// and independent running balance where available), deliberately excluding Bank name,
    /// Bank account display text and source filename/path.
    /// </summary>
    public bool AutoImportTransactionDuplicateFallback { get; set; }
    public IReadOnlyDictionary<string, string> SourceDisplayNames { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> SourceIdentityPaths { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Trusted client/FY metadata fallbacks used only when the source itself does not
    /// expose a reliable value. Auto Import populates these from an already linked
    /// Bank Account; normal/manual imports remain source-driven.
    /// </summary>
    public IReadOnlyDictionary<string, string> MetadataFallbackBankNames { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> MetadataFallbackAccountNumbers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> MetadataFallbackAccountTypes { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> MetadataFallbackBranchNames { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> MetadataFallbackIfscCodes { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Optional linked-Tally pre-import handoff. The importer first persists this file's
    /// candidate rows as hidden Archived Pre-Import Staging rows, invokes this callback,
    /// then finalizes every still-staged row as Active. A callback failure/Tally-offline
    /// result must never hold or cancel the Bank Statement import.
    /// </summary>
    public Func<CancellationToken, Task<BankStatementPreImportReconciliationResult>>? PreImportReconciliationAsync { get; set; }

    /// <summary>
    /// Optional post-finalization checkpoint. Used by linked Tally to remember the exact
    /// local Bank input revision after staging has become Active/Archived so client open
    /// and Tally refresh can skip unchanged data. Failures are non-blocking for import.
    /// </summary>
    public Func<CancellationToken, Task>? PostImportCheckpointAsync { get; set; }
}

