using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AccountingAutomationHub.Domain.Finance;

public sealed class FinanceAuditEvent
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityKey { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string OldValue { get; set; } = string.Empty;
    public string NewValue { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime ChangedUtc { get; set; }
}

public sealed class AccountingPeriodLock
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Module { get; set; } = "All";
    public DateTime LockDate { get; set; }
    public string Reason { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedUtc { get; set; }
}

public sealed class MonthEndCloseCheck
{
    public string Area { get; set; } = string.Empty;
    public string Check { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int Count { get; set; }
    public string Detail { get; set; } = string.Empty;
    public bool IsReady => Status.Equals("Ready", StringComparison.OrdinalIgnoreCase);
}

public sealed class FinanceCashFlowRow
{
    public string Period { get; set; } = string.Empty;
    public DateTime PeriodStart { get; set; }
    public decimal ActualInflow { get; set; }
    public decimal ActualOutflow { get; set; }
    public decimal PlannedInflow { get; set; }
    public decimal PlannedOutflow { get; set; }
    public decimal Net => ActualInflow - ActualOutflow + PlannedInflow - PlannedOutflow;
    public decimal Closing { get; set; }
}

public sealed class FinanceAgingRow
{
    public string Side { get; set; } = string.Empty;
    public string Party { get; set; } = string.Empty;
    public string DocumentNumber { get; set; } = string.Empty;
    public DateTime? DocumentDate { get; set; }
    public DateTime? DueDate { get; set; }
    public decimal OriginalAmount { get; set; }
    public decimal SettledAmount { get; set; }
    public decimal OutstandingAmount => Math.Max(0m, OriginalAmount - SettledAmount);
    public int AgeDays { get; set; }
    public string Bucket { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

public sealed class FinanceSettlement
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public string Party { get; set; } = string.Empty;
    public string DocumentNumber { get; set; } = string.Empty;
    public DateTime? DocumentDate { get; set; }
    public decimal SettledAmount { get; set; }
    public DateTime SettlementDate { get; set; }
    public string Reference { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}

public sealed class FixedAssetRecord : INotifyPropertyChanged
{
    private long _id;
    private long _clientId;
    private string _financialYear = string.Empty;
    private string _assetCode = string.Empty;
    private string _assetName = string.Empty;
    private string _assetGroup = "Fixed Assets";
    private DateTime? _purchaseDate;
    private DateTime? _putToUseDate;
    private decimal _purchaseCost;
    private decimal _residualValue;
    private string _depreciationMethod = "SLM";
    private decimal _depreciationRate;
    private decimal _openingAccumulatedDepreciation;
    private DateTime? _disposalDate;
    private decimal? _disposalValue;
    private string _location = string.Empty;
    private string _serialNumber = string.Empty;
    private string _notes = string.Empty;
    private DateTime _updatedUtc;
    private decimal _currentBookValue;

    // FIXED39 register/source fields. Tally remains the accounting authority; these
    // fields preserve the exact source identity and any deliberate non-Tally opening.
    private string _tallyLedgerName = string.Empty;
    private string _tallyLedgerMasterId = string.Empty;
    private string _supplierName = string.Empty;
    private decimal _openingBalance;
    private decimal _grossOpeningBalance;
    private string _openingSource = string.Empty;
    private decimal _currentAdditions;
    private decimal _currentDeletions;
    private decimal _normalDepreciation;
    private decimal _additionalDepreciation;
    private decimal _currentDepreciation;
    private decimal _postedDepreciation;
    private string _policySource = string.Empty;
    private string _detectionStatus = string.Empty;
    private bool _isTransientTallyRow;

    public long Id { get => _id; set => SetField(ref _id, value); }
    public long ClientId { get => _clientId; set => SetField(ref _clientId, value); }
    public string FinancialYear { get => _financialYear; set => SetField(ref _financialYear, value ?? string.Empty); }
    public string AssetCode { get => _assetCode; set => SetField(ref _assetCode, value ?? string.Empty); }
    public string AssetName { get => _assetName; set => SetField(ref _assetName, value ?? string.Empty); }
    public string AssetGroup { get => _assetGroup; set => SetField(ref _assetGroup, value ?? "Fixed Assets"); }
    public DateTime? PurchaseDate { get => _purchaseDate; set => SetField(ref _purchaseDate, value); }
    public DateTime? PutToUseDate { get => _putToUseDate; set => SetField(ref _putToUseDate, value); }
    public decimal PurchaseCost { get => _purchaseCost; set => SetField(ref _purchaseCost, value); }
    public decimal ResidualValue { get => _residualValue; set => SetField(ref _residualValue, value); }
    public string DepreciationMethod { get => _depreciationMethod; set => SetField(ref _depreciationMethod, value ?? "SLM"); }
    public decimal DepreciationRate { get => _depreciationRate; set => SetField(ref _depreciationRate, value); }
    public decimal OpeningAccumulatedDepreciation { get => _openingAccumulatedDepreciation; set => SetField(ref _openingAccumulatedDepreciation, value); }
    public DateTime? DisposalDate { get => _disposalDate; set => SetField(ref _disposalDate, value); }
    public decimal? DisposalValue { get => _disposalValue; set => SetField(ref _disposalValue, value); }
    public string Location { get => _location; set => SetField(ref _location, value ?? string.Empty); }
    public string SerialNumber { get => _serialNumber; set => SetField(ref _serialNumber, value ?? string.Empty); }
    public string Notes { get => _notes; set => SetField(ref _notes, value ?? string.Empty); }
    public DateTime UpdatedUtc { get => _updatedUtc; set => SetField(ref _updatedUtc, value); }
    public decimal CurrentBookValue { get => _currentBookValue; set => SetField(ref _currentBookValue, value); }

    public string TallyLedgerName { get => _tallyLedgerName; set => SetField(ref _tallyLedgerName, value ?? string.Empty); }
    public string TallyLedgerMasterId { get => _tallyLedgerMasterId; set => SetField(ref _tallyLedgerMasterId, value ?? string.Empty); }
    public string SupplierName { get => _supplierName; set => SetField(ref _supplierName, value ?? string.Empty); }
    public decimal OpeningBalance { get => _openingBalance; set => SetField(ref _openingBalance, value); }
    public decimal GrossOpeningBalance { get => _grossOpeningBalance; set => SetField(ref _grossOpeningBalance, value); }
    public string OpeningSource { get => _openingSource; set => SetField(ref _openingSource, value ?? string.Empty); }
    public decimal CurrentAdditions { get => _currentAdditions; set => SetField(ref _currentAdditions, value); }
    public decimal CurrentDeletions { get => _currentDeletions; set => SetField(ref _currentDeletions, value); }
    public decimal NormalDepreciation { get => _normalDepreciation; set => SetField(ref _normalDepreciation, value); }
    public decimal AdditionalDepreciation { get => _additionalDepreciation; set => SetField(ref _additionalDepreciation, value); }
    public decimal CurrentDepreciation { get => _currentDepreciation; set => SetField(ref _currentDepreciation, value); }
    public decimal PostedDepreciation { get => _postedDepreciation; set => SetField(ref _postedDepreciation, value); }
    public string PolicySource { get => _policySource; set => SetField(ref _policySource, value ?? string.Empty); }
    public string DetectionStatus { get => _detectionStatus; set => SetField(ref _detectionStatus, value ?? string.Empty); }
    public bool IsTransientTallyRow { get => _isTransientTallyRow; set => SetField(ref _isTransientTallyRow, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class BankRuleRecord
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public int Priority { get; set; } = 100;
    public string RuleName { get; set; } = string.Empty;
    public string BankContains { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string NarrationContains { get; set; } = string.Empty;
    public string NarrationStartsWith { get; set; } = string.Empty;
    public string ReferenceContains { get; set; } = string.Empty;
    public string NarrationRegex { get; set; } = string.Empty;
    public decimal? MinAmount { get; set; }
    public decimal? MaxAmount { get; set; }
    public string LedgerName { get; set; } = string.Empty;
    public string LedgerGroup { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int AppliedCount { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class FinanceBudgetRecord
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Period { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal BudgetAmount { get; set; }
    public decimal ActualAmount { get; set; }
    public decimal Variance => ActualAmount - BudgetAmount;
    public DateTime UpdatedUtc { get; set; }
}

public sealed class RecurringCashFlowTemplate
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Direction { get; set; } = "Outflow";
    public decimal Amount { get; set; }
    public int DayOfMonth { get; set; } = 1;
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string LedgerName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedUtc { get; set; }
}

public sealed class FinanceApprovalItem
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string DecisionNote { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class InventoryPolicyRecord
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string StockItemName { get; set; } = string.Empty;
    public decimal OpeningQuantity { get; set; }
    public decimal ReorderLevel { get; set; }
    public decimal MinimumStock { get; set; }
    public decimal MaximumStock { get; set; }
    public int ExpiryWarningDays { get; set; } = 60;
    public DateTime UpdatedUtc { get; set; }
}

public sealed class InventoryIntelligenceRow
{
    public string StockItemName { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public decimal OpeningQuantity { get; set; }
    public decimal SalesQuantity { get; set; }
    public decimal EstimatedAvailable { get; set; }
    public decimal ReorderLevel { get; set; }
    public string Alert { get; set; } = string.Empty;
    public DateTime? NearestExpiry { get; set; }
}

public sealed class FinanceSearchResult
{
    public string Module { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string PrimaryText { get; set; } = string.Empty;
    public string SecondaryText { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public DateTime? Date { get; set; }
    public decimal? Amount { get; set; }
}

public sealed class DocumentInboxRow
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public DateTime ProcessedUtc { get; set; }
}

public sealed class FinanceKpiRow
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

public sealed class BankRulePreviewRow
{
    public long TransactionId { get; set; }
    public DateTime? Date { get; set; }
    public string Bank { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string Narration { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string CurrentLedger { get; set; } = string.Empty;
    public string ProposedLedger { get; set; } = string.Empty;
}

public sealed class FinanceIntegrityCheckRow
{
    public string Check { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}
