using AccountingAutomationHub.Desktop.Controls;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Domain.Banking;

namespace AccountingAutomationHub.Desktop.Models;

public sealed class LedgerMappingWorkbenchRow : ViewModelBase
{
    // Ledger Mapping action selection defaults to ON. Bulk actions can opt into
    // this reusable selection layer without changing DataGrid row/cell selection.
    private bool _isSelected = true;
    private string _finalLedger = string.Empty;
    private string _ledgerGroup = string.Empty;
    private string _status = "Pending";
    private string _displayType = string.Empty;
    private string _exactMatch = string.Empty;
    private int _displayCount;

    public required BankLedgerMapping Mapping { get; init; }
    public IReadOnlyList<BankLedgerMapping> MemberMappings { get; init; } = [];
    public IReadOnlySet<string> MemberBanks { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<LedgerMappingTransactionFacet> TransactionFacets { get; init; } = [];
    public int Count { get => _displayCount; set => SetProperty(ref _displayCount, value); }
    public int FullStatementCount { get; set; }
    public int FullStatementPaymentCount { get; set; }
    public int FullStatementReceiptCount { get; set; }
    public int TallyExportDateCount { get; set; }
    public int TallyExportPaymentCount { get; set; }
    public int TallyExportReceiptCount { get; set; }
    public string SummaryName { get; init; } = string.Empty;
    public string Type { get => _displayType; set => SetProperty(ref _displayType, value ?? string.Empty); }
    public string ExactMatch { get => _exactMatch; set => SetProperty(ref _exactMatch, value ?? string.Empty); }
    public string SuggestedMatch { get; init; } = string.Empty;
    public double SuggestionConfidence { get; init; }
    public string SuggestionReason { get; init; } = string.Empty;
    public string SuggestionConfidenceDisplay => SuggestedMatch.Length == 0 ? string.Empty : $"{SuggestionConfidence:0}%";
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public string FinalLedger { get => _finalLedger; set => SetProperty(ref _finalLedger, value ?? string.Empty); }
    public string LedgerGroup { get => _ledgerGroup; set => SetProperty(ref _ledgerGroup, value ?? string.Empty); }
    public string Status { get => _status; set => SetProperty(ref _status, value ?? string.Empty); }
}

public sealed record LedgerMappingTransactionFacet(
    long TransactionId,
    DateTime Date,
    string VoucherType,
    string Method,
    string Bank);

public sealed class BulkVoucherTypeMappingRow : ViewModelBase
{
    private string _finalLedger = string.Empty;
    private string _ledgerGroup = string.Empty;
    private string _status = "Pending";

    public string VoucherType { get; init; } = string.Empty;
    public int MappingCount { get; init; }
    public int TransactionCount { get; init; }
    public IReadOnlyList<BankLedgerMapping> Mappings { get; init; } = [];
    public string FinalLedger { get => _finalLedger; set => SetProperty(ref _finalLedger, value ?? string.Empty); }
    public string LedgerGroup { get => _ledgerGroup; set => SetProperty(ref _ledgerGroup, value ?? string.Empty); }
    public string Status { get => _status; set => SetProperty(ref _status, value ?? string.Empty); }
}

public sealed class LedgerMappingDetailRow : ViewModelBase
{
    private bool _isSelected;
    public required BankTransactionRecord Transaction { get; init; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public DateTime Date => Transaction.TransactionDate;
    public string Narration => Transaction.Narration;
    public decimal Debit => Transaction.Debit;
    public decimal Credit => Transaction.Credit;
    public string Mode => Transaction.Mode;
    public string Method { get; init; } = string.Empty;
    public string Ledger => Transaction.LedgerName;
    public string Bank => Transaction.BankAccountName;
    public int RowNo { get; init; }
}

public sealed class CashEntryMasterMappingRow : ViewModelBase
{
    private bool _isSelected;
    private string _mapCreateAs = string.Empty;
    private string _parentGroup = string.Empty;
    private string _result = string.Empty;

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public string EntryLedger { get; init; } = string.Empty;
    public int EntryCount { get; init; }
    public string MasterStatus { get; init; } = string.Empty;
    public string MapCreateAs { get => _mapCreateAs; set => SetProperty(ref _mapCreateAs, value ?? string.Empty); }
    public string ParentGroup { get => _parentGroup; set => SetProperty(ref _parentGroup, value ?? string.Empty); }
    public string Result { get => _result; set => SetProperty(ref _result, value ?? string.Empty); }
}

public sealed class CashEntryCommonMappingRow : ViewModelBase
{
    private bool _isSelected;
    public required CashEntryRecord Entry { get; init; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public string EntryId => Entry.EntryId;
    public DateTime Date => Entry.EntryDate;
    public string LedgerName => Entry.EntryLedger;
    public decimal Amount => Entry.Amount;
    public string Narration => Entry.Narration;
    public string DrCr => Entry.DrCr;
    public string CurrentCommonLedger => Entry.CommonLedger;
}

public sealed class BankLedgerRenameOption : ISuggestionTextItem
{
    public string Name { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;

    public string SuggestionValue => Name;
    public string SuggestionDisplay
    {
        get
        {
            var source = NormalizeSourceLabel(Source);
            var group = Group.Trim();
            return group.Length > 0
                ? $"{Name}  •  {source}  •  {group}"
                : $"{Name}  •  {source}";
        }
    }

    public override string ToString() => SuggestionDisplay;

    public static string NormalizeSourceLabel(string? source)
    {
        var value = (source ?? string.Empty).Trim();
        // Bank Rename is deliberately a two-authority selector. The user only
        // needs to know whether the Master is authoritative from Tally or is an
        // application/manual Master. Internal workflow provenance (Bank Statement,
        // Bank Ledger Mapping, imports, Purchase/Sales/Journal, etc.) must never
        // leak into this dropdown as a third source label.
        if (value.Contains("Tally", StringComparison.OrdinalIgnoreCase)) return "Tally";
        return "Manual";
    }
}

public sealed class BankRenameRow : ViewModelBase
{
    private string _renameTo = string.Empty;
    public long BankAccountId { get; init; }
    public string CurrentBankName { get; init; } = string.Empty;
    public string RenameTo { get => _renameTo; set => SetProperty(ref _renameTo, value ?? string.Empty); }
}


public sealed class ObVerificationRow : ViewModelBase
{
    private decimal? _enteredOpeningBalance;
    private string _status = "PENDING";

    public required BankStatementRecord Statement { get; init; }
    public string ClientName { get; init; } = string.Empty;
    public string AccountNumber { get; init; } = string.Empty;
    public string FileName => Statement.SourceFile;
    public DateTime? ObDate => Statement.PeriodFrom;
    public decimal? SuggestedOpeningBalance { get; init; }
    public string SuggestedOpeningBalanceText => BankBusinessBalance.Format(SuggestedOpeningBalance);
    public decimal? EnteredOpeningBalance
    {
        get => _enteredOpeningBalance;
        set
        {
            if (!SetProperty(ref _enteredOpeningBalance, value)) return;
            OnPropertyChanged(nameof(EnteredOpeningAmount));
            OnPropertyChanged(nameof(OpeningBalanceNature));
            OnPropertyChanged(nameof(EnteredOpeningBalanceText));
            if (value.HasValue && !Status.Equals("VERIFIED", StringComparison.OrdinalIgnoreCase))
                Status = "PENDING";
        }
    }
    public decimal? EnteredOpeningAmount
    {
        get => EnteredOpeningBalance.HasValue ? Math.Abs(EnteredOpeningBalance.Value) : null;
        set => EnteredOpeningBalance = BankBusinessBalance.WithNature(value, OpeningBalanceNature.Length > 0 ? OpeningBalanceNature : "Dr");
    }
    public string OpeningBalanceNature
    {
        get => BankBusinessBalance.Nature(EnteredOpeningBalance);
        set
        {
            var nature = (value ?? string.Empty).Trim();
            if (nature.Length == 0 || !EnteredOpeningBalance.HasValue) return;
            EnteredOpeningBalance = BankBusinessBalance.WithNature(Math.Abs(EnteredOpeningBalance.Value), nature);
        }
    }
    public string EnteredOpeningBalanceText => BankBusinessBalance.Format(EnteredOpeningBalance);
    public string ClosingBalanceText => BankBusinessBalance.Format(Statement.ClosingBalance);
    public string Status { get => _status; set => SetProperty(ref _status, value ?? string.Empty); }
    public decimal? ClosingBalance => Statement.ClosingBalance;
    public decimal TotalDebits => Statement.TotalDebit;
    public decimal TotalCredits => Statement.TotalCredit;
}
