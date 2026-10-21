namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class CashEntryViewModel(
    BankingViewModel core)
    : PageViewModel(
        "Cash Entry",
        "Cash Payment, Cash Receipt, Journal, mapping and registry")
{
    public BankingViewModel Core { get; } = core;
}
