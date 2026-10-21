using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AccountingAutomationHub.Domain.Journals;

public sealed class JournalLine : INotifyPropertyChanged
{
    private long _persistentId;
    private long _voucherId;
    private int _lineNumber;
    private string _ledgerName = string.Empty;
    private decimal _amount;
    private string _drCr = "Dr";
    private string _taxHead = string.Empty;
    private string _lineRole = string.Empty;

    public long PersistentId
    {
        get => _persistentId;
        set => SetField(ref _persistentId, value);
    }

    public long VoucherId
    {
        get => _voucherId;
        set => SetField(ref _voucherId, value);
    }

    public int LineNumber
    {
        get => _lineNumber;
        set => SetField(ref _lineNumber, value);
    }

    public string LedgerName
    {
        get => _ledgerName;
        set => SetField(ref _ledgerName, value ?? string.Empty);
    }

    public decimal Amount
    {
        get => _amount;
        set
        {
            if (SetField(ref _amount, Math.Abs(value)))
            {
                OnPropertyChanged(nameof(DebitAmount));
                OnPropertyChanged(nameof(CreditAmount));
            }
        }
    }

    public string DrCr
    {
        get => _drCr;
        set
        {
            var normalized = value.StartsWith("C", StringComparison.OrdinalIgnoreCase)
                ? "Cr"
                : "Dr";
            if (SetField(ref _drCr, normalized))
            {
                OnPropertyChanged(nameof(DebitAmount));
                OnPropertyChanged(nameof(CreditAmount));
            }
        }
    }

    public decimal DebitAmount
    {
        get => DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase) ? Amount : 0m;
        set
        {
            if (value == 0m && DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase))
            {
                Amount = 0m;
                return;
            }

            if (value != 0m)
            {
                DrCr = "Dr";
                Amount = value;
            }
        }
    }

    public decimal CreditAmount
    {
        get => DrCr.Equals("Cr", StringComparison.OrdinalIgnoreCase) ? Amount : 0m;
        set
        {
            if (value == 0m && DrCr.Equals("Cr", StringComparison.OrdinalIgnoreCase))
            {
                Amount = 0m;
                return;
            }

            if (value != 0m)
            {
                DrCr = "Cr";
                Amount = value;
            }
        }
    }

    public string TaxHead
    {
        get => _taxHead;
        set => SetField(ref _taxHead, value ?? string.Empty);
    }

    public string LineRole
    {
        get => _lineRole;
        set => SetField(ref _lineRole, value ?? string.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
