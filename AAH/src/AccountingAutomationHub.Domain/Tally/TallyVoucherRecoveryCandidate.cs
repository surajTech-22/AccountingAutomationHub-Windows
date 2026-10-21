namespace AccountingAutomationHub.Domain.Tally;

/// <summary>
/// One AAH-owned Voucher whose previously verified Tally MASTERID is no longer present
/// in the complete current Client/FY Tally authority, or whose explicit recovery is pending.
/// Recovery is always user initiated; this model never grants automatic CREATE permission.
/// </summary>
public sealed class TallyVoucherRecoveryCandidate
{
    public string EntityType { get; set; } = string.Empty;
    public string SourceAnchor { get; set; } = string.Empty;
    public string SourceKey { get; set; } = string.Empty;
    public string PreviousMasterId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string VerifiedVersion { get; set; } = string.Empty;
    public string LastResponse { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
    public DateTime? VoucherDate { get; set; }
    public string VoucherType { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Narration { get; set; } = string.Empty;

    public string Module => EntityType switch
    {
        "Bank Voucher" => "Banking",
        "Purchase Voucher" => "Purchase",
        "Sales Voucher" => "Sales",
        "Cash Voucher" => "Cash",
        "Manual Journal" => "Journal",
        "GST Journal" => "Journal",
        _ => EntityType
    };

    // R38: RebindRequired is still never automatic CREATE permission. It becomes
    // user-recoverable only after one fresh full-FY authority check and explicit user
    // approval in the client Tally Recovery module. ExternalDeleteConflict remains blocked.
    public bool CanReimport =>
        State.Equals("DeletedInTally", StringComparison.OrdinalIgnoreCase) ||
        State.Equals("MissingLiveCandidate", StringComparison.OrdinalIgnoreCase) ||
        // User-reviewed local edits after Tally deletion; never an automatic CREATE.
        State.Equals("ExternalDeleteConflict", StringComparison.OrdinalIgnoreCase) ||
        State.Equals("RebindRequired", StringComparison.OrdinalIgnoreCase) ||
        State.Equals("RestoreRequested", StringComparison.OrdinalIgnoreCase);

    public string StatusText => State switch
    {
        "DeletedInTally" => "Deleted in Tally",
        "MissingLiveCandidate" => "Missing from Tally",
        "ExternalDeleteConflict" => "Missing + edited in AAH (explicit review required)",
        "RebindRequired" => "Tally identity rebuild/rebind required",
        "RestoreRequested" => "Re-import requested",
        _ => State
    };
}
