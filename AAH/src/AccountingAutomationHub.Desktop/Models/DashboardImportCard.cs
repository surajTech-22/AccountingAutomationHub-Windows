namespace AccountingAutomationHub.Desktop.Models;

public sealed class DashboardImportCard :
    AccountingAutomationHub.Desktop.ViewModels.ViewModelBase
{
    private bool _isImported;
    private string _statusText = "Not imported";
    private string _periodText = "No period imported";
    private string _filingStatus = "Not checked";
    private string _countText = "0 records";
    private string _lastImportedText = "Never";
    private string _details = string.Empty;

    public DashboardImportCard(
        string importType,
        string title,
        string icon)
    {
        ImportType = importType;
        Title = title;
        Icon = icon;
    }

    public string ImportType { get; }

    public string Title { get; }

    public string Icon { get; }

    public bool IsImported
    {
        get => _isImported;
        set => SetProperty(
            ref _isImported,
            value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(
            ref _statusText,
            value);
    }

    public string PeriodText
    {
        get => _periodText;
        set => SetProperty(
            ref _periodText,
            value);
    }

    public string FilingStatus
    {
        get => _filingStatus;
        set => SetProperty(
            ref _filingStatus,
            value);
    }

    public string CountText
    {
        get => _countText;
        set => SetProperty(
            ref _countText,
            value);
    }

    public string LastImportedText
    {
        get => _lastImportedText;
        set => SetProperty(
            ref _lastImportedText,
            value);
    }

    public string Details
    {
        get => _details;
        set => SetProperty(
            ref _details,
            value);
    }
}
