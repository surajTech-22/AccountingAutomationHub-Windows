using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AccountingAutomationHub.Domain.Journals;

public sealed class JournalLedgerMapping : INotifyPropertyChanged
{
    private long _persistentId;
    private long _clientId;
    private string _financialYear = string.Empty;
    private string _ledgerName = string.Empty;
    private string _masterGroup = string.Empty;
    private string _status = "Pending";
    private string _source = "Journal";
    private DateTime _updatedUtc;

    public long PersistentId { get => _persistentId; set => SetField(ref _persistentId, value); }
    public long ClientId { get => _clientId; set => SetField(ref _clientId, value); }
    public string FinancialYear { get => _financialYear; set => SetField(ref _financialYear, value ?? string.Empty); }
    public string LedgerName { get => _ledgerName; set => SetField(ref _ledgerName, value ?? string.Empty); }
    public string MasterGroup { get => _masterGroup; set => SetField(ref _masterGroup, value ?? string.Empty); }
    public string Status { get => _status; set => SetField(ref _status, value ?? string.Empty); }
    public string Source { get => _source; set => SetField(ref _source, value ?? string.Empty); }
    public DateTime UpdatedUtc { get => _updatedUtc; set => SetField(ref _updatedUtc, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
