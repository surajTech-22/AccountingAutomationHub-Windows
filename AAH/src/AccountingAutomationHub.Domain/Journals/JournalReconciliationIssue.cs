using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AccountingAutomationHub.Domain.Journals;

public sealed class JournalReconciliationIssue : INotifyPropertyChanged
{
    private string _matchingLedger = string.Empty;

    public long VoucherId { get; set; }
    public string VoucherNumber { get; set; } = string.Empty;
    public DateTime VoucherDate { get; set; }
    public string Narration { get; set; } = string.Empty;
    public decimal DebitTotal { get; set; }
    public decimal CreditTotal { get; set; }
    public decimal Difference => DebitTotal - CreditTotal;
    public string RequiredSide => Difference > 0m ? "Cr" : "Dr";
    public decimal RequiredAmount => Math.Abs(Difference);
    public string MatchingLedger
    {
        get => _matchingLedger;
        set
        {
            if (string.Equals(_matchingLedger, value, StringComparison.Ordinal))
            {
                return;
            }
            _matchingLedger = value ?? string.Empty;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MatchingLedger)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
