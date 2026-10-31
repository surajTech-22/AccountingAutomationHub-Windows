using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AccountingAutomationHub.Infrastructure.Clients;
using AccountingAutomationHub.Domain.Clients;

namespace AccountingAutomationHub.Desktop.ViewModels;

/// <summary>
/// Reads database-owned child scopes asynchronously without importing or moving
/// source records. The embedded original Dashboard Tally panel separately owns
/// its existing link and export commands; this viewmodel never creates a second
/// Tally link or combines unreviewed parent and private-branch vouchers.
/// </summary>
public sealed class ConsolidatedBranchDashboardViewModel : PageViewModel
{
    private readonly long _parentClientId;
    private readonly string _financialYear;
    private readonly SqliteConsolidatedBranchReadbackRepository _readback = new();
    private string _status = "Reading saved branch accounting records...";
    private string _warning = string.Empty;

    public ConsolidatedBranchDashboardViewModel(ClientProfile parent,
        DashboardViewModel existingDashboardWorkspace)
        : base("Dashboard", "Detailed consolidated client workspace overview")
    {
        if (parent is null) throw new ArgumentNullException(nameof(parent));
        // Reuse the SAME dashboard Tally controls and export authority as a
        // normal client. The dedicated Tally Import page is not the dashboard.
        ExistingDashboardWorkspace = existingDashboardWorkspace
            ?? throw new ArgumentNullException(nameof(existingDashboardWorkspace));
        _parentClientId = parent.Id;
        _financialYear = parent.FinancialYear;
        ClientName = parent.ClientName;
        Pan = parent.Pan ?? string.Empty;
        PrimaryGstin = parent.Gstin ?? string.Empty;
        State = parent.State ?? string.Empty;
        _ = RefreshAsync();
    }

    public string ClientName { get; }
    public string Pan { get; }
    public string PrimaryGstin { get; }
    public string State { get; }
    public DashboardViewModel ExistingDashboardWorkspace { get; }
    public string BranchSummary => $"{BranchCount} branches · FY {_financialYear}";

    public ObservableCollection<BranchLedgerReadbackRow> Branches { get; } = [];
    public ObservableCollection<BranchLedgerReadbackRow> VerifiedBranches { get; } = [];
    public string HistoricalParentSummary
    {
        get
        {
            var parent = Branches.FirstOrDefault(x => x.IsLegacyUnassigned);
            if (parent is null || !parent.HasAnyRecords)
                return "No unassigned historical parent transactions were found.";
            return $"Historical parent (reconciliation only): Sales {parent.Sales ?? 0}, Purchase {parent.Purchases ?? 0}, Banking {parent.Banking ?? 0}, Cash {parent.Cash ?? 0}, Journal {parent.Journals ?? 0}. These may overlap manually imported branch records; never add them to the branch totals without source-level verification.";
        }
    }
    public string FinancialYear => _financialYear;
    public int BranchCount => Branches.Count(x => !x.IsLegacyUnassigned);
    public int VerifiedSalesCount => Branches.Where(x => !x.IsLegacyUnassigned).Sum(x => x.Sales ?? 0);
    public int VerifiedPurchaseCount => Branches.Where(x => !x.IsLegacyUnassigned).Sum(x => x.Purchases ?? 0);
    public int VerifiedBankingCount => Branches.Where(x => !x.IsLegacyUnassigned).Sum(x => x.Banking ?? 0);
    public int VerifiedJournalCount => Branches.Where(x => !x.IsLegacyUnassigned).Sum(x => x.Journals ?? 0);
    public int VerifiedCashCount => Branches.Where(x => !x.IsLegacyUnassigned).Sum(x => x.Cash ?? 0);
    public bool HasUnassignedHistory => Branches.Any(x => x.IsLegacyUnassigned && x.HasAnyRecords);
    public string ConsolidationNotice => HasUnassignedHistory
        ? "Historical parent records have not been allocated to branches. These figures deliberately exclude that unverified bucket to avoid double counting."
        : "Showing source-owned branch records only. Tally balances are not yet reconciled.";
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
            VerifiedBranches.Clear();
            foreach (var row in snapshot.Branches)
            {
                Branches.Add(row);
                if (!row.IsLegacyUnassigned) VerifiedBranches.Add(row);
            }
            OnPropertyChanged(nameof(HistoricalParentSummary));
            OnPropertyChanged(nameof(BranchCount));
            OnPropertyChanged(nameof(VerifiedSalesCount));
            OnPropertyChanged(nameof(VerifiedPurchaseCount));
            OnPropertyChanged(nameof(VerifiedBankingCount));
            OnPropertyChanged(nameof(VerifiedJournalCount));
            OnPropertyChanged(nameof(VerifiedCashCount));
            OnPropertyChanged(nameof(BranchSummary));
            OnPropertyChanged(nameof(HasUnassignedHistory));
            OnPropertyChanged(nameof(ConsolidationNotice));
            Status = $"{BranchCount} verified branch book(s) · FY {_financialYear}";
            Warning = snapshot.Warning;
        }
        catch (Exception e)
        {
            Status = "Consolidated accounting readback could not be verified.";
            Warning = e.Message;
        }
    }
}
