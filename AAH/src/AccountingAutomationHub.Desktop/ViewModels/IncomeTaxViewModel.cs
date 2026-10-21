using System.ComponentModel;
using System.Text.Json;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Clients;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class IncomeTaxViewModel : PageViewModel
{
    private readonly ActiveClientContext _clientContext;
    private readonly ApplicationPreferenceService _preferences;
    private string _statusText = "Open a Client/FY to prepare Income Tax Form 3CB details.";
    private string _assesseeName = string.Empty;
    private string _address = string.Empty;
    private string _pan = string.Empty;
    private string _status = string.Empty;
    private string _previousYear = string.Empty;
    private string _assessmentYear = string.Empty;
    private string _businessProfession = string.Empty;
    private string _auditSection = "Section 44AB";
    private string _booksLocation = string.Empty;
    private string _accountsExamined = string.Empty;
    private string _balanceSheetDate = string.Empty;
    private string _profitLossPeriod = string.Empty;
    private string _profitLossAmount = string.Empty;
    private string _auditFirmName = string.Empty;
    private string _firmRegistrationNumber = string.Empty;
    private string _auditorName = string.Empty;
    private string _membershipNumber = string.Empty;
    private string _udin = string.Empty;
    private string _reportDate = string.Empty;
    private string _reportPlace = string.Empty;
    private string _observations = string.Empty;
    private string _qualifications = string.Empty;
    private string _annexureStatement = "Form 3CD is annexed to this report.";
    private bool _accountsNotAuditedUnderOtherLaw = true;
    private bool _informationTrueAndCorrect = true;
    private bool _informationAndExplanationsObtained = true;
    private bool _properBooksKept = true;
    private bool _financialStatementsAgreeWithBooks = true;

    public IncomeTaxViewModel(ActiveClientContext clientContext, ApplicationPreferenceService preferences)
        : base("Income Tax", "Form 3CB preparation and future Income Tax reports")
    {
        _clientContext = clientContext;
        _preferences = preferences;
        SaveCommand = new RelayCommand(Save, () => _clientContext.CurrentClient is not null);
        ReloadCommand = new RelayCommand(Load);
        _clientContext.PropertyChanged += OnClientContextPropertyChanged;
        Load();
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string AssesseeName { get => _assesseeName; set => SetProperty(ref _assesseeName, value ?? string.Empty); }
    public string Address { get => _address; set => SetProperty(ref _address, value ?? string.Empty); }
    public string Pan { get => _pan; set => SetProperty(ref _pan, value ?? string.Empty); }
    public string Status { get => _status; set => SetProperty(ref _status, value ?? string.Empty); }
    public string PreviousYear { get => _previousYear; set => SetProperty(ref _previousYear, value ?? string.Empty); }
    public string AssessmentYear { get => _assessmentYear; set => SetProperty(ref _assessmentYear, value ?? string.Empty); }
    public string BusinessProfession { get => _businessProfession; set => SetProperty(ref _businessProfession, value ?? string.Empty); }
    public string AuditSection { get => _auditSection; set => SetProperty(ref _auditSection, value ?? string.Empty); }
    public string BooksLocation { get => _booksLocation; set => SetProperty(ref _booksLocation, value ?? string.Empty); }
    public string AccountsExamined { get => _accountsExamined; set => SetProperty(ref _accountsExamined, value ?? string.Empty); }
    public string BalanceSheetDate { get => _balanceSheetDate; set => SetProperty(ref _balanceSheetDate, value ?? string.Empty); }
    public string ProfitLossPeriod { get => _profitLossPeriod; set => SetProperty(ref _profitLossPeriod, value ?? string.Empty); }
    public string ProfitLossAmount { get => _profitLossAmount; set => SetProperty(ref _profitLossAmount, value ?? string.Empty); }
    public string AuditFirmName { get => _auditFirmName; set => SetProperty(ref _auditFirmName, value ?? string.Empty); }
    public string FirmRegistrationNumber { get => _firmRegistrationNumber; set => SetProperty(ref _firmRegistrationNumber, value ?? string.Empty); }
    public string AuditorName { get => _auditorName; set => SetProperty(ref _auditorName, value ?? string.Empty); }
    public string MembershipNumber { get => _membershipNumber; set => SetProperty(ref _membershipNumber, value ?? string.Empty); }
    public string Udin { get => _udin; set => SetProperty(ref _udin, value ?? string.Empty); }
    public string ReportDate { get => _reportDate; set => SetProperty(ref _reportDate, value ?? string.Empty); }
    public string ReportPlace { get => _reportPlace; set => SetProperty(ref _reportPlace, value ?? string.Empty); }
    public string Observations { get => _observations; set => SetProperty(ref _observations, value ?? string.Empty); }
    public string Qualifications { get => _qualifications; set => SetProperty(ref _qualifications, value ?? string.Empty); }
    public string AnnexureStatement { get => _annexureStatement; set => SetProperty(ref _annexureStatement, value ?? string.Empty); }
    public bool AccountsNotAuditedUnderOtherLaw { get => _accountsNotAuditedUnderOtherLaw; set => SetProperty(ref _accountsNotAuditedUnderOtherLaw, value); }
    public bool InformationTrueAndCorrect { get => _informationTrueAndCorrect; set => SetProperty(ref _informationTrueAndCorrect, value); }
    public bool InformationAndExplanationsObtained { get => _informationAndExplanationsObtained; set => SetProperty(ref _informationAndExplanationsObtained, value); }
    public bool ProperBooksKept { get => _properBooksKept; set => SetProperty(ref _properBooksKept, value); }
    public bool FinancialStatementsAgreeWithBooks { get => _financialStatementsAgreeWithBooks; set => SetProperty(ref _financialStatementsAgreeWithBooks, value); }

    private void OnClientContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveClientContext.CurrentClient)) return;
        Load();
        SaveCommand.RaiseCanExecuteChanged();
    }

    private string Key(ClientProfile client) => $"IncomeTax.Form3CB.{client.Id}.{client.FinancialYear.Trim().ToUpperInvariant()}";

    private void Load()
    {
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            Clear();
            StatusText = "Open a Client/FY to prepare Income Tax Form 3CB details.";
            return;
        }

        var raw = _preferences.Get(Key(client));
        Form3CbState state;
        try { state = string.IsNullOrWhiteSpace(raw) ? new Form3CbState() : JsonSerializer.Deserialize<Form3CbState>(raw) ?? new Form3CbState(); }
        catch { state = new Form3CbState(); }

        AssesseeName = First(state.AssesseeName, client.LegalName, client.ClientName);
        Address = First(state.Address, client.Address);
        Pan = First(state.Pan, client.Pan);
        Status = state.Status;
        PreviousYear = First(state.PreviousYear, client.FinancialYear);
        AssessmentYear = state.AssessmentYear;
        BusinessProfession = state.BusinessProfession;
        AuditSection = First(state.AuditSection, "Section 44AB");
        BooksLocation = state.BooksLocation;
        AccountsExamined = state.AccountsExamined;
        BalanceSheetDate = state.BalanceSheetDate;
        ProfitLossPeriod = state.ProfitLossPeriod;
        ProfitLossAmount = state.ProfitLossAmount;
        AuditFirmName = state.AuditFirmName;
        FirmRegistrationNumber = state.FirmRegistrationNumber;
        AuditorName = state.AuditorName;
        MembershipNumber = state.MembershipNumber;
        Udin = state.Udin;
        ReportDate = state.ReportDate;
        ReportPlace = state.ReportPlace;
        Observations = state.Observations;
        Qualifications = state.Qualifications;
        AnnexureStatement = First(state.AnnexureStatement, "Form 3CD is annexed to this report.");
        AccountsNotAuditedUnderOtherLaw = state.AccountsNotAuditedUnderOtherLaw;
        InformationTrueAndCorrect = state.InformationTrueAndCorrect;
        InformationAndExplanationsObtained = state.InformationAndExplanationsObtained;
        ProperBooksKept = state.ProperBooksKept;
        FinancialStatementsAgreeWithBooks = state.FinancialStatementsAgreeWithBooks;
        StatusText = $"{client.ClientName} · FY {client.FinancialYear} · Form 3CB preparation ready.";
    }

    private void Save()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        var state = new Form3CbState
        {
            AssesseeName = AssesseeName, Address = Address, Pan = Pan, Status = Status,
            PreviousYear = PreviousYear, AssessmentYear = AssessmentYear, BusinessProfession = BusinessProfession,
            AuditSection = AuditSection, BooksLocation = BooksLocation, AccountsExamined = AccountsExamined,
            BalanceSheetDate = BalanceSheetDate, ProfitLossPeriod = ProfitLossPeriod, ProfitLossAmount = ProfitLossAmount,
            AuditFirmName = AuditFirmName, FirmRegistrationNumber = FirmRegistrationNumber, AuditorName = AuditorName,
            MembershipNumber = MembershipNumber, Udin = Udin, ReportDate = ReportDate, ReportPlace = ReportPlace,
            Observations = Observations, Qualifications = Qualifications, AnnexureStatement = AnnexureStatement,
            AccountsNotAuditedUnderOtherLaw = AccountsNotAuditedUnderOtherLaw, InformationTrueAndCorrect = InformationTrueAndCorrect,
            InformationAndExplanationsObtained = InformationAndExplanationsObtained, ProperBooksKept = ProperBooksKept,
            FinancialStatementsAgreeWithBooks = FinancialStatementsAgreeWithBooks
        };
        _preferences.Set(Key(client), JsonSerializer.Serialize(state));
        StatusText = $"Form 3CB details saved for {client.ClientName} · FY {client.FinancialYear}.";
    }

    private void Clear()
    {
        AssesseeName = Address = Pan = Status = PreviousYear = AssessmentYear = BusinessProfession = string.Empty;
        AuditSection = "Section 44AB";
        BooksLocation = AccountsExamined = BalanceSheetDate = ProfitLossPeriod = ProfitLossAmount = string.Empty;
        AuditFirmName = FirmRegistrationNumber = AuditorName = MembershipNumber = Udin = ReportDate = ReportPlace = string.Empty;
        Observations = Qualifications = string.Empty;
        AnnexureStatement = "Form 3CD is annexed to this report.";
        AccountsNotAuditedUnderOtherLaw = true;
        InformationTrueAndCorrect = true;
        InformationAndExplanationsObtained = true;
        ProperBooksKept = true;
        FinancialStatementsAgreeWithBooks = true;
    }

    private static string First(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

    private sealed class Form3CbState
    {
        public string AssesseeName { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string Pan { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string PreviousYear { get; set; } = string.Empty;
        public string AssessmentYear { get; set; } = string.Empty;
        public string BusinessProfession { get; set; } = string.Empty;
        public string AuditSection { get; set; } = string.Empty;
        public string BooksLocation { get; set; } = string.Empty;
        public string AccountsExamined { get; set; } = string.Empty;
        public string BalanceSheetDate { get; set; } = string.Empty;
        public string ProfitLossPeriod { get; set; } = string.Empty;
        public string ProfitLossAmount { get; set; } = string.Empty;
        public string AuditFirmName { get; set; } = string.Empty;
        public string FirmRegistrationNumber { get; set; } = string.Empty;
        public string AuditorName { get; set; } = string.Empty;
        public string MembershipNumber { get; set; } = string.Empty;
        public string Udin { get; set; } = string.Empty;
        public string ReportDate { get; set; } = string.Empty;
        public string ReportPlace { get; set; } = string.Empty;
        public string Observations { get; set; } = string.Empty;
        public string Qualifications { get; set; } = string.Empty;
        public string AnnexureStatement { get; set; } = string.Empty;
        public bool AccountsNotAuditedUnderOtherLaw { get; set; } = true;
        public bool InformationTrueAndCorrect { get; set; } = true;
        public bool InformationAndExplanationsObtained { get; set; } = true;
        public bool ProperBooksKept { get; set; } = true;
        public bool FinancialStatementsAgreeWithBooks { get; set; } = true;
    }
}
