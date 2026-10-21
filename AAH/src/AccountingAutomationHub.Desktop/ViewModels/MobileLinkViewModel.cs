using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Services;

namespace AccountingAutomationHub.Desktop.ViewModels;

/// <summary>
/// Fix137 R42 client/FY-specific phone invoice bridge. Presence is event-driven from the
/// existing authenticated mobile heartbeat; this page does not create another refresh timer.
/// </summary>
public sealed class MobileLinkViewModel : PageViewModel, IDisposable
{
    private readonly ActiveClientContext _clientContext;
    private readonly SecureLanServerService? _server;
    private string _automaticMobileIdentityText = "Waiting for an authenticated Android heartbeat";
    private bool _showMappingHere;
    private string _presenceText = "Offline / Waiting";
    private bool _sameClientOnline;
    private bool _mobileOnline;
    private int _pendingCount;
    private MobileInvoiceQueueItem? _selectedScan;
    private string _statusText = "Open this Client/FY on an authorised signed-in Android phone; Mobile Link matches it automatically.";
    private string _rawOcr = string.Empty;
    private string _payloadPreview = string.Empty;
    private string _scanImagePath = string.Empty;
    // R76: Review stays in the authenticated Phone/Mobile Link FIFO. No second
    // Purchase/Sales scanner, ZIP selector, XLSX intermediary or import exit.
    private string _verifiedDirection = string.Empty;
    private string _invoiceSourceParties = string.Empty;
    private string _accountingReviewFocus = "Waiting for a Phone Link invoice.";

    private bool _loading;
    private bool _disposed;
    private bool _reconcilingMapping;
    private string _mappingValidationText = "Select a queued invoice to validate GST and totals.";
    private readonly HashSet<decimal> _recognizedSlabRates = new();
    private readonly Dictionary<decimal, MobileInvoiceTaxSlabRow> _authoritativeTaxSlabs = new();
    private bool _summaryLayoutAuthority;
    // R90 review is per selected scan; never persists across scans or Clients/FYs.
    private readonly List<string> _ocrSourceFlags = new();
    private bool _reviewedOcrDiscrepancies;
    private bool _isOnnxReview;

    private string _documentOcrStatusText = "Windows document OCR has not started.";
    private static readonly decimal[] SupportedGstRates = [0m, 0.1m, 0.25m, 1m, 1.5m, 3m, 5m, 6m, 7.5m, 12m, 18m, 28m, 40m];

    public MobileLinkViewModel(ActiveClientContext clientContext, SecureLanServerService? server)
        : base("Mobile Link", "Client/FY-specific phone link, live presence and invoice verification queue")
    {
        _clientContext = clientContext;
        _server = server;
        Queue = new ObservableCollection<MobileInvoiceQueueItem>();
        HeaderFields = new ObservableCollection<MobileInvoiceFieldRow>();
        Lines = new ObservableCollection<MobileInvoiceLineRow>();
        TaxSlabs = new ObservableCollection<MobileInvoiceTaxSlabRow>();
        LedgerOptions = new ObservableCollection<string>();
        StockItemOptions = new ObservableCollection<string>();
        UnitOptions = new ObservableCollection<string>();
        RefreshCommand = new RelayCommand(Refresh);
        OpenInInvoiceOcrCommand = new RelayCommand(OpenSelectedInInvoiceOcr,
            () => SelectedScan is { ImagePaths.Count: > 0 });
        RetryChildStage1Command = new RelayCommand(RetrySelectedChildStage1,
            () => SelectedScan is { ImagePaths.Count: > 0 } scan &&
            scan.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase));
        RetryOcrCommand = new RelayCommand(RetrySelectedOcr, () => SelectedScan is { } scan &&
            scan.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase) &&
            !scan.DocumentOcrState.Equals("Ready", StringComparison.OrdinalIgnoreCase) &&
            !scan.DocumentOcrState.Equals("Processing", StringComparison.OrdinalIgnoreCase));
        ConfirmAndNextCommand = new RelayCommand(() => CompleteCurrent(true), CanConfirmCurrent);
        RejectAndNextCommand = new RelayCommand(() => CompleteCurrent(false), () => SelectedScan is not null);
        if (_server is not null)
        {
            _server.MobileLinkChanged += OnMobileLinkChanged;
            _server.MobilePresenceChanged += OnMobilePresenceChanged;
        }
    }

    public long InvoiceVisionClientId => _clientContext.CurrentClient?.Id ?? 0L;
    public string InvoiceVisionFinancialYear => _clientContext.CurrentClient?.FinancialYear ?? string.Empty;

    public ObservableCollection<MobileInvoiceQueueItem> Queue { get; }
    public ObservableCollection<MobileInvoiceFieldRow> HeaderFields { get; }
    public ObservableCollection<MobileInvoiceLineRow> Lines { get; }
    public ObservableCollection<MobileInvoiceTaxSlabRow> TaxSlabs { get; }
    public ObservableCollection<string> LedgerOptions { get; }
    public ObservableCollection<string> StockItemOptions { get; }
    public ObservableCollection<string> UnitOptions { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenInInvoiceOcrCommand { get; }
    public RelayCommand RetryChildStage1Command { get; }
    public RelayCommand RetryOcrCommand { get; }
    public RelayCommand ConfirmAndNextCommand { get; }
    public RelayCommand RejectAndNextCommand { get; }

    public string AutomaticMobileIdentityText { get => _automaticMobileIdentityText; private set => SetProperty(ref _automaticMobileIdentityText, value); }
    public bool ShowMappingHere
    {
        get => _showMappingHere;
        set
        {
            if (!SetProperty(ref _showMappingHere, value) || _loading || _disposed) return;
            // R46 R22: routing is Client/FY-specific, but mobile identity comes from the
            // authenticated Android heartbeat. There is no editable User-ID binding.
            if (_server is null || _clientContext.CurrentClient is not { } client) return;
            try
            {
                _server.SaveMobileLinkRoutingConfiguration(client.Id, client.FinancialYear, value);
                StatusText = value
                    ? "Show Mapping Here is ON. Authorised phone scans for this Client/FY will queue on Windows."
                    : "Show Mapping Here is OFF. New scans will stay in mobile review mode.";
            }
            catch (Exception exception)
            {
                _showMappingHere = !value;
                OnPropertyChanged(nameof(ShowMappingHere));
                StatusText = "Could not change Mobile Link routing: " + exception.Message;
            }
        }
    }
    public string PresenceText { get => _presenceText; private set => SetProperty(ref _presenceText, value); }
    public bool SameClientOnline
    {
        get => _sameClientOnline;
        private set
        {
            if (!SetProperty(ref _sameClientOnline, value)) return;
            OnPropertyChanged(nameof(PresenceBrush));
            OnPropertyChanged(nameof(PresenceDotText));
        }
    }
    public bool MobileOnline
    {
        get => _mobileOnline;
        private set
        {
            if (!SetProperty(ref _mobileOnline, value)) return;
            OnPropertyChanged(nameof(PresenceBrush));
            OnPropertyChanged(nameof(PresenceDotText));
        }
    }
    public Brush PresenceBrush => SameClientOnline ? Brushes.LimeGreen : MobileOnline ? Brushes.Orange : Brushes.Gray;
    public string PresenceDotText => SameClientOnline ? "ONLINE" : MobileOnline ? "OTHER CLIENT/FY" : "OFFLINE";
    public int PendingCount { get => _pendingCount; private set => SetProperty(ref _pendingCount, value); }
    public string QueueSummary => PendingCount == 1 ? "1 invoice waiting" : $"{PendingCount:N0} invoices waiting";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string MappingValidationText { get => _mappingValidationText; private set => SetProperty(ref _mappingValidationText, value); }
    public string DocumentOcrStatusText { get => _documentOcrStatusText; private set => SetProperty(ref _documentOcrStatusText, value); }
    private string _childStage1StatusText = "Invoice OCR Child: select a Phone Link invoice.";
    public string ChildStage1StatusText { get => _childStage1StatusText; private set => SetProperty(ref _childStage1StatusText, value); }
    public string RawOcr { get => _rawOcr; private set => SetProperty(ref _rawOcr, value); }
    public string PayloadPreview { get => _payloadPreview; private set => SetProperty(ref _payloadPreview, value); }
    public string ScanImagePath { get => _scanImagePath; private set => SetProperty(ref _scanImagePath, value); }
    public IReadOnlyList<string> InvoiceDirectionOptions { get; } = ["Purchase", "Sales"];
    public string VerifiedDirection
    {
        get => _verifiedDirection;
        set
        {
            if (!SetProperty(ref _verifiedDirection, value ?? string.Empty) || _loading) return;
            SyncCounterpartyFromVerifiedIdentity();
            UpdateValidationState();
        }
    }
    public string InvoiceSourceParties { get => _invoiceSourceParties; private set => SetProperty(ref _invoiceSourceParties, value); }
    public string AccountingReviewFocus { get => _accountingReviewFocus; private set => SetProperty(ref _accountingReviewFocus, value); }
    public Visibility OcrDiscrepancyVisibility => _ocrSourceFlags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string OcrDiscrepancyText => _ocrSourceFlags.Count > 0
        ? "Source OCR discrepancies (check the original image, correct the fields, then acknowledge): " + string.Join(" · ", _ocrSourceFlags.Take(7))
        : string.Empty;
    public bool ReviewedOcrDiscrepancies
    {
        get => _reviewedOcrDiscrepancies;
        set
        {
            if (!SetProperty(ref _reviewedOcrDiscrepancies, value) || _loading) return;
            UpdateValidationState();
        }
    }


    public bool IsAccountingInvoice => SelectedScan?.RecognitionMode.Equals("Accounting Invoice", StringComparison.OrdinalIgnoreCase) == true;
    public bool IsInventoryInvoice => !IsAccountingInvoice;
    public Visibility AccountingRowsVisibility => IsAccountingInvoice ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InventoryRowsVisibility => IsInventoryInvoice ? Visibility.Visible : Visibility.Collapsed;

    public MobileInvoiceQueueItem? SelectedScan
    {
        get => _selectedScan;
        set
        {
            if (!SetProperty(ref _selectedScan, value)) return;
            LoadSelectedScan();
            ConfirmAndNextCommand.RaiseCanExecuteChanged();
            RejectAndNextCommand.RaiseCanExecuteChanged();
            RetryOcrCommand.RaiseCanExecuteChanged();
            OpenInInvoiceOcrCommand.RaiseCanExecuteChanged();
            RetryChildStage1Command.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsAccountingInvoice));
            OnPropertyChanged(nameof(IsInventoryInvoice));
            OnPropertyChanged(nameof(AccountingRowsVisibility));
            OnPropertyChanged(nameof(InventoryRowsVisibility));
        }
    }

    public void PrepareForDisplay() => Refresh();

    private void RetrySelectedOcr()
    {
        if (_server is null || _clientContext.CurrentClient is not { } client || SelectedScan is not { } scan)
            return;
        var result = _server.RetryMobileInvoiceDocumentOcr(client.Id, client.FinancialYear, scan.ScanId);
        StatusText = result;
        // A manual refresh reports the most recent recognition state. A retry must
        // not create a duplicate scan or bypass confirmation when OCR is unavailable.
        Refresh();
        if (result.StartsWith("Cannot retry", StringComparison.OrdinalIgnoreCase) ||
            result.Contains("missing", StringComparison.OrdinalIgnoreCase))
            StatusText = result;
    }

    public void Refresh()
    {
        if (_disposed || _loading) return;
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        if (_server is null)
        {
            StatusText = "Mobile Link is available on the Main Server Windows app only.";
            return;
        }
        _loading = true;
        try
        {
            _server.EnsureInitializedForUi();
            var state = _server.GetMobileLinkClientState(client.Id, client.FinancialYear);
            AutomaticMobileIdentityText = state.LinkedUserId.Length == 0 ? "Waiting for an authenticated Android heartbeat" : $"{state.LinkedUserId} · authenticated";
            ShowMappingHere = state.ShowMappingHere;
            SameClientOnline = state.SameClientOnline;
            MobileOnline = state.MobileOnline;
            PresenceText = state.PresenceText + (string.IsNullOrWhiteSpace(state.CurrentMobileModule) ? string.Empty : $" · {state.CurrentMobileModule}");
            var rows = _server.GetMobileInvoiceQueue(client.Id, client.FinancialYear).Where(x => x.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase)).ToArray();
            // R93: a presence/queue refresh must not erase edits or re-acknowledge
            // a different photograph. Retain only while exact source/payload identity is unchanged.
            var previousScan = SelectedScan;
            var selectedId = previousScan?.ScanId;
            var previousHeaders = HeaderFields.ToDictionary(x => x.Key, x => x.SelectedValue, StringComparer.OrdinalIgnoreCase);
            var previousLines = Lines.Select(x => new[] { x.Description, x.HsnSac, x.Quantity, x.Unit,
                x.Rate, x.Discount, x.Taxable, x.GstRate, x.Cgst, x.Sgst, x.Igst, x.Cess,
                x.LineTotal, x.StockItem, x.Ledger }).ToArray();
            var previousDirection = VerifiedDirection;
            var previousReviewed = ReviewedOcrDiscrepancies;
            Queue.Clear(); foreach (var row in rows) Queue.Add(row);
            PendingCount = rows.Length;
            OnPropertyChanged(nameof(QueueSummary));
            // Fix137 R46 R3: Mobile Link verifies OCR/header evidence and Inventory Stock/Unit
            // choices only. Posting Ledger allocation belongs to Sales/Purchase and is not loaded here.
            StockItemOptions.ReplaceWith(_server.GetInvoiceStockItemNames(client.Id, client.FinancialYear));
            UnitOptions.ReplaceWith(_server.GetInvoiceUnitNames(client.Id, client.FinancialYear));
            var nextSelected = rows.FirstOrDefault(x => x.ScanId.Equals(selectedId, StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault();
            if (ReferenceEquals(SelectedScan, nextSelected)) LoadSelectedScan();
            else SelectedScan = nextSelected;
            if (previousScan is not null && nextSelected is not null &&
                previousScan.ScanId.Equals(nextSelected.ScanId, StringComparison.OrdinalIgnoreCase) &&
                previousScan.ClientId == nextSelected.ClientId &&
                previousScan.FinancialYear.Equals(nextSelected.FinancialYear, StringComparison.OrdinalIgnoreCase) &&
                previousScan.DocumentOcrState.Equals("Ready", StringComparison.OrdinalIgnoreCase) &&
                nextSelected.DocumentOcrState.Equals("Ready", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(previousScan.PayloadJson, nextSelected.PayloadJson, StringComparison.Ordinal) &&
                previousLines.Length == Lines.Count)
            {
                foreach (var field in HeaderFields)
                    if (previousHeaders.TryGetValue(field.Key, out var edited)) field.SelectedValue = edited;
                for (var i = 0; i < Lines.Count; i++)
                {
                    var line = Lines[i]; var values = previousLines[i];
                    line.Description = values[0]; line.HsnSac = values[1]; line.Quantity = values[2];
                    line.Unit = values[3]; line.Rate = values[4]; line.Discount = values[5];
                    line.Taxable = values[6]; line.GstRate = values[7]; line.Cgst = values[8];
                    line.Sgst = values[9]; line.Igst = values[10]; line.Cess = values[11];
                    line.LineTotal = values[12]; line.StockItem = values[13]; line.Ledger = values[14];
                }
                VerifiedDirection = previousDirection;
                _reviewedOcrDiscrepancies = previousReviewed && _ocrSourceFlags.Count > 0;
                OnPropertyChanged(nameof(ReviewedOcrDiscrepancies));
                ReconcileTaxEvidenceFromLines();
                RebuildTaxSlabs();
                UpdateValidationState();
            }
            RetryOcrCommand.RaiseCanExecuteChanged();
            StatusText = state.SameClientOnline
                ? $"Mobile Link online automatically for this Client/FY · {QueueSummary}."
                : state.MobileOnline
                    ? $"An authorised Android session is online on another Client/FY · {QueueSummary}."
                    : $"Waiting for an authenticated Android heartbeat for this Client/FY · {QueueSummary}.";
        }
        catch (Exception exception) { StatusText = "Mobile Link refresh failed: " + exception.Message; }
        finally { _loading = false; }
    }

    private void RefreshPresence()
    {
        if (_disposed) return;
        var client = _clientContext.CurrentClient;
        if (client is null || _server is null) return;
        try
        {
            var state = _server.GetMobileLinkPresenceState(client.Id, client.FinancialYear);
            SameClientOnline = state.SameClientOnline;
            MobileOnline = state.MobileOnline;
            AutomaticMobileIdentityText = state.LinkedUserId.Length == 0 ? "Waiting for an authenticated Android heartbeat" : $"{state.LinkedUserId} · authenticated";
            PresenceText = state.PresenceText + (string.IsNullOrWhiteSpace(state.CurrentMobileModule) ? string.Empty : $" · {state.CurrentMobileModule}");
        }
        catch { /* Presence heartbeat must never disturb the mapping queue UI. */ }
    }

    private void RetrySelectedChildStage1()
    {
        if (_server is null || _clientContext.CurrentClient is not { } client || SelectedScan is not { } scan)
            return;
        var status = _server.RetryMobileInvoiceChildStage1(client.Id, client.FinancialYear, scan.ScanId);
        Refresh();
        StatusText = status;
    }

    private void OpenSelectedInInvoiceOcr()
    {
        if (SelectedScan is not { } scan) return;
        try {
            var verifiedStage1 = InvoiceOcrChildBridge.OpenReview(scan.ImagePaths, scan.ClientId, scan.FinancialYear, scan.ScanId, scan.ChildStage1Archive, scan.EvidenceFolder);
            StatusText = verifiedStage1 ? "Opened original photo AND verified provisional Stage 1 layout in Invoice OCR Child." : "Opened the original Phone Link photograph. Stage 1 has not returned a verified handoff result; see Child status.";
        } catch (Exception ex) {
            StatusText = "Invoice OCR Child could not open: " + ex.Message;
        }
    }

    private void LoadSelectedScan()
    {
        HeaderFields.Clear(); Lines.Clear(); TaxSlabs.Clear(); _recognizedSlabRates.Clear(); _authoritativeTaxSlabs.Clear(); _summaryLayoutAuthority = false; RawOcr = string.Empty; PayloadPreview = string.Empty; ScanImagePath = string.Empty;
        _ocrSourceFlags.Clear(); _reviewedOcrDiscrepancies = false; _isOnnxReview = false;
        OnPropertyChanged(nameof(ReviewedOcrDiscrepancies));
        OnPropertyChanged(nameof(OcrDiscrepancyVisibility));
        OnPropertyChanged(nameof(OcrDiscrepancyText));
        VerifiedDirection = string.Empty; InvoiceSourceParties = string.Empty; AccountingReviewFocus = "Waiting for a Phone Link invoice.";
        var scan = SelectedScan; if (scan is null) { DocumentOcrStatusText = "No invoice selected."; ChildStage1StatusText = "Invoice OCR Child: no scan selected."; return; }
        ChildStage1StatusText = "Invoice OCR Child · " + scan.ChildStage1Status +
            (File.Exists(scan.ChildStage1Archive) ? "  |  Open in Invoice OCR Child to inspect Stage 1" : "");
        DocumentOcrStatusText = $"Document OCR: {scan.DocumentOcrState} · {scan.DocumentOcrMessage}";
        try
        {
            RawOcr = scan.RawOcr;
            ScanImagePath = scan.ImagePaths.FirstOrDefault(File.Exists) ?? string.Empty;
            PayloadPreview = scan.PayloadJson;

            // R46 R37: phone ML Kit is preview evidence only. Until the dedicated Windows invoice
            // OCR + geometry pass is Ready, do not materialize phone-derived accounting fields,
            // item rows or GST slabs into the Mapping authority. This prevents false 0%/5%/18%
            // accounting rows from appearing while Windows document OCR is unavailable.
            // R46 R39 historical proof literals (retired from visible R90 flow):
            // AAH Paddle Invoice OCR spatial document understanding is still processing
            // AAH Paddle Invoice OCR requires action
            if (scan.ImagePaths.Count > 0 && !scan.DocumentOcrState.Equals("Ready", StringComparison.OrdinalIgnoreCase))
            {
                MappingValidationText = scan.DocumentOcrState.Equals("Processing", StringComparison.OrdinalIgnoreCase)
                    ? "Needs Review · independent ONNX invoice OCR is still processing. Phone ML Kit is visible only in Raw OCR preview."
                    : "Needs Review · independent ONNX invoice OCR requires action. Phone ML Kit is visible only in Raw OCR preview and is not accounting authority.";
                ConfirmAndNextCommand.RaiseCanExecuteChanged();
                return;
            }

            var candidates = LoadCandidates(scan.CandidatesJson);
            var root = JsonNode.Parse(scan.PayloadJson)?.AsObject(); if (root is null) return;
            var sourceDirection = (root["direction"]?.ToString() ?? scan.Direction).Trim();
            var sourceIdentity = MobileInvoicePartyIdentityResolver.Select(root,
                _clientContext.CurrentClient?.Gstin, sourceDirection);
            VerifiedDirection = sourceIdentity.Direction;
            _isOnnxReview = root["ocrEngine"]?.ToString() == "AAH-RAPIDOCR-ONNX-R90";
            if (_isOnnxReview && root["ocrBlockingFlags"] is JsonArray r90Flags)
            {
                foreach (var flag in r90Flags)
                {
                    var value = flag?.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(value)) _ocrSourceFlags.Add(value);
                }
                OnPropertyChanged(nameof(OcrDiscrepancyVisibility));
                OnPropertyChanged(nameof(OcrDiscrepancyText));
            }

            string Role(string role)
            {
                if (root[role] is not JsonObject party) return "not identified";
                var sourceName = party["name"]?.ToString().Trim() ?? string.Empty;
                var gstin = MobileInvoicePartyIdentityResolver.NormalizeGstin(party["gstin"]?.ToString());
                var active = _clientContext.CurrentClient;
                // The exact registered tax identity may provide the canonical AAH client
                // display spelling, without rewriting the photographed source evidence.
                var displayedName = active is not null &&
                    MobileInvoicePartyIdentityResolver.ValidGstin(gstin) &&
                    gstin == MobileInvoicePartyIdentityResolver.NormalizeGstin(active.Gstin)
                        ? active.ClientName : sourceName;
                return displayedName + " / " + gstin;
            }
            InvoiceSourceParties = "Supplier: " + Role("seller") + "     Buyer: " + Role("buyer");

            _summaryLayoutAuthority = bool.TryParse(root["summaryLayoutAuthority"]?.ToString(), out var layoutAuthority) && layoutAuthority;
            if (root["gstSlabs"] is JsonArray authoritativeSlabs)
            {
                foreach (var slabNode in authoritativeSlabs.OfType<JsonObject>())
                {
                    if (!TryAmount(MobileInvoiceGstRateTranslator.Translate(Text(slabNode, "gstRate")), out var rate) || rate <= 0m) continue;
                    var row = new MobileInvoiceTaxSlabRow(
                        rate,
                        AmountOrZero(Text(slabNode, "taxable")),
                        AmountOrZero(Text(slabNode, "cgst")),
                        AmountOrZero(Text(slabNode, "sgst")),
                        AmountOrZero(Text(slabNode, "igst")),
                        "AAH invoice geometry + GST summary authority");
                    _authoritativeTaxSlabs[rate] = row;
                    _recognizedSlabRates.Add(rate);
                }
            }
            // R92: the recognized opposite invoice party is the ONE counterparty authority.
            // Do not show stale buyer/phone OCR alternatives in Purchase party dropdown.
            AddHeader("counterparty", "Counterparty", sourceIdentity.CounterpartyName,
                new Dictionary<string, IReadOnlyList<MobileInvoiceCandidate>>(StringComparer.OrdinalIgnoreCase));
            AddHeader("counterpartyPan", "Counterparty PAN", sourceIdentity.CounterpartyPan,
                new Dictionary<string, IReadOnlyList<MobileInvoiceCandidate>>(StringComparer.OrdinalIgnoreCase));
            AddHeader("counterpartyGstin", "Counterparty GSTIN", sourceIdentity.CounterpartyGstin,
                new Dictionary<string, IReadOnlyList<MobileInvoiceCandidate>>(StringComparer.OrdinalIgnoreCase));
            SyncCounterpartyFromVerifiedIdentity();
            AddHeader("invoiceNo", "Invoice / Document Number", root["invoiceNumber"]?.ToString() ?? string.Empty, candidates);
            AddHeader("invoiceDate", "Invoice / Document Date", root["invoiceDate"]?.ToString() ?? string.Empty, candidates);
            AddHeader("pos", "Place of Supply", root["placeOfSupply"]?.ToString() ?? string.Empty, candidates);
            AddHeader("taxableTotal", "Taxable Total", root["taxableTotal"]?.ToString() ?? string.Empty, candidates);
            AddHeader("discountTotal", "Discount Total (review only · not posted)", root["discountTotal"]?.ToString() ?? string.Empty, candidates);
            AddHeader("cgstTotal", "CGST Total", root["cgstTotal"]?.ToString() ?? string.Empty, candidates);
            AddHeader("sgstTotal", "SGST / UTGST Total", root["sgstTotal"]?.ToString() ?? string.Empty, candidates);
            AddHeader("igstTotal", "IGST Total", root["igstTotal"]?.ToString() ?? string.Empty, candidates);
            AddHeader("cessTotal", "Cess Total", root["cessTotal"]?.ToString() ?? string.Empty, candidates);
            AddHeader("otherCharges", "Other Charges", root["otherCharges"]?.ToString() ?? string.Empty, candidates);
            AddHeader("roundOff", "Round Off", root["roundOff"]?.ToString() ?? string.Empty, candidates);
            AddHeader("invoiceTotal", "Invoice Total", root["invoiceTotal"]?.ToString() ?? string.Empty, candidates);
            if (root["rows"] is JsonArray rows)
            {
                foreach (var node in rows.OfType<JsonObject>())
                {
                    var row = new MobileInvoiceLineRow
                    {
                        Description = Text(node,"description"), HsnSac = Text(node,"hsnSac"), Quantity = Text(node,"quantity"), Unit = Text(node,"unit"),
                        Rate = Text(node,"rate"), Discount = Text(node,"discount"), Taxable = Text(node,"taxable"), GstRate = Text(node,"gstRate"),
                        Cgst = Text(node,"cgst"), Sgst = Text(node,"sgst"), Igst = Text(node,"igst"), Cess = Text(node,"cess"), LineTotal = Text(node,"lineTotal"),
                        StockItem = Text(node,"stockItem"), Ledger = Text(node,"ledger")
                    };
                    row.PropertyChanged += OnMappingValueChanged;
                    Lines.Add(row);
                }
            }
            if (root["gstSlabRates"] is JsonArray slabRates)
            {
                foreach (var slab in slabRates)
                    if (TryAmount(MobileInvoiceGstRateTranslator.Translate(slab?.ToString()), out var rate) && IsSupportedGstRate(rate)) _recognizedSlabRates.Add(rate);
            }
            foreach (var line in Lines)
                if (TryAmount(line.GstRate, out var rate) && IsSupportedGstRate(rate)) _recognizedSlabRates.Add(rate);
            ReconcileTaxEvidenceFromLines();
            RebuildTaxSlabs();
            var completeRows = Lines.Count(x => x.Description.Length > 0 && AmountOrZero(x.Taxable) > 0m);
            AccountingReviewFocus = $"Accounting fields: invoice number/date, supplier and buyer, {completeRows}/{Lines.Count} taxable item rows, HSN, quantities, rates, GST slabs and payable. QR, stamps and footer terms are non-posting evidence.";
            UpdateValidationState();
        }
        catch (Exception exception) { StatusText = "Could not open the selected scan: " + exception.Message; MappingValidationText = "Mapping could not be validated."; }
    }

    private void SyncCounterpartyFromVerifiedIdentity()
    {
        var scan = SelectedScan;
        if (scan is null || HeaderFields.Count == 0 ||
            !InvoiceDirectionOptions.Contains(VerifiedDirection, StringComparer.OrdinalIgnoreCase)) return;
        try
        {
            var root = JsonNode.Parse(scan.PayloadJson)?.AsObject();
            if (root is null) return;
            var identity = MobileInvoicePartyIdentityResolver.Select(root, _clientContext.CurrentClient?.Gstin,
                VerifiedDirection);
            // The dropdown cannot silently switch to a name belonging to this Client's buyer
            // side; only the opposite seller/buyer identified by the reviewed direction.
            // The source opposite party is the correct review name. An existing AAH
            // ledger is NOT assumed from a similar name: duplicate names can have
            // different Tally MASTERIDs and this form lacks a verified GSTIN Master link.
            var partyName = identity.CounterpartyName;
            void Set(string key, string value)
            {
                var row = HeaderFields.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (row is null) return;
                row.AddCandidate(value, "Verified invoice party", "Source tax identity", 100);
                if (!string.Equals(row.SelectedValue, value, StringComparison.Ordinal)) row.SelectedValue = value;
            }
            _reconcilingMapping = true;
            try
            {
                Set("counterparty", partyName);
                Set("counterpartyGstin", identity.CounterpartyGstin);
                Set("counterpartyPan", identity.CounterpartyPan);
            }
            finally { _reconcilingMapping = false; }
        }
        catch (JsonException) { /* invalid source remains blocked by review validation */ }
        catch (InvalidOperationException) { /* data unavailable: do not guess a master */ }
    }

    private static string Text(JsonObject node, string key) => node[key]?.ToString() ?? string.Empty;

    private void AddHeader(string key, string label, string value, IReadOnlyDictionary<string, IReadOnlyList<MobileInvoiceCandidate>> candidates)
    {
        var row = new MobileInvoiceFieldRow(key, label, value);
        if (candidates.TryGetValue(key, out var found))
        {
            foreach (var candidate in found)
            {
                row.AddCandidate(candidate.Value, candidate.Label, candidate.Source, candidate.Confidence);
            }
        }
        row.AddCandidate(value, "Recognized value", "Current recognition", 100);
        row.PropertyChanged += OnMappingValueChanged;
        HeaderFields.Add(row);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<MobileInvoiceCandidate>> LoadCandidates(string candidatesJson)
    {
        var result = new Dictionary<string, IReadOnlyList<MobileInvoiceCandidate>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (string.IsNullOrWhiteSpace(candidatesJson)) return result;
            using var doc = JsonDocument.Parse(candidatesJson);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array) continue;
                var list = new List<MobileInvoiceCandidate>();
                foreach (var item in property.Value.EnumerateArray())
                    list.Add(new MobileInvoiceCandidate(item.TryGetProperty("label", out var l) ? l.GetString() ?? string.Empty : string.Empty,
                        item.TryGetProperty("value", out var v) ? v.GetString() ?? string.Empty : string.Empty,
                        item.TryGetProperty("source", out var s) ? s.GetString() ?? string.Empty : string.Empty,
                        item.TryGetProperty("confidence", out var c) && c.TryGetInt32(out var ci) ? ci : 0));
                result[property.Name] = list;
            }
        }
        catch { }
        return result;
    }

    private void CompleteCurrent(bool confirmed)
    {
        var client = _clientContext.CurrentClient; var scan = SelectedScan; if (client is null || scan is null || _server is null) return;
        try
        {
            if (confirmed)
            {
                ReconcileTaxEvidenceFromLines();
                RebuildTaxSlabs();
                var validation = ValidateCurrentMapping();
                if (validation.Count > 0)
                {
                    StatusText = "Cannot confirm this invoice yet: " + string.Join(" · ", validation.Take(8));
                    return;
                }
            }
            var mapped = confirmed ? BuildMappedResult(scan) : null;
            _server.CompleteMobileInvoiceReview(client.Id, client.FinancialYear, scan.ScanId, confirmed, mapped);
            if (confirmed) _server.SaveMobileInvoiceLearning(client.Id, client.FinancialYear, BuildLearningJson());
            StatusText = confirmed ? "Invoice verified and handed directly to Sales/Purchase · opening next queued scan." : "Invoice rejected · opening next queued scan.";
            Refresh();
        }
        catch (Exception exception) { StatusText = "Could not complete mobile invoice review: " + exception.Message; }
    }

    private List<string> ValidateCurrentMapping()
    {
        var issues = new List<string>();
        if (SelectedScan is { } scan && scan.ImagePaths.Count > 0 && !scan.DocumentOcrState.Equals("Ready", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(scan.DocumentOcrState.Equals("Processing", StringComparison.OrdinalIgnoreCase)
                ? "Independent ONNX invoice OCR is still processing"
                : "Independent ONNX invoice OCR requires action: " + scan.DocumentOcrMessage);
        }
        string Header(string key) => HeaderFields.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.SelectedValue.Trim() ?? string.Empty;
        // R90: OCR/geometry Ready does not mean source amount identity is reconciled.
        // A discrepancy cannot silently pass the earlier 0.25 tolerance. Only explicit
        // review together with a manually corrected exact sum can release it.
        if (_ocrSourceFlags.Count > 0 && !ReviewedOcrDiscrepancies)
            issues.Add("OCR source discrepancies require original-image review and explicit acknowledgement");
        if (_ocrSourceFlags.Count > 0 && VerifiedDirection.Length == 0)
            issues.Add("R89 seller/buyer identity and direction are unresolved");
        // Unlike R75, only the current authenticated Client/FY Phone Link queue can
        // supply a verified invoice. The reviewed direction determines which existing
        // Purchase/Sales repository receives the confirmed record.
        if (!InvoiceDirectionOptions.Contains(VerifiedDirection, StringComparer.OrdinalIgnoreCase))
            issues.Add("Select Purchase or Sales direction before confirmation");
        if (SelectedScan is { } selectedScan)
        {
            try
            {
                var source = JsonNode.Parse(selectedScan.PayloadJson)?.AsObject();
                var activeGstin = NormalizeGstin(_clientContext.CurrentClient?.Gstin ?? string.Empty);
                if (source is not null && activeGstin.Length == 15)
                {
                    var sellerGstin = NormalizeGstin(source["seller"]?["gstin"]?.ToString() ?? string.Empty);
                    var buyerGstin = NormalizeGstin(source["buyer"]?["gstin"]?.ToString() ?? string.Empty);
                    if (sellerGstin.Length == 15 && buyerGstin.Length == 15)
                    {
                        var expected = VerifiedDirection.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? sellerGstin : buyerGstin;
                        if (!expected.Equals(activeGstin, StringComparison.OrdinalIgnoreCase))
                            issues.Add("Phone Link seller/buyer GSTIN disagrees with selected Client and Purchase/Sales direction; review document identity");
                        var opposite = VerifiedDirection.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ? sellerGstin : buyerGstin;
                        var entered = NormalizeGstin(HeaderFields.FirstOrDefault(x => x.Key == "counterpartyGstin")?.SelectedValue ?? string.Empty);
                        if (!MobileInvoicePartyIdentityResolver.ValidGstin(opposite) || entered != opposite)
                            issues.Add("Counterparty GSTIN must equal the verified opposite invoice party, not the active Client");
                    }
                }
            }
            catch (JsonException) { issues.Add("Phone Link invoice identity payload cannot be validated"); }
        }
        var documentType = SelectedScan is null ? "Tax Invoice" : ReadDocumentType(SelectedScan.PayloadJson);
        var billOfSupply = documentType.Equals("Bill of Supply", StringComparison.OrdinalIgnoreCase);
        var referenceDocument = documentType.Equals("Purchase Order", StringComparison.OrdinalIgnoreCase)
            || documentType.Equals("Proforma Invoice", StringComparison.OrdinalIgnoreCase)
            || documentType.Equals("Delivery Challan", StringComparison.OrdinalIgnoreCase)
            || documentType.Equals("Receipt Voucher", StringComparison.OrdinalIgnoreCase)
            || documentType.Equals("Refund Voucher", StringComparison.OrdinalIgnoreCase)
            || documentType.Equals("Payment Voucher", StringComparison.OrdinalIgnoreCase);
        var noteDocument = documentType.Equals("Credit Note", StringComparison.OrdinalIgnoreCase) || documentType.Equals("Debit Note", StringComparison.OrdinalIgnoreCase);
        if (referenceDocument) issues.Add($"{documentType} is a reference/non-invoice document and cannot be posted through normal Sales/Purchase Confirm Mapping");
        if (noteDocument) issues.Add($"{documentType} requires the dedicated note-adjustment posting workflow and cannot be posted as a normal invoice");
        void RequiredHeader(string key, string name) { if (Header(key).Length == 0) issues.Add(name + " is required"); }
        RequiredHeader("counterparty", "Counterparty");
        RequiredHeader("invoiceNo", "Invoice / Document Number");
        RequiredHeader("invoiceDate", "Invoice / Document Date");
        RequiredHeader("invoiceTotal", "Invoice Total");
        // R46 R23: GST % is compulsory evidence for every reconstructed taxable row, including
        // Accounting-mode evidence rows that are not posting/allocation controls on this page.
        for (var index = 0; index < Lines.Count; index++)
        {
            var line = Lines[index];
            if (!TryAmount(line.Taxable, out var rowTaxable) || rowTaxable <= 0m) continue;
            if (!TryAmount(line.GstRate, out var gstRate) || !IsSupportedGstRate(gstRate))
            {
                if (!billOfSupply) issues.Add($"Row {index + 1} GST % is compulsory and must be a supported GST slab");
                continue;
            }
            var rowCgst = AmountOrZero(line.Cgst);
            var rowSgst = AmountOrZero(line.Sgst);
            var rowIgst = AmountOrZero(line.Igst);
            var expectedTax = Math.Round(rowTaxable * gstRate / 100m, 2, MidpointRounding.AwayFromZero);
            if (rowIgst > 0m)
            {
                if (rowCgst > 0m || rowSgst > 0m) issues.Add($"Row {index + 1} mixes IGST with CGST/SGST");
                if (Math.Abs(rowIgst - expectedTax) > 0.20m) issues.Add($"Row {index + 1} IGST does not reconcile to Taxable × GST %");
            }
            else if (gstRate > 0m)
            {
                var expectedComponent = Math.Round(rowTaxable * gstRate / 200m, 2, MidpointRounding.AwayFromZero);
                if (rowCgst <= 0m || rowSgst <= 0m) issues.Add($"Row {index + 1} local GST requires both CGST and SGST / UTGST");
                else
                {
                    if (Math.Abs(rowCgst - rowSgst) > 0.10m) issues.Add($"Row {index + 1} CGST and SGST / UTGST must reconcile equally");
                    if (Math.Abs(rowCgst - expectedComponent) > 0.20m || Math.Abs(rowSgst - expectedComponent) > 0.20m)
                        issues.Add($"Row {index + 1} CGST/SGST does not reconcile to Taxable × GST %");
                }
            }
        }
        // Fix137 R46 R3: Accounting-mode line reconstruction is evidence only. Sales/Purchase
        // owns item and Ledger allocation. Inventory mode still verifies the item evidence that
        // belongs to scanning, but no Mobile Link row ever requires or proposes a posting Ledger.
        if (IsInventoryInvoice)
        {
            if (Lines.Count == 0) issues.Add("At least one inventory row is required");
            for (var index = 0; index < Lines.Count; index++)
            {
                var line = Lines[index]; var prefix = $"Row {index + 1}";
                if (string.IsNullOrWhiteSpace(line.Description)) issues.Add(prefix + " description is required");
                if (string.IsNullOrWhiteSpace(line.Quantity)) issues.Add(prefix + " Quantity is required");
                if (string.IsNullOrWhiteSpace(line.Unit)) issues.Add(prefix + " Unit is required");
                if (string.IsNullOrWhiteSpace(line.Rate)) issues.Add(prefix + " Rate is required");
                if (string.IsNullOrWhiteSpace(line.StockItem)) issues.Add(prefix + " Stock Item is required");
            }
        }
        // Do not force a balance by changing source amounts: a small printed rounding
        // residual is permissible; missing rows, rather than OCR word boxes, block review.
        if (Lines.Count > 0)
        {
            var rowsWithTaxable = Lines.Where(x => AmountOrZero(x.Taxable) > 0m).ToArray();
            if (rowsWithTaxable.Length != Lines.Count)
                issues.Add("Every extracted product row requires a verified taxable value");
            var summaryTaxable = AmountOrZero(Header("taxableTotal"));
            if (summaryTaxable > 0m && (_isOnnxReview
                ? rowsWithTaxable.Sum(x => AmountOrZero(x.Taxable)) != summaryTaxable
                : Math.Abs(rowsWithTaxable.Sum(x => AmountOrZero(x.Taxable)) - summaryTaxable) > 0.25m))
                issues.Add("Product-row taxable total does not match the printed invoice summary");
        }
        if (!TryAmount(Header("invoiceTotal"), out var invoiceTotal) || invoiceTotal <= 0m) issues.Add("Invoice Total must be a recognized positive amount");
        var taxable = AmountOrZero(Header("taxableTotal"));
        var cgst = AmountOrZero(Header("cgstTotal"));
        var sgst = AmountOrZero(Header("sgstTotal"));
        var igst = AmountOrZero(Header("igstTotal"));
        var cess = AmountOrZero(Header("cessTotal"));
        var charges = AmountOrZero(Header("otherCharges"));
        var roundOff = SignedAmountOrZero(Header("roundOff"));

        if (igst <= 0m && (cgst > 0m || sgst > 0m))
        {
            if (cgst <= 0m || sgst <= 0m) issues.Add("Local invoice requires both CGST and SGST / UTGST");
            else if (Math.Abs(cgst - sgst) > 0.10m) issues.Add($"CGST and SGST / UTGST must reconcile equally for a local invoice ({cgst:0.00} vs {sgst:0.00})");
        }

        var lineRates = Lines.Select(x => AmountOrZero(x.GstRate)).Where(IsSupportedGstRate).Where(x => x > 0m).ToHashSet();
        var requiredRates = _recognizedSlabRates.Where(x => x > 0m).ToHashSet();
        if (!billOfSupply && taxable > 0m && (cgst + sgst + igst) > 0m && requiredRates.Count == 0 && lineRates.Count == 0)
            issues.Add("GST % / slab is compulsory for a taxable GST invoice");
        if (requiredRates.Count > 1 && Lines.Count == 0)
            issues.Add("Multiple GST slabs were recognized but taxable item/slab rows were not reconstructed");
        foreach (var rate in requiredRates)
            if (Lines.Count > 0 && !lineRates.Contains(rate)) issues.Add($"Recognized GST slab {rate:0.##}% is missing from reconstructed taxable rows");

        if (invoiceTotal > 0m && (taxable > 0m || cgst > 0m || sgst > 0m || igst > 0m || cess > 0m || charges > 0m))
        {
            // R46 R27: Taxable Total is already post-discount authority. Discount remains
            // review evidence only and is never subtracted again or posted to Tally.
            var reconciled = taxable + cgst + sgst + igst + cess + charges + roundOff;
            if (Math.Abs(reconciled - invoiceTotal) > 0.10m)
                issues.Add($"Post-discount Taxable + GST + charges + Round Off does not reconcile ({reconciled:0.00} vs invoice total {invoiceTotal:0.00})");

            var calculatedRoundOff = Math.Round(invoiceTotal - (taxable + cgst + sgst + igst + cess + charges), 2, MidpointRounding.AwayFromZero);
            if (Math.Abs(calculatedRoundOff) <= 2m && Header("roundOff").Length > 0 && Math.Abs(calculatedRoundOff - roundOff) > 0.05m)
                issues.Add($"Round Off {roundOff:0.00} does not match calculated invoice residual {calculatedRoundOff:0.00}");
        }
        return issues;
    }

    private bool CanConfirmCurrent() => SelectedScan is not null && HeaderFields.Count > 0 && ValidateCurrentMapping().Count == 0;

    private void OnMappingValueChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_loading || _reconcilingMapping) return;
        if (sender is MobileInvoiceFieldRow { Key: "counterpartyGstin" } gstinField &&
            e.PropertyName == nameof(MobileInvoiceFieldRow.SelectedValue))
        {
            var panField = HeaderFields.FirstOrDefault(x => x.Key == "counterpartyPan");
            var derived = MobileInvoicePartyIdentityResolver.PanFromGstin(gstinField.SelectedValue);
            if (panField is not null && derived.Length > 0)
            {
                _reconcilingMapping = true;
                try { panField.SelectedValue = derived; }
                finally { _reconcilingMapping = false; }
            }
        }
        ReconcileTaxEvidenceFromLines();
        RebuildTaxSlabs();
        UpdateValidationState();
    }

    private void UpdateValidationState()
    {
        var issues = SelectedScan is null ? new List<string> { "No invoice selected" } : ValidateCurrentMapping();
        MappingValidationText = issues.Count == 0
            ? "✓ GST %, CGST/SGST/IGST, post-discount Taxable, Round Off and Invoice Total reconcile. Mobile discount is review-only and will not be posted to Tally."
            : "Needs Review · " + string.Join(" · ", issues.Take(6));
        ConfirmAndNextCommand.RaiseCanExecuteChanged();
    }

    private static bool IsSupportedGstRate(decimal value) => SupportedGstRates.Any(x => Math.Abs(x - value) <= 0.01m);

    private static decimal SnapGstRate(decimal value)
    {
        var nearest = SupportedGstRates.OrderBy(x => Math.Abs(x - value)).First();
        return Math.Abs(nearest - value) <= 0.25m ? nearest : value;
    }

    private void SetHeaderAmount(string key, decimal value, bool signed = false)
    {
        var row = HeaderFields.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (row is null) return;
        var next = (signed ? value : Math.Abs(value)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        if (!row.SelectedValue.Equals(next, StringComparison.Ordinal)) row.SelectedValue = next;
    }

    private void ReconcileTaxEvidenceFromLines()
    {
        if (_reconcilingMapping || Lines.Count == 0) return;
        _reconcilingMapping = true;
        try
        {
            string Header(string key) => HeaderFields.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.SelectedValue.Trim() ?? string.Empty;
            var headerInterstate = AmountOrZero(Header("igstTotal")) > 0m && AmountOrZero(Header("cgstTotal")) == 0m && AmountOrZero(Header("sgstTotal")) == 0m;
            foreach (var line in Lines)
            {
                var taxable = AmountOrZero(line.Taxable);
                var rate = AmountOrZero(line.GstRate);
                var cgst = AmountOrZero(line.Cgst);
                var sgst = AmountOrZero(line.Sgst);
                var igst = AmountOrZero(line.Igst);

                if (taxable > 0m && (!IsSupportedGstRate(rate) || rate <= 0m))
                {
                    var derived = igst > 0m ? igst * 100m / taxable : (cgst + sgst) > 0m ? (cgst + sgst) * 100m / taxable : 0m;
                    var snapped = SnapGstRate(derived);
                    if (IsSupportedGstRate(snapped) && snapped > 0m) { rate = snapped; line.GstRate = snapped.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture); }
                }
                if (taxable <= 0m && IsSupportedGstRate(rate) && rate > 0m)
                {
                    if (igst > 0m) taxable = igst * 100m / rate;
                    else if (cgst > 0m && sgst > 0m && Math.Abs(cgst - sgst) <= 0.10m) taxable = (cgst + sgst) * 100m / rate;
                    if (taxable > 0m) line.Taxable = Math.Round(taxable, 2, MidpointRounding.AwayFromZero).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                }
                if (taxable <= 0m || !IsSupportedGstRate(rate) || rate <= 0m) continue;

                var interstate = igst > 0m || (headerInterstate && cgst == 0m && sgst == 0m);
                if (interstate)
                {
                    var expected = Math.Round(taxable * rate / 100m, 2, MidpointRounding.AwayFromZero);
                    if (Math.Abs(igst - expected) > 0.10m) line.Igst = expected.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                    if (cgst > 0m) line.Cgst = "";
                    if (sgst > 0m) line.Sgst = "";
                }
                else
                {
                    var expected = Math.Round(taxable * rate / 200m, 2, MidpointRounding.AwayFromZero);
                    if (Math.Abs(cgst - expected) > 0.10m) line.Cgst = expected.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                    if (Math.Abs(sgst - expected) > 0.10m) line.Sgst = expected.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                    if (igst > 0m) line.Igst = "";
                }
            }

            var taxableLines = Lines.Where(x => AmountOrZero(x.Taxable) > 0m).ToArray();
            if (taxableLines.Length == 0 || taxableLines.Any(x => !IsSupportedGstRate(AmountOrZero(x.GstRate)))) return;
            if (_summaryLayoutAuthority && _authoritativeTaxSlabs.Count > 0)
            {
                var invoiceTotalAuthority = AmountOrZero(Header("invoiceTotal"));
                var taxableAuthority = AmountOrZero(Header("taxableTotal"));
                var cgstAuthority = AmountOrZero(Header("cgstTotal"));
                var sgstAuthority = AmountOrZero(Header("sgstTotal"));
                var igstAuthority = AmountOrZero(Header("igstTotal"));
                var cessAuthority = AmountOrZero(Header("cessTotal"));
                var chargesAuthority = AmountOrZero(Header("otherCharges"));
                if (invoiceTotalAuthority > 0m)
                {
                    var residualAuthority = Math.Round(invoiceTotalAuthority - (taxableAuthority + cgstAuthority + sgstAuthority + igstAuthority + cessAuthority + chargesAuthority), 2, MidpointRounding.AwayFromZero);
                    if (Math.Abs(residualAuthority) <= 2m) SetHeaderAmount("roundOff", residualAuthority, signed: true);
                }
                return;
            }
            var taxableTotal = taxableLines.Sum(x => AmountOrZero(x.Taxable));
            var cgstTotal = taxableLines.Sum(x => AmountOrZero(x.Cgst));
            var sgstTotal = taxableLines.Sum(x => AmountOrZero(x.Sgst));
            var igstTotal = taxableLines.Sum(x => AmountOrZero(x.Igst));
            var cessTotal = taxableLines.Sum(x => AmountOrZero(x.Cess));
            SetHeaderAmount("taxableTotal", taxableTotal);
            SetHeaderAmount("cgstTotal", cgstTotal);
            SetHeaderAmount("sgstTotal", sgstTotal);
            SetHeaderAmount("igstTotal", igstTotal);
            SetHeaderAmount("cessTotal", cessTotal);
            var invoiceTotal = AmountOrZero(Header("invoiceTotal"));
            var charges = AmountOrZero(Header("otherCharges"));
            if (invoiceTotal > 0m)
            {
                var residual = Math.Round(invoiceTotal - (taxableTotal + cgstTotal + sgstTotal + igstTotal + cessTotal + charges), 2, MidpointRounding.AwayFromZero);
                if (Math.Abs(residual) <= 2m) SetHeaderAmount("roundOff", residual, signed: true);
            }
        }
        finally { _reconcilingMapping = false; }
    }

    private void RebuildTaxSlabs()
    {
        TaxSlabs.Clear();
        if (_summaryLayoutAuthority && _authoritativeTaxSlabs.Count > 0)
        {
            foreach (var row in _authoritativeTaxSlabs.Values.OrderBy(x => x.GstRate)) TaxSlabs.Add(row);
            return;
        }
        var grouped = Lines.Where(x => AmountOrZero(x.Taxable) > 0m && IsSupportedGstRate(AmountOrZero(x.GstRate)))
            .GroupBy(x => AmountOrZero(x.GstRate)).OrderBy(x => x.Key);
        var seen = new HashSet<decimal>();
        foreach (var group in grouped)
        {
            var rate = group.Key; seen.Add(rate);
            var taxable = group.Sum(x => AmountOrZero(x.Taxable));
            var cgst = group.Sum(x => AmountOrZero(x.Cgst));
            var sgst = group.Sum(x => AmountOrZero(x.Sgst));
            var igst = group.Sum(x => AmountOrZero(x.Igst));
            var expected = Math.Round(taxable * rate / 100m, 2, MidpointRounding.AwayFromZero);
            var actual = cgst + sgst + igst;
            var status = Math.Abs(actual - expected) <= 0.20m && (igst > 0m || Math.Abs(cgst - sgst) <= 0.10m) ? "Reconciled" : "Needs review";
            TaxSlabs.Add(new MobileInvoiceTaxSlabRow(rate, taxable, cgst, sgst, igst, status));
        }
        foreach (var rate in _recognizedSlabRates.OrderBy(x => x))
            if (!seen.Contains(rate)) TaxSlabs.Add(new MobileInvoiceTaxSlabRow(rate, 0m, 0m, 0m, 0m, "Recognized slab · row allocation missing"));
    }

    private static string ReadDocumentType(string payloadJson)
    {
        try
        {
            var root = JsonNode.Parse(payloadJson)?.AsObject();
            return root?["documentType"]?.ToString().Trim() is { Length: > 0 } value ? value : "Tax Invoice";
        }
        catch { return "Tax Invoice"; }
    }

    private static decimal AmountOrZero(string value) => TryAmount(value, out var amount) ? Math.Abs(amount) : 0m;
    private static decimal SignedAmountOrZero(string value) => TryAmount(value, out var amount) ? amount : 0m;
    private static string NormalizeGstin(string value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static bool TryAmount(string value, out decimal amount)
    {
        var normalized = (value ?? string.Empty).Replace(",", string.Empty).Replace("₹", string.Empty).Replace("INR", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("Rs.", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("Rs", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return decimal.TryParse(normalized, System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out amount);
    }

    private MobileInvoiceMappedResult BuildMappedResult(MobileInvoiceQueueItem scan)
    {
        string Header(string key) => HeaderFields.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.SelectedValue.Trim() ?? string.Empty;
        var root = JsonNode.Parse(scan.PayloadJson)?.AsObject() ?? new JsonObject();
        var direction = VerifiedDirection.Trim();
        var documentType = root["documentType"]?.ToString().Trim() ?? "Tax Invoice";
        var counterpartyGstin = Header("counterpartyGstin");
        if (string.IsNullOrWhiteSpace(counterpartyGstin))
            counterpartyGstin = root["counterparty"]?["gstin"]?.ToString().Trim() ?? string.Empty;
        var invoiceDate = ParseInvoiceDate(Header("invoiceDate"));
        var result = new MobileInvoiceMappedResult
        {
            Direction = direction,
            DocumentType = documentType,
            InvoiceNumber = Header("invoiceNo"),
            InvoiceDate = invoiceDate,
            CounterpartyName = Header("counterparty"),
            CounterpartyGstin = counterpartyGstin,
            InvoiceTotal = AmountOrZero(Header("invoiceTotal")),
            TaxableTotal = AmountOrZero(Header("taxableTotal")),
            CgstTotal = AmountOrZero(Header("cgstTotal")),
            SgstTotal = AmountOrZero(Header("sgstTotal")),
            IgstTotal = AmountOrZero(Header("igstTotal")),
            CessTotal = AmountOrZero(Header("cessTotal")),
            RoundOff = SignedAmountOrZero(Header("roundOff")),
            SourceDiscrepanciesReviewed = ReviewedOcrDiscrepancies,
            SourceImageSha256 = root["ocrSourceSha256"]?.ToString() ?? string.Empty
        };
        foreach (var line in Lines)
        {
            result.Lines.Add(new MobileInvoiceMappedLine
            {
                Description = line.Description,
                HsnSac = line.HsnSac,
                Quantity = AmountOrZero(line.Quantity),
                Unit = line.Unit,
                Rate = AmountOrZero(line.Rate),
                // R46 R27: retain discount on the review screen only. Mobile imports use the
                // already post-discount Taxable authority and never hand a discount into Sales/Purchase/Tally.
                Discount = 0m,
                Taxable = AmountOrZero(line.Taxable),
                GstRate = AmountOrZero(line.GstRate),
                Cgst = AmountOrZero(line.Cgst),
                Sgst = AmountOrZero(line.Sgst),
                Igst = AmountOrZero(line.Igst),
                Cess = AmountOrZero(line.Cess),
                LineTotal = AmountOrZero(line.LineTotal),
                StockItem = IsAccountingInvoice ? string.Empty : line.StockItem
            });
        }
        return result;
    }

    private static DateTime? ParseInvoiceDate(string value)
    {
        foreach (var format in new[] { "dd-MM-yyyy", "dd/MM/yyyy", "yyyy-MM-dd" })
            if (DateTime.TryParseExact(value, format, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)) return parsed;
        return DateTime.TryParse(value, out var fallback) ? fallback : null;
    }

    private string BuildConfirmedPayload(string original)
    {
        var root = JsonNode.Parse(original)?.AsObject() ?? new JsonObject();
        root["direction"] = VerifiedDirection.Trim();
        foreach (var field in HeaderFields)
        {
            switch (field.Key)
            {
                case "counterparty":
                    if (root["counterparty"] is not JsonObject party) { party = new JsonObject(); root["counterparty"] = party; }
                    party["name"] = field.SelectedValue; break;
                case "counterpartyPan":
                    if (root["counterparty"] is not JsonObject panParty) { panParty = new JsonObject(); root["counterparty"] = panParty; }
                    panParty["pan"] = field.SelectedValue; break;
                case "counterpartyGstin":
                    if (root["counterparty"] is not JsonObject gstParty) { gstParty = new JsonObject(); root["counterparty"] = gstParty; }
                    gstParty["gstin"] = MobileInvoicePartyIdentityResolver.NormalizeGstin(field.SelectedValue); break;
                case "invoiceNo": root["invoiceNumber"] = field.SelectedValue; break;
                case "invoiceDate": root["invoiceDate"] = field.SelectedValue; break;
                case "pos": root["placeOfSupply"] = field.SelectedValue; break;
                default: root[field.Key] = field.SelectedValue; break;
            }
        }
        var rows = new JsonArray();
        foreach (var line in Lines)
        {
            rows.Add(new JsonObject
            {
                ["description"] = line.Description, ["hsnSac"] = line.HsnSac, ["quantity"] = IsAccountingInvoice ? "" : line.Quantity,
                ["unit"] = IsAccountingInvoice ? "" : line.Unit, ["rate"] = IsAccountingInvoice ? "" : line.Rate, ["discount"] = line.Discount,
                ["taxable"] = line.Taxable, ["gstRate"] = line.GstRate, ["cgst"] = line.Cgst, ["sgst"] = line.Sgst,
                ["igst"] = line.Igst, ["cess"] = line.Cess, ["lineTotal"] = line.LineTotal,
                ["stockItem"] = IsAccountingInvoice ? "" : line.StockItem, ["ledger"] = ""
            });
        }
        root["rows"] = rows;
        root["windowsVerifiedUtc"] = DateTime.UtcNow.ToString("O");
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private string BuildLearningJson()
    {
        var root = new JsonObject();
        foreach (var field in HeaderFields)
        {
            var labels = field.CandidateDetails.Where(x => x.Value.Equals(field.SelectedValue, StringComparison.OrdinalIgnoreCase) &&
                                                           !x.Label.Equals("Recognized value", StringComparison.OrdinalIgnoreCase) && x.Label.Length > 1)
                .Select(x => x.Label).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (labels.Length == 0) continue;
            root[field.Key] = new JsonArray(labels.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private void OnMobilePresenceChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) RefreshPresence();
        else _ = dispatcher.BeginInvoke(new Action(RefreshPresence), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnMobileLinkChanged(object? sender, EventArgs e)
    {
        if (_disposed || _loading) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Refresh();
        else _ = dispatcher.BeginInvoke(new Action(Refresh), System.Windows.Threading.DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_server is not null)
        {
            _server.MobileLinkChanged -= OnMobileLinkChanged;
            _server.MobilePresenceChanged -= OnMobilePresenceChanged;
        }
    }
}

public sealed class MobileInvoiceFieldRow : ViewModelBase
{
    private string _selectedValue;
    public MobileInvoiceFieldRow(string key, string label, string value) { Key = key; Label = label; _selectedValue = value; }
    public string Key { get; }
    public string Label { get; }
    public ObservableCollection<string> Candidates { get; } = new();
    public ObservableCollection<MobileInvoiceCandidate> CandidateDetails { get; } = new();
    public string SelectedValue { get => _selectedValue; set { if (SetProperty(ref _selectedValue, value ?? string.Empty)) OnPropertyChanged(nameof(RelationshipText)); } }
    public string RelationshipText => CandidateDetails.FirstOrDefault(x => x.Value.Equals(SelectedValue, StringComparison.OrdinalIgnoreCase)) is { } c ? $"{c.Label} · {c.Source} · {c.Confidence}%" : "Manual value · confirm to keep this mapping";
    public void AddCandidate(string value, string label, string source, int confidence)
    {
        value = (value ?? string.Empty).Trim(); if (value.Length == 0) return;
        if (!Candidates.Any(x => x.Equals(value, StringComparison.OrdinalIgnoreCase))) Candidates.Add(value);
        if (!CandidateDetails.Any(x => x.Value.Equals(value, StringComparison.OrdinalIgnoreCase) && x.Label.Equals(label, StringComparison.OrdinalIgnoreCase)))
            CandidateDetails.Add(new MobileInvoiceCandidate(label, value, source, confidence));
        OnPropertyChanged(nameof(RelationshipText));
    }
}

public sealed record MobileInvoiceCandidate(string Label, string Value, string Source, int Confidence);

public sealed class MobileInvoiceLineRow : ViewModelBase
{
    private string _description="",_hsnSac="",_quantity="",_unit="",_rate="",_discount="",_taxable="",_gstRate="",_cgst="",_sgst="",_igst="",_cess="",_lineTotal="",_stockItem="",_ledger="";
    public string Description{get=>_description;set=>SetProperty(ref _description,value??"");} public string HsnSac{get=>_hsnSac;set=>SetProperty(ref _hsnSac,value??"");}
    public string Quantity{get=>_quantity;set=>SetProperty(ref _quantity,value??"");} public string Unit{get=>_unit;set=>SetProperty(ref _unit,value??"");} public string Rate{get=>_rate;set=>SetProperty(ref _rate,value??"");}
    public string Discount{get=>_discount;set=>SetProperty(ref _discount,value??"");} public string Taxable{get=>_taxable;set=>SetProperty(ref _taxable,value??"");} public string GstRate{get=>_gstRate;set=>SetProperty(ref _gstRate,MobileInvoiceGstRateTranslator.Translate(value));}
    public string Cgst{get=>_cgst;set=>SetProperty(ref _cgst,value??"");} public string Sgst{get=>_sgst;set=>SetProperty(ref _sgst,value??"");} public string Igst{get=>_igst;set=>SetProperty(ref _igst,value??"");}
    public string Cess{get=>_cess;set=>SetProperty(ref _cess,value??"");} public string LineTotal{get=>_lineTotal;set=>SetProperty(ref _lineTotal,value??"");} public string StockItem{get=>_stockItem;set=>SetProperty(ref _stockItem,value??"");}
    public string Ledger{get=>_ledger;set=>SetProperty(ref _ledger,value??"");}
}

public sealed record MobileInvoiceTaxSlabRow(decimal GstRate, decimal Taxable, decimal Cgst, decimal Sgst, decimal Igst, string Status)
{
    public string GstRateDisplay => GstRate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%";
}

// R92 R2: Numeric-format translator at the OCR -> Mobile Link mapping boundary only.
// Keep recognition, invoice party/direction, GST slab classification and source evidence unchanged.
// The source OCR text is still retained in the original document evidence/payload.
internal static class MobileInvoiceGstRateTranslator
{
    public static string Translate(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (!text.EndsWith('%')) return text;
        var number = text[..^1].Trim();
        return decimal.TryParse(number,
            System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint,
            System.Globalization.CultureInfo.InvariantCulture, out _)
            ? number : text;
    }
}

internal static class MobileLinkCollectionExtensions
{
    public static void ReplaceWith(this ObservableCollection<string> target, IEnumerable<string> source)
    {
        target.Clear(); foreach (var item in source) target.Add(item);
    }
}
