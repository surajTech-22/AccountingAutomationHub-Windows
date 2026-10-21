namespace AccountingAutomationHub.Desktop.Services;

public sealed class ImportActivityContext
{
    public event EventHandler? Changed;

    public void NotifyChanged() =>
        Changed?.Invoke(
            this,
            EventArgs.Empty);
}
