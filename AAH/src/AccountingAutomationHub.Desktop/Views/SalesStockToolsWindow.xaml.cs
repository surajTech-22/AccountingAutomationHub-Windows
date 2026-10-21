using System.ComponentModel;
using System.Globalization;
using System.Windows;
using AccountingAutomationHub.Infrastructure.Sales;

namespace AccountingAutomationHub.Desktop.Views;

public partial class SalesStockToolsWindow : Window, INotifyPropertyChanged
{
    public const string ItemAliasMode = "ITEM";
    public const string UnitMappingMode = "UNIT";
    public const string QuantityConversionMode = "QTY";

    private string _stockItemName = string.Empty;
    private string _sourceValue = string.Empty;
    private string _finalValue = string.Empty;
    private string _factorText = "1";
    private bool _rememberConversion = true;
    private string _validationText = string.Empty;
    private bool _factorWasUserEdited;
    private bool _settingSuggestedFactor;

    public SalesStockToolsWindow(
        string mode,
        IEnumerable<string> stockItems,
        IEnumerable<string> sourceValues,
        IEnumerable<string> finalValues)
    {
        InitializeComponent();
        Mode = mode;
        StockItems = stockItems.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToArray();
        SourceValues = sourceValues.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToArray();
        FinalValues = finalValues.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToArray();
        DataContext = this;
    }

    public string Mode { get; }
    public IReadOnlyList<string> StockItems { get; }
    public IReadOnlyList<string> SourceValues { get; }
    public IReadOnlyList<string> FinalValues { get; }

    public string Heading => Mode switch
    {
        ItemAliasMode => "Item Name Mapping",
        UnitMappingMode => "Item-specific Unit Mapping",
        _ => "Quantity Conversion"
    };

    public string Explanation => Mode switch
    {
        ItemAliasMode => "Map an imported item name to the exact Stock Master name. The alias is remembered for this client and used automatically on later imports.",
        UnitMappingMode => "Change only the unit name for this Stock Item. Quantity and rate are not changed by Unit Mapping.",
        _ => "Convert quantity, billed quantity and rate together from the original imported values. This is separate from a Unit Mapping."
    };

    public string SourceLabel => Mode == ItemAliasMode ? "Imported Item Name" : "Imported Unit";
    public string FinalLabel => Mode == ItemAliasMode ? "Final Stock Item" : "Final Unit";
    public Visibility SourceValueVisibility => Visibility.Visible;
    public Visibility FactorVisibility => Mode == QuantityConversionMode ? Visibility.Visible : Visibility.Collapsed;

    public string StockItemName
    {
        get => _stockItemName;
        set { _stockItemName = value ?? string.Empty; OnChanged(nameof(StockItemName)); TrySuggestFactor(); }
    }

    public string SourceValue
    {
        get => _sourceValue;
        set { _sourceValue = value ?? string.Empty; OnChanged(nameof(SourceValue)); TrySuggestFactor(); }
    }

    public string FinalValue
    {
        get => _finalValue;
        set { _finalValue = value ?? string.Empty; OnChanged(nameof(FinalValue)); TrySuggestFactor(); }
    }

    public string FactorText
    {
        get => _factorText;
        set
        {
            _factorText = value ?? string.Empty;
            if (!_settingSuggestedFactor) _factorWasUserEdited = true;
            OnChanged(nameof(FactorText));
        }
    }

    public bool RememberConversion
    {
        get => _rememberConversion;
        set { _rememberConversion = value; OnChanged(nameof(RememberConversion)); }
    }

    public string ValidationText
    {
        get => _validationText;
        private set { _validationText = value; OnChanged(nameof(ValidationText)); }
    }

    public decimal ConversionFactor =>
        decimal.TryParse(FactorText, NumberStyles.Number, CultureInfo.CurrentCulture, out var value)
            ? value
            : decimal.TryParse(FactorText, NumberStyles.Number, CultureInfo.InvariantCulture, out value)
                ? value
                : 0m;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void TrySuggestFactor()
    {
        if (Mode != QuantityConversionMode || _factorWasUserEdited ||
            string.IsNullOrWhiteSpace(SourceValue) || string.IsNullOrWhiteSpace(FinalValue))
            return;
        if (SalesStockNormalizationService.TryUniversalFactor(SourceValue, FinalValue, out var factor))
        {
            _settingSuggestedFactor = true;
            try
            {
                FactorText = factor.ToString("0.########", CultureInfo.InvariantCulture);
            }
            finally
            {
                _settingSuggestedFactor = false;
            }
        }
    }

    private bool ValidateInput()
    {
        if (Mode != ItemAliasMode && string.IsNullOrWhiteSpace(StockItemName))
        {
            ValidationText = "Select the Stock Item.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(SourceValue))
        {
            ValidationText = Mode == ItemAliasMode ? "Enter the imported Item Name." : "Enter the imported Unit.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(FinalValue))
        {
            ValidationText = Mode == ItemAliasMode ? "Select the final Stock Item." : "Select the final Unit.";
            return false;
        }
        if (Mode == QuantityConversionMode && ConversionFactor <= 0m)
        {
            ValidationText = "Enter a conversion factor greater than zero.";
            return false;
        }
        ValidationText = string.Empty;
        return true;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInput()) return;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
