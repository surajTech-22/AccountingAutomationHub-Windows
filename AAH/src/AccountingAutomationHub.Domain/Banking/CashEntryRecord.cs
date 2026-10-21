using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AccountingAutomationHub.Domain.Banking;

public sealed class CashEntryRecord : INotifyPropertyChanged
{
    private decimal _singleAmount;
    private string _singleDrCr = string.Empty;
    private string _suggestedDebitAmount = string.Empty;
    private string _suggestedCreditAmount = string.Empty;
    private string _suggestedSingleAmount = string.Empty;
    private string _suggestedSingleDrCr = string.Empty;
    private string _closingBalanceSuggestionText = string.Empty;
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public DateTime EntryDate { get; set; }
    public string EntryLedger { get; set; } = string.Empty;
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string Narration { get; set; } = string.Empty;
    public string CommonLedger { get; set; } = string.Empty;
    public string MappedMasterLedger { get; set; } = string.Empty;
    public string VoucherType { get; set; } = string.Empty;
    public string Status { get; set; } = "Review Required";
    public string WorkflowStatus { get; set; } = "Draft";
    public string EntryId { get; set; } = string.Empty;
    public string ReferenceNumber { get; set; } = string.Empty;
    public string AttachmentPath { get; set; } = string.Empty;
    public long? ReversalOfId { get; set; }
    public long? ClonedFromId { get; set; }
    public long? RecurringProfileId { get; set; }
    public string SourceRunId { get; set; } = string.Empty;
    public string SourceJobId { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTime? ApprovedUtc { get; set; }
    public bool IsUserEdited { get; set; } = true;
    public DateTime UpdatedUtc { get; set; }

    public decimal Amount => Debit > 0 ? Debit : Credit;
    public string DrCr => Debit > 0 ? "Dr" : Credit > 0 ? "Cr" : string.Empty;
    public decimal SingleAmount
    {
        get => _singleAmount != 0m ? _singleAmount : Amount;
        set => _singleAmount = value;
    }
    public string SingleDrCr
    {
        get => _singleDrCr.Length > 0 ? _singleDrCr : DrCr;
        set => _singleDrCr = value ?? string.Empty;
    }
    // Transient display hints only; these values are never persisted and never write
    // Debit/Credit/SingleAmount/SingleDrCr.
    public string SuggestedDebitAmount { get => _suggestedDebitAmount; private set => SetField(ref _suggestedDebitAmount, value); }
    public string SuggestedCreditAmount { get => _suggestedCreditAmount; private set => SetField(ref _suggestedCreditAmount, value); }
    public string SuggestedSingleAmount { get => _suggestedSingleAmount; private set => SetField(ref _suggestedSingleAmount, value); }
    public string SuggestedSingleDrCr { get => _suggestedSingleDrCr; private set => SetField(ref _suggestedSingleDrCr, value); }
    public string ClosingBalanceSuggestionText { get => _closingBalanceSuggestionText; private set => SetField(ref _closingBalanceSuggestionText, value); }

    public void SetClosingBalanceSuggestion(decimal? closingBalance)
    {
        SuggestedDebitAmount = string.Empty;
        SuggestedCreditAmount = string.Empty;
        SuggestedSingleAmount = string.Empty;
        SuggestedSingleDrCr = string.Empty;

        if (!closingBalance.HasValue || Math.Abs(closingBalance.Value) < 0.005m)
        {
            ClosingBalanceSuggestionText = closingBalance.HasValue ? "Closing balance: 0.00" : string.Empty;
            return;
        }

        var value = closingBalance.Value;
        var amount = Math.Abs(value);
        var balanceSide = value > 0m ? "Dr" : "Cr";
        var settlementSide = value > 0m ? "Cr" : "Dr";
        ClosingBalanceSuggestionText = $"Closing balance: {amount:N2} {balanceSide}";
        if (settlementSide == "Dr")
            SuggestedDebitAmount = $"Suggested {amount:N2}";
        else
            SuggestedCreditAmount = $"Suggested {amount:N2}";
        SuggestedSingleAmount = $"Suggested {amount:N2}";
        SuggestedSingleDrCr = $"Suggested {settlementSide}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        value ??= string.Empty;
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

}
