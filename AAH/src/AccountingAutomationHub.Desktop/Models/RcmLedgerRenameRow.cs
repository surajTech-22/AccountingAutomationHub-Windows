namespace AccountingAutomationHub.Desktop.Models;

public sealed class RcmLedgerRenameRow : AccountingAutomationHub.Desktop.ViewModels.ViewModelBase
{
    private string _renameValue = string.Empty;

    public required string Key { get; init; }
    public required string ExistingName { get; init; }

    public string RenameValue
    {
        get => _renameValue;
        set
        {
            if (SetProperty(ref _renameValue, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(EffectiveName));
            }
        }
    }

    public string EffectiveName => string.IsNullOrWhiteSpace(RenameValue)
        ? ExistingName
        : RenameValue.Trim();
}
