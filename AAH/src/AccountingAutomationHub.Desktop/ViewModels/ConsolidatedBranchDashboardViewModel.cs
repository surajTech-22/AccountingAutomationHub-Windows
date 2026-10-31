using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AccountingAutomationHub.Infrastructure.Clients;

namespace AccountingAutomationHub.Desktop.ViewModels;

/// <summary>
/// Reads database-owned child scopes asynchronously. Never makes a Tally call,
/// triggers an import, auto-migrates historical records or sums unreviewed
/// vouchers across private branch and legacy parent scopes.
/// </summary>
public sealed class ConsolidatedBranchDashboardViewModel : PageViewModel
{
    private readonly long _parentClientId;
    private readonly string _financialYear;
    private readonly SqliteConsolidatedBranchReadbackRepository _readback = new();
    private string _status = "Reading saved branch accounting records...";
    private string _warning = string.Empty;

    public ConsolidatedBranchDashboardViewModel(long parentClientId, string financialYear)
        : base("Dashboard", "Consolidated / source-verified branch accounting")
    {
        _parentClientId = parentClientId;
        _financialYear = financialYear;
        _ = RefreshAsync();
    }

    public ObservableCollection<BranchLedgerReadbackRow> Branches { get; } = [];
    public string FinancialYear => _financialYear;
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }
    public string Warning
    {
        get => _warning;
        private set => SetProperty(ref _warning, value);
    }

    private async Task RefreshAsync()
    {
        try
        {
            var snapshot = await Task.Run(() => _readback.Read(_parentClientId, _financialYear));
            Branches.Clear();
            foreach (var row in snapshot.Branches) Branches.Add(row);
            Status = $"{snapshot.Branches.Count} accounting scope(s) read from saved SQLite Client/FY ownership.";
            Warning = snapshot.Warning;
        }
        catch (Exception e)
        {
            Status = "Consolidated accounting readback could not be verified.";
            Warning = e.Message;
        }
    }
}
