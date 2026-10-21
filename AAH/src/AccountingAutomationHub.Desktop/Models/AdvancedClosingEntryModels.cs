using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AccountingAutomationHub.Desktop.Models;

public sealed class AdvancedClosingLedgerRow : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _oppositeLedger = string.Empty;
    private string _status = string.Empty;
    private int _plannedEntryCount;
    private decimal _plannedAmount;
    private decimal _openingBalance;
    private decimal _totalDebit;
    private decimal _totalCredit;
    private decimal _closingBalance;
    private int _movementCount;

    public string LedgerName { get; init; } = string.Empty;
    public string GroupName { get; init; } = string.Empty;
    public decimal OpeningBalance { get => _openingBalance; set => SetField(ref _openingBalance, value); }
    public decimal TotalDebit { get => _totalDebit; set => SetField(ref _totalDebit, value); }
    public decimal TotalCredit { get => _totalCredit; set => SetField(ref _totalCredit, value); }
    public decimal ClosingBalance
    {
        get => _closingBalance;
        set
        {
            if (_closingBalance == value) return;
            _closingBalance = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ClosingText));
        }
    }
    public int MovementCount { get => _movementCount; set => SetField(ref _movementCount, value); }
    public ObservableCollection<AdvancedClosingVoucherBucket> Buckets { get; } = [];
    // Fix36: session-only source-voucher choices shown in Details. Unchecking a source
    // excludes that exact Sales/Purchase voucher from the current Closure plan without
    // changing the underlying accounting voucher.
    public ObservableCollection<AdvancedClosingSourceSelection> SourceSelections { get; } = [];

    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }
    public string OppositeLedger { get => _oppositeLedger; set => SetField(ref _oppositeLedger, value ?? string.Empty); }
    public string Status { get => _status; set => SetField(ref _status, value ?? string.Empty); }
    public int PlannedEntryCount { get => _plannedEntryCount; set => SetField(ref _plannedEntryCount, value); }
    public decimal PlannedAmount { get => _plannedAmount; set => SetField(ref _plannedAmount, value); }
    public string ClosingText => FormatBalance(ClosingBalance);
    public string PlannedAmountText => PlannedAmount <= 0.005m ? "-" : PlannedAmount.ToString("N2");

    public void NotifyPlanChanged()
    {
        OnPropertyChanged(nameof(PlannedEntryCount));
        OnPropertyChanged(nameof(PlannedAmount));
        OnPropertyChanged(nameof(PlannedAmountText));
        OnPropertyChanged(nameof(Status));
    }

    private static string FormatBalance(decimal value) => Math.Abs(value) < 0.005m ? "0.00" : $"{Math.Abs(value):N2} {(value > 0m ? "Dr" : "Cr")}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class AdvancedClosingVoucherBucket : INotifyPropertyChanged
{
    private int? _manualSplitCount;
    private string _status = string.Empty;

    public string Identity { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string VoucherType { get; set; } = string.Empty;
    public int SourceVoucherCount { get; set; }
    public decimal SignedSourceAmount { get; set; }
    public decimal ExistingGeneratedAmount { get; set; }
    // Fix33: when live closing is the authority, source vouchers are amount/date templates
    // rather than a single aggregate to divide by Maximum/Day.  The authority amount here
    // is the NEW amount assigned to this bucket after walking individual source vouchers.
    public decimal? NewReverseAmountAuthority { get; set; }
    public string ReverseDrCrOverride { get; set; } = string.Empty;
    public IReadOnlyList<AdvancedClosingSourceEvent>? AuthoritySourceEvents { get; set; }
    public bool HasClosingAuthorityTemplate => NewReverseAmountAuthority.HasValue;
    public decimal ReverseAmount => NewReverseAmountAuthority ?? Math.Abs(SignedSourceAmount);
    public decimal RemainingAmount => HasClosingAuthorityTemplate
        ? Math.Max(0m, NewReverseAmountAuthority!.Value)
        : Math.Max(0m, ReverseAmount - ExistingGeneratedAmount);
    public string ReverseDrCr => ReverseDrCrOverride.Length > 0
        ? ReverseDrCrOverride
        : SignedSourceAmount > 0m ? "Cr" : "Dr";
    public IReadOnlyList<DateTime> SourceDates { get; set; } = [];
    public IReadOnlyList<AdvancedClosingSourceEvent> SourceEvents { get; set; } = [];
    public ObservableCollection<AdvancedClosingPlannedEntry> Entries { get; } = [];
    public bool AlreadyGenerated { get; set; }
    public int? ManualSplitCount
    {
        get => _manualSplitCount;
        set
        {
            if (_manualSplitCount == value) return;
            _manualSplitCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SplitText));
        }
    }
    public string SplitText => ManualSplitCount.HasValue ? ManualSplitCount.Value.ToString() : "Auto";
    public string Status { get => _status; set { if (_status == value) return; _status = value ?? string.Empty; OnPropertyChanged(); } }
    public string ReverseAmountText => ReverseAmount.ToString("N2");

    public event PropertyChangedEventHandler? PropertyChanged;
    public void NotifyEntriesChanged()
    {
        OnPropertyChanged(nameof(Entries));
        OnPropertyChanged(nameof(SplitText));
        OnPropertyChanged(nameof(Status));
    }
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}


public sealed class AdvancedClosingSourceSelection : INotifyPropertyChanged
{
    private bool _isIncluded = true;
    private decimal _plannedReverseAmount;

    public string SourceVoucherIdentity { get; init; } = string.Empty;
    public string VoucherType { get; init; } = string.Empty;
    public DateTime Date { get; init; }
    public string VoucherOrReference { get; init; } = string.Empty;
    public decimal SignedAmount { get; init; }
    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (_isIncluded == value) return;
            _isIncluded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectionState));
        }
    }
    public decimal PlannedReverseAmount
    {
        get => _plannedReverseAmount;
        set
        {
            if (_plannedReverseAmount == value) return;
            _plannedReverseAmount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlannedReverseAmountText));
            OnPropertyChanged(nameof(SelectionState));
        }
    }
    public string DateText => Date.ToString("dd-MM-yyyy");
    public string SourceAmountText => Math.Abs(SignedAmount).ToString("N2");
    public string SourceDrCr => SignedAmount >= 0m ? "Dr" : "Cr";
    public string PlannedReverseAmountText => PlannedReverseAmount < 0.005m ? "-" : PlannedReverseAmount.ToString("N2");
    public string SelectionState => !IsIncluded ? "Ignored" : PlannedReverseAmount >= 0.005m ? "Planned" : "Included / not required";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class AdvancedClosingSourceEvent
{
    public DateTime Date { get; init; }
    public decimal SignedAmount { get; init; }
    public string SourceVoucherIdentity { get; init; } = string.Empty;
}

public sealed class AdvancedClosingPlannedEntry
{
    public int Sequence { get; init; }
    public DateTime Date { get; init; }
    public decimal Amount { get; init; }
    public string DrCr { get; init; } = string.Empty;
    public string VoucherType { get; init; } = string.Empty;
    // Fix34: keep the exact source-voucher binding all the way through planning so
    // a reverse can never silently borrow another voucher's date.
    public string SourceVoucherIdentity { get; init; } = string.Empty;
    public DateTime? SourceDate { get; init; }
    public bool Existing { get; init; }
    public string AmountText => Amount.ToString("N2");
    public string DateText => Date.ToString("dd-MM-yyyy");
    public string SourceDateText => SourceDate.HasValue ? SourceDate.Value.ToString("dd-MM-yyyy") : "-";
    public string StateText => Existing ? "Existing" : "Planned";
}
