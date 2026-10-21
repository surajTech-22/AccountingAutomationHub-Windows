namespace AccountingAutomationHub.Domain.Masters;

public sealed class StockItemRecord
{
    public long PersistentId { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string StockGroup { get; set; } = "Primary";
    public string StockCategory { get; set; } = string.Empty;
    public string BaseUnit { get; set; } = string.Empty;
    public string AdditionalUnit { get; set; } = string.Empty;
    // Fix117: the Stock Item, not the imported transaction, owns inventory identity and unit policy.
    public string PartNumberSku { get; set; } = string.Empty;
    public string DefaultPurchaseUnit { get; set; } = string.Empty;
    public string DefaultSalesUnit { get; set; } = string.Empty;
    public string RateUnit { get; set; } = string.Empty;
    public string DefaultGodown { get; set; } = string.Empty;
    public string DefaultPurchaseLedger { get; set; } = string.Empty;
    public string DefaultSalesLedger { get; set; } = string.Empty;
    public decimal ReorderPoint { get; set; }
    public decimal MinimumStock { get; set; }
    public decimal MaximumStock { get; set; }
    public string TallyMasterId { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonIgnore]
    public string AuthoritySource => long.TryParse(TallyMasterId, out var masterId) && masterId > 0
        ? "Tally" : "Manual / Local";
    public string TallyGuid { get; set; } = string.Empty;
    public long TallyAlterId { get; set; }
    public string HsnSac { get; set; } = string.Empty;
    public decimal? GstRate { get; set; }
    public string GstTypeOfSupply { get; set; } = string.Empty;
    public bool BatchWise { get; set; }
    public bool Perishable { get; set; }
    public bool HasManufacturingDate { get; set; }
    public string CostingMethod { get; set; } = string.Empty;
    public string ValuationMethod { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public bool IsArchived { get; set; }
    public bool IsUserEdited { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class StockSimpleMasterRecord
{
    public long PersistentId { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Parent { get; set; } = "Primary";
    public string Company { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public bool IsArchived { get; set; }
    public bool IsUserEdited { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class StockUnitRecord
{
    public long PersistentId { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FormalName { get; set; } = string.Empty;
    public int DecimalPlaces { get; set; }
    public string ReportingUqc { get; set; } = string.Empty;
    public decimal? Conversion { get; set; }
    public string Company { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public bool IsArchived { get; set; }
    public bool IsUserEdited { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}


public sealed class GstHsnSacDirectoryRecord
{
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty; // HSN or SAC
    public string Source { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}

public sealed class StockItemAliasRecord
{
    public long PersistentId { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string ImportedAlias { get; set; } = string.Empty;
    public string StockItemName { get; set; } = string.Empty;
    // Fix123: distinguishes an explicit user Item Rename/Mapping from an
    // automatic SKU/compact-name suggestion. Explicit mappings are authoritative
    // even when an older same-named Stock Master still exists.
    public string Source { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}

public sealed class StockUnitMappingRecord
{
    public long PersistentId { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string StockItemName { get; set; } = string.Empty;
    public string ImportedUnit { get; set; } = string.Empty;
    public string FinalUnit { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}



// Fix117: authoritative units permitted for one concrete Stock Item. FactorToBase means
// 1 UnitCode = FactorToBase × StockItem.BaseUnit. Physical conversions can be derived
// by AAH; package conversions (BOX/BAG/CTN etc.) are explicitly item-specific.
public sealed class StockItemUnitProfileRecord
{
    public long PersistentId { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public long StockItemId { get; set; }
    public string StockItemName { get; set; } = string.Empty;
    public string UnitCode { get; set; } = string.Empty;
    public decimal FactorToBase { get; set; } = 1m;
    public bool IsAllowed { get; set; } = true;
    public bool IsTallyAlternateUnit { get; set; }
    public bool IsPackageConversion { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}

public sealed class QuantityConversionRule
{
    public long PersistentId { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string StockItemName { get; set; } = string.Empty;
    public string ImportedUnit { get; set; } = string.Empty;
    public string FinalUnit { get; set; } = string.Empty;
    public decimal Factor { get; set; } = 1m;
    public bool IsUniversal { get; set; }
    public bool RememberForClient { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
