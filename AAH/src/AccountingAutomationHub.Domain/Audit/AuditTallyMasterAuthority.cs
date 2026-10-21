namespace AccountingAutomationHub.Domain.Audit;

/// <summary>
/// Audit-owned snapshot of the explicitly linked Tally Ledger authority.
/// This is stored in audit-hub.db and does not create or change Accounting Masters.
/// </summary>
public sealed class AuditTallyLedgerAuthority
{
    public long AuditClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string CompanyKey { get; set; } = string.Empty;
    public string ExactName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Parent { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    /// <summary>AAH business-sign opening balance: positive = Dr, negative = Cr.</summary>
    public decimal? OpeningBalance { get; set; }

    /// <summary>AAH business-sign Tally books closing balance for the linked Audit company/FY when returned by Tally.</summary>
    public decimal? ClosingBalance { get; set; }
    public string MasterId { get; set; } = string.Empty;
    public string Guid { get; set; } = string.Empty;
    public long AlterId { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// Audit-owned Tally Group parent snapshot used to resolve inherited group membership.
/// </summary>
public sealed class AuditTallyGroupAuthority
{
    public long AuditClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string CompanyKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Parent { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}
