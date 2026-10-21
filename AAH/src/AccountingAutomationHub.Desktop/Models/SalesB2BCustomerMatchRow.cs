using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AccountingAutomationHub.Desktop.Models;

public sealed class SalesB2BCustomerMatchRow : INotifyPropertyChanged
{
    private string _customerName = string.Empty;
    private string _nameSource = string.Empty;
    private string _status = string.Empty;
    private bool _isMatched;

    public string Gstin { get; init; } = string.Empty;
    public string CustomerName { get => _customerName; set => Set(ref _customerName, value ?? string.Empty); }
    public string RecipientName { get; init; } = string.Empty;
    public string TradeName { get; init; } = string.Empty;
    public string NameSource { get => _nameSource; set => Set(ref _nameSource, value ?? string.Empty); }
    public string Status { get => _status; set => Set(ref _status, value ?? string.Empty); }
    public string DuplicateStatus { get; init; } = string.Empty;
    public int TransactionCount { get; init; }
    public bool IsMatched
    {
        get => _isMatched;
        set
        {
            if (!Set(ref _isMatched, value)) return;
            OnPropertyChanged(nameof(Status));
        }
    }
    public bool HasDuplicate => DuplicateStatus.Length > 0;
    public string OriginalNamesText
    {
        get
        {
            var recipient = RecipientName.Trim();
            var trade = TradeName.Trim();
            if (recipient.Length == 0 && trade.Length == 0) return string.Empty;
            if (recipient.Length > 0 && trade.Length > 0 && recipient.Equals(trade, StringComparison.OrdinalIgnoreCase))
                return $"Recipient / Trade: {recipient}";
            if (recipient.Length == 0) return $"Trade: {trade}";
            if (trade.Length == 0) return $"Recipient: {recipient}";
            return $"Recipient: {recipient}  |  Trade: {trade}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
