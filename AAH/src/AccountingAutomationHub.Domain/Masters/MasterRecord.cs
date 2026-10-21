namespace AccountingAutomationHub.Domain.Masters;

public sealed class MasterRecord
{
    public long PersistentId { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Parent { get; set; } = "Primary";
    public string Type { get; set; } = "Ledger";
    public string Company { get; set; } = string.Empty;
    private string _source = string.Empty;
    public string Source
    {
        get
        {
            // Fix79: Ledger/Group source authority is derived from the stable Tally
            // MASTERID, never from a stale persisted Source label. Once a MASTERID
            // exists the Master is Live Tally; without a MASTERID an old Tally label
            // is only local/unbound history until fresh Tally authority binds it.
            if (Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) ||
                Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
            {
                if (TallyMasterId.Trim().Length > 0) return "Live Tally";
                if ((_source ?? string.Empty).Contains("Tally", StringComparison.OrdinalIgnoreCase))
                    return "Local / Unbound";
            }
            return _source ?? string.Empty;
        }
        set => _source = value ?? string.Empty;
    }
    public decimal? OpeningBalance { get; set; }
    // Optional inventory-master details populated by Tally master parsing.
    public string BaseUnit { get; set; } = string.Empty;
    public string StockCategoryName { get; set; } = string.Empty;
    public string AdditionalUnit { get; set; } = string.Empty;
    public string PartNumberSku { get; set; } = string.Empty;
    public string HsnSac { get; set; } = string.Empty;
    public decimal? GstRate { get; set; }
    public string GstTypeOfSupply { get; set; } = string.Empty;
    public bool BatchWise { get; set; }
    public bool Perishable { get; set; }
    public bool HasManufacturingDate { get; set; }
    public string CostingMethod { get; set; } = string.Empty;
    public string ValuationMethod { get; set; } = string.Empty;
    public string FormalName { get; set; } = string.Empty;
    public int DecimalPlaces { get; set; }
    public string ReportingUqc { get; set; } = string.Empty;
    public decimal? Conversion { get; set; }
    // Stock Item alternate-unit ratio. FactorToBase = Denominator / Conversion.
    public decimal? Denominator { get; set; }
    public bool IsArchived { get; set; }
    public bool IsUserEdited { get; set; }

    // Stable live-Tally Master identity. Name is mutable in Tally; MASTERID is not.
    // AAH persists these fields so a Ledger rename updates local references instead of
    // making the renamed Ledger appear deleted/new.
    public string TallyMasterId { get; set; } = string.Empty;
    public string TallyGuid { get; set; } = string.Empty;
    public long TallyAlterId { get; set; }

    // Fix71 business Master identity. Ledger/Group Masters use the Tally MASTERID as
    // soon as Tally authority is known. PersistentId remains an invisible SQLite row
    // key only, because one database can contain several clients whose Tally companies
    // legitimately reuse the same numeric MASTERID (for example Cash can be 31 in more
    // than one company). A newly-created local Master therefore starts with its local
    // row id as a temporary AAH identity and automatically switches to the Tally
    // MASTERID when the normal background Master sync binds that row.
    public string AahMasterId =>
        (Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) ||
         Type.Equals("Group", StringComparison.OrdinalIgnoreCase)) &&
        TallyMasterId.Trim().Length > 0
            ? TallyMasterId.Trim()
            : PersistentId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Transient Master Centre review fields. These are calculated from the active
    // client/FY collection and are deliberately not persisted in SQLite/Tally.
    public int DuplicateCount { get; set; }
    public string DuplicateStatus { get; set; } = "Unique";
    public int UsageCount { get; set; }
    public string NameWithUsage => Type.Equals("Group", StringComparison.OrdinalIgnoreCase)
        ? $"{Name}  •  {UsageCount:N0} child master{(UsageCount == 1 ? string.Empty : "s")}"
        : Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)
            ? $"{Name}  •  {UsageCount:N0} entr{(UsageCount == 1 ? "y" : "ies")}"
            : Name;
    public bool IsManualSource => (Source ?? string.Empty).Trim().StartsWith("Manual", StringComparison.OrdinalIgnoreCase);
    public bool IsLocalEditableSource => MasterAuthority.IsLocalEditableSource(Source);
    public string EditAuthorityStatus => MasterAuthority.EditAuthorityStatus(Source);

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
