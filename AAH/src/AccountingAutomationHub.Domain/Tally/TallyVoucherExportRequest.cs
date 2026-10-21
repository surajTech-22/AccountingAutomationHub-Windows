namespace AccountingAutomationHub.Domain.Tally;

public sealed class TallyVoucherExportRequest
{
    public IReadOnlyList<TallyVoucherExportRange> BankStatementRanges { get; init; } = [];
    public IReadOnlyList<TallyVoucherExportRange> PurchaseRanges { get; init; } = [];
    public IReadOnlyList<TallyVoucherExportRange> SalesRanges { get; init; } = [];
    public IReadOnlyList<TallyVoucherExportRange> CashEntryRanges { get; init; } = [];
    public IReadOnlyList<TallyVoucherExportRange> JournalRanges { get; init; } = [];

    // R37: when this is non-empty the export is an explicit Restore-to-Tally operation.
    // Only these durable source anchors are allowed to reach CREATE/ALTER classification.
    // Ordinary exports leave this empty and retain the existing range-based behavior.
    public IReadOnlyList<string> RestoreSourceAnchors { get; init; } = [];

    // FIXED28 Auto Sync is non-interactive. Manual export retains the historical
    // recovery confirmation dialog; automatic sync fails closed and keeps work queued.
    public bool AllowInteractiveRecoveryPrompt { get; init; } = true;

    // Recurring Cash vouchers are part of the Cash authority by default. FIXED28
    // allows Auto Sync Selected mode to switch that recurring sub-stream separately.
    public bool IncludeRecurringCash { get; init; } = true;

    // FIXED31 Auto Sync-only Banking option. Manual Tally export leaves this false, so
    // unresolved Bank rows still wait for Final Ledger. When true, an unmapped Auto Import
    // row may first use the dedicated Suspense Ledger; the durable source MASTERID/version
    // then makes a later Final Ledger assignment ALTER that same Voucher instead of CREATE.
    public bool AllowBankSuspenseForUnmapped { get; init; }

    // Only outbound automatic sync may opt in; regular/manual exports leave false.
    // Recreate never means restoring RebindRequired, missing identity or live conflicts.
    public bool AutoRecreateConfirmedTallyDeletions { get; init; }

    // Manual exports retain the full diagnostic pack. High-frequency Auto Sync uses
    // the same write/verification path but skips the expensive per-run diagnostic ZIP;
    // failures still remain in normal Tally status/diagnostic logging.
    public bool GenerateDiagnosticPack { get; init; } = true;

    // Automatic sessions should generate the SAME report as Manual Export after
    // a meaningful import/failure, but not on empty background rechecks.
    // This is diagnostics-only and cannot change CREATE/ALTER/IGNORE decisions.
    public bool GenerateDiagnosticPackOnlyWhenMaterial { get; init; }

    // Compatibility accessors for older callers and persisted diagnostics.
    public TallyVoucherExportRange? BankStatementRange => BankStatementRanges.FirstOrDefault();
    public TallyVoucherExportRange? PurchaseRange => PurchaseRanges.FirstOrDefault();
    public TallyVoucherExportRange? SalesRange => SalesRanges.FirstOrDefault();
    public TallyVoucherExportRange? CashEntryRange => CashEntryRanges.FirstOrDefault();
    public TallyVoucherExportRange? JournalRange => JournalRanges.FirstOrDefault();

    public bool IncludeBankStatement => BankStatementRanges.Count > 0;
    public bool IncludePurchases => PurchaseRanges.Count > 0;
    public bool IncludeSales => SalesRanges.Count > 0;
    public bool IncludeCashEntries => CashEntryRanges.Count > 0;
    public bool IncludeJournals => JournalRanges.Count > 0;
}

public sealed class TallyVoucherExportRange
{
    public DateTime FromDate { get; init; }
    public DateTime ToDate { get; init; }

    public TallyVoucherExportRange Normalize()
    {
        var from = FromDate.Date;
        var to = ToDate.Date;
        if (from > to)
        {
            throw new InvalidOperationException("The Tally export From Date cannot be after the To Date.");
        }
        return new TallyVoucherExportRange
        {
            FromDate = from,
            ToDate = to
        };
    }

    public bool Contains(DateTime date) =>
        date.Date >= FromDate.Date && date.Date <= ToDate.Date;
}
