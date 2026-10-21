using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.ComponentModel;
using System.Windows.Data;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Audit;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Infrastructure.Settings;
using AccountingAutomationHub.Infrastructure.Tally;
using Microsoft.Win32;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class AuditFinancialStatementsViewModel : ViewModelBase
{
    private readonly ApplicationPreferenceService _preferences;
    private AuditClientProfile? _client;
    private string _statusText = "Select an Audit client.";
    private string _selectedNote = "All Notes";
    private CaProfileRow? _selectedCaProfile;
    private FinancialStatementBasicInfo _basicInfo = new();
    private bool _isNotesPreparationVisible;
    private bool _hideZeroClosingBalance = true;
    private FinancialStatementLedgerRow? _selectedFyTrialBalanceRow;
    private string _sameTallyGroupScope = "FY only";
    private IReadOnlyList<FinancialStatementMappingOption> _mappingCatalog = [];

    public AuditFinancialStatementsViewModel()
    {
        var repo = new SqliteApplicationPreferenceRepository();
        repo.Initialize();
        _preferences = new ApplicationPreferenceService(repo);
        Notes = [];
        NotePreviewRows = [];
        Signatories = [];
        OwnerPartners = [];
        ShareCapitalDetails = [];
        CaProfiles = [];
        FyTrialBalance = []; PyTrialBalance = []; FyStock = []; PyStock = []; FyDebtors = []; PyDebtors = []; FyCreditors = []; PyCreditors = [];
        NceTypes = new ObservableCollection<string>(FinancialStatementWorkbookRules.NceEntityTypes);
        RoundingOptions = new ObservableCollection<string>(FinancialStatementWorkbookRules.RoundOffChoices);
        PpeBlockOptions = new ObservableCollection<string>(FinancialStatementWorkbookRules.PpeBlockChoices);
        DebtorCreditorDetailOptions = new ObservableCollection<string>(FinancialStatementWorkbookRules.DebtorCreditorDetailChoices);
        StockDetailOptions = new ObservableCollection<string>(FinancialStatementWorkbookRules.StockDetailChoices);
        FsHeadOptions = []; NoteHeadOptions = []; NoteSubHeadOptions = [];
        SameTallyGroupScopes = ["FY only", "FY + PY"];
        FyTrialBalanceView = CollectionViewSource.GetDefaultView(FyTrialBalance);
        PyTrialBalanceView = CollectionViewSource.GetDefaultView(PyTrialBalance);
        FyStockView = CollectionViewSource.GetDefaultView(FyStock);
        PyStockView = CollectionViewSource.GetDefaultView(PyStock);
        FyDebtorsView = CollectionViewSource.GetDefaultView(FyDebtors);
        PyDebtorsView = CollectionViewSource.GetDefaultView(PyDebtors);
        FyCreditorsView = CollectionViewSource.GetDefaultView(FyCreditors);
        PyCreditorsView = CollectionViewSource.GetDefaultView(PyCreditors);
        FyTrialBalanceView.Filter = FilterTrialBalanceRow;
        PyTrialBalanceView.Filter = FilterTrialBalanceRow;
        FyDebtorsView.Filter = FilterTrialBalanceRow;
        PyDebtorsView.Filter = FilterTrialBalanceRow;
        FyCreditorsView.Filter = FilterTrialBalanceRow;
        PyCreditorsView.Filter = FilterTrialBalanceRow;
        FyStockView.Filter = FilterStockRow;
        PyStockView.Filter = FilterStockRow;
        ExportWithLinksCommand = new RelayCommand(() => ExportWorkbook(true));
        ExportWithoutLinksCommand = new RelayCommand(() => ExportWorkbook(false));
        ExportPdfCommand = new RelayCommand(ExportPdf);
        NotesPreparationCommand = new RelayCommand(() => { IsNotesPreparationVisible = !IsNotesPreparationVisible; RefreshNotePreview(); StatusText = IsNotesPreparationVisible ? $"Notes Preparation · {SelectedNote}" : "Notes Preparation closed."; });
        SaveMappingsCommand = new RelayCommand(SaveMappings);
        ApplyMappingToSameTallyGroupCommand = new RelayCommand(ApplyMappingToSameTallyGroup);
        ConnectPreviousYearTallyCommand = new AsyncRelayCommand(ConnectPreviousYearTallyAsync);
        SaveBasicInfoCommand = new RelayCommand(SaveBasicInfo);
        RefreshCommand = new RelayCommand(() => StatusText = _client is null ? "Select an Audit client." : $"Audit Financial Statements ready · {_client.ClientCode} · FY {_client.FinancialYear}.");
        BuildNoteList(); LoadCaProfiles();
    }

    public ObservableCollection<string> Notes { get; }
    public ObservableCollection<FinancialStatementNotePreviewRow> NotePreviewRows { get; }
    public ObservableCollection<EntitySignatoryRow> Signatories { get; }
    public ObservableCollection<OwnerPartnerRow> OwnerPartners { get; }
    public ObservableCollection<ShareCapitalDetailRow> ShareCapitalDetails { get; }
    public ObservableCollection<CaProfileRow> CaProfiles { get; }
    public ObservableCollection<FinancialStatementLedgerRow> FyTrialBalance { get; }
    public ObservableCollection<FinancialStatementLedgerRow> PyTrialBalance { get; }
    public ObservableCollection<FinancialStatementStockRow> FyStock { get; }
    public ObservableCollection<FinancialStatementStockRow> PyStock { get; }
    public ObservableCollection<FinancialStatementLedgerRow> FyDebtors { get; }
    public ObservableCollection<FinancialStatementLedgerRow> PyDebtors { get; }
    public ObservableCollection<FinancialStatementLedgerRow> FyCreditors { get; }
    public ObservableCollection<FinancialStatementLedgerRow> PyCreditors { get; }
    public ObservableCollection<string> NceTypes { get; }
    public ObservableCollection<string> RoundingOptions { get; }
    public ObservableCollection<string> PpeBlockOptions { get; }
    public ObservableCollection<string> DebtorCreditorDetailOptions { get; }
    public ObservableCollection<string> StockDetailOptions { get; }
    public ObservableCollection<string> FsHeadOptions { get; }
    public ObservableCollection<string> NoteHeadOptions { get; }
    public ObservableCollection<string> NoteSubHeadOptions { get; }
    public ObservableCollection<string> SameTallyGroupScopes { get; }
    public ICollectionView FyTrialBalanceView { get; }
    public ICollectionView PyTrialBalanceView { get; }
    public ICollectionView FyStockView { get; }
    public ICollectionView PyStockView { get; }
    public ICollectionView FyDebtorsView { get; }
    public ICollectionView PyDebtorsView { get; }
    public ICollectionView FyCreditorsView { get; }
    public ICollectionView PyCreditorsView { get; }
    public RelayCommand ExportWithLinksCommand { get; }
    public RelayCommand ExportWithoutLinksCommand { get; }
    public RelayCommand ExportPdfCommand { get; }
    public RelayCommand NotesPreparationCommand { get; }
    public RelayCommand SaveMappingsCommand { get; }
    public RelayCommand ApplyMappingToSameTallyGroupCommand { get; }
    public AsyncRelayCommand ConnectPreviousYearTallyCommand { get; }
    public RelayCommand SaveBasicInfoCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public FinancialStatementBasicInfo BasicInfo { get => _basicInfo; private set => SetProperty(ref _basicInfo, value); }
    public bool ShowClientFinancialStatementSettings => false;
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public FinancialStatementLedgerRow? SelectedFyTrialBalanceRow { get => _selectedFyTrialBalanceRow; set => SetProperty(ref _selectedFyTrialBalanceRow, value); }
    public string SameTallyGroupScope { get => _sameTallyGroupScope; set => SetProperty(ref _sameTallyGroupScope, string.IsNullOrWhiteSpace(value) ? "FY only" : value); }
    public string EntityTypeSelection
    {
        get => BasicInfo.EntityType;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (BasicInfo.EntityType.Equals(normalized, StringComparison.Ordinal)) return;
            BasicInfo.EntityType = normalized;
            if (!IsNceEntityType) BasicInfo.NceType = string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNceEntityType));
            OnPropertyChanged(nameof(IsCompanyScheduleIIIEntityType));
            OnPropertyChanged(nameof(RegistrationIdLabel));
            OnPropertyChanged(nameof(BasicInfo));
            RebuildTemplatePresentation();
            ReloadSelectedProfileMappings();
        }
    }
    public bool IsNceEntityType => BasicInfo.EntityType.Equals("NCE", StringComparison.OrdinalIgnoreCase) || BasicInfo.EntityType.StartsWith("NCE -", StringComparison.OrdinalIgnoreCase);
    public bool IsCompanyScheduleIIIEntityType => FinancialStatementWorkbookService.ResolveProfileSheet(BasicInfo.EntityType).Equals("C_AS", StringComparison.OrdinalIgnoreCase);
    public string RegistrationIdLabel => IsCompanyScheduleIIIEntityType
        ? "Corporate Identification Number (CIN)"
        : "Limited Liability Partnership Identification Number (LLPIN)";
    public bool HideZeroClosingBalance
    {
        get => _hideZeroClosingBalance;
        set
        {
            if (!SetProperty(ref _hideZeroClosingBalance, value)) return;
            FyTrialBalanceView.Refresh();
            PyTrialBalanceView.Refresh();
            FyStockView.Refresh();
            PyStockView.Refresh();
            FyDebtorsView.Refresh();
            PyDebtorsView.Refresh();
            FyCreditorsView.Refresh();
            PyCreditorsView.Refresh();
        }
    }
    public string SelectedNote { get => _selectedNote; set { if (SetProperty(ref _selectedNote, value)) RefreshNotePreview(); } }
    public bool IsNotesPreparationVisible { get => _isNotesPreparationVisible; private set => SetProperty(ref _isNotesPreparationVisible, value); }
    public string FyCaption => _client?.FinancialYear ?? string.Empty;
    public string PyCaption => PreviousFinancialYear(FyCaption);
    public bool PreviousYearDataAvailable => PyTrialBalance.Count > 0 || PyStock.Count > 0;
    public string PreviousYearStatus => PreviousYearDataAvailable ? $"PY {PyCaption} data ready" : $"PY {PyCaption} data not found in current Audit authority.";
    public CaProfileRow? SelectedCaProfile
    {
        get => _selectedCaProfile;
        set
        {
            if (!SetProperty(ref _selectedCaProfile, value) || value is null) return;
            BasicInfo.CaName = value.CaName; BasicInfo.CaDesignation = value.Designation; BasicInfo.CaMembershipNumber = value.MembershipNumber;
            BasicInfo.AuditFirmName = value.FirmName; BasicInfo.FirmRegistrationNumber = value.FirmRegistrationNumber; BasicInfo.AuditPlace = value.Place;
            OnPropertyChanged(nameof(BasicInfo));
        }
    }

    public void Refresh(AuditClientProfile? client, IEnumerable<AuditFinancialStatementRow> statementRows, IEnumerable<AuditRecordRow> auditRows)
    {
        _client = client;
        FyTrialBalance.Clear(); PyTrialBalance.Clear(); FyStock.Clear(); PyStock.Clear(); FyDebtors.Clear(); PyDebtors.Clear(); FyCreditors.Clear(); PyCreditors.Clear();
        if (client is null) { StatusText = "Select an Audit client."; return; }
        LoadBasicInfo(client); LoadCaProfiles();
        var fySourceOrder = 0;
        var fyRows = statementRows.Where(x => !x.IsGroupSummary).Select(source => new FinancialStatementLedgerRow
        {
            Ledger = source.Ledger, ParentGroup = source.LedgerGroup, PrimaryGroup = source.PrimaryGroup, MasterId = source.MasterId,
            Opening = source.OpeningBalance ?? 0m, Closing = source.ClosingBalance ?? 0m, SourceOrder = fySourceOrder++
        }).ToArray();
        foreach (var row in FinancialStatementTrialBalanceOrder.Order(fyRows))
        {
            FyTrialBalance.Add(row);
            var grouping = $"{row.ParentGroup} {row.PrimaryGroup}";
            if (grouping.Contains("Sundry Debtors", StringComparison.OrdinalIgnoreCase)) { row.ConfigureScheduleContext("Debtor"); FyDebtors.Add(row); }
            if (grouping.Contains("Sundry Creditors", StringComparison.OrdinalIgnoreCase)) { row.ConfigureScheduleContext("Creditor"); FyCreditors.Add(row); }
        }
        // Build owner-aware catalog only after Audit TB authority is loaded.
        SeedOwnerPartnersFromCapitalLedgersWhenBlank();
        RebuildTemplatePresentation();
        var mappings = LoadMappings(client.Id, client.FinancialYear);
        ApplyMappings(mappings, FyTrialBalance);
        BuildNoteList();
        RefreshNotePreview();
        OnPropertyChanged(nameof(FyCaption)); OnPropertyChanged(nameof(PyCaption)); OnPropertyChanged(nameof(PreviousYearDataAvailable)); OnPropertyChanged(nameof(PreviousYearStatus));
        StatusText = $"Audit Financial Statements ready · {client.ClientCode} · FY {client.FinancialYear}.";
    }

    private void SeedOwnerPartnersFromCapitalLedgersWhenBlank()
    {
        if (!IsNceEntityType) return;

        // FIXED8: the original workbook Owner/Partners table is the only capital C3 authority.
        // Older R9 builds incorrectly mined every person-like Capital ledger and wrote those
        // names back into Basic Info. For an Individual there can only be one proprietor, so
        // repair that legacy pollution when multiple blank-detail rows exactly match the old
        // ledger-derived candidate set. Partnerships/HUFs/LLPs are never rewritten here.
        var populated = OwnerPartners.Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToArray();
        if (BasicInfo.NceType.Equals("Individual", StringComparison.OrdinalIgnoreCase) && populated.Length > 1 &&
            populated.All(x => string.IsNullOrWhiteSpace(x.Designation) && x.ShareOfProfitLoss == 0m))
        {
            var legacyCandidates = FyTrialBalance
                .Select(x => FinancialStatementTrialBalanceOrder.OwnerNameCandidate(x.Ledger, x.ParentGroup, x.PrimaryGroup))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (populated.All(x => legacyCandidates.Contains(x.Name.Trim())))
            {
                foreach (var row in OwnerPartners) { row.Name = string.Empty; row.Designation = string.Empty; row.ShareOfProfitLoss = 0m; }
                populated = Array.Empty<OwnerPartnerRow>();
            }
        }

        if (OwnerPartners.Any(x => !string.IsNullOrWhiteSpace(x.Name))) return;

        // Only an unambiguous Individual signatory may seed the proprietor row. Never seed from
        // ledger names, because Drawings/insurance/salary/other capital-group ledgers are not
        // proprietor identities.
        if (BasicInfo.NceType.Equals("Individual", StringComparison.OrdinalIgnoreCase))
        {
            var signatoryNames = Signatories.Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .Select(x => x.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
            if (signatoryNames.Length == 1 && OwnerPartners.Count > 0) OwnerPartners[0].Name = signatoryNames[0];
        }
        BasicInfo.OwnerPartners = OwnerPartners.Where(x => !string.IsNullOrWhiteSpace(x.Name)).Select(x => x.Clone()).ToList();
        OnPropertyChanged(nameof(BasicInfo));
    }

    private void LoadBasicInfo(AuditClientProfile client)
    {
        FinancialStatementBasicInfo? saved = null;
        try { saved = JsonSerializer.Deserialize<FinancialStatementBasicInfo>(_preferences.Get(StateKey(client.Id, client.FinancialYear))); } catch { }
        BasicInfo = saved ?? new FinancialStatementBasicInfo();
        if (BasicInfo.EntityType.StartsWith("NCE -", StringComparison.OrdinalIgnoreCase)) BasicInfo.EntityType = "NCE";
        if (BasicInfo.NceType.Equals("Individual / Proprietorship", StringComparison.OrdinalIgnoreCase)) BasicInfo.NceType = "Individual";
        if (!FinancialStatementWorkbookRules.RoundOffChoices.Contains(BasicInfo.Rounding, StringComparer.OrdinalIgnoreCase)) BasicInfo.Rounding = "Default";
        if (!FinancialStatementWorkbookRules.PpeBlockChoices.Contains(BasicInfo.PpeBlock, StringComparer.OrdinalIgnoreCase)) BasicInfo.PpeBlock = "Gross Block";
        if (!FinancialStatementWorkbookRules.DebtorCreditorDetailChoices.Contains(BasicInfo.DebtorCreditorDetail, StringComparer.OrdinalIgnoreCase)) BasicInfo.DebtorCreditorDetail = "Party-wise";
        if (!FinancialStatementWorkbookRules.StockDetailChoices.Contains(BasicInfo.StockDetail, StringComparer.OrdinalIgnoreCase)) BasicInfo.StockDetail = "Item-wise";
        if (IsNceEntityType && !string.IsNullOrWhiteSpace(BasicInfo.NceType) &&
            !FinancialStatementWorkbookRules.NceEntityTypes.Contains(BasicInfo.NceType, StringComparer.OrdinalIgnoreCase)) BasicInfo.NceType = string.Empty;
        Signatories.Clear(); foreach (var row in BasicInfo.Signatories ?? []) Signatories.Add(row);
        if (Signatories.Count == 0 && !string.IsNullOrWhiteSpace(BasicInfo.SignatoryName)) Signatories.Add(new EntitySignatoryRow { Name = BasicInfo.SignatoryName, Designation = BasicInfo.SignatoryDesignation, Din = BasicInfo.Din, Place = BasicInfo.SigningPlace, SigningDate = BasicInfo.SigningDate });
        while (Signatories.Count < 7) Signatories.Add(new EntitySignatoryRow());
        OwnerPartners.Clear(); foreach (var row in BasicInfo.OwnerPartners ?? []) OwnerPartners.Add(row); while (OwnerPartners.Count < 11) OwnerPartners.Add(new OwnerPartnerRow());
        ShareCapitalDetails.Clear(); foreach (var row in BasicInfo.ShareCapitalDetails ?? []) ShareCapitalDetails.Add(row); while (ShareCapitalDetails.Count < 11) ShareCapitalDetails.Add(new ShareCapitalDetailRow());
        BasicInfo.EntityName = First(BasicInfo.EntityName, client.LegalName, client.TradeName, client.ClientName);
        BasicInfo.CurrentYearCompanyName = First(BasicInfo.CurrentYearCompanyName, client.ClientName, client.TradeName, client.LegalName, BasicInfo.EntityName);
        BasicInfo.PreviousYearCompanyName = First(BasicInfo.PreviousYearCompanyName, BasicInfo.PreviousYearTallyCompany, BasicInfo.CurrentYearCompanyName);
        BasicInfo.MailingName = First(BasicInfo.MailingName, client.TradeName, client.ClientName);
        BasicInfo.Address = First(BasicInfo.Address, client.Address); BasicInfo.Pan = First(BasicInfo.Pan, client.Pan); BasicInfo.Gstin = First(BasicInfo.Gstin, client.Gstin);
        BasicInfo.CurrentYearEnd = FyEnd(client.FinancialYear); BasicInfo.PreviousYearEnd = FyEnd(PreviousFinancialYear(client.FinancialYear));
        OnPropertyChanged(nameof(BasicInfo));
        OnPropertyChanged(nameof(EntityTypeSelection));
        OnPropertyChanged(nameof(IsNceEntityType));
        OnPropertyChanged(nameof(IsCompanyScheduleIIIEntityType));
        OnPropertyChanged(nameof(RegistrationIdLabel));
    }
    private void SaveBasicInfo()
    {
        if (_client is null) return;
        if (IsCompanyScheduleIIIEntityType)
        {
            var incompleteShare = ShareCapitalDetails.FirstOrDefault(x =>
                string.IsNullOrWhiteSpace(x.TypeOfShares) != (x.FaceValue == 0m));
            if (incompleteShare is not null || ShareCapitalDetails.Any(x => !string.IsNullOrWhiteSpace(x.TypeOfShares) && x.FaceValue <= 0m))
            {
                StatusText = "Complete both Type of Shares and a positive Face Value for each Share Capital Details row before saving.";
                return;
            }
        }
        var first = Signatories.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Name));
        if (first is not null)
        {
            BasicInfo.SignatoryName = first.Name;
            BasicInfo.SignatoryDesignation = first.Designation;
            BasicInfo.Din = first.Din;
            if (!string.IsNullOrWhiteSpace(first.Place)) BasicInfo.SigningPlace = first.Place;
            if (!string.IsNullOrWhiteSpace(first.SigningDate)) BasicInfo.SigningDate = first.SigningDate;
        }
        BasicInfo.Signatories = Signatories.Where(x => !string.IsNullOrWhiteSpace(x.Name)).Select(x => x.Clone()).ToList();
        BasicInfo.OwnerPartners = OwnerPartners.Where(x => !string.IsNullOrWhiteSpace(x.Name)).Select(x => x.Clone()).ToList();
        BasicInfo.ShareCapitalDetails = ShareCapitalDetails.Where(x => !string.IsNullOrWhiteSpace(x.TypeOfShares) || x.FaceValue != 0m).Select(x => x.Clone()).ToList();
        _preferences.Set(StateKey(_client.Id, _client.FinancialYear), JsonSerializer.Serialize(BasicInfo));
        RebuildTemplatePresentation();
        StatusText = "Audit Financial Statements Basic Info saved. Owner/partner names refreshed in Capital/Current A/c mapping choices.";
    }
    private void LoadCaProfiles() { CaProfiles.Clear(); try { foreach (var x in JsonSerializer.Deserialize<List<CaProfileRow>>(_preferences.Get(SettingsViewModel.CaProfilesPreferenceKey)) ?? []) CaProfiles.Add(x); } catch { } }
    private void BuildNoteList()
    {
        Notes.Clear();
        foreach (var value in FinancialStatementWorkbookService.BuildNoteSelector(TemplatePath(), BasicInfo.EntityType)) Notes.Add(value);
        if (Notes.Count == 0) Notes.Add("All Notes");
        if (!Notes.Contains(SelectedNote)) SelectedNote = Notes[0];
    }
    private void RefreshNotePreview()
    {
        NotePreviewRows.Clear();
        foreach (var row in FinancialStatementWorkbookService.BuildNotePreview(TemplatePath(), BasicInfo.EntityType, SelectedNote, FyTrialBalance, PyTrialBalance)) NotePreviewRows.Add(row);
    }
    private IReadOnlyList<FinancialStatementMappingPreference> LoadMappings(long clientId, string fy)
    {
        var profileRows = ReadMappings(ProfileMappingKey(clientId, fy));
        if (profileRows.Count > 0) return profileRows;
        return ReadMappings(LegacyMappingKey(clientId, fy))
            .Where(IsLegacyMappingCompatibleWithActiveProfile)
            .ToArray();
    }
    private bool IsLegacyMappingCompatibleWithActiveProfile(FinancialStatementMappingPreference item)
    {
        if (!item.Kind.Equals("Ledger", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(item.MappingKey)) return false;
        var candidates = _mappingCatalog.Where(x => x.MappingKey.Equals(item.MappingKey, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0) return false;
        if (string.IsNullOrWhiteSpace(item.FsHead) && string.IsNullOrWhiteSpace(item.NoteHead) && string.IsNullOrWhiteSpace(item.NoteSubHead)) return true;
        return candidates.Any(x =>
            x.FsHead.Equals(item.FsHead ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
            x.NoteHead.Equals(item.NoteHead ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
            x.NoteSubHead.Equals(item.NoteSubHead ?? string.Empty, StringComparison.OrdinalIgnoreCase));
    }
    private IReadOnlyList<FinancialStatementMappingPreference> ReadMappings(string key)
    {
        try { return JsonSerializer.Deserialize<List<FinancialStatementMappingPreference>>(_preferences.Get(key)) ?? []; } catch { return []; }
    }
    private void ApplyMappings(IReadOnlyList<FinancialStatementMappingPreference> saved, IEnumerable<FinancialStatementLedgerRow> ledgers)
    {
        var previousPeriod = ReferenceEquals(ledgers, PyTrialBalance);
        var period = previousPeriod ? "PY" : "FY";
        bool PeriodMatch(FinancialStatementMappingPreference x) => string.IsNullOrWhiteSpace(x.Period) || x.Period.Equals(period, StringComparison.OrdinalIgnoreCase);
        var byId = saved.Where(x => x.Kind == "Ledger" && PeriodMatch(x) &&
                                    FinancialStatementPresentationExportService.IsUsableTallyMasterId(x.Identity) &&
                                    !x.Identity.Equals(x.Name, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.Identity, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        var byName = saved.Where(x => x.Kind == "Ledger" && PeriodMatch(x) && x.Name.Length > 0).GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        var profile = FinancialStatementWorkbookService.ResolveProfileSheet(BasicInfo.EntityType);
        var ownerNames = OwnerMappingNames();
        foreach (var row in ledgers)
        {
            row.ConfigureMappingCatalog(_mappingCatalog, profile, ownerNames);
            FinancialStatementMappingPreference? item = null;
            if (FinancialStatementPresentationExportService.IsUsableTallyMasterId(row.MasterId)) byId.TryGetValue(row.MasterId.Trim(), out item);
            if (item is null) byName.TryGetValue(row.Ledger.Trim(), out item);
            if (item is null)
            {
                row.ApplyTrialSuggestion(FinancialStatementPresentationExportService.SuggestTrialHierarchy(row, BasicInfo.EntityType));
                continue;
            }
            row.AmountSign = string.IsNullOrWhiteSpace(item.AmountSign) ? "Auto" : item.AmountSign;
            row.NonCurrentAmount = item.NonCurrentAmount;
            var migratedLegacySchedule = row.RestoreScheduleMappingWithLegacy(item.ScheduleClass1, item.ScheduleClass2, item.Class5);
            if (migratedLegacySchedule)
            {
                row.SetPartialHierarchy(item.Class1, item.Class2, item.Class3, item.Class4, string.Empty,
                    string.IsNullOrWhiteSpace(item.MappingOrigin) ? "User Mapped" : item.MappingOrigin);
                continue;
            }
            // FIXED8 re-evaluates every previously automatic hierarchy against the corrected
            // original-TB defaults. Explicit User Mapped rows remain untouched. This prevents a
            // valid-but-wrong old Auto Mapped C1/C2 from surviving indefinitely after upgrade.
            if (item.MappingOrigin.Equals("Auto Mapped", StringComparison.OrdinalIgnoreCase))
            {
                row.ApplyTrialSuggestion(FinancialStatementPresentationExportService.SuggestTrialHierarchy(row, BasicInfo.EntityType));
                continue;
            }

            var legacyBlankOwnerC3 = profile.Equals("NCE", StringComparison.OrdinalIgnoreCase) &&
                item.Class3.Equals("Owner/Partners Capital Account", StringComparison.OrdinalIgnoreCase) &&
                (item.Class4.Equals("Capital Account", StringComparison.OrdinalIgnoreCase) ||
                 item.Class4.Equals("Current Account", StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(item.Class5) ||
                 item.Class5.StartsWith("Capital A/c ", StringComparison.OrdinalIgnoreCase) ||
                 item.Class5.StartsWith("Current A/c ", StringComparison.OrdinalIgnoreCase));
            if (legacyBlankOwnerC3)
            {
                var ownerDetail = row.ResolveOwnerThirdLevelFromLedger(item.Class3, item.Class4);
                row.SetPartialHierarchy(item.Class1, item.Class2, item.Class3, item.Class4, ownerDetail,
                    string.IsNullOrWhiteSpace(item.MappingOrigin) ? "User Mapped" : item.MappingOrigin);
                continue;
            }
            FinancialStatementMappingOption? option = null;
            if (!string.IsNullOrWhiteSpace(item.MappingKey))
            {
                // Restore the saved visible hierarchy first via a stable identity; never by shared MappingKey alone.
                var stableId = item.StableHierarchyId;
                if (string.IsNullOrWhiteSpace(stableId) &&
                    (!string.IsNullOrWhiteSpace(item.Class1) || !string.IsNullOrWhiteSpace(item.Class4)))
                    stableId = FinancialStatementClassificationService.StableHierarchyId(
                        item.MappingKey, item.Class1, item.Class2, item.Class3, item.Class4, item.Class5);
                if (!string.IsNullOrWhiteSpace(stableId))
                    option = _mappingCatalog.FirstOrDefault(x => x.StableHierarchyId.Equals(stableId, StringComparison.OrdinalIgnoreCase));

                option ??= _mappingCatalog.FirstOrDefault(x =>
                    x.MappingKey.Equals(item.MappingKey, StringComparison.OrdinalIgnoreCase) &&
                    x.Class1.Equals(item.Class1 ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                    x.Class2.Equals(item.Class2 ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                    x.Class3.Equals(item.Class3 ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                    x.Class4.Equals(item.Class4 ?? string.Empty, StringComparison.OrdinalIgnoreCase));

                var keyMatches = _mappingCatalog.Where(x => x.MappingKey.Equals(item.MappingKey, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                if (option is null && keyMatches.Length == 1) option = keyMatches[0];
            }
            if (string.IsNullOrWhiteSpace(item.MappingOrigin) && FinancialStatementPresentationExportService.ShouldReReviewLegacyMapping(item.MappingKey))
                option = null;

            // R9 FIXED13: explicit user selections are workbook authority. Parent-group
            // heuristics may refresh Auto Mapped rows, but must not erase a valid saved
            // User Mapped C1/C2/C3 path after the Tally group changes. Resolve again from
            // the visible workbook path so stale internal keys cannot override the grid.
            var explicitUserMapping = string.Equals(item.MappingOrigin, "User Mapped", StringComparison.OrdinalIgnoreCase);
            var explicitUserHasVisibleHierarchy = !string.IsNullOrWhiteSpace(item.Class3) ||
                                                  !string.IsNullOrWhiteSpace(item.Class4) ||
                                                  !string.IsNullOrWhiteSpace(item.Class5);
            var explicitUserVisiblePathIsValid = explicitUserMapping &&
                FinancialStatementWorkbookRules.IsValidVisiblePath(profile, item.Class3, item.Class4, item.Class5);
            if (explicitUserVisiblePathIsValid)
            {
                row.SetPartialHierarchy(item.Class1, item.Class2, item.Class3, item.Class4, item.Class5, "User Mapped");
                if (row.HasResolvedMapping) continue;
            }

            if (option is not null &&
                (!explicitUserMapping || !explicitUserHasVisibleHierarchy) &&
                !FinancialStatementPresentationExportService.IsObviouslyIncompatibleHierarchy(row, item.Class1, item.Class2, item.Class3, item.Class4, profile) &&
                !FinancialStatementPresentationExportService.IsObviouslyIncompatibleMapping(row, option.MappingKey, profile))
            {
                row.SetMappingOption(option, string.IsNullOrWhiteSpace(item.MappingOrigin) ? "User Mapped" : item.MappingOrigin, item.Class5);
                continue;
            }
            // R15 R14 migration: expand R15 R13 C1/C2-only automatic rows through the
            // deterministic trial-workbook suggestion. Explicit User Mapped rows remain intact.
            if (!item.MappingOrigin.Equals("User Mapped", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(item.MappingKey) && string.IsNullOrWhiteSpace(item.Class4))
            {
                row.ApplyTrialSuggestion(FinancialStatementPresentationExportService.SuggestTrialHierarchy(row, BasicInfo.EntityType));
                continue;
            }

            if (!FinancialStatementLedgerRow.IsHierarchySequenceContiguous(item.Class1, item.Class2, item.Class3, item.Class4, item.Class5))
            {
                row.ApplyTrialSuggestion(FinancialStatementPresentationExportService.SuggestTrialHierarchy(row, BasicInfo.EntityType));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(item.Class1) || !string.IsNullOrWhiteSpace(item.Class2) ||
                !string.IsNullOrWhiteSpace(item.Class3) || !string.IsNullOrWhiteSpace(item.Class4))
            {
                // R9 FIXED13: only automatic/legacy rows may be re-suggested from the live
                // Tally group. A User Mapped row is restored exactly as saved and then resolved
                // against the current workbook catalog; if the workbook path is no longer valid
                // it remains visibly Unmapped for review instead of being silently replaced.
                if (!explicitUserMapping &&
                    FinancialStatementPresentationExportService.IsObviouslyIncompatibleHierarchy(row, item.Class1, item.Class2, item.Class3, item.Class4, profile))
                {
                    row.ApplyTrialSuggestion(FinancialStatementPresentationExportService.SuggestTrialHierarchy(row, BasicInfo.EntityType));
                    continue;
                }
                row.SetPartialHierarchy(item.Class1, item.Class2, item.Class3, item.Class4, item.Class5,
                    string.IsNullOrWhiteSpace(item.MappingOrigin) ? "User Mapped" : item.MappingOrigin);
                if (!explicitUserMapping && !string.IsNullOrWhiteSpace(row.Mapping) &&
                    FinancialStatementPresentationExportService.IsObviouslyIncompatibleMapping(row, row.Mapping, profile))
                    row.ApplyTrialSuggestion(FinancialStatementPresentationExportService.SuggestTrialHierarchy(row, BasicInfo.EntityType));
                continue;
            }
            row.ApplyTrialSuggestion(FinancialStatementPresentationExportService.SuggestTrialHierarchy(row, BasicInfo.EntityType));
        }
    }
    private void SaveMappings()
    {
        if (_client is null) return;
        var rows = new List<FinancialStatementMappingPreference>();
        var mappedCount = 0;
        void AddLedgerRows(IEnumerable<FinancialStatementLedgerRow> sourceRows, string period)
        {
            foreach (var row in sourceRows)
            {
                if (row.HasResolvedMapping && row.HasCompleteScheduleMapping) mappedCount++;
                var hasHierarchy = !string.IsNullOrWhiteSpace(row.Classification1) || !string.IsNullOrWhiteSpace(row.Classification2) ||
                                   !string.IsNullOrWhiteSpace(row.Classification3) || !string.IsNullOrWhiteSpace(row.Classification4) ||
                                   !string.IsNullOrWhiteSpace(row.Classification5);
                var hasSchedule = !string.IsNullOrWhiteSpace(row.ScheduleClass1) || !string.IsNullOrWhiteSpace(row.ScheduleClass2);
                if (!string.IsNullOrWhiteSpace(row.Mapping) || hasHierarchy || hasSchedule)
                    rows.Add(new FinancialStatementMappingPreference(
                        "Ledger", MappingIdentity(row), row.Ledger.Trim(),
                        row.Mapping.Trim(), row.Note.Trim(), row.AmountSign, row.FsHead, row.NoteHead, row.NoteSubHead,
                        row.Classification1, row.Classification2, row.Classification3, row.Classification4, row.Classification5,
                        row.MappingOrigin, row.NonCurrentAmount, row.CanonicalMappingId, null, row.StableHierarchyId,
                        period, row.ScheduleClass1, row.ScheduleClass2));
            }
        }
        AddLedgerRows(FyTrialBalance, "FY");
        AddLedgerRows(PyTrialBalance, "PY");
        _preferences.Set(ProfileMappingKey(_client.Id, _client.FinancialYear), JsonSerializer.Serialize(rows));
        ApplyMappings(rows, PyTrialBalance);
        RefreshNotePreview();
        var tbPending = FyTrialBalance.Count(x => (!HideZeroClosingBalance || x.Closing != 0m) && !x.HasResolvedMapping);
        var schedulePending = FyTrialBalance.Count(x => (!HideZeroClosingBalance || x.Closing != 0m) && x.HasScheduleContext && !x.HasCompleteScheduleMapping);
        StatusText = $"Audit financial-statement mappings saved · {mappedCount} fully ready · {tbPending} TB row(s) still require classification" +
                     (schedulePending > 0 ? $" · {schedulePending} party schedule row(s) still require classification." : ".");
    }

    private void ApplyMappingToSameTallyGroup()
    {
        var source = SelectedFyTrialBalanceRow;
        if (source is null) { StatusText = "Select one FY Trial Balance ledger first."; return; }
        var tallyGroup = source.ParentGroup.Trim();
        if (tallyGroup.Length == 0) { StatusText = $"{source.Ledger} has no Tally Parent Group, so a group mapping cannot be applied."; return; }
        if (!source.HasResolvedMapping)
        {
            StatusText = "Complete a valid workbook mapping for the selected ledger first. Apply to Same Tally Group copies only C1-C3; schedule-only detail is never propagated.";
            return;
        }

        var fyResult = ApplyHierarchyToGroup(source, FyTrialBalance, tallyGroup);
        var pyResult = SameTallyGroupScope.Equals("FY + PY", StringComparison.OrdinalIgnoreCase)
            ? ApplyHierarchyToGroup(source, PyTrialBalance, tallyGroup)
            : (Applied: 0, Preserved: 0, Skipped: 0);
        RefreshNotePreview();
        StatusText = $"Tally Group '{tallyGroup}' · FY applied {fyResult.Applied} · already/partly mapped preserved {fyResult.Preserved} · incompatible skipped {fyResult.Skipped}" +
                     (SameTallyGroupScope.Equals("FY + PY", StringComparison.OrdinalIgnoreCase)
                         ? $" · PY applied {pyResult.Applied} · already/partly mapped preserved {pyResult.Preserved} · incompatible skipped {pyResult.Skipped}."
                         : ".");
    }

    private static (int Applied, int Preserved, int Skipped) ApplyHierarchyToGroup(
        FinancialStatementLedgerRow source, IEnumerable<FinancialStatementLedgerRow> rows, string tallyGroup)
    {
        var applied = 0; var preserved = 0; var skipped = 0;
        foreach (var target in rows.Where(x => x.ParentGroup.Trim().Equals(tallyGroup, StringComparison.OrdinalIgnoreCase)))
        {
            if (ReferenceEquals(source, target)) continue;
            if (FinancialStatementTrialBalanceOrder.IsProfitLossAccountLedger(target.Ledger)) { skipped++; continue; }
            if (target.HasAnyMappingSelection) { preserved++; continue; }
            if (target.TryApplyResolvedHierarchyFrom(source, "User Mapped")) applied++;
            else skipped++;
        }
        return (applied, preserved, skipped);
    }

    private static string MappingIdentity(FinancialStatementLedgerRow row)
        => FinancialStatementPresentationExportService.IsUsableTallyMasterId(row.MasterId) ? row.MasterId.Trim() : row.Ledger.Trim();

    private static bool SameLedgerIdentity(FinancialStatementLedgerRow left, FinancialStatementLedgerRow right)
    {
        if (FinancialStatementPresentationExportService.IsUsableTallyMasterId(left.MasterId) &&
            FinancialStatementPresentationExportService.IsUsableTallyMasterId(right.MasterId))
            return left.MasterId.Trim().Equals(right.MasterId.Trim(), StringComparison.OrdinalIgnoreCase);
        return left.Ledger.Trim().Equals(right.Ledger.Trim(), StringComparison.OrdinalIgnoreCase);
    }
    private void RebuildTemplatePresentation()
    {
        var ownerNames = OwnerMappingNames();
        _mappingCatalog = FinancialStatementClassificationService.ApplyOwnerAccountLabels(
            FinancialStatementWorkbookService.BuildMappingCatalog(TemplatePath(), BasicInfo.EntityType),
            ownerNames);
        ReplaceOptions(FsHeadOptions, _mappingCatalog.Select(x => x.FsHead));
        ReplaceOptions(NoteHeadOptions, _mappingCatalog.Select(x => x.NoteHead));
        ReplaceOptions(NoteSubHeadOptions, _mappingCatalog.Select(x => x.NoteSubHead));
        foreach (var row in FyTrialBalance.Concat(PyTrialBalance))
            row.ConfigureMappingCatalog(_mappingCatalog, FinancialStatementWorkbookService.ResolveProfileSheet(BasicInfo.EntityType), ownerNames);
        BuildNoteList();
        RefreshNotePreview();
    }
    private void ReloadSelectedProfileMappings()
    {
        if (_client is null || (FyTrialBalance.Count == 0 && PyTrialBalance.Count == 0)) return;
        var mappings = LoadMappings(_client.Id, _client.FinancialYear);
        ApplyMappings(mappings, FyTrialBalance);
        ApplyMappings(mappings, PyTrialBalance);
        RefreshNotePreview();
        StatusText = $"Mapping profile changed to {BasicInfo.EntityType}. C1-C3 now follow this profile's original-workbook hierarchy.";
    }
    private IReadOnlyList<string> OwnerMappingNames()
    {
        // Original NCE workbook authority: only Basic Info Owner/Partners feeds capital C3.
        var names = OwnerPartners.Where(x => !string.IsNullOrWhiteSpace(x.Name)).Select(x => x.Name.Trim()).ToList();
        if (names.Count == 0)
            names.AddRange(Signatories.Where(x => !string.IsNullOrWhiteSpace(x.Name)).Select(x => x.Name.Trim()));
        if (names.Count == 0 && !string.IsNullOrWhiteSpace(BasicInfo.SignatoryName)) names.Add(BasicInfo.SignatoryName.Trim());
        return names.Distinct(StringComparer.OrdinalIgnoreCase).Take(11).ToArray();
    }

    private static void ReplaceOptions(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x))) if (seen.Add(value)) target.Add(value);
    }
    private FinancialStatementMappingOption? ResolveMappingOption(FinancialStatementLedgerRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.StableHierarchyId))
        {
            var stable = _mappingCatalog.FirstOrDefault(x => x.StableHierarchyId.Equals(row.StableHierarchyId, StringComparison.OrdinalIgnoreCase));
            if (stable is not null) return stable;
        }
        if (!string.IsNullOrWhiteSpace(row.Classification1) && !string.IsNullOrWhiteSpace(row.Classification2) &&
            !string.IsNullOrWhiteSpace(row.Classification3) && !string.IsNullOrWhiteSpace(row.Classification4))
        {
            var exact = _mappingCatalog.FirstOrDefault(x =>
                (string.IsNullOrWhiteSpace(row.Mapping) || x.MappingKey.Equals(row.Mapping, StringComparison.OrdinalIgnoreCase)) &&
                x.Class1.Equals(row.Classification1, StringComparison.OrdinalIgnoreCase) &&
                x.Class2.Equals(row.Classification2, StringComparison.OrdinalIgnoreCase) &&
                x.Class3.Equals(row.Classification3, StringComparison.OrdinalIgnoreCase) &&
                x.Class4.Equals(row.Classification4, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }
        if (string.IsNullOrWhiteSpace(row.Mapping)) return null;
        var keyMatches = _mappingCatalog.Where(x => x.MappingKey.Equals(row.Mapping, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return keyMatches.Length == 1 ? keyMatches[0] : null;
    }
    private bool FilterTrialBalanceRow(object item)
        => item is not FinancialStatementLedgerRow row || !HideZeroClosingBalance || row.Closing != 0m;

    private bool FilterStockRow(object item)
        => item is not FinancialStatementStockRow row || !HideZeroClosingBalance || !row.ClosingBalance.HasValue || row.ClosingBalance.Value != 0m;

    private async Task ConnectPreviousYearTallyAsync()
    {
        if (_client is null) { StatusText = "Select an Audit client first."; return; }
        try
        {
            using var http = new TallyHttpService(
                _preferences.Get(ApplicationSettingKeys.TallyHost),
                _preferences.GetInt(ApplicationSettingKeys.TallyPort, 9000));
            var companies = await http.DiscoverCompaniesAsync().ConfigureAwait(true);
            var py = PyCaption;
            var normalizedGstin = TallyCompanyInfo.NormalizeGstin(_client.Gstin);
            var candidates = companies
                .Where(c => TallyCompanyInfo.NormalizeFinancialYear(c.FinancialYear, c.BooksFrom).Equals(py, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => normalizedGstin.Length > 0 && TallyCompanyInfo.NormalizeGstin(c.Gstin).Equals(normalizedGstin, StringComparison.OrdinalIgnoreCase))
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (candidates.Length == 0)
            {
                StatusText = $"No open Tally company for PY {py} was found on the configured local Tally endpoint.";
                return;
            }

            var selected = candidates[0];
            BasicInfo.PreviousYearTallyCompany = selected.Name;
            BasicInfo.PreviousYearCompanyName = selected.Name;
            BasicInfo.PreviousYearTallyCompanyKey = selected.CompanyKey;

            PyTrialBalance.Clear(); PyDebtors.Clear(); PyCreditors.Clear(); PyStock.Clear();
            var ledgers = await http.ExportLedgerNameAuthoritiesAsync(selected).ConfigureAwait(true);
            var pySourceOrder = 0;
            var pyRows = ledgers.Select(ledger => new FinancialStatementLedgerRow
            {
                Ledger = string.IsNullOrWhiteSpace(ledger.DisplayName) ? ledger.ExactName : ledger.DisplayName,
                ParentGroup = ledger.Parent ?? string.Empty,
                PrimaryGroup = FinancialStatementTrialBalanceOrder.ResolvePrimaryGroup(ledger.Parent ?? string.Empty, null),
                MasterId = ledger.MasterId,
                Opening = ledger.OpeningBalance ?? 0m,
                Closing = ledger.ClosingBalance ?? ledger.OpeningBalance ?? 0m,
                SourceOrder = pySourceOrder++
            }).ToArray();
            foreach (var row in FinancialStatementTrialBalanceOrder.Order(pyRows))
            {
                PyTrialBalance.Add(row);
                var grouping = $"{row.ParentGroup} {row.PrimaryGroup}";
                if (grouping.Contains("Sundry Debtors", StringComparison.OrdinalIgnoreCase)) { row.ConfigureScheduleContext("Debtor"); PyDebtors.Add(row); }
                if (grouping.Contains("Sundry Creditors", StringComparison.OrdinalIgnoreCase)) { row.ConfigureScheduleContext("Creditor"); PyCreditors.Add(row); }
            }

            try
            {
                var stockXml = await http.ExportStockMastersAsync(selected).ConfigureAwait(true);
                var parsed = new TallyMasterImportService().ParseXmlContent(stockXml, _client.Id, py, selected.Name, "Audit PY Tally Stock");
                foreach (var item in parsed.Records.Where(x => x.Type.Equals("Stock Item", StringComparison.OrdinalIgnoreCase)))
                    PyStock.Add(new FinancialStatementStockRow
                    {
                        Item = item.Name, Group = item.Parent, Category = item.StockCategoryName, Unit = item.BaseUnit,
                        HsnSac = item.HsnSac, GstRate = item.GstRate ?? 0m, ValuationMethod = item.ValuationMethod
                    });
            }
            catch { }

            var mappings = LoadMappings(_client.Id, _client.FinancialYear);
            ApplyMappings(mappings, PyTrialBalance);
            SaveBasicInfo();
            RefreshNotePreview();
            OnPropertyChanged(nameof(BasicInfo));
            OnPropertyChanged(nameof(PreviousYearDataAvailable));
            OnPropertyChanged(nameof(PreviousYearStatus));
            StatusText = $"Previous-year Audit Tally source selected: {selected.Name} · FY {py}. PY TB/party authority refreshed.";
        }
        catch (Exception ex)
        {
            StatusText = $"Previous-year Audit Tally discovery failed: {ex.Message}";
        }
    }

    private void ExportWorkbook(bool withLinks)
    {
        if (_client is null) return;
        // R15 R13: export is read-only over the current Audit preparation grid; save is explicit.
        var template=TemplatePath(); if(!File.Exists(template)){StatusText="Financial Statement template library is missing.";return;}
        var d=new SaveFileDialog{Filter="Excel Workbook (*.xlsx)|*.xlsx",FileName=$"{Safe(_client.ClientName)}_Audit_Financial_Statements_{Safe(_client.FinancialYear)}{(withLinks?"_Linked":"_Values")}.xlsx"};
        if(d.ShowDialog()!=true)return; var presentation=PresentationTemplatePath();
        try{FinancialStatementPresentationExportService.Export(presentation,template,d.FileName,BasicInfo,FyTrialBalance,PyTrialBalance,Signatories,OwnerPartners,withLinks);StatusText=withLinks?"Complete linked Audit financial-statement pack exported.":"Complete values-only Audit financial-statement pack exported.";}
        catch(Exception ex){StatusText=$"Audit Excel export finalisation failed: {ex.Message}";}
    }
    private void ExportPdf()
    {
        if(_client is null)return;
        // R15 R13: PDF export must not persist or remap rows.
        var template=TemplatePath(); if(!File.Exists(template)){StatusText="Financial Statement template library is missing.";return;}
        var d=new SaveFileDialog{Filter="PDF (*.pdf)|*.pdf",FileName=$"{Safe(_client.ClientName)}_Audit_Financial_Statements_{Safe(_client.FinancialYear)}.pdf"}; if(d.ShowDialog()!=true)return;
        var t=Path.Combine(Path.GetTempPath(),$"AAH_AUDIT_FS_{Guid.NewGuid():N}.xlsx"); var presentation=PresentationTemplatePath();
        try{FinancialStatementPresentationExportService.Export(presentation,template,t,BasicInfo,FyTrialBalance,PyTrialBalance,Signatories,OwnerPartners,true,d.FileName);StatusText="Complete Audit financial-statement PDF exported.";}
        catch(Exception ex){StatusText=$"PDF export requires desktop Microsoft Excel: {ex.Message}";} finally{try{File.Delete(t);}catch{}}
    }
    private static string TemplatePath()=>Path.Combine(AppContext.BaseDirectory,"Assets","AAH_FS_Final_Complete_Library.xlsx");
    private static string PresentationTemplatePath()=>Path.Combine(AppContext.BaseDirectory,"Assets","AAH_FS_Presentation_Master.xlsx");
    private static string StateKey(long id,string fy)=>$"AuditFinancialStatements.BasicInfo.{id}.{fy.Trim()}";
    private static string LegacyMappingKey(long id,string fy)=>$"AuditFinancialStatements.Mapping.{id}.{fy.Trim()}";
    private string ProfileMappingKey(long id,string fy)=>$"{LegacyMappingKey(id, fy)}.{FinancialStatementWorkbookService.ResolveProfileSheet(BasicInfo.EntityType)}";
    private static string First(params string[] v)=>v.FirstOrDefault(x=>!string.IsNullOrWhiteSpace(x))?.Trim()??string.Empty;
    private static string Safe(string v)=>string.Concat((v??string.Empty).Select(ch=>Path.GetInvalidFileNameChars().Contains(ch)?'_':ch));
    private static string PreviousFinancialYear(string fy){var p=(fy??"").Split('-',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);return p.Length==2&&int.TryParse(p[0],out var y)?$"{y-1}-{y%100:00}":string.Empty;}
    private static string FyEnd(string fy){var p=(fy??"").Split('-',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);return p.Length==2&&int.TryParse(p[0],out var y)?$"31-Mar-{y+1}":string.Empty;}
}
