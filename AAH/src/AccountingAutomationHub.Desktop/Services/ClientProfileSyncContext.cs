using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Services;

public sealed class ClientProfileSyncContext :
    ViewModelBase
{
    private long _clientId;
    private string _statusText =
        "GSTR-3B profile check has not run yet.";
    private DateTime _updatedUtc;

    public event EventHandler? Changed;

    public long ClientId
    {
        get => _clientId;
        private set => SetProperty(ref _clientId, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public DateTime UpdatedUtc
    {
        get => _updatedUtc;
        private set => SetProperty(ref _updatedUtc, value);
    }

    public void Publish(long clientId, string statusText)
    {
        ClientId = clientId;
        StatusText = statusText;
        UpdatedUtc = DateTime.UtcNow;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
