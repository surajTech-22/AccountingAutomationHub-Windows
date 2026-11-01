namespace AccountingAutomationHub.Domain.Tally;

/// <summary>
/// Locally generated, continuously refreshed Tally accounting XML preview.
/// It is never sent to Tally automatically. Tally Export remains the explicit
/// send/duplicate-check/live-verification action.
/// </summary>
public sealed class TallyLiveAccountingXmlSnapshot
{
    public string Xml { get; init; } = string.Empty;
    // Original AAH durable source anchors, parallel to the voucher XML list.
    // Consolidated must not replace these with hashes of mutable XML content:
    // changed invoices/Journals must retain their source authority on re-export.
    public IReadOnlyList<string> SourceAnchors { get; init; } = [];
    public IReadOnlyList<string> SourceEntityTypes { get; init; } = [];
    public DateTime FromDate { get; init; }
    public DateTime ToDate { get; init; }
    public int Selected { get; init; }
    public int Ready { get; init; }
    public int MissingLedgers { get; init; }
    public int Unsupported { get; init; }
    public int RepeatedSourceCandidatesSkipped { get; init; }
    public int ExactDuplicatesSkipped { get; init; }
    public int ConflictsBlocked { get; init; }
    public long AuthorityAlterId { get; init; }
    public int NewMastersToCreate { get; init; }
}
