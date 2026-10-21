using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AccountingAutomationHub.Application.Purchases;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Infrastructure.Imports;

namespace AccountingAutomationHub.Desktop.Views;

public partial class PurchaseHeaderMappingWindow : Window, INotifyPropertyChanged
{
    private readonly IPurchaseManualImportService _manualImportService;
    private readonly string _sourcePath;
    private string _validationText = string.Empty;
    private string _detectionText = string.Empty;
    private string _selectedSheetName = string.Empty;
    private string _headerRowText = "1";
    private string _dataStartRowText = "2";
    private bool _suppressLayoutDefaults;
    private string _lastScannedSheet = string.Empty;
    private int _lastScannedHeaderRow;
    private string _gstTreatment = PurchaseTreatmentPolicy.NotShownInTwoB;

    public PurchaseHeaderMappingWindow(
        IPurchaseManualImportService manualImportService,
        string sourcePath,
        PurchaseManualInspection inspection,
        string gstTreatment = PurchaseTreatmentPolicy.NotShownInTwoB,
        bool preserveInitialMapping = false)
    {
        _manualImportService = manualImportService;
        _sourcePath = sourcePath;

        InitializeComponent();
        DataContext = this;
        GstTreatment = PurchaseTreatmentPolicy.NormalizePurchaseTreatment(gstTreatment);
        TargetFields = new[] { string.Empty }
            .Concat(ImportHeaderMappingCatalog.GetFields(ImportHeaderMappingCatalog.Purchase).Select(field => field.Key))
            .ToArray();

        ApplyInspection(inspection);
        // New/unknown formats start from a fresh exact-row rescan. Explicit Remap opens
        // the saved profile as-is so the user can adjust only the intended fields.
        if (!preserveInitialMapping)
            RescanSelection(showSuccess: false);
        ValidationText = "Review this Purchase format once. After Import & Remember Mapping, Manual Import and Auto Import will use the same saved profile for this client and Financial Year.";
    }

    public ObservableCollection<SalesHeaderMappingRow> Rows { get; } = [];
    public IReadOnlyList<string> TargetFields { get; }
    public IReadOnlyList<string> AvailableSheets { get; private set; } = [];
    public IReadOnlyList<string> HiddenSheets { get; private set; } = [];
    public string HeaderSignature { get; private set; } = string.Empty;
    public IReadOnlyList<string> GstTreatmentOptions { get; } =
    [
        PurchaseTreatmentPolicy.NotShownInTwoB,
        PurchaseTreatmentPolicy.ShownInTwoB,
        PurchaseTreatmentPolicy.GstIncludedInCost
    ];

    public string GstTreatment
    {
        get => _gstTreatment;
        set
        {
            var normalized = PurchaseTreatmentPolicy.NormalizePurchaseTreatment(value);
            if (_gstTreatment.Equals(normalized, StringComparison.OrdinalIgnoreCase)) return;
            _gstTreatment = normalized;
            OnPropertyChanged(nameof(GstTreatment));
        }
    }


    public IReadOnlyDictionary<string, string> Mapping => Rows.ToDictionary(
        row => row.SourceHeader,
        row => row.TargetField ?? string.Empty,
        StringComparer.OrdinalIgnoreCase);

    public string SelectedSheetName
    {
        get => _selectedSheetName;
        set
        {
            if (string.Equals(_selectedSheetName, value, StringComparison.Ordinal)) return;
            _selectedSheetName = value ?? string.Empty;
            OnPropertyChanged(nameof(SelectedSheetName));
        }
    }

    public string HeaderRowText
    {
        get => _headerRowText;
        set
        {
            var oldHeader = ParsePositive(_headerRowText);
            var dataWasDefault = oldHeader.HasValue && ParsePositive(_dataStartRowText) == oldHeader.Value + 1;
            _headerRowText = value ?? string.Empty;
            OnPropertyChanged(nameof(HeaderRowText));

            if (_suppressLayoutDefaults) return;
            var newHeader = ParsePositive(_headerRowText);
            if (newHeader.HasValue && (string.IsNullOrWhiteSpace(_dataStartRowText) || dataWasDefault))
                DataStartRowText = (newHeader.Value + 1).ToString(CultureInfo.InvariantCulture);
        }
    }

    public string DataStartRowText
    {
        get => _dataStartRowText;
        set
        {
            _dataStartRowText = value ?? string.Empty;
            OnPropertyChanged(nameof(DataStartRowText));
        }
    }

    public int HeaderRow => ParsePositive(HeaderRowText) ?? 0;
    public int DataStartRow => ParsePositive(DataStartRowText) ?? 0;

    public string DetectionText
    {
        get => _detectionText;
        private set
        {
            _detectionText = value;
            OnPropertyChanged(nameof(DetectionText));
        }
    }

    public string ValidationText
    {
        get => _validationText;
        private set
        {
            _validationText = value;
            OnPropertyChanged(nameof(ValidationText));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void ApplyInspection(PurchaseManualInspection inspection)
    {
        _suppressLayoutDefaults = true;
        try
        {
            AvailableSheets = inspection.AvailableSheets;
            HiddenSheets = inspection.HiddenSheets;
            OnPropertyChanged(nameof(AvailableSheets));
            OnPropertyChanged(nameof(HiddenSheets));

            SelectedSheetName = inspection.SheetName;
            HeaderRowText = Math.Max(1, inspection.HeaderRow).ToString(CultureInfo.InvariantCulture);
            DataStartRowText = Math.Max(inspection.HeaderRow + 1, inspection.DataStartRow).ToString(CultureInfo.InvariantCulture);
            HeaderSignature = inspection.HeaderSignature;
            DetectionText = inspection.DetectionMessage +
                            (inspection.SelectedSheetIsHidden
                                ? " The selected worksheet is hidden in Excel; saving this profile explicitly authorizes this exact sheet for future Manual and Auto Import."
                                : string.Empty);

            Rows.Clear();
            var seenHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < inspection.Headers.Count; index++)
            {
                var header = inspection.Headers[index];
                if (string.IsNullOrWhiteSpace(header) || !seenHeaders.Add(header)) continue;

                inspection.SuggestedMapping.TryGetValue(header, out var target);
                inspection.SampleValues.TryGetValue(header, out var sample);
                var builtIn = ImportHeaderMappingCatalog.ResolveBuiltIn(ImportHeaderMappingCatalog.Purchase, header);

                Rows.Add(new SalesHeaderMappingRow
                {
                    SourceColumn = ExcelColumnName(index + 1),
                    SourceHeader = header,
                    SampleValue = sample ?? string.Empty,
                    TargetField = target ?? string.Empty,
                    Rule = string.IsNullOrWhiteSpace(target)
                        ? "Unmapped — choose a field only if this column is needed"
                        : target.Equals(builtIn, StringComparison.OrdinalIgnoreCase)
                            ? "Detected from this header name"
                            : "Saved Purchase profile"
                });
            }

            _lastScannedSheet = inspection.SheetName;
            _lastScannedHeaderRow = inspection.HeaderRow;
        }
        finally
        {
            _suppressLayoutDefaults = false;
        }
    }

    private bool ValidateLayout(out int headerRow, out int dataStartRow)
    {
        headerRow = ParsePositive(HeaderRowText) ?? 0;
        dataStartRow = ParsePositive(DataStartRowText) ?? 0;
        if (string.IsNullOrWhiteSpace(SelectedSheetName))
        {
            ValidationText = "Choose the worksheet containing the Purchase Register.";
            return false;
        }
        if (headerRow <= 0)
        {
            ValidationText = "Header Row must be a positive row number.";
            return false;
        }
        if (dataStartRow <= headerRow)
        {
            ValidationText = "Data Start Row must be below Header Row. It defaults to Header Row + 1, but you can move it further down.";
            return false;
        }
        return true;
    }

    private bool RescanSelection(bool showSuccess = true)
    {
        if (!ValidateLayout(out var headerRow, out var dataStartRow)) return false;

        try
        {
            // Rescan means reset from the exact selected source row. It deliberately does
            // not merge old mappings or the edits currently shown in the grid.
            var inspection = _manualImportService.Inspect(
                _sourcePath,
                savedMapping: null,
                sheetName: SelectedSheetName,
                headerRow: headerRow,
                dataStartRow: dataStartRow);
            ApplyInspection(inspection);

            if (showSuccess)
            {
                ValidationText = inspection.IsComplete
                    ? $"Headers rebuilt from '{inspection.SheetName}' row {inspection.HeaderRow:N0}; data starts at row {inspection.DataStartRow:N0}."
                    : $"Headers rebuilt from '{inspection.SheetName}' row {inspection.HeaderRow:N0}. Unknown columns are blank; map only the fields you need.";
            }
            return true;
        }
        catch (Exception exception)
        {
            ValidationText = exception.Message;
            return false;
        }
    }

    private bool LayoutChangedSinceScan()
    {
        if (!ValidateLayout(out var headerRow, out var dataStartRow)) return true;
        // Data Start Row changes where invoice rows begin, not what the header means.
        // Do not erase the user's mapping just because they move the first data row.
        // Worksheet/Header Row changes still require a fresh rescan.
        return !string.Equals(_lastScannedSheet, SelectedSheetName, StringComparison.OrdinalIgnoreCase) ||
               _lastScannedHeaderRow != headerRow;
    }

    private bool ValidateMapping()
    {
        if (!ValidateLayout(out _, out _)) return false;

        var targets = Rows.Select(row => row.TargetField)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        var duplicate = targets.GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            ValidationText = $"'{duplicate.Key}' is mapped more than once. Keep only the intended source column.";
            return false;
        }

        var set = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!set.Contains("InvoiceDate") && !set.Contains("NoteDate"))
        {
            ValidationText = "Map Invoice Date or Debit Note Date before importing.";
            return false;
        }
        if (!set.Contains("InvoiceNo") && !set.Contains("NoteNo"))
        {
            ValidationText = "Map Invoice Number or Debit Note Number before importing.";
            return false;
        }
        if (!set.Contains("SupplierName") && !set.Contains("GSTIN"))
        {
            ValidationText = "Map Supplier Name or Supplier GSTIN before importing.";
            return false;
        }
        if (!set.Contains("InvoiceValue") && !set.Contains("TaxableValue") && !set.Contains("ItemAmount") && !set.Contains("NoteValue"))
        {
            ValidationText = "Map at least one amount field: Invoice Value, Taxable Value, Item Amount or Debit Note Value.";
            return false;
        }

        ValidationText = $"Mapping is valid. Header row {HeaderRow:N0}; data starts at row {DataStartRow:N0}; default GST Treatment is '{GstTreatment}'. This exact profile will be shared by Manual Import and Auto Import.";
        return true;
    }

    private static int? ParsePositive(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result > 0
            ? result
            : null;

    private static string ExcelColumnName(int column)
    {
        if (column <= 0) return string.Empty;
        var value = column;
        var result = string.Empty;
        while (value > 0)
        {
            value--;
            result = (char)('A' + value % 26) + result;
            value /= 26;
        }
        return result;
    }

    private void Sheet_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLayoutDefaults || !IsLoaded || string.IsNullOrWhiteSpace(SelectedSheetName)) return;
        RescanSelection(showSuccess: false);
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => RescanSelection();
    private void Validate_Click(object sender, RoutedEventArgs e) => ValidateMapping();

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (LayoutChangedSinceScan() && !RescanSelection()) return;
        if (!ValidateMapping()) return;
        DialogResult = true;
        Close();
    }

    private void ClearMapping_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SalesHeaderMappingRow row })
            row.TargetField = string.Empty;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
