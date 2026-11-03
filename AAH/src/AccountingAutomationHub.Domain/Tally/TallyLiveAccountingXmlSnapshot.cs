namespace AccountingAutomationHub.Domain.Tally;

/// <summary>
/// Locally generated, continuously refreshed Tally accounting XML preview.
/// It is never sent to Tally automatically. Tally Export remains the explicit
/// send/duplicate-check/live-verification action.
/// </summary>
public sealed class TallyLiveAccountingXmlSnapshot
{
    public string Xml { get; init; } = string.Empty;
    public DateTime FromDate { get; init; }
    public DateTime ToDate { get; init; }
    public int Selected { get; init; }
    public int Ready { get; init; }
    // Read-only offline branch Bank eligibility counts; distinguish original
    // selected statement rows from actual Bank vouchers prepared for export.
    // A matched transfer pair or reject/reversal may intentionally yield fewer
    // vouchers. This is presentation/diagnostic metadata, not import authority.
    public int BankStatementSelected { get; init; }
    public int BankStatementPrepared { get; init; }
    public int MissingLedgers { get; init; }
    public int Unsupported { get; init; }
    public int RepeatedSourceCandidatesSkipped { get; init; }
    public int ExactDuplicatesSkipped { get; init; }
    public int ConflictsBlocked { get; init; }
    public long AuthorityAlterId { get; init; }
    public int NewMastersToCreate { get; init; }
}
