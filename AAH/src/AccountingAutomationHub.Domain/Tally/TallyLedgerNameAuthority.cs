namespace AccountingAutomationHub.Domain.Tally;

/// <summary>
/// Exact Ledger identity as returned by live Tally. DisplayName is whitespace-cleaned
/// for AAH matching/UI, while ExactName retains Tally's original characters so voucher
/// XML can address legacy Ledgers containing trailing spaces or CR/LF characters.
/// </summary>
public sealed class TallyLedgerNameAuthority
{
    public string ExactName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Parent { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    /// <summary>AAH business-sign opening balance: positive = Dr, negative = Cr.</summary>
    public decimal? OpeningBalance { get; set; }

    /// <summary>AAH business-sign Tally books closing balance for the active company/FY period when returned by Tally.</summary>
    public decimal? ClosingBalance { get; set; }
    public string MasterId { get; set; } = string.Empty;
    public string Guid { get; set; } = string.Empty;
    public long AlterId { get; set; }
}
