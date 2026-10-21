using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Domain.Clients;

namespace AccountingAutomationHub.Desktop.Services;

public sealed class ActiveClientContext :
    ViewModelBase
{
    private ClientProfile? _currentClient;
    private string _displayNameMode =
        ClientDisplayNameModes.ClientName;

    public ClientProfile? CurrentClient
    {
        get => _currentClient;
        private set
        {
            if (!SetProperty(
                    ref _currentClient,
                    value))
            {
                return;
            }

            OnPropertyChanged(
                nameof(HasCurrentClient));

            OnPropertyChanged(
                nameof(DisplayName));

            OnPropertyChanged(
                nameof(DetailText));
        }
    }

    public string DisplayNameMode
    {
        get => _displayNameMode;
        private set
        {
            if (!SetProperty(
                    ref _displayNameMode,
                    ClientDisplayNameModes.Normalize(value)))
            {
                return;
            }

            OnPropertyChanged(nameof(DisplayName));
        }
    }

    public bool HasCurrentClient =>
        CurrentClient is not null;

    public string DisplayName =>
        ClientDisplayNameModes.Resolve(
            CurrentClient,
            DisplayNameMode);

    public string DetailText
    {
        get
        {
            if (CurrentClient is null)
            {
                return "Select a client to open its available modules";
            }

            var details =
                new List<string>();

            if (!string.IsNullOrWhiteSpace(
                    CurrentClient.Gstin))
            {
                details.Add(
                    CurrentClient.Gstin);
            }

            if (!string.IsNullOrWhiteSpace(
                    CurrentClient.FinancialYear))
            {
                details.Add(
                    $"FY {CurrentClient.FinancialYear}");
            }

            return details.Count == 0
                ? CurrentClient.ClientCode
                : string.Join(
                    " • ",
                    details);
        }
    }

    public void SetCurrent(
        ClientProfile? client) =>
        CurrentClient =
            client?.Clone();

    public void SetDisplayNameMode(
        string? mode) =>
        DisplayNameMode =
            ClientDisplayNameModes.Normalize(mode);
}
