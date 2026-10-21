using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AccountingAutomationHub.Application.Sales;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Infrastructure.Sales;

namespace AccountingAutomationHub.Desktop.Views;

public partial class SalesHeaderMappingWindow : Window, INotifyPropertyChanged
{
    private string _gstTreatment = "Local - CGST and SGST";
    private string _taxColumnMode = "Single Tax Amount Column";
    private string _validationText = string.Empty;
    private readonly bool _isB2cs;
    private readonly bool _hasRateBuckets;

    public SalesHeaderMappingWindow(SalesManualInspection inspection, string gstTreatment = "LOCAL", string taxColumnMode = "SINGLE")
    {
        InitializeComponent();
        DataContext = this;
        _isB2cs = inspection.IsB2cs || inspection.DetectedMode.Equals("B2CS", StringComparison.OrdinalIgnoreCase);
        _hasRateBuckets = inspection.HasRateBuckets;
        TargetFields = new[] { string.Empty }.Concat(SalesManualFieldCatalog.TargetFields).ToArray();
        foreach (var header in inspection.Headers)
        {
            inspection.SuggestedMapping.TryGetValue(header, out var target);
            inspection.SampleValues.TryGetValue(header, out var sample);
            Rows.Add(new SalesHeaderMappingRow
            {
                SourceHeader = header,
                SampleValue = sample ?? string.Empty,
                TargetField = target ?? string.Empty,
                Rule = string.IsNullOrWhiteSpace(target) ? "Manual mapping required only if this column is needed." : "Existing built-in mapping"
            });
        }
        GstTreatment = gstTreatment.StartsWith("INTER", StringComparison.OrdinalIgnoreCase) ? GstTreatments[1] : GstTreatments[0];
        TaxColumnMode = taxColumnMode.StartsWith("DOUBLE", StringComparison.OrdinalIgnoreCase) ? TaxColumnModes[1] : TaxColumnModes[0];
    }

    public ObservableCollection<SalesHeaderMappingRow> Rows { get; } = [];
    public IReadOnlyList<string> TargetFields { get; }
    public IReadOnlyList<string> GstTreatments { get; } = ["Local - CGST and SGST", "Interstate - IGST"];
    public IReadOnlyList<string> TaxColumnModes { get; } = ["Single Tax Amount Column", "Double Tax Amount Columns"];
    public string GstTreatment { get => _gstTreatment; set { _gstTreatment=value;PropertyChanged?.Invoke(this,new(nameof(GstTreatment))); } }
    public string TaxColumnMode { get => _taxColumnMode; set { _taxColumnMode=value;PropertyChanged?.Invoke(this,new(nameof(TaxColumnMode))); } }
    public string ValidationText { get => _validationText; private set { _validationText=value;PropertyChanged?.Invoke(this,new(nameof(ValidationText))); } }
    public IReadOnlyDictionary<string,string> Mapping => Rows.ToDictionary(row=>row.SourceHeader,row=>row.TargetField??string.Empty,StringComparer.OrdinalIgnoreCase);
    public string GstTreatmentCode => GstTreatment.StartsWith("Inter",StringComparison.OrdinalIgnoreCase)?"INTERSTATE":"LOCAL";
    public string TaxColumnModeCode => TaxColumnMode.StartsWith("Double",StringComparison.OrdinalIgnoreCase)?"DOUBLE":"SINGLE";

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool ValidateMapping()
    {
        var targets=Rows.Select(row=>row.TargetField).Where(value=>!string.IsNullOrWhiteSpace(value)).ToArray();
        var duplicate=targets.GroupBy(value=>value,StringComparer.OrdinalIgnoreCase).FirstOrDefault(group=>group.Count()>1 && !group.Key.Equals("TaxableValue",StringComparison.OrdinalIgnoreCase));
        if(duplicate is not null){ValidationText=$"'{SalesManualFieldCatalog.DisplayName(duplicate.Key)}' is mapped more than once. Keep only the intended source column.";return false;}
        var set=targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(!set.Contains("DocumentDate")){ValidationText="Map Invoice / Note Date before importing.";return false;}
        if(!set.Contains("DocumentNumber") && !_isB2cs){ValidationText="Map Invoice / Note Number before importing.";return false;}
        if(!set.Contains("DocumentValue")&&!set.Contains("TaxableValue")&&!set.Contains("ItemAmount")&&!_hasRateBuckets){ValidationText="Map at least one amount field: Invoice Value, Taxable Value or Item Amount.";return false;}
        ValidationText="Mapping is valid. Optional fields are imported only when present.";return true;
    }
    private void Validate_Click(object sender,RoutedEventArgs e)=>ValidateMapping();
    private void Import_Click(object sender,RoutedEventArgs e){if(!ValidateMapping())return;DialogResult=true;Close();}
    private void ClearMapping_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SalesHeaderMappingRow row })
            row.TargetField = string.Empty;
    }

    private void Cancel_Click(object sender,RoutedEventArgs e){DialogResult=false;Close();}
}
