using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Collections;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Accounting;
using AccountingAutomationHub.Domain.Journals;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Infrastructure.Accounting;
using AccountingAutomationHub.Infrastructure.Gst;
using AccountingAutomationHub.Infrastructure.Governance;
using AccountingAutomationHub.Infrastructure.Journals;
using AccountingAutomationHub.Infrastructure.Masters;
using Microsoft.Win32;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class JournalViewModel : PageViewModel
{
    private const decimal BalanceTolerance = 0.005m;
    private const string DefaultTemporaryRcmIgstLedger = "Input IGST RCM";
    private const string DefaultTemporaryRcmCgstLedger = "Input CGST RCM";
    private const string DefaultTemporaryRcmSgstLedger = "Input SGST RCM";
    private const string DefaultRcmExpenseIgstLedger = "RCM Input Expenses";
    private const string DefaultRcmExpenseCgstLedger = "RCM Input Expenses";
    private const string DefaultRcmExpenseSgstLedger = "RCM Input Expenses";

    private readonly SqliteJournalRepository _repository;
    private readonly SqliteMasterRepository _masterRepository;
    private readonly LedgerTotalService _ledgerTotalService;
    private readonly SqliteGstRepository _gstRepository;
    private readonly AccountingRefreshCoordinator _refreshCoordinator;
    private readonly AccountingLiveChangeHub _liveChangeHub;
    private readonly ManualJournalBulkImportService _bulkImportService;
    private readonly ActiveClientContext _clientContext;
    private readonly TransactionAutomationService _transactionAutomationService;
    private readonly SqliteGovernanceRepository _governanceRepository;
    private readonly BackgroundAutomationService? _automationService;

    private JournalVoucher? _selectedVoucher;
    private JournalLine? _selectedLine;
    private LedgerTotalSnapshot? _selectedJournalLedgerTotal;
    private string _selectedJournalLedgerTotalStatus = "Select a Journal line to view its Ledger total.";
    private int _ledgerTotalRefreshVersion;
    private JournalLedgerMapping? _selectedLedgerMapping;
    private JournalReconciliationIssue? _selectedReconciliationIssue;
    private string _filingFrequency = "Monthly";
    private string _filingFrequencySource = "Manual fallback until GSTR-3B is imported";
    private bool _isFilingFrequencyDetected;
    private string _settlementLedgerMode = "Single Ledger";
    private string _balanceType = "Payable";
    private string _singleSettlementLedger = "GST Payable / Receivable";
    private string _igstSettlementLedger = "IGST Payable / Receivable";
    private string _cgstSettlementLedger = "CGST Payable / Receivable";
    private string _sgstSettlementLedger = "SGST Payable / Receivable";
    private string _temporaryRcmIgstLedger = "Input IGST RCM";
    private string _temporaryRcmCgstLedger = "Input CGST RCM";
    private string _temporaryRcmSgstLedger = "Input SGST RCM";
    private string _rcmExpenseIgstLedger = "RCM Input Expenses";
    private string _rcmExpenseCgstLedger = "RCM Input Expenses";
    private string _rcmExpenseSgstLedger = "RCM Input Expenses";
    private decimal _openingBooksBalance;
    private string _openingBalanceStatus = "Select a settlement ledger to read its opening balance.";
    private readonly Dictionary<string, decimal?> _ledgerOpeningBalances = new(StringComparer.OrdinalIgnoreCase);
    private string _statusText = "GST Journal ready.";
    private bool _isBusy;
    private bool _replaceSelectedVoucherLines;
    private JournalPastePreview? _pastePreview;
    private bool _isPageActive;
    private long _preparedClientId;
    private string _preparedFinancialYear = string.Empty;

    public JournalViewModel(
        SqliteJournalRepository repository,
        SqliteMasterRepository masterRepository,
        LedgerTotalService ledgerTotalService,
        SqliteGstRepository gstRepository,
        AccountingRefreshCoordinator refreshCoordinator,
        AccountingLiveChangeHub liveChangeHub,
        ManualJournalBulkImportService bulkImportService,
        ActiveClientContext clientContext,
        TransactionAutomationService transactionAutomationService,
        BackgroundAutomationService? automationService = null)
        : base(
            "Journal Center",
            "Manual, GST, recurring, reversing and controlled balanced Journal workflows")
    {
        _repository = repository;
        _masterRepository = masterRepository;
        _ledgerTotalService = ledgerTotalService;
        _gstRepository = gstRepository;
        _refreshCoordinator = refreshCoordinator;
        _liveChangeHub = liveChangeHub;
        _bulkImportService = bulkImportService;
        _clientContext = clientContext;
        _transactionAutomationService = transactionAutomationService;
        _automationService = automationService;
        _governanceRepository = new SqliteGovernanceRepository();
        _governanceRepository.Initialize();

        _clientContext.PropertyChanged += OnClientContextPropertyChanged;
        _liveChangeHub.Changed += OnLiveChange;
        Lines.CollectionChanged += OnLinesCollectionChanged;
        InitializeRcmLedgerRenameRows();

        RefreshCommand = new RelayCommand(Refresh);
        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => CurrentClientId > 0 && CanEditJournal);
        SaveSettingsCommand = new RelayCommand(SaveSettings, () => CurrentClientId > 0 && CanEditJournal);
        SaveRcmNamesCommand = new AsyncRelayCommand(SaveRcmNamesAsync, () => CurrentClientId > 0 && CanEditJournal);
        RestoreAutomaticRcmNamesCommand = new RelayCommand(RestoreAutomaticRcmNames, () => CurrentClientId > 0 && CanEditJournal);
        AddManualCommand = new RelayCommand(AddManual, () => CurrentClientId > 0 && CanEditJournal);
        AddLineCommand = new RelayCommand(AddLine, () => SelectedVoucher is not null && CanEditJournal);
        ImportManualExcelCommand = new RelayCommand(PreviewManualExcel, () => CurrentClientId > 0 && CanEditJournal);
        PasteManualCommand = new RelayCommand(PreviewSmartClipboard, () => CurrentClientId > 0 && CanEditJournal);
        PasteLinesCommand = new RelayCommand(PreviewLineClipboard, () => CurrentClientId > 0 && CanEditJournal);
        ApplyPastePreviewCommand = new RelayCommand(ApplyPastePreview, () => HasPastePreview && CanEditJournal);
        ClearPastePreviewCommand = new RelayCommand(ClearPastePreview, () => HasPastePreview);
        CopyPasteTemplateCommand = new RelayCommand(CopyPasteTemplate);
        CopyLineTemplateCommand = new RelayCommand(CopyLineTemplate);
        ExportExcelFormatCommand = new RelayCommand(ExportExcelFormat, () => CurrentClientId > 0 && CanExportJournal);
        SaveVoucherCommand = new RelayCommand(SaveVoucher, () => SelectedVoucher is not null && CanEditJournal);
        DeleteVoucherCommand = new RelayCommand(DeleteSelectedVoucher, () => SelectedVoucher is not null && CanDeleteJournal);
        CloneJournalCommand = new RelayCommand(CloneSelectedJournal, () => CanEditJournal && SelectedVoucher is not null && SelectedVoucher.PersistentId > 0);
        ReverseJournalCommand = new RelayCommand(ReverseSelectedJournal, () => CanEditJournal && SelectedVoucher is not null && SelectedVoucher.PersistentId > 0);
        MakeJournalRecurringCommand = new RelayCommand(MakeSelectedJournalRecurring, () => CanEditJournal && SelectedVoucher is not null && SelectedVoucher.PersistentId > 0);
        ApproveJournalCommand = new RelayCommand(ApproveSelectedJournal, () => CanApproveJournal && SelectedVoucher is not null && SelectedVoucher.PersistentId > 0);
        DeleteLineCommand = new RelayCommand(DeleteSelectedLine, () => CanEditJournal && SelectedLine is not null);
        RefreshLedgerMappingsCommand = new RelayCommand(RefreshLedgerMappings, () => CurrentClientId > 0);
        ApplyLedgerMappingCommand = new RelayCommand(
            ApplySelectedLedgerMapping,
            () => CanEditJournal && SelectedLedgerMapping is not null &&
                  !string.IsNullOrWhiteSpace(SelectedLedgerMapping.MasterGroup));
        ApplyReconciliationCommand = new RelayCommand(
            ApplySelectedReconciliation,
            () => CanEditJournal && SelectedReconciliationIssue is not null &&
                  !string.IsNullOrWhiteSpace(SelectedReconciliationIssue.MatchingLedger));

        StatusText = "Journal is ready to load when opened.";
    }

    public BulkObservableCollection<JournalVoucher> Vouchers { get; } = [];
    public ObservableCollection<JournalLine> Lines { get; } = [];
    public BulkObservableCollection<JournalSummaryRow> SummaryRows { get; } = [];
    public ObservableCollection<JournalVoucher> PastePreviewVouchers { get; } = [];
    public ObservableCollection<JournalLine> PastePreviewLines { get; } = [];
    public ObservableCollection<string> PasteWarnings { get; } = [];
    public BulkObservableCollection<JournalLedgerMapping> LedgerMappings { get; } = [];
    public BulkObservableCollection<string> MasterGroupOptions { get; } = [];
    public BulkObservableCollection<string> MasterLedgerOptions { get; } = [];
    public BulkObservableCollection<LedgerPickerOption> LedgerPickerOptions { get; } = [];
    public ObservableCollection<RcmLedgerRenameRow> RcmLedgerRenames { get; } = [];
    public ObservableCollection<JournalReconciliationIssue> ReconciliationIssues { get; } = [];

    public IReadOnlyList<string> VoucherTypeOptions { get; } = ["Journal", "Adjustment", "Reclassification"];
    public IReadOnlyList<string> TaxHeadOptions { get; } = ["", "IGST", "CGST", "SGST", "UTGST", "GST", "RCM"];
    public IReadOnlyList<string> LineRoleOptions { get; } =
    [
        "Output Tax",
        "Normal Input Tax",
        "RCM Input Claim",
        "RCM Receivable Claim",
        "RCM Input Receivable",
        "RCM Input -> Receivable",
        "RCM Input Expenses",
        "RCM Input -> Expenses",
        // Legacy manual-role labels remain selectable for older saved/manual vouchers.
        "RCM ITC Claim",
        "RCM Input Transfer",
        "RCM ITC Reversal",
        "Settlement Payable",
        "Settlement Receivable",
        "Manual Note"
    ];

    public bool CanEditJournal => DesktopPermissionService.CanMutateAccounting(_governanceRepository, GovernancePermissions.JournalCreate);
    public bool CanDeleteJournal => DesktopPermissionService.CanMutateAccounting(_governanceRepository, GovernancePermissions.DeleteAccounting);
    public bool CanApproveJournal => !NetworkClientRuntime.IsNetworkClient &&
        (DesktopPermissionService.HasPermission(_governanceRepository, GovernancePermissions.JournalApprove) ||
         DesktopPermissionService.HasPermission(_governanceRepository, GovernancePermissions.Approve));
    public bool CanExportJournal => DesktopPermissionService.HasPermission(_governanceRepository, GovernancePermissions.ExportData);
    public bool IsJournalReadOnly => !CanEditJournal;
    public string JournalPermissionSummary => $"Role: {DesktopPermissionService.EffectiveRoleName(_governanceRepository)} • {(NetworkClientRuntime.IsNetworkClient ? "Windows processing node • " : string.Empty)}Edit {(CanEditJournal ? "allowed" : "denied")} • Approve {(CanApproveJournal ? "allowed" : "denied")} • Delete {(CanDeleteJournal ? "allowed" : "denied")}";

    public RelayCommand RefreshCommand { get; }
    public AsyncRelayCommand GenerateCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand SaveRcmNamesCommand { get; }
    public RelayCommand RestoreAutomaticRcmNamesCommand { get; }
    public RelayCommand AddManualCommand { get; }
    public RelayCommand AddLineCommand { get; }
    public RelayCommand ImportManualExcelCommand { get; }
    public RelayCommand PasteManualCommand { get; }
    public RelayCommand PasteLinesCommand { get; }
    public RelayCommand ApplyPastePreviewCommand { get; }
    public RelayCommand ClearPastePreviewCommand { get; }
    public RelayCommand CopyPasteTemplateCommand { get; }
    public RelayCommand CopyLineTemplateCommand { get; }
    public RelayCommand ExportExcelFormatCommand { get; }
    public RelayCommand SaveVoucherCommand { get; }
    public RelayCommand DeleteVoucherCommand { get; }
    public RelayCommand CloneJournalCommand { get; }
    public RelayCommand ReverseJournalCommand { get; }
    public RelayCommand MakeJournalRecurringCommand { get; }
    public RelayCommand ApproveJournalCommand { get; }
    public RelayCommand DeleteLineCommand { get; }
    public RelayCommand RefreshLedgerMappingsCommand { get; }
    public RelayCommand ApplyLedgerMappingCommand { get; }
    public RelayCommand ApplyReconciliationCommand { get; }

    public JournalVoucher? SelectedVoucher
    {
        get => _selectedVoucher;
        set
        {
            if (!SetProperty(ref _selectedVoucher, value))
            {
                return;
            }

            Lines.Clear();
            SelectedLine = null;
            if (value is not null)
            {
                foreach (var line in value.Lines)
                {
                    Lines.Add(line);
                }
            }

            SaveVoucherCommand.RaiseCanExecuteChanged();
            DeleteVoucherCommand.RaiseCanExecuteChanged();
            CloneJournalCommand.RaiseCanExecuteChanged();
            ReverseJournalCommand.RaiseCanExecuteChanged();
            MakeJournalRecurringCommand.RaiseCanExecuteChanged();
            ApproveJournalCommand.RaiseCanExecuteChanged();
            AddLineCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(HasSelectedVoucher));
            OnPropertyChanged(nameof(SelectedVoucherHeading));
            NotifyLineSummary();
        }
    }

    public JournalLine? SelectedLine
    {
        get => _selectedLine;
        set
        {
            if (SetProperty(ref _selectedLine, value))
            {
                DeleteLineCommand.RaiseCanExecuteChanged();
                QueueSelectedJournalLedgerTotalRefresh(value);
            }
        }
    }

    public LedgerTotalSnapshot? SelectedJournalLedgerTotal
    {
        get => _selectedJournalLedgerTotal;
        private set
        {
            if (!SetProperty(ref _selectedJournalLedgerTotal, value)) return;
            OnPropertyChanged(nameof(SelectedJournalLedgerName));
            OnPropertyChanged(nameof(SelectedJournalLedgerGroup));
            OnPropertyChanged(nameof(SelectedJournalLedgerOpeningText));
            OnPropertyChanged(nameof(SelectedJournalLedgerAsOnLabel));
            OnPropertyChanged(nameof(SelectedJournalLedgerAsOnText));
            OnPropertyChanged(nameof(SelectedJournalLedgerClosingText));
            OnPropertyChanged(nameof(SelectedJournalLedgerMovementText));
        }
    }

    public string SelectedJournalLedgerTotalStatus
    {
        get => _selectedJournalLedgerTotalStatus;
        private set => SetProperty(ref _selectedJournalLedgerTotalStatus, value);
    }

    public string SelectedJournalLedgerName => SelectedJournalLedgerTotal?.LedgerName ?? "No Ledger selected";
    public string SelectedJournalLedgerGroup => string.IsNullOrWhiteSpace(SelectedJournalLedgerTotal?.ParentGroup)
        ? "Select a Journal line"
        : SelectedJournalLedgerTotal!.ParentGroup;
    public string SelectedJournalLedgerOpeningText => LedgerPickerOption.FormatBalance(SelectedJournalLedgerTotal?.OpeningBalance ?? 0m);
    public string SelectedJournalLedgerAsOnLabel => SelectedJournalLedgerTotal is null
        ? "Balance as on voucher date"
        : $"As on {SelectedJournalLedgerTotal.AsOnDate:dd.MM.yyyy}";
    public string SelectedJournalLedgerAsOnText => LedgerPickerOption.FormatBalance(SelectedJournalLedgerTotal?.BalanceAsOnDate ?? 0m);
    public string SelectedJournalLedgerClosingText => LedgerPickerOption.FormatBalance(SelectedJournalLedgerTotal?.ClosingBalance ?? 0m);
    public string SelectedJournalLedgerMovementText => SelectedJournalLedgerTotal is null
        ? string.Empty
        : $"FY Dr {SelectedJournalLedgerTotal.TotalDebit:N2} • Cr {SelectedJournalLedgerTotal.TotalCredit:N2} • {SelectedJournalLedgerTotal.MovementCount:N0} entr{(SelectedJournalLedgerTotal.MovementCount == 1 ? "y" : "ies")}";

    public JournalLedgerMapping? SelectedLedgerMapping
    {
        get => _selectedLedgerMapping;
        set
        {
            if (_selectedLedgerMapping is not null)
            {
                _selectedLedgerMapping.PropertyChanged -= OnLedgerMappingPropertyChanged;
            }
            if (!SetProperty(ref _selectedLedgerMapping, value))
            {
                return;
            }
            if (_selectedLedgerMapping is not null)
            {
                _selectedLedgerMapping.PropertyChanged += OnLedgerMappingPropertyChanged;
            }
            ApplyLedgerMappingCommand.RaiseCanExecuteChanged();
        }
    }

    public JournalReconciliationIssue? SelectedReconciliationIssue
    {
        get => _selectedReconciliationIssue;
        set
        {
            if (_selectedReconciliationIssue is not null)
            {
                _selectedReconciliationIssue.PropertyChanged -= OnReconciliationIssuePropertyChanged;
            }
            if (!SetProperty(ref _selectedReconciliationIssue, value))
            {
                return;
            }
            if (_selectedReconciliationIssue is not null)
            {
                _selectedReconciliationIssue.PropertyChanged += OnReconciliationIssuePropertyChanged;
            }
            ApplyReconciliationCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelectedVoucher => SelectedVoucher is not null;
    public string SelectedVoucherHeading => SelectedVoucher is null
        ? "Select or create a voucher"
        : $"{SelectedVoucher.VoucherNumber} · {SelectedVoucher.PeriodLabel}";

    public string FilingFrequency
    {
        get => _filingFrequency;
        set
        {
            if (SetProperty(ref _filingFrequency, value))
            {
                OnPropertyChanged(nameof(FilingFrequencyBadge));
                OnPropertyChanged(nameof(IsMonthlyFiling));
                OnPropertyChanged(nameof(IsQuarterlyFiling));
            }
        }
    }

    public string FilingFrequencyBadge => FilingFrequency.Contains("Quarter", StringComparison.OrdinalIgnoreCase)
        ? "QUARTERLY"
        : "MONTHLY";

    public string FilingFrequencySource
    {
        get => _filingFrequencySource;
        private set => SetProperty(ref _filingFrequencySource, value);
    }

    public bool IsFilingFrequencyDetected
    {
        get => _isFilingFrequencyDetected;
        private set
        {
            if (SetProperty(ref _isFilingFrequencyDetected, value))
            {
                OnPropertyChanged(nameof(CanEditFilingFrequency));
            }
        }
    }

    public bool CanEditFilingFrequency => CanEditJournal && !IsFilingFrequencyDetected;
    public bool IsMonthlyFiling
    {
        get => !FilingFrequency.Contains("Quarter", StringComparison.OrdinalIgnoreCase);
        set { if (value && CanEditFilingFrequency) FilingFrequency = "Monthly"; }
    }
    public bool IsQuarterlyFiling
    {
        get => !IsMonthlyFiling;
        set { if (value && CanEditFilingFrequency) FilingFrequency = "Quarterly"; }
    }

    public string SettlementLedgerMode
    {
        get => _settlementLedgerMode;
        set
        {
            var normalized = string.Equals(value, "Separate Ledgers", StringComparison.OrdinalIgnoreCase)
                ? "Separate Ledgers"
                : "Single Ledger";
            if (!SetProperty(ref _settlementLedgerMode, normalized)) return;
            OnPropertyChanged(nameof(IsSingleSettlementMode));
            OnPropertyChanged(nameof(IsSeparateSettlementMode));
            UpdateOpeningBalanceFromSelectedLedgers();
        }
    }
    public bool IsSingleSettlementMode
    {
        get => SettlementLedgerMode.Equals("Single Ledger", StringComparison.OrdinalIgnoreCase);
        set { if (value) SettlementLedgerMode = "Single Ledger"; }
    }
    public bool IsSeparateSettlementMode
    {
        get => !IsSingleSettlementMode;
        set { if (value) SettlementLedgerMode = "Separate Ledgers"; }
    }

    public string BalanceType
    {
        get => _balanceType;
        set
        {
            var normalized = string.Equals(value, "Receivable", StringComparison.OrdinalIgnoreCase)
                ? "Receivable"
                : "Payable";
            if (!SetProperty(ref _balanceType, normalized)) return;
            OnPropertyChanged(nameof(IsPayableBalance));
            OnPropertyChanged(nameof(IsReceivableBalance));
            if (normalized.Equals("Receivable", StringComparison.OrdinalIgnoreCase))
            {
                // Fix82: normalize only the legacy generic defaults. Explicit custom
                // settlement names remain exactly as the user configured them.
                SingleSettlementLedger = GstMasterClassificationPolicy.NormalizeSingleSettlementLedger(
                    SingleSettlementLedger, receivable: true);
                IgstSettlementLedger = GstMasterClassificationPolicy.NormalizeTaxSettlementLedger(
                    "IGST", IgstSettlementLedger, receivable: true);
                CgstSettlementLedger = GstMasterClassificationPolicy.NormalizeTaxSettlementLedger(
                    "CGST", CgstSettlementLedger, receivable: true);
                SgstSettlementLedger = GstMasterClassificationPolicy.NormalizeTaxSettlementLedger(
                    "SGST", SgstSettlementLedger, receivable: true);
            }
            else
            {
                // This phase does not redesign the Payable/credit side. If the user
                // switches back after Fix82 auto-selected the standard Receivable names,
                // restore the pre-Fix82 generic defaults so Payable behavior stays exactly
                // on the existing path until the separate credit-ledger review.
                if (SingleSettlementLedger.Equals(GstMasterClassificationPolicy.SingleGstReceivableLedger, StringComparison.OrdinalIgnoreCase))
                    SingleSettlementLedger = "GST Payable / Receivable";
                if (IgstSettlementLedger.Equals(GstMasterClassificationPolicy.IgstReceivableLedger, StringComparison.OrdinalIgnoreCase))
                    IgstSettlementLedger = "IGST Payable / Receivable";
                if (CgstSettlementLedger.Equals(GstMasterClassificationPolicy.CgstReceivableLedger, StringComparison.OrdinalIgnoreCase))
                    CgstSettlementLedger = "CGST Payable / Receivable";
                if (SgstSettlementLedger.Equals(GstMasterClassificationPolicy.SgstReceivableLedger, StringComparison.OrdinalIgnoreCase))
                    SgstSettlementLedger = "SGST Payable / Receivable";
            }
        }
    }
    public bool IsPayableBalance
    {
        get => BalanceType.Equals("Payable", StringComparison.OrdinalIgnoreCase);
        set { if (value) BalanceType = "Payable"; }
    }
    public bool IsReceivableBalance
    {
        get => !IsPayableBalance;
        set { if (value) BalanceType = "Receivable"; }
    }

    public string SingleSettlementLedger
    {
        get => _singleSettlementLedger;
        set { if (SetProperty(ref _singleSettlementLedger, value)) UpdateOpeningBalanceFromSelectedLedgers(); }
    }
    public string IgstSettlementLedger
    {
        get => _igstSettlementLedger;
        set { if (SetProperty(ref _igstSettlementLedger, value)) UpdateOpeningBalanceFromSelectedLedgers(); }
    }
    public string CgstSettlementLedger
    {
        get => _cgstSettlementLedger;
        set { if (SetProperty(ref _cgstSettlementLedger, value)) UpdateOpeningBalanceFromSelectedLedgers(); }
    }
    public string SgstSettlementLedger
    {
        get => _sgstSettlementLedger;
        set { if (SetProperty(ref _sgstSettlementLedger, value)) UpdateOpeningBalanceFromSelectedLedgers(); }
    }
    public string TemporaryRcmIgstLedger { get => _temporaryRcmIgstLedger; private set => SetProperty(ref _temporaryRcmIgstLedger, value); }
    public string TemporaryRcmCgstLedger { get => _temporaryRcmCgstLedger; private set => SetProperty(ref _temporaryRcmCgstLedger, value); }
    public string TemporaryRcmSgstLedger { get => _temporaryRcmSgstLedger; private set => SetProperty(ref _temporaryRcmSgstLedger, value); }
    public string RcmExpenseIgstLedger { get => _rcmExpenseIgstLedger; private set => SetProperty(ref _rcmExpenseIgstLedger, value); }
    public string RcmExpenseCgstLedger { get => _rcmExpenseCgstLedger; private set => SetProperty(ref _rcmExpenseCgstLedger, value); }
    public string RcmExpenseSgstLedger { get => _rcmExpenseSgstLedger; private set => SetProperty(ref _rcmExpenseSgstLedger, value); }
    public decimal OpeningBooksBalance { get => _openingBooksBalance; private set => SetProperty(ref _openingBooksBalance, value); }
    public string OpeningBalanceStatus { get => _openingBalanceStatus; private set => SetProperty(ref _openingBalanceStatus, value); }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    public bool ReplaceSelectedVoucherLines
    {
        get => _replaceSelectedVoucherLines;
        set => SetProperty(ref _replaceSelectedVoucherLines, value);
    }

    public bool HasPastePreview => _pastePreview is not null;
    public string PastePreviewMode => _pastePreview?.Mode ?? "No preview loaded";
    public string PastePreviewSource => _pastePreview?.SourceName ?? string.Empty;
    public string PastePreviewSummary => _pastePreview is null
        ? "Preview a workbook or clipboard block before any data is saved."
        : $"{_pastePreview.VoucherCount:N0} voucher(s), {_pastePreview.LineCount:N0} line(s), " +
          $"Debit {_pastePreview.DebitTotal:N2}, Credit {_pastePreview.CreditTotal:N2}, " +
          $"{_pastePreview.Warnings.Count:N0} warning(s).";

    public int ProtectedVoucherCount => Vouchers.Count(voucher =>
        voucher.GenerationMode.Equals("Automatic", StringComparison.OrdinalIgnoreCase) && voucher.IsUserEdited);
    public int ManualVoucherCount => Vouchers.Count(voucher =>
        voucher.GenerationMode.Equals("Manual", StringComparison.OrdinalIgnoreCase));
    public decimal TotalNetTax => Vouchers.Sum(voucher => voucher.NetTax);
    public int DraftJournalCount => Vouchers.Count(v => v.WorkflowStatus.Equals("Draft", StringComparison.OrdinalIgnoreCase));
    public int ReadyJournalCount => Vouchers.Count(v => v.WorkflowStatus.Equals("Ready", StringComparison.OrdinalIgnoreCase));
    public int ApprovedJournalCount => Vouchers.Count(v => v.WorkflowStatus.Equals("Approved", StringComparison.OrdinalIgnoreCase));
    public int RecurringJournalCount => Vouchers.Count(v => v.RecurringProfileId.HasValue);
    public int ReversedJournalCount => Vouchers.Count(v => v.ReversalOfId.HasValue);
    public decimal SelectedDebitTotal => Lines
        .Where(line => line.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase))
        .Sum(line => Math.Abs(line.Amount));
    public decimal SelectedCreditTotal => Lines
        .Where(line => line.DrCr.Equals("Cr", StringComparison.OrdinalIgnoreCase))
        .Sum(line => Math.Abs(line.Amount));
    public decimal SelectedDifference => SelectedDebitTotal - SelectedCreditTotal;
    public bool IsSelectedVoucherBalanced => Lines.Count > 0 && Math.Abs(SelectedDifference) <= BalanceTolerance;
    public string BalanceStatusText => Lines.Count == 0
        ? "No journal lines"
        : IsSelectedVoucherBalanced
            ? "Balanced"
            : $"Difference {SelectedDifference:N2}";

    private long CurrentClientId => _clientContext.CurrentClient?.Id ?? 0;
    public long RecurringClientId => CurrentClientId;
    public string RecurringFinancialYear => _clientContext.CurrentClient?.FinancialYear ?? string.Empty;

    public void PrepareForDisplay()
    {
        _isPageActive = true;
        var client = _clientContext.CurrentClient;
        if (client is not null && (_preparedClientId != client.Id ||
            !_preparedFinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase)))
            StatusText = "Journal stream is preparing in the background…";
    }

    public void WarmFromPreparedSnapshot(ClientWorkspaceSnapshot snapshot)
    {
        var client = _clientContext.CurrentClient;
        if (client is null || client.Id != snapshot.ClientId ||
            !client.FinancialYear.Equals(snapshot.FinancialYear, StringComparison.OrdinalIgnoreCase)) return;

        var selectedId = SelectedVoucher?.PersistentId ?? 0;
        Vouchers.ReplaceRange(snapshot.Journals);
        Lines.Clear();
        SummaryRows.Clear();
        SelectedVoucher = selectedId > 0
            ? Vouchers.FirstOrDefault(item => item.PersistentId == selectedId) ?? Vouchers.FirstOrDefault()
            : Vouchers.FirstOrDefault();

        if (snapshot.JournalSettings is not null)
        {
            FilingFrequency = string.IsNullOrWhiteSpace(snapshot.JournalSettings.FilingFrequency)
                ? "Monthly"
                : snapshot.JournalSettings.FilingFrequency;
            ApplyFrequencyDetection(string.Empty);
            LoadSettings(snapshot.JournalSettings);
        }

        BuildSummaryRows(snapshot.GstReconciliation);
        RefreshLedgerMappingsFromPreparedSnapshot(snapshot);
        BuildReconciliationIssues();
        _preparedClientId = client.Id;
        _preparedFinancialYear = client.FinancialYear;
        StatusText = $"Journal ready • {Vouchers.Count:N0} voucher(s) prepared in its own cache stream.";
        NotifySummary();
    }

    public void WarmMasterDependenciesFromPreparedSnapshot(ClientWorkspaceSnapshot snapshot)
    {
        var client = _clientContext.CurrentClient;
        if (client is null || client.Id != snapshot.ClientId ||
            !client.FinancialYear.Equals(snapshot.FinancialYear, StringComparison.OrdinalIgnoreCase)) return;
        RefreshLedgerMappingsFromPreparedSnapshot(snapshot);
        BuildReconciliationIssues();
    }


    public void WarmGstDependenciesFromPreparedSnapshot(ClientWorkspaceSnapshot snapshot)
    {
        var client = _clientContext.CurrentClient;
        if (client is null || client.Id != snapshot.ClientId ||
            !client.FinancialYear.Equals(snapshot.FinancialYear, StringComparison.OrdinalIgnoreCase)) return;
        BuildSummaryRows(snapshot.GstReconciliation);
        NotifySummary();
    }

    private void RefreshLedgerMappingsFromPreparedSnapshot(ClientWorkspaceSnapshot snapshot)
    {
        LedgerMappings.Clear();
        MasterGroupOptions.Clear();
        MasterLedgerOptions.Clear();
        _ledgerOpeningBalances.Clear();

        var masters = snapshot.Masters;
        var ledgerMasters = masters
            .Where(record => record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !record.IsArchived)
            .OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ledgerNames = ledgerMasters
            .Select(ledger => ledger.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        MasterLedgerOptions.ReplaceRange(ledgerNames);
        foreach (var ledger in ledgerMasters) _ledgerOpeningBalances[ledger.Name] = ledger.OpeningBalance;
        UpdateOpeningBalanceFromSelectedLedgers();

        var groups = masters
            .Where(record => record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) && !record.IsArchived)
            .Select(record => record.Name)
            .Concat(MasterSynchronizationService.TallyPrebuiltLedgerGroupNames)
            .Append("Current Liabilities")
            .Append("Duties & Taxes")
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        MasterGroupOptions.ReplaceRange(groups);

        var knownLedgers = ledgerNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preparedMappings = snapshot.JournalLedgerMappings
            .Where(mapping => !knownLedgers.Contains(mapping.LedgerName))
            .ToList();

        if (!CanEditJournal)
        {
            var persistedNames = snapshot.JournalLedgerMappings.Select(x => x.LedgerName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            preparedMappings.AddRange(Vouchers
                .Where(voucher => voucher.GenerationMode.Equals("Manual", StringComparison.OrdinalIgnoreCase))
                .SelectMany(voucher => voucher.Lines)
                .Select(line => line.LedgerName.Trim())
                .Where(name => name.Length > 0 && !knownLedgers.Contains(name) && !persistedNames.Contains(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(ledgerName => new JournalLedgerMapping
                {
                    ClientId = snapshot.ClientId,
                    FinancialYear = snapshot.FinancialYear,
                    LedgerName = ledgerName,
                    Status = "Review only",
                    Source = "Journal cached preview",
                    UpdatedUtc = DateTime.UtcNow
                }));
        }
        LedgerMappings.ReplaceRange(preparedMappings);

        SelectedLedgerMapping = LedgerMappings.FirstOrDefault();
        QueueLedgerTotalsRefresh(invalidate: false);
    }

    public void ReleaseForNavigation()
    {
        _isPageActive = false;
        // Completion 59: retain the Journal visual/data snapshot between modules.
        // Invalidate only late per-ledger continuations; do not clear the user-visible cache.
        _ledgerTotalRefreshVersion++;
    }

    public void Refresh()
    {
        var selectedId = SelectedVoucher?.PersistentId ?? 0;
        var selectedNumber = SelectedVoucher?.VoucherNumber ?? string.Empty;
        Vouchers.Clear();
        Lines.Clear();
        SummaryRows.Clear();
        LedgerMappings.Clear();
        MasterGroupOptions.Clear();
        MasterLedgerOptions.Clear();
        LedgerPickerOptions.Clear();
        SelectedJournalLedgerTotal = null;
        SelectedJournalLedgerTotalStatus = "Select a Journal line to view its Ledger total.";
        _ledgerOpeningBalances.Clear();
        ReconciliationIssues.Clear();

        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            _preparedClientId = 0;
            _preparedFinancialYear = string.Empty;
            SelectedVoucher = null;
            StatusText = "Open a client to use the GST Journal.";
            ApplyFrequencyDetection(string.Empty);
            NotifySummary();
            return;
        }

        _ledgerTotalService.Invalidate(client.Id, client.FinancialYear);
        var settings = _repository.GetSettings(client.Id, client.FinancialYear);
        var detectedFrequency = _refreshCoordinator.DetectFilingFrequency(client.Id, client.FinancialYear);
        if (detectedFrequency.Length > 0 &&
            !settings.FilingFrequency.Equals(detectedFrequency, StringComparison.OrdinalIgnoreCase))
        {
            settings.FilingFrequency = detectedFrequency;
            _repository.SaveSettings(settings);
        }

        FilingFrequency = detectedFrequency.Length > 0 ? detectedFrequency : settings.FilingFrequency;
        ApplyFrequencyDetection(detectedFrequency);
        LoadSettings(settings);

        foreach (var voucher in _repository.GetVouchers(client.Id, client.FinancialYear))
        {
            Vouchers.Add(voucher);
        }

        SelectedVoucher = selectedId > 0
            ? Vouchers.FirstOrDefault(item => item.PersistentId == selectedId)
            : Vouchers.FirstOrDefault(item =>
                  item.VoucherNumber.Equals(selectedNumber, StringComparison.OrdinalIgnoreCase))
              ?? Vouchers.FirstOrDefault();

        BuildSummaryRows();
        RefreshLedgerMappings();
        BuildReconciliationIssues();

        StatusText =
            $"{Vouchers.Count:N0} {FilingFrequency.ToLowerInvariant()} voucher(s) loaded: " +
            $"{ManualVoucherCount:N0} manual adjustment(s), {ProtectedVoucherCount:N0} edited automatic voucher(s). " +
            "Manual adjustments coexist with refreshed automatic vouchers. " + FilingFrequencySource + ".";
        _preparedClientId = client.Id;
        _preparedFinancialYear = client.FinancialYear;
        NotifySummary();
    }

    public bool OpenSummaryPeriod(string periodKey)
    {
        var normalizedPeriod = periodKey?.Trim() ?? string.Empty;
        if (normalizedPeriod.Length == 0)
        {
            return false;
        }

        var target = Vouchers
            .Where(voucher => voucher.PeriodKey.Equals(normalizedPeriod, StringComparison.OrdinalIgnoreCase))
            .OrderBy(voucher => voucher.GenerationMode.Equals("Automatic", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(voucher => voucher.PersistentId > 0 ? 0 : 1)
            .ThenBy(voucher => voucher.VoucherDate)
            .ThenBy(voucher => voucher.VoucherNumber, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (target is null)
        {
            StatusText = $"No Journal voucher is available for period {normalizedPeriod}.";
            return false;
        }

        SelectedVoucher = target;
        StatusText = $"Opened {target.PeriodLabel} • {target.VoucherNumber}.";
        return true;
    }

    public void SaveSettings()
    {
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            return;
        }

        var detected = _refreshCoordinator.DetectFilingFrequency(client.Id, client.FinancialYear);
        if (detected.Length > 0)
        {
            FilingFrequency = detected;
            ApplyFrequencyDetection(detected);
        }

        try
        {
            ApplyRcmRenameRowsToProperties();
            _repository.SaveSettings(CreateSettings(client.Id, client.FinancialYear));
            StatusText = IsFilingFrequencyDetected
                ? $"Journal settings saved. Filing frequency remains {FilingFrequency} from imported GSTR-3B."
                : "GST Journal settings saved.";
        }
        catch (Exception exception)
        {
            StatusText = $"Journal settings were not saved: {exception.Message}";
        }
    }

    private async Task GenerateAsync()
    {
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            SaveSettings();
            StatusText = "Synchronising Masters, rebuilding workbook-parity Journal and refreshing GST reconciliation…";
            StatusText = await _refreshCoordinator.RefreshAsync(client);
            Refresh();
        }
        catch (Exception exception)
        {
            StatusText = $"GST Journal generation failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void AddManual()
    {
        if (!DemandPermission(GovernancePermissions.JournalCreate, "create Journal entries")) return;
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            return;
        }

        var voucherDate = DefaultManualVoucherDate(client.FinancialYear);
        var periodKey = GstJournalGenerationService.PeriodKey(voucherDate, FilingFrequency);
        var voucher = new JournalVoucher
        {
            ClientId = client.Id,
            FinancialYear = client.FinancialYear,
            PeriodKey = periodKey,
            PeriodLabel = GstJournalGenerationService.PeriodLabel(periodKey, FilingFrequency),
            VoucherDate = voucherDate,
            VoucherNumber = $"MANUAL-{periodKey}-{DateTime.Now:HHmmss}",
            VoucherType = "Journal",
            Narration = "Manual GST adjustment",
            GenerationMode = "Manual",
            IsUserEdited = true,
            Lines = [CreateBlankLine(1)]
        };

        Vouchers.Insert(0, voucher);
        SelectedVoucher = voucher;
        SelectedLine = Lines.FirstOrDefault();
        StatusText = "Manual voucher created. It will coexist with the automatic voucher for the same period.";
    }

    public void AddLine()
    {
        if (SelectedVoucher is null)
        {
            return;
        }
        var line = CreateBlankLine(Lines.Count + 1);
        Lines.Add(line);
        SelectedLine = line;
        StatusText = "Blank journal line added.";
    }

    private void PreviewManualExcel()
    {
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Preview Manual Journals from Excel or CSV",
            Filter = "Journal files (*.xlsx;*.xlsm;*.csv)|*.xlsx;*.xlsm;*.csv|Excel files (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|CSV files (*.csv)|*.csv",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var result = _bulkImportService.ParseFile(
                dialog.FileName,
                client.Id,
                client.FinancialYear,
                FilingFrequency);
            SetVoucherPreview(result, System.IO.Path.GetFileName(dialog.FileName));
        }
        catch (Exception exception)
        {
            StatusText = $"Manual-journal preview failed: {exception.Message}";
        }
    }

    public void PreviewSmartClipboard()
    {
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            return;
        }

        try
        {
            var text = GetClipboardText();
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusText = "Copy journal rows in Excel first, then preview the clipboard.";
                return;
            }

            if (_bulkImportService.ClipboardContainsVoucherMetadata(text))
            {
                SetVoucherPreview(
                    _bulkImportService.ParseClipboard(
                        text,
                        client.Id,
                        client.FinancialYear,
                        FilingFrequency),
                    "Excel clipboard");
            }
            else
            {
                SetLinePreview(_bulkImportService.ParseClipboardLines(text), "Excel clipboard");
            }
        }
        catch (Exception exception)
        {
            StatusText = $"Clipboard preview failed: {exception.Message}";
        }
    }

    public void PreviewLineClipboard()
    {
        try
        {
            var text = GetClipboardText();
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusText = "Copy Ledger with Debit/Credit, or Ledger with Amount and Dr/Cr, then preview lines.";
                return;
            }
            SetLinePreview(_bulkImportService.ParseClipboardLines(text), "Excel clipboard");
        }
        catch (Exception exception)
        {
            StatusText = $"Journal-line preview failed: {exception.Message}";
        }
    }

    private void SetVoucherPreview(ManualJournalBulkImportResult result, string sourceName)
    {
        _pastePreview = new JournalPastePreview
        {
            SourceName = sourceName,
            Mode = "Full Vouchers",
            Vouchers = result.Vouchers.Select(CloneVoucher).ToList(),
            Warnings = result.Warnings.ToList()
        };
        RefreshPreviewCollections();
        StatusText = "Preview loaded. No voucher has been saved. Review the Preview tab and press Apply Preview.";
    }

    private void SetLinePreview(ManualJournalLinePasteResult result, string sourceName)
    {
        _pastePreview = new JournalPastePreview
        {
            SourceName = sourceName,
            Mode = "Selected Voucher Lines",
            Lines = result.Lines.Select(CloneLine).ToList(),
            Warnings = result.Warnings.ToList()
        };
        RefreshPreviewCollections();
        StatusText = "Line preview loaded. Choose append or replace, then press Apply Preview.";
    }

    private void RefreshPreviewCollections()
    {
        PastePreviewVouchers.Clear();
        PastePreviewLines.Clear();
        PasteWarnings.Clear();

        if (_pastePreview is not null)
        {
            foreach (var voucher in _pastePreview.Vouchers)
            {
                PastePreviewVouchers.Add(voucher);
            }
            foreach (var line in _pastePreview.Lines)
            {
                PastePreviewLines.Add(line);
            }
            foreach (var warning in _pastePreview.Warnings)
            {
                PasteWarnings.Add(warning);
            }
        }

        OnPropertyChanged(nameof(HasPastePreview));
        OnPropertyChanged(nameof(PastePreviewMode));
        OnPropertyChanged(nameof(PastePreviewSource));
        OnPropertyChanged(nameof(PastePreviewSummary));
        ApplyPastePreviewCommand.RaiseCanExecuteChanged();
        ClearPastePreviewCommand.RaiseCanExecuteChanged();
    }

    private void ApplyPastePreview()
    {
        if (_pastePreview is null)
        {
            return;
        }

        if (_pastePreview.Vouchers.Count > 0)
        {
            try
            {
                SaveBulkVouchers(new ManualJournalBulkImportResult(
                    _pastePreview.Vouchers.Select(CloneVoucher).ToArray(),
                    _pastePreview.Warnings));
                ClearPastePreview(false);
            }
            catch (Exception exception)
            {
                StatusText = $"Paste preview was not applied: {exception.Message}";
            }
            return;
        }

        if (_pastePreview.Lines.Count == 0)
        {
            StatusText = "The preview has no valid journal lines.";
            return;
        }

        if (SelectedVoucher is null)
        {
            AddManual();
        }
        if (SelectedVoucher is null)
        {
            return;
        }

        if (ReplaceSelectedVoucherLines)
        {
            Lines.Clear();
        }
        else
        {
            foreach (var placeholder in Lines
                         .Where(line => string.IsNullOrWhiteSpace(line.LedgerName) && line.Amount == 0m)
                         .ToArray())
            {
                Lines.Remove(placeholder);
            }
        }

        foreach (var source in _pastePreview.Lines)
        {
            var line = CloneLine(source);
            line.LineNumber = Lines.Count + 1;
            Lines.Add(line);
        }
        RenumberLines();
        SelectedLine = Lines.LastOrDefault();
        var appliedCount = _pastePreview.Lines.Count;
        ClearPastePreview(false);
        StatusText =
            $"Applied {appliedCount:N0} previewed line(s) to {SelectedVoucher.VoucherNumber}. " +
            $"{BalanceStatusText}. Review and press Save Voucher.";
    }

    private void ClearPastePreview() => ClearPastePreview(true);

    private void ClearPastePreview(bool updateStatus)
    {
        _pastePreview = null;
        RefreshPreviewCollections();
        if (updateStatus)
        {
            StatusText = "Paste preview cleared. No journal data was changed.";
        }
    }

    private void ExportExcelFormat()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        try
        {
            var folder = ResolveJournalFormatFolder(client.Id, client.FinancialYear);
            Directory.CreateDirectory(folder);
            var clientName = SafeFormatFilePart(
                string.IsNullOrWhiteSpace(client.ClientName) ? client.ClientCode : client.ClientName);
            var path = Path.Combine(folder, $"{clientName}.xlsx");

            JournalExcelTemplateService.Write(path, client);
            StatusText =
                $"Journal Excel import format created automatically: {path}. " +
                "Fill the rows and save the workbook; Auto Import recognizes the AAH Journal format directly and a blank template is not Action Required.";
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            StatusText = $"Journal Excel import format could not be created: {exception.Message}";
            MessageBox.Show(exception.Message, "Journal Excel Format", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string ResolveJournalFormatFolder(long clientId, string financialYear)
    {
        var linked = _automationService?.GetFolders()
            .Where(item => item.ClientId == clientId &&
                           item.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase) &&
                           Directory.Exists(item.FolderPath))
            .OrderBy(item => item.FolderPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var root = linked?.FolderPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Accounting Automation Hub",
            "Import Formats");
        return Path.Combine(root, "AAH Formats", "Journal");
    }

    private static string SafeFormatFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = (value ?? string.Empty).Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var result = new string(chars).Trim();
        return result.Length == 0 ? "Client" : result;
    }

    private void CopyPasteTemplate()
    {
        Clipboard.SetText(_bulkImportService.PasteTemplate, TextDataFormat.UnicodeText);
        StatusText = "Full-voucher template copied: Voucher No, Date, Ledger, Amount, Dr/Cr, Narration, Tax Head, Role and Voucher Type.";
    }

    private void CopyLineTemplate()
    {
        Clipboard.SetText(_bulkImportService.LinePasteTemplate, TextDataFormat.UnicodeText);
        StatusText = "Selected-voucher template copied: Ledger, Debit, Credit, Tax Head and Role.";
    }

    public void SaveVoucher()
    {
        if (!DemandPermission(GovernancePermissions.JournalCreate, "save Journal entries")) return;
        var voucher = SelectedVoucher;
        var client = _clientContext.CurrentClient;
        if (voucher is null || client is null)
        {
            return;
        }

        var masterLedgerGroups = _masterRepository.Get(client.Id, client.FinancialYear, false, string.Empty)
            .Where(item => item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !item.IsArchived && !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => AccountingText.DuplicateKey(item.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var cleanLines = Lines
            .Where(line => !string.IsNullOrWhiteSpace(line.LedgerName) && Math.Abs(line.Amount) > 0m)
            .Select(CloneLine)
            .ToList();
        foreach (var line in cleanLines)
        {
            var key = AccountingText.DuplicateKey(line.LedgerName);
            if (masterLedgerGroups.TryGetValue(key, out var matches))
            {
                if (matches.Length > 1)
                {
                    StatusText = $"Voucher not saved: '{line.LedgerName}' resolves to multiple duplicate Ledger Masters. Resolve Master duplicates first.";
                    return;
                }
                line.LedgerName = matches[0].Name.Trim();
            }
            else
            {
                line.LedgerName = AccountingText.DisplayName(line.LedgerName);
            }
        }
        if (cleanLines.Count < 2)
        {
            StatusText = "A journal voucher requires at least two non-zero ledger lines.";
            return;
        }

        var debit = cleanLines
            .Where(line => line.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase))
            .Sum(line => Math.Abs(line.Amount));
        var credit = cleanLines
            .Where(line => line.DrCr.Equals("Cr", StringComparison.OrdinalIgnoreCase))
            .Sum(line => Math.Abs(line.Amount));
        if (Math.Abs(debit - credit) > BalanceTolerance)
        {
            StatusText = $"Voucher not saved: Debit {debit:N2} and Credit {credit:N2} differ by {debit - credit:N2}.";
            return;
        }

        voucher.Lines = cleanLines;
        for (var index = 0; index < voucher.Lines.Count; index++)
        {
            voucher.Lines[index].LineNumber = index + 1;
        }

        voucher.ClientId = client.Id;
        voucher.FinancialYear = client.FinancialYear;
        voucher.PeriodKey = GstJournalGenerationService.PeriodKey(voucher.VoucherDate, FilingFrequency);
        voucher.PeriodLabel = GstJournalGenerationService.PeriodLabel(voucher.PeriodKey, FilingFrequency);
        if (string.IsNullOrWhiteSpace(voucher.VoucherNumber))
        {
            voucher.VoucherNumber = $"MANUAL-{voucher.VoucherDate:yyyyMMdd}-{DateTime.Now:HHmmss}";
        }
        var wasAutomatic = voucher.GenerationMode.Equals("Automatic", StringComparison.OrdinalIgnoreCase);
        RecalculateTaxTotals(voucher, preserveRcmLiability: wasAutomatic);
        voucher.IsUserEdited = true;
        voucher.CreatedBy = string.IsNullOrWhiteSpace(voucher.CreatedBy) ? Environment.UserName : voucher.CreatedBy;
        if (!voucher.WorkflowStatus.Equals("Approved", StringComparison.OrdinalIgnoreCase))
            voucher.WorkflowStatus = "Ready";
        try
        {
            _repository.SaveVoucher(voucher);
        }
        catch (Exception exception)
        {
            StatusText = $"Voucher was not saved: {exception.Message}";
            return;
        }
        var savedId = voucher.PersistentId;
        Refresh();
        SelectedVoucher = Vouchers.FirstOrDefault(item => item.PersistentId == savedId)
                          ?? Vouchers.FirstOrDefault(item =>
                              item.VoucherNumber.Equals(voucher.VoucherNumber, StringComparison.OrdinalIgnoreCase));
        StatusText = wasAutomatic
            ? $"Edited automatic voucher {voucher.VoucherNumber} saved and protected from regeneration."
            : $"Manual voucher {voucher.VoucherNumber} saved. The automatic period voucher remains independent.";
        _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Journal, "Manual journal voucher saved");
        _ = RefreshAccountingAfterManualJournalAsync(client);
    }

    public void CreateJournalCloneSeries(IReadOnlyList<CloneScheduleEntry> schedule)
    {
        var source = SelectedVoucher;
        if (source is null || source.PersistentId <= 0 || schedule is null || schedule.Count == 0) return;
        try
        {
            var created = _transactionAutomationService.CloneJournalSeries(
                source,
                schedule.Select(item => (item.Date, item.Amount)).ToArray());
            Refresh();
            var last = created.LastOrDefault();
            if (last is not null) SelectedVoucher = Vouchers.FirstOrDefault(v => v.PersistentId == last.PersistentId);
            StatusText = $"Created {created.Count:N0} Journal clone(s) from {source.VoucherNumber}. All generated vouchers are Draft.";
            NotifySummary();
        }
        catch (Exception ex) { StatusText = $"Advanced Journal clone failed: {ex.Message}"; }
    }

    private void CloneSelectedJournal()
    {
        var source = SelectedVoucher;
        if (source is null || source.PersistentId <= 0) return;
        try
        {
            var clone = _transactionAutomationService.CloneJournal(source, source.VoucherDate);
            Refresh();
            SelectedVoucher = Vouchers.FirstOrDefault(v => v.PersistentId == clone.PersistentId);
            StatusText = $"Cloned Journal {source.VoucherNumber} as {clone.VoucherNumber}. The clone is a Draft.";
        }
        catch (Exception ex) { StatusText = $"Journal clone failed: {ex.Message}"; }
    }

    private void ReverseSelectedJournal()
    {
        var source = SelectedVoucher;
        if (source is null || source.PersistentId <= 0) return;
        var date = DateTime.Today;
        if (!IsInsideCurrentFinancialYear(date)) date = source.VoucherDate;
        try
        {
            var reversal = _transactionAutomationService.ReverseJournal(source, date);
            Refresh();
            SelectedVoucher = Vouchers.FirstOrDefault(v => v.PersistentId == reversal.PersistentId);
            StatusText = $"Reversing Journal {reversal.VoucherNumber} created. The original voucher was retained.";
        }
        catch (Exception ex) { StatusText = $"Journal reversal failed: {ex.Message}"; }
    }

    public void CreateJournalRecurringSchedule(string profileName, string frequency, DateTime startDate, DateTime? endDate)
    {
        var source = SelectedVoucher;
        if (source is null || source.PersistentId <= 0) return;
        try
        {
            var profile = _transactionAutomationService.CreateJournalRecurringProfile(source, profileName, frequency, startDate, endDate);
            StatusText = $"Recurring Journal profile '{profile.ProfileName}' created. Next run: {profile.NextRunDate:dd-MMM-yyyy}.";
            NotifySummary();
        }
        catch (Exception ex) { StatusText = $"Recurring Journal setup failed: {ex.Message}"; }
    }

    private void MakeSelectedJournalRecurring()
    {
        var source = SelectedVoucher;
        if (source is null || source.PersistentId <= 0) return;
        var start = source.VoucherDate.AddMonths(1);
        try
        {
            var profile = _transactionAutomationService.CreateJournalRecurringProfile(
                source, $"{source.VoucherNumber} recurring", "Monthly", start, null);
            StatusText = $"Recurring Journal profile '{profile.ProfileName}' created. Next run: {profile.NextRunDate:dd-MMM-yyyy}.";
            NotifySummary();
        }
        catch (Exception ex) { StatusText = $"Recurring Journal setup failed: {ex.Message}"; }
    }

    private void ApproveSelectedJournal()
    {
        if (!CanApproveJournal)
        {
            var denied = DesktopPermissionService.DeniedMessage(_governanceRepository, "approve Journal entries", accountingMutation: true);
            StatusText = denied;
            MessageBox.Show(denied, "Permissions", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var source = SelectedVoucher;
        if (source is null || source.PersistentId <= 0) return;
        if (!IsSelectedVoucherBalanced)
        {
            StatusText = "Journal must be balanced before approval.";
            return;
        }
        try
        {
            _transactionAutomationService.ApproveJournal(source);
            Refresh();
            SelectedVoucher = Vouchers.FirstOrDefault(v => v.PersistentId == source.PersistentId);
            StatusText = $"Journal {source.VoucherNumber} approved by {Environment.UserName}.";
        }
        catch (Exception ex) { StatusText = $"Journal approval failed: {ex.Message}"; }
    }

    private bool DemandPermission(string permission, string action)
    {
        if (DesktopPermissionService.CanMutateAccounting(_governanceRepository, permission)) return true;
        var message = DesktopPermissionService.DeniedMessage(_governanceRepository, action, accountingMutation: true);
        StatusText = message;
        MessageBox.Show(message, "Permissions", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private bool IsInsideCurrentFinancialYear(DateTime date)
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return false;
        var first = client.FinancialYear.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return !int.TryParse(first, out var year) || (date.Date >= new DateTime(year, 4, 1) && date.Date <= new DateTime(year + 1, 3, 31));
    }

    public void DeleteSelectedVoucher()
    {
        if (!DemandPermission(GovernancePermissions.DeleteAccounting, "delete Journal entries")) return;
        var voucher = SelectedVoucher;
        if (voucher is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"Delete voucher '{voucher.VoucherNumber}' and all its lines?",
            "Delete Journal Voucher",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        if (voucher.PersistentId > 0)
        {
            try
            {
                _repository.DeleteVoucher(voucher.PersistentId);
            }
            catch (Exception exception)
            {
                StatusText = $"Voucher was not deleted: {exception.Message}";
                return;
            }
        }
        Vouchers.Remove(voucher);
        SelectedVoucher = Vouchers.FirstOrDefault();
        BuildSummaryRows();
        RefreshLedgerMappings();
        BuildReconciliationIssues();
        StatusText = $"Voucher {voucher.VoucherNumber} deleted.";
        NotifySummary();
        var client = _clientContext.CurrentClient;
        if (client is not null)
        {
            _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Journal, "Manual journal voucher deleted");
            _ = RefreshAccountingAfterManualJournalAsync(client);
        }
    }

    public void DeleteSelectedLine()
    {
        var line = SelectedLine;
        if (line is null)
        {
            return;
        }
        Lines.Remove(line);
        RenumberLines();
        SelectedLine = Lines.FirstOrDefault();
        StatusText = "Journal line removed. Review the balance and press Save Voucher to persist the change.";
    }

    private void SaveBulkVouchers(ManualJournalBulkImportResult result)
    {
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            return;
        }

        var existing = _repository.GetVouchers(client.Id, client.FinancialYear);
        foreach (var voucher in result.Vouchers)
        {
            var match = existing.FirstOrDefault(item =>
                item.VoucherNumber.Equals(voucher.VoucherNumber, StringComparison.OrdinalIgnoreCase) &&
                item.VoucherDate.Date == voucher.VoucherDate.Date &&
                item.GenerationMode.Equals("Manual", StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                voucher.PersistentId = match.PersistentId;
                voucher.CreatedUtc = match.CreatedUtc;
            }

            NormalizeVoucherLedgerNames(client.Id, client.FinancialYear, voucher);
            RecalculateTaxTotals(voucher);
            try
            {
                _repository.SaveVoucher(voucher);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Voucher {voucher.VoucherNumber} could not be saved: {exception.Message}",
                    exception);
            }
        }

        Refresh();
        StatusText =
            $"Applied and saved {result.Vouchers.Count:N0} balanced manual voucher(s). " +
            WarningSummary(result.Warnings);
        if (_automationService is not null)
        {
            _automationService.PublishCommittedAccountingImport(
                client.Id,
                client.FinancialYear,
                "Journal",
                result.Vouchers.Count,
                _pastePreview?.SourceName ?? string.Empty,
                "Manual Import");
        }
        else
        {
            _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Journal, "Manual journal bulk import saved");
        }
        _ = RefreshAccountingAfterManualJournalAsync(client);
    }

    private void BuildSummaryRows(IReadOnlyList<AccountingAutomationHub.Domain.Gst.GstReconciliationRow>? preparedReconciliation = null)
    {
        var client = _clientContext.CurrentClient;
        var reconciliationRows = preparedReconciliation ?? (client is null
            ? Array.Empty<AccountingAutomationHub.Domain.Gst.GstReconciliationRow>()
            : _gstRepository.GetReconciliation(client.Id, client.FinancialYear));
        var reconciliationByPeriod = reconciliationRows
            .GroupBy(row => row.PeriodKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var summary = new List<JournalSummaryRow>();
        var runningBalance = OpeningBooksBalance;
        foreach (var group in Vouchers
                     .GroupBy(voucher => voucher.PeriodKey, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var output = group.Sum(voucher =>
                voucher.OutputIgst + voucher.OutputCgst + voucher.OutputSgst);
            var input = group.Sum(voucher =>
                voucher.InputIgst + voucher.InputCgst + voucher.InputSgst);
            var rcm = group.Sum(voucher =>
                voucher.RcmIgst + voucher.RcmCgst + voucher.RcmSgst);
            var rcmClaimed = group.Sum(voucher =>
                voucher.RcmClaimIgst + voucher.RcmClaimCgst + voucher.RcmClaimSgst);
            var rcmReversed = group.Sum(voucher =>
                voucher.RcmReverseIgst + voucher.RcmReverseCgst + voucher.RcmReverseSgst);
            var net = input - output;
            runningBalance += net;
            reconciliationByPeriod.TryGetValue(group.Key, out var evidence);
            summary.Add(new JournalSummaryRow
            {
                PeriodKey = group.Key,
                Particulars = group.First().PeriodLabel,
                OutputGst = output,
                InputGst = input,
                RcmGst = rcm,
                RcmClaimed = rcmClaimed,
                RcmReversed = rcmReversed,
                NetGst = net,
                ClosingBalance = runningBalance,
                // The Excel Journal summary keeps this column at zero. Detailed
                // portal/ledger differences remain in the GST Reconciliation tab.
                Difference = 0m,
                EvidenceStatus = evidence is null
                    ? "GST evidence not refreshed; Bank GST-payment integration pending"
                    : $"GST tab: {evidence.Status}; Bank GST-payment integration pending"
            });
        }
        SummaryRows.ReplaceRange(summary);
    }

    private void RefreshLedgerMappings()
    {
        LedgerMappings.Clear();
        MasterGroupOptions.Clear();
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            return;
        }

        var masters = _masterRepository.Get(client.Id, client.FinancialYear, false);
        _ledgerOpeningBalances.Clear();
        var ledgerMasters = masters
            .Where(record => record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
            .OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ledgerNames = ledgerMasters
            .Select(ledger => ledger.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        MasterLedgerOptions.ReplaceRange(ledgerNames);
        foreach (var ledger in ledgerMasters)
            _ledgerOpeningBalances[ledger.Name] = ledger.OpeningBalance;
        var knownLedgers = ledgerMasters
            .Select(record => record.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        UpdateOpeningBalanceFromSelectedLedgers();
        var groups = masters
            .Where(record => record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
            .Select(record => record.Name)
            .Append("Current Liabilities")
            .Append("Duties & Taxes")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        MasterGroupOptions.ReplaceRange(groups);

        var unknownManualLedgers = Vouchers
            .Where(voucher => voucher.GenerationMode.Equals("Manual", StringComparison.OrdinalIgnoreCase))
            .SelectMany(voucher => voucher.Lines)
            .Select(line => line.LedgerName.Trim())
            .Where(name => name.Length > 0 && !knownLedgers.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (CanEditJournal)
            _repository.UpsertPendingLedgerMappings(client.Id, client.FinancialYear, unknownManualLedgers);

        var persistedMappings = _repository.GetLedgerMappings(client.Id, client.FinancialYear);
        foreach (var mapping in persistedMappings)
        {
            if (knownLedgers.Contains(mapping.LedgerName))
            {
                if (CanEditJournal)
                {
                    var parent = masters.FirstOrDefault(record =>
                        record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                        record.Name.Equals(mapping.LedgerName, StringComparison.OrdinalIgnoreCase))?.Parent ?? mapping.MasterGroup;
                    _repository.ResolveLedgerMapping(mapping.PersistentId, parent);
                }
                continue;
            }
            LedgerMappings.Add(mapping);
        }

        if (!CanEditJournal)
        {
            var persistedNames = persistedMappings.Select(x => x.LedgerName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var ledgerName in unknownManualLedgers.Where(name => !persistedNames.Contains(name)))
            {
                LedgerMappings.Add(new JournalLedgerMapping
                {
                    ClientId = client.Id,
                    FinancialYear = client.FinancialYear,
                    LedgerName = ledgerName,
                    Status = "Review only",
                    Source = "Journal (read-only preview)",
                    UpdatedUtc = DateTime.UtcNow
                });
            }
        }
        SelectedLedgerMapping = LedgerMappings.FirstOrDefault();
        QueueLedgerTotalsRefresh(invalidate: false);
    }

    private void ApplySelectedLedgerMapping()
    {
        if (!CanEditJournal)
        {
            StatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, "apply Journal Ledger Mapping", accountingMutation: true);
            return;
        }

        var client = _clientContext.CurrentClient;
        var mapping = SelectedLedgerMapping;
        if (client is null || mapping is null || string.IsNullOrWhiteSpace(mapping.MasterGroup))
        {
            StatusText = "Choose a Master Group before applying the ledger mapping.";
            return;
        }

        var company = !string.IsNullOrWhiteSpace(client.LegalName)
            ? client.LegalName
            : !string.IsNullOrWhiteSpace(client.TradeName)
                ? client.TradeName
                : client.ClientName;
        try
        {
            _masterRepository.Upsert(
                client.Id,
                client.FinancialYear,
                [
                    new MasterRecord
                    {
                        ClientId = client.Id,
                        FinancialYear = client.FinancialYear,
                        Name = mapping.LedgerName,
                        Parent = mapping.MasterGroup,
                        Type = "Ledger",
                        Company = company,
                        Source = "Journal Ledger Mapping",
                        IsUserEdited = true
                    }
                ],
                true);
            _repository.ResolveLedgerMapping(mapping.PersistentId, mapping.MasterGroup);
            _ledgerTotalService.Invalidate(client.Id, client.FinancialYear);
            StatusText = $"Ledger '{mapping.LedgerName}' added to Master under '{mapping.MasterGroup}'.";
            RefreshLedgerMappings();
        }
        catch (Exception exception)
        {
            StatusText = $"Ledger mapping was not applied: {exception.Message}";
        }
    }

    private void BuildReconciliationIssues()
    {
        ReconciliationIssues.Clear();
        foreach (var voucher in Vouchers.Where(item => item.PersistentId > 0))
        {
            var debit = voucher.Lines
                .Where(line => line.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase))
                .Sum(line => Math.Abs(line.Amount));
            var credit = voucher.Lines
                .Where(line => line.DrCr.Equals("Cr", StringComparison.OrdinalIgnoreCase))
                .Sum(line => Math.Abs(line.Amount));
            if (Math.Abs(debit - credit) <= BalanceTolerance)
            {
                continue;
            }
            ReconciliationIssues.Add(new JournalReconciliationIssue
            {
                VoucherId = voucher.PersistentId,
                VoucherNumber = voucher.VoucherNumber,
                VoucherDate = voucher.VoucherDate,
                Narration = voucher.Narration,
                DebitTotal = debit,
                CreditTotal = credit
            });
        }
        SelectedReconciliationIssue = ReconciliationIssues.FirstOrDefault();
    }

    private void ApplySelectedReconciliation()
    {
        if (!CanEditJournal)
        {
            StatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, "apply Journal reconciliation", accountingMutation: true);
            return;
        }

        var issue = SelectedReconciliationIssue;
        if (issue is null || string.IsNullOrWhiteSpace(issue.MatchingLedger))
        {
            StatusText = "Enter the matching ledger before applying the balancing line.";
            return;
        }

        var voucher = Vouchers.FirstOrDefault(item => item.PersistentId == issue.VoucherId);
        if (voucher is null)
        {
            StatusText = "The selected reconciliation voucher is no longer available.";
            BuildReconciliationIssues();
            return;
        }

        string resolvedLedger;
        try
        {
            resolvedLedger = ResolveManualLedgerName(voucher.ClientId, voucher.FinancialYear, issue.MatchingLedger);
        }
        catch (InvalidOperationException exception)
        {
            StatusText = exception.Message;
            return;
        }
        voucher.Lines.Add(new JournalLine
        {
            LineNumber = voucher.Lines.Count + 1,
            LedgerName = resolvedLedger,
            Amount = issue.RequiredAmount,
            DrCr = issue.RequiredSide,
            TaxHead = string.Empty,
            LineRole = "Manual Note"
        });
        RecalculateTaxTotals(
            voucher,
            preserveRcmLiability: voucher.GenerationMode.Equals("Automatic", StringComparison.OrdinalIgnoreCase));
        voucher.IsUserEdited = true;
        try
        {
            _repository.SaveVoucher(voucher);
        }
        catch (Exception exception)
        {
            StatusText = $"Balancing line was not saved: {exception.Message}";
            return;
        }
        StatusText =
            $"Added {issue.RequiredSide} {issue.RequiredAmount:N2} to '{resolvedLedger}' in {voucher.VoucherNumber}.";
        Refresh();
        var client = _clientContext.CurrentClient;
        if (client is not null)
        {
            _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Journal, "Journal reconciliation balancing line saved");
            _ = RefreshAccountingAfterManualJournalAsync(client);
        }
    }

    private void LoadSettings(JournalGenerationSettings settings)
    {
        SettlementLedgerMode = settings.SettlementLedgerMode;
        BalanceType = settings.BalanceType;
        SingleSettlementLedger = settings.SingleSettlementLedger;
        IgstSettlementLedger = settings.IgstSettlementLedger;
        CgstSettlementLedger = settings.CgstSettlementLedger;
        SgstSettlementLedger = settings.SgstSettlementLedger;
        TemporaryRcmIgstLedger = settings.TemporaryRcmIgstLedger;
        TemporaryRcmCgstLedger = settings.TemporaryRcmCgstLedger;
        TemporaryRcmSgstLedger = settings.TemporaryRcmSgstLedger;
        RcmExpenseIgstLedger = PurchaseTreatmentPolicy.RcmInputExpensesLedger;
        RcmExpenseCgstLedger = PurchaseTreatmentPolicy.RcmInputExpensesLedger;
        RcmExpenseSgstLedger = PurchaseTreatmentPolicy.RcmInputExpensesLedger;
        LoadRcmRenameRows(settings);
        OpeningBooksBalance = settings.OpeningBooksBalance;
        OpeningBalanceStatus = "Opening balance will refresh from the selected settlement ledger.";
    }

    private JournalGenerationSettings CreateSettings(long clientId, string financialYear)
    {
        // GST Control Panel owns Nil-Sales period mirrors. Journal settings edits
        // must preserve them exactly instead of silently resetting them.
        var existing = _repository.GetSettings(clientId, financialYear);
        return new JournalGenerationSettings
        {
            ClientId = clientId,
            FinancialYear = financialYear,
            FilingFrequency = FilingFrequency,
            SettlementLedgerMode = SettlementLedgerMode,
            BalanceType = BalanceType,
            SingleSettlementLedger = ResolveManualLedgerName(clientId, financialYear, SingleSettlementLedger),
            IgstSettlementLedger = ResolveManualLedgerName(clientId, financialYear, IgstSettlementLedger),
            CgstSettlementLedger = ResolveManualLedgerName(clientId, financialYear, CgstSettlementLedger),
            SgstSettlementLedger = ResolveManualLedgerName(clientId, financialYear, SgstSettlementLedger),
            TemporaryRcmIgstLedger = ResolveManualLedgerName(clientId, financialYear, EffectiveRcmName(nameof(TemporaryRcmIgstLedger))),
            TemporaryRcmCgstLedger = ResolveManualLedgerName(clientId, financialYear, EffectiveRcmName(nameof(TemporaryRcmCgstLedger))),
            TemporaryRcmSgstLedger = ResolveManualLedgerName(clientId, financialYear, EffectiveRcmName(nameof(TemporaryRcmSgstLedger))),
            RcmExpenseIgstLedger = PurchaseTreatmentPolicy.RcmInputExpensesLedger,
            RcmExpenseCgstLedger = PurchaseTreatmentPolicy.RcmInputExpensesLedger,
            RcmExpenseSgstLedger = PurchaseTreatmentPolicy.RcmInputExpensesLedger,
            NilSalesPeriods = existing.NilSalesPeriods,
            NilSalesConflicts = existing.NilSalesConflicts,
            OpeningBooksBalance = OpeningBooksBalance
        };
    }

    private string ResolveManualLedgerName(long clientId, string financialYear, string? value)
    {
        var clean = AccountingText.CleanWhitespace(value);
        if (clean.Length == 0) return string.Empty;
        var key = AccountingText.DuplicateKey(clean);
        var matches = _masterRepository.Get(clientId, financialYear, false, string.Empty)
            .Where(item => item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !item.IsArchived)
            .Where(item => AccountingText.DuplicateKey(item.Name) == key)
            .GroupBy(item => item.PersistentId)
            .Select(group => group.First())
            .Take(2)
            .ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException($"Ledger '{clean}' matches duplicate Masters. Resolve the duplicate Master records first.");
        return matches.Length == 1 ? matches[0].Name.Trim() : AccountingText.DisplayName(clean);
    }

    private void NormalizeVoucherLedgerNames(long clientId, string financialYear, JournalVoucher voucher)
    {
        foreach (var line in voucher.Lines.Where(line => !string.IsNullOrWhiteSpace(line.LedgerName)))
            line.LedgerName = ResolveManualLedgerName(clientId, financialYear, line.LedgerName);
    }

    private void InitializeRcmLedgerRenameRows()
    {
        RcmLedgerRenames.Clear();
        RcmLedgerRenames.Add(new RcmLedgerRenameRow { Key = nameof(TemporaryRcmIgstLedger), ExistingName = DefaultTemporaryRcmIgstLedger });
        RcmLedgerRenames.Add(new RcmLedgerRenameRow { Key = nameof(TemporaryRcmCgstLedger), ExistingName = DefaultTemporaryRcmCgstLedger });
        RcmLedgerRenames.Add(new RcmLedgerRenameRow { Key = nameof(TemporaryRcmSgstLedger), ExistingName = DefaultTemporaryRcmSgstLedger });
        // Reversed RCM uses one fixed shared expense account.  It is intentionally
        // not exposed as three tax-wise rename rows.
    }

    private void LoadRcmRenameRows(JournalGenerationSettings settings)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(TemporaryRcmIgstLedger)] = settings.TemporaryRcmIgstLedger,
            [nameof(TemporaryRcmCgstLedger)] = settings.TemporaryRcmCgstLedger,
            [nameof(TemporaryRcmSgstLedger)] = settings.TemporaryRcmSgstLedger,
            [nameof(RcmExpenseIgstLedger)] = PurchaseTreatmentPolicy.RcmInputExpensesLedger,
            [nameof(RcmExpenseCgstLedger)] = PurchaseTreatmentPolicy.RcmInputExpensesLedger,
            [nameof(RcmExpenseSgstLedger)] = PurchaseTreatmentPolicy.RcmInputExpensesLedger
        };
        foreach (var row in RcmLedgerRenames)
        {
            var value = values.GetValueOrDefault(row.Key, row.ExistingName).Trim();
            row.RenameValue = value.Equals(row.ExistingName, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : value;
        }
        ApplyRcmRenameRowsToProperties();
    }

    private string EffectiveRcmName(string key) =>
        RcmLedgerRenames.FirstOrDefault(row => row.Key.Equals(key, StringComparison.Ordinal))?.EffectiveName
        ?? key switch
        {
            nameof(TemporaryRcmIgstLedger) => DefaultTemporaryRcmIgstLedger,
            nameof(TemporaryRcmCgstLedger) => DefaultTemporaryRcmCgstLedger,
            nameof(TemporaryRcmSgstLedger) => DefaultTemporaryRcmSgstLedger,
            nameof(RcmExpenseIgstLedger) => PurchaseTreatmentPolicy.RcmInputExpensesLedger,
            nameof(RcmExpenseCgstLedger) => PurchaseTreatmentPolicy.RcmInputExpensesLedger,
            nameof(RcmExpenseSgstLedger) => PurchaseTreatmentPolicy.RcmInputExpensesLedger,
            _ => string.Empty
        };

    private void ApplyRcmRenameRowsToProperties()
    {
        TemporaryRcmIgstLedger = EffectiveRcmName(nameof(TemporaryRcmIgstLedger));
        TemporaryRcmCgstLedger = EffectiveRcmName(nameof(TemporaryRcmCgstLedger));
        TemporaryRcmSgstLedger = EffectiveRcmName(nameof(TemporaryRcmSgstLedger));
        RcmExpenseIgstLedger = PurchaseTreatmentPolicy.RcmInputExpensesLedger;
        RcmExpenseCgstLedger = PurchaseTreatmentPolicy.RcmInputExpensesLedger;
        RcmExpenseSgstLedger = PurchaseTreatmentPolicy.RcmInputExpensesLedger;
    }

    private async Task SaveRcmNamesAsync()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        try
        {
            ApplyRcmRenameRowsToProperties();
            _repository.SaveSettings(CreateSettings(client.Id, client.FinancialYear));
            StatusText = "RCM ledger names saved. Refreshing automatic GST journals and master references...";
            await _refreshCoordinator.RefreshAsync(client);
            Refresh();
            StatusText = "RCM ledger names updated everywhere used by automatic GST journals and future Tally exports.";
        }
        catch (Exception exception)
        {
            StatusText = $"RCM ledger names were not saved: {exception.Message}";
        }
    }

    private void RestoreAutomaticRcmNames()
    {
        foreach (var row in RcmLedgerRenames)
        {
            row.RenameValue = string.Empty;
        }
        ApplyRcmRenameRowsToProperties();
        StatusText = "Automatic RCM names restored in the editor. Press Save Names to apply them everywhere.";
    }

    private void UpdateOpeningBalanceFromSelectedLedgers()
    {
        if (_ledgerOpeningBalances.Count == 0)
        {
            OpeningBalanceStatus = "Opening balance unavailable until Tally masters are synchronized.";
            return;
        }

        var selected = IsSingleSettlementMode
            ? new[] { SingleSettlementLedger }
            : new[] { IgstSettlementLedger, CgstSettlementLedger, SgstSettlementLedger };
        var balances = selected
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => (Name: name.Trim(), Found: _ledgerOpeningBalances.TryGetValue(name.Trim(), out var balance), Balance: balance))
            .ToArray();
        if (balances.Length == 0 || balances.Any(item => !item.Found || !item.Balance.HasValue))
        {
            OpeningBooksBalance = 0m;
            OpeningBalanceStatus = "Opening balance unavailable for one or more selected settlement ledgers.";
            return;
        }

        OpeningBooksBalance = balances.Sum(item => item.Balance!.Value);
        OpeningBalanceStatus = IsSingleSettlementMode
            ? $"Opening balance read from {balances[0].Name}."
            : "Opening balance is the total of the selected IGST, CGST and SGST settlement ledgers.";
    }

    private static void RecalculateTaxTotals(
        JournalVoucher voucher,
        bool preserveRcmLiability = false)
    {
        var retainedRcmIgst = voucher.RcmIgst;
        var retainedRcmCgst = voucher.RcmCgst;
        var retainedRcmSgst = voucher.RcmSgst;
        var foundRcmLiabilityLine = false;

        voucher.OutputIgst = 0m;
        voucher.OutputCgst = 0m;
        voucher.OutputSgst = 0m;
        voucher.RcmIgst = 0m;
        voucher.RcmCgst = 0m;
        voucher.RcmSgst = 0m;
        voucher.InputIgst = 0m;
        voucher.InputCgst = 0m;
        voucher.InputSgst = 0m;
        voucher.NormalInputIgst = 0m;
        voucher.NormalInputCgst = 0m;
        voucher.NormalInputSgst = 0m;
        voucher.RcmClaimIgst = 0m;
        voucher.RcmClaimCgst = 0m;
        voucher.RcmClaimSgst = 0m;
        voucher.RcmReverseIgst = 0m;
        voucher.RcmReverseCgst = 0m;
        voucher.RcmReverseSgst = 0m;

        foreach (var line in voucher.Lines)
        {
            var role = line.LineRole.Trim();
            var taxHead = NormalizeTaxHead(line.TaxHead);
            var amount = Math.Abs(line.Amount);

            if (role.Equals("RCM Input Claim", StringComparison.OrdinalIgnoreCase) ||
                role.Equals("RCM Receivable Claim", StringComparison.OrdinalIgnoreCase) ||
                role.Contains("RCM ITC Claim", StringComparison.OrdinalIgnoreCase))
            {
                var signed = line.DrCr.Equals("Cr", StringComparison.OrdinalIgnoreCase) ? amount : -amount;
                AddTaxAmount(voucher, "RCMCLAIM", taxHead, signed);
                AddTaxAmount(voucher, "INPUT", taxHead, signed);
            }
            else if (role.Equals("RCM Input -> Expenses", StringComparison.OrdinalIgnoreCase) ||
                     role.Contains("RCM ITC Reversal", StringComparison.OrdinalIgnoreCase))
            {
                var signed = line.DrCr.Equals("Cr", StringComparison.OrdinalIgnoreCase) ? amount : -amount;
                AddTaxAmount(voucher, "RCMREVERSE", taxHead, signed);
            }
            else if (role.Contains("Normal Input", StringComparison.OrdinalIgnoreCase) ||
                     role.Equals("Input Tax", StringComparison.OrdinalIgnoreCase))
            {
                var signed = line.DrCr.Equals("Cr", StringComparison.OrdinalIgnoreCase) ? amount : -amount;
                AddTaxAmount(voucher, "NORMALINPUT", taxHead, signed);
                AddTaxAmount(voucher, "INPUT", taxHead, signed);
            }
            else if (role.Equals("RCM Tax", StringComparison.OrdinalIgnoreCase))
            {
                foundRcmLiabilityLine = true;
                var signed = line.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase) ? amount : -amount;
                AddTaxAmount(voucher, "RCM", taxHead, signed);
            }
            else if (role.Contains("Output", StringComparison.OrdinalIgnoreCase))
            {
                var signed = line.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase) ? amount : -amount;
                AddTaxAmount(voucher, "OUTPUT", taxHead, signed);
            }
        }

        // Automatic RCM liability is source-voucher metadata and deliberately has
        // no line in the adjustment voucher. Editing lines must not erase it.
        if (preserveRcmLiability && !foundRcmLiabilityLine)
        {
            voucher.RcmIgst = retainedRcmIgst;
            voucher.RcmCgst = retainedRcmCgst;
            voucher.RcmSgst = retainedRcmSgst;
        }
    }

    private static void AddTaxAmount(
        JournalVoucher voucher,
        string category,
        string taxHead,
        decimal amount)
    {
        amount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        switch ((category.ToUpperInvariant(), taxHead))
        {
            case ("OUTPUT", "IGST"): voucher.OutputIgst += amount; break;
            case ("OUTPUT", "CGST"): voucher.OutputCgst += amount; break;
            case ("OUTPUT", "SGST"): voucher.OutputSgst += amount; break;
            case ("RCM", "IGST"): voucher.RcmIgst += amount; break;
            case ("RCM", "CGST"): voucher.RcmCgst += amount; break;
            case ("RCM", "SGST"): voucher.RcmSgst += amount; break;
            case ("INPUT", "IGST"): voucher.InputIgst += amount; break;
            case ("INPUT", "CGST"): voucher.InputCgst += amount; break;
            case ("INPUT", "SGST"): voucher.InputSgst += amount; break;
            case ("NORMALINPUT", "IGST"): voucher.NormalInputIgst += amount; break;
            case ("NORMALINPUT", "CGST"): voucher.NormalInputCgst += amount; break;
            case ("NORMALINPUT", "SGST"): voucher.NormalInputSgst += amount; break;
            case ("RCMCLAIM", "IGST"): voucher.RcmClaimIgst += amount; break;
            case ("RCMCLAIM", "CGST"): voucher.RcmClaimCgst += amount; break;
            case ("RCMCLAIM", "SGST"): voucher.RcmClaimSgst += amount; break;
            case ("RCMREVERSE", "IGST"): voucher.RcmReverseIgst += amount; break;
            case ("RCMREVERSE", "CGST"): voucher.RcmReverseCgst += amount; break;
            case ("RCMREVERSE", "SGST"): voucher.RcmReverseSgst += amount; break;
        }
    }

    private static string NormalizeTaxHead(string value)
    {
        var taxHead = value.Trim().ToUpperInvariant();
        return taxHead == "UTGST" ? "SGST" : taxHead;
    }

    private void ApplyFrequencyDetection(string detectedFrequency)
    {
        IsFilingFrequencyDetected = detectedFrequency.Length > 0;
        FilingFrequencySource = IsFilingFrequencyDetected
            ? $"Detected automatically from imported GSTR-3B: {detectedFrequency}"
            : "No GSTR-3B frequency found; manual fallback is active";
        OnPropertyChanged(nameof(CanEditFilingFrequency));
        OnPropertyChanged(nameof(FilingFrequencyBadge));
    }

    private static DateTime DefaultManualVoucherDate(string financialYear)
    {
        var parts = financialYear.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out var startYear))
        {
            return DateTime.Today;
        }

        var start = new DateTime(startYear, 4, 1);
        var end = new DateTime(startYear + 1, 3, 31);
        if (DateTime.Today < start)
        {
            return start;
        }
        if (DateTime.Today > end)
        {
            return end;
        }
        return DateTime.Today;
    }

    private static JournalLine CreateBlankLine(int lineNumber) =>
        new()
        {
            LineNumber = lineNumber,
            LedgerName = string.Empty,
            Amount = 0m,
            DrCr = "Dr",
            TaxHead = string.Empty,
            LineRole = "Manual Note"
        };

    private static string GetClipboardText()
    {
        if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
        {
            return Clipboard.GetText(TextDataFormat.UnicodeText);
        }
        return Clipboard.ContainsText(TextDataFormat.Text)
            ? Clipboard.GetText(TextDataFormat.Text)
            : string.Empty;
    }

    private static JournalVoucher CloneVoucher(JournalVoucher source) =>
        new()
        {
            PersistentId = source.PersistentId,
            ClientId = source.ClientId,
            FinancialYear = source.FinancialYear,
            PeriodKey = source.PeriodKey,
            PeriodLabel = source.PeriodLabel,
            VoucherDate = source.VoucherDate,
            VoucherNumber = source.VoucherNumber,
            VoucherType = source.VoucherType,
            Narration = source.Narration,
            GenerationMode = source.GenerationMode,
            IsUserEdited = source.IsUserEdited,
            OutputIgst = source.OutputIgst,
            OutputCgst = source.OutputCgst,
            OutputSgst = source.OutputSgst,
            RcmIgst = source.RcmIgst,
            RcmCgst = source.RcmCgst,
            RcmSgst = source.RcmSgst,
            InputIgst = source.InputIgst,
            InputCgst = source.InputCgst,
            InputSgst = source.InputSgst,
            NormalInputIgst = source.NormalInputIgst,
            NormalInputCgst = source.NormalInputCgst,
            NormalInputSgst = source.NormalInputSgst,
            RcmClaimIgst = source.RcmClaimIgst,
            RcmClaimCgst = source.RcmClaimCgst,
            RcmClaimSgst = source.RcmClaimSgst,
            RcmReverseIgst = source.RcmReverseIgst,
            RcmReverseCgst = source.RcmReverseCgst,
            RcmReverseSgst = source.RcmReverseSgst,
            Lines = source.Lines.Select(CloneLine).ToList(),
            CreatedUtc = source.CreatedUtc,
            UpdatedUtc = source.UpdatedUtc
        };

    private static JournalLine CloneLine(JournalLine source) =>
        new()
        {
            PersistentId = source.PersistentId,
            VoucherId = source.VoucherId,
            LineNumber = source.LineNumber,
            LedgerName = source.LedgerName,
            Amount = source.Amount,
            DrCr = source.DrCr,
            TaxHead = source.TaxHead,
            LineRole = source.LineRole
        };

    private void OnLinesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (JournalLine line in e.OldItems)
            {
                line.PropertyChanged -= OnJournalLinePropertyChanged;
            }
        }
        if (e.NewItems is not null)
        {
            foreach (JournalLine line in e.NewItems)
            {
                line.PropertyChanged += OnJournalLinePropertyChanged;
            }
        }
        NotifyLineSummary();
    }

    private void OnJournalLinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        NotifyLineSummary();
        if (sender is JournalLine line && ReferenceEquals(line, SelectedLine) &&
            e.PropertyName == nameof(JournalLine.LedgerName))
        {
            QueueSelectedJournalLedgerTotalRefresh(line);
        }
    }

    private void OnLedgerMappingPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        ApplyLedgerMappingCommand.RaiseCanExecuteChanged();

    private void OnReconciliationIssuePropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        ApplyReconciliationCommand.RaiseCanExecuteChanged();

    private void NotifyLineSummary()
    {
        OnPropertyChanged(nameof(SelectedDebitTotal));
        OnPropertyChanged(nameof(SelectedCreditTotal));
        OnPropertyChanged(nameof(SelectedDifference));
        OnPropertyChanged(nameof(IsSelectedVoucherBalanced));
        OnPropertyChanged(nameof(BalanceStatusText));
        OnPropertyChanged(nameof(SelectedVoucherHeading));
    }

    private void RenumberLines()
    {
        for (var index = 0; index < Lines.Count; index++)
        {
            Lines[index].LineNumber = index + 1;
        }
    }

    private static string WarningSummary(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return "No warnings.";
        }
        var first = string.Join(" ", warnings.Take(2));
        return $"{warnings.Count:N0} warning(s): {first}";
    }

    private async Task RefreshAccountingAfterManualJournalAsync(AccountingAutomationHub.Domain.Clients.ClientProfile client)
    {
        try
        {
            await _refreshCoordinator.RefreshAsync(client);
        }
        catch (Exception exception)
        {
            StatusText = $"Journal saved, but live accounting refresh needs attention: {exception.Message}";
        }
    }

    private void OnLiveChange(object? sender, AccountingLiveChange change)
    {
        var client = _clientContext.CurrentClient;
        if (client is null || change.ClientId != client.Id ||
            !change.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase)) return;

        if (!_isPageActive)
        {
            if (change.Area is AccountingLiveArea.Banking or AccountingLiveArea.Cash or AccountingLiveArea.Purchase or AccountingLiveArea.Sales or AccountingLiveArea.Journal or AccountingLiveArea.Masters)
                _ledgerTotalService.Invalidate(client.Id, client.FinancialYear);
            return;
        }

        if (change.Area is AccountingLiveArea.Banking or AccountingLiveArea.Cash or AccountingLiveArea.Purchase or AccountingLiveArea.Sales or AccountingLiveArea.Journal or AccountingLiveArea.Masters)
        {
            _ledgerTotalService.Invalidate(client.Id, client.FinancialYear);
            QueueLedgerTotalsRefresh(invalidate: false);
        }

        if (change.Area != AccountingLiveArea.Masters) return;
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            // Completion 61: the MainWindow cache coordinator refreshes the Masters stream
            // and calls WarmMasterDependenciesFromPreparedSnapshot. Do not issue a second
            // synchronous Master/Journal mapping SQLite pass from this UI callback.
            QueueLedgerTotalsRefresh(invalidate: false);
            StatusText = "Ledger Master changed • Journal dependency cache refresh queued.";
        });
    }

    public void RefreshSelectedJournalLedgerTotal() =>
        QueueSelectedJournalLedgerTotalRefresh(SelectedLine);

    private void QueueLedgerTotalsRefresh(bool invalidate)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess())
            _ = RefreshLedgerTotalsAsync(invalidate);
        else
            dispatcher.BeginInvoke(() => _ = RefreshLedgerTotalsAsync(invalidate));
    }

    private async Task RefreshLedgerTotalsAsync(bool invalidate)
    {
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            LedgerPickerOptions.Clear();
            ApplySelectedJournalLedgerTotal(null, "Open a client to view Ledger totals.");
            return;
        }

        var clientId = client.Id;
        var financialYear = client.FinancialYear;
        var version = ++_ledgerTotalRefreshVersion;
        if (invalidate) _ledgerTotalService.Invalidate(clientId, financialYear);

        IReadOnlyList<LedgerPickerOption> options;
        try
        {
            options = await Task.Run(() =>
            {
                var masterLedgerNames = _masterRepository
                    .Get(clientId, financialYear, false, string.Empty)
                    .Where(item => item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !item.IsArchived)
                    .Select(item => item.Name.Trim())
                    .Where(name => name.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return _ledgerTotalService
                    .GetAll(clientId, financialYear)
                    .Where(snapshot => masterLedgerNames.Contains(snapshot.LedgerName))
                    .Select(LedgerPickerOption.FromSnapshot)
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            });
        }
        catch (Exception exception)
        {
            if (version == _ledgerTotalRefreshVersion)
                ApplySelectedJournalLedgerTotal(null, $"Ledger totals could not be refreshed: {exception.Message}");
            return;
        }

        var current = _clientContext.CurrentClient;
        if (version != _ledgerTotalRefreshVersion || current is null || current.Id != clientId ||
            !current.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase)) return;

        LedgerPickerOptions.ReplaceRange(options);
        await RefreshSelectedJournalLedgerTotalAsync(SelectedLine, version);
    }

    private void QueueSelectedJournalLedgerTotalRefresh(JournalLine? line)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess())
            _ = RefreshSelectedJournalLedgerTotalAsync(line);
        else
            dispatcher.BeginInvoke(() => _ = RefreshSelectedJournalLedgerTotalAsync(line));
    }

    private async Task RefreshSelectedJournalLedgerTotalAsync(JournalLine? line, int? requiredVersion = null)
    {
        var client = _clientContext.CurrentClient;
        var ledgerName = line?.LedgerName.Trim() ?? string.Empty;
        if (client is null || ledgerName.Length == 0)
        {
            ApplySelectedJournalLedgerTotal(null, line is null
                ? "Select a Journal line to view its Ledger total."
                : "Select or type a Ledger to view its balance.");
            return;
        }

        var clientId = client.Id;
        var financialYear = client.FinancialYear;
        var asOnDate = SelectedVoucher?.VoucherDate.Date;
        LedgerTotalSnapshot? snapshot;
        try
        {
            snapshot = await Task.Run(() => _ledgerTotalService.Get(clientId, financialYear, ledgerName, asOnDate));
        }
        catch (Exception exception)
        {
            ApplySelectedJournalLedgerTotal(null, $"Ledger total unavailable: {exception.Message}");
            return;
        }

        if (requiredVersion.HasValue && requiredVersion.Value != _ledgerTotalRefreshVersion) return;
        var currentClient = _clientContext.CurrentClient;
        if (currentClient is null || currentClient.Id != clientId ||
            !currentClient.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase)) return;
        if (SelectedLine is null || !SelectedLine.LedgerName.Trim().Equals(ledgerName, StringComparison.OrdinalIgnoreCase)) return;

        ApplySelectedJournalLedgerTotal(
            snapshot,
            snapshot is null
                ? $"'{ledgerName}' is not yet a Master Ledger. Save/map it before it becomes part of the Ledger total book."
                : "Posted client/FY movements • unsaved Journal edits are excluded until the voucher is saved.");
    }

    private void ApplySelectedJournalLedgerTotal(LedgerTotalSnapshot? snapshot, string status)
    {
        SelectedJournalLedgerTotal = snapshot;
        SelectedJournalLedgerTotalStatus = status;
    }

    private void OnClientContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveClientContext.CurrentClient))
        {
            return;
        }

        _preparedClientId = 0;
        _preparedFinancialYear = string.Empty;
        StatusText = _clientContext.CurrentClient is null
            ? "Open a client to use Journal."
            : "Journal is preparing in the background.";
        OnPropertyChanged(nameof(CanEditJournal));
        OnPropertyChanged(nameof(CanDeleteJournal));
        OnPropertyChanged(nameof(CanApproveJournal));
        OnPropertyChanged(nameof(CanExportJournal));
        OnPropertyChanged(nameof(IsJournalReadOnly));
        OnPropertyChanged(nameof(JournalPermissionSummary));
        OnPropertyChanged(nameof(CanEditFilingFrequency));
        GenerateCommand.RaiseCanExecuteChanged();
        SaveSettingsCommand.RaiseCanExecuteChanged();
        SaveRcmNamesCommand.RaiseCanExecuteChanged();
        RestoreAutomaticRcmNamesCommand.RaiseCanExecuteChanged();
        AddManualCommand.RaiseCanExecuteChanged();
        ImportManualExcelCommand.RaiseCanExecuteChanged();
        PasteManualCommand.RaiseCanExecuteChanged();
        PasteLinesCommand.RaiseCanExecuteChanged();
        RefreshLedgerMappingsCommand.RaiseCanExecuteChanged();
    }

    private void NotifySummary()
    {
        OnPropertyChanged(nameof(ProtectedVoucherCount));
        OnPropertyChanged(nameof(ManualVoucherCount));
        OnPropertyChanged(nameof(TotalNetTax));
        OnPropertyChanged(nameof(DraftJournalCount));
        OnPropertyChanged(nameof(ReadyJournalCount));
        OnPropertyChanged(nameof(ApprovedJournalCount));
        OnPropertyChanged(nameof(RecurringJournalCount));
        OnPropertyChanged(nameof(ReversedJournalCount));
        NotifyLineSummary();
    }
}
