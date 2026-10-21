using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Domain.Sales;

namespace AccountingAutomationHub.Desktop.Views;

public partial class InventoryItemNameAssignmentWindow : Window, INotifyPropertyChanged
{
    private string _itemName = string.Empty;
    private string _validationText = string.Empty;
    private readonly string _scopeLabel;

    public InventoryItemNameAssignmentWindow(
        IEnumerable<SalesImportRecord> rows,
        IEnumerable<string> stockItems)
    {
        _scopeLabel = "Sales / Credit Note";
        InitializeComponent();
        StockItems = BuildStockItems(stockItems);
        foreach (var row in rows.Where(HasInventoryEvidence))
        {
            Entries.Add(new InventoryItemNameAssignmentRow(
                row,
                row.DocumentDate,
                row.DocumentNumber,
                row.CustomerName,
                row.ItemName,
                PreferredQuantity(row.ActualQuantity, row.BilledQuantity),
                row.Unit,
                row.ItemRate,
                PreferredAmount(row.ItemAmount, row.TaxableValue),
                row.Destination));
        }
        DataContext = this;
    }

    public InventoryItemNameAssignmentWindow(
        IEnumerable<PurchaseImportRecord> rows,
        IEnumerable<string> stockItems)
    {
        _scopeLabel = "Purchase / Debit Note";
        InitializeComponent();
        StockItems = BuildStockItems(stockItems);
        foreach (var row in rows.Where(HasInventoryEvidence))
        {
            Entries.Add(new InventoryItemNameAssignmentRow(
                row,
                row.DocumentDate,
                row.DocumentNumber,
                row.SupplierName,
                row.ItemName,
                PreferredQuantity(row.ActualQuantity, row.BilledQuantity),
                row.Unit,
                row.ItemRate,
                PreferredAmount(row.ItemAmount, row.TaxableValue),
                row.Destination));
        }
        DataContext = this;
    }

    public ObservableCollection<InventoryItemNameAssignmentRow> Entries { get; } = [];
    public IReadOnlyList<string> StockItems { get; }
    public IReadOnlyList<object> TargetRecords { get; private set; } = [];
    public bool AppliedToAll { get; private set; }

    public string Heading => $"Item Name Assignment — {_scopeLabel}";
    public string Explanation =>
        "Use this when Quantity, Unit, Item Rate and Amount were imported but Item Name was absent, or when selected entries need to be moved to another Stock Item.";
    public string SummaryText =>
        $"{Entries.Count:N0} inventory-eligible entr{(Entries.Count == 1 ? "y" : "ies")}. Blank Item Name rows are included.";

    public string ItemName
    {
        get => _itemName;
        set
        {
            _itemName = value ?? string.Empty;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemName)));
        }
    }

    public string ValidationText
    {
        get => _validationText;
        private set
        {
            _validationText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ValidationText)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static IReadOnlyList<string> BuildStockItems(IEnumerable<string> values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private bool ValidateItemName()
    {
        if (ItemName.Trim().Length == 0)
        {
            ValidationText = "Enter or select an Item Name.";
            return false;
        }
        if (Entries.Count == 0)
        {
            ValidationText = "There are no inventory-eligible entries to update.";
            return false;
        }
        ValidationText = string.Empty;
        return true;
    }

    private void ApplySelected_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateItemName()) return;
        var selected = EntriesGrid.SelectedItems.OfType<InventoryItemNameAssignmentRow>().ToArray();
        if (selected.Length == 0)
        {
            ValidationText = "Select one or more entries first, then choose Apply Selected.";
            return;
        }

        TargetRecords = selected.Select(row => row.SourceRecord).Distinct().ToArray();
        AppliedToAll = false;
        DialogResult = true;
        Close();
    }

    private void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateItemName()) return;
        TargetRecords = Entries.Select(row => row.SourceRecord).Distinct().ToArray();
        AppliedToAll = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DialogResult = false;
        e.Handled = true;
        Close();
    }

    private static decimal PreferredQuantity(decimal actual, decimal billed) =>
        Math.Abs(actual) > 0.000001m ? actual : billed;

    private static decimal PreferredAmount(decimal itemAmount, decimal taxable) =>
        Math.Abs(itemAmount) > 0.005m ? itemAmount : taxable;

    private static bool HasInventoryEvidence(SalesImportRecord row) =>
        row.ItemName.Trim().Length > 0 || row.Unit.Trim().Length > 0 ||
        Math.Abs(row.ActualQuantity) > 0.000001m || Math.Abs(row.BilledQuantity) > 0.000001m ||
        Math.Abs(row.ItemRate) > 0.000001m || Math.Abs(row.ItemAmount) > 0.005m;

    private static bool HasInventoryEvidence(PurchaseImportRecord row) =>
        row.ItemName.Trim().Length > 0 || row.Unit.Trim().Length > 0 ||
        Math.Abs(row.ActualQuantity) > 0.000001m || Math.Abs(row.BilledQuantity) > 0.000001m ||
        Math.Abs(row.ItemRate) > 0.000001m || Math.Abs(row.ItemAmount) > 0.005m;
}

public sealed class InventoryItemNameAssignmentRow
{
    public InventoryItemNameAssignmentRow(
        object sourceRecord,
        DateTime? documentDate,
        string documentNumber,
        string partyName,
        string currentItemName,
        decimal quantity,
        string unit,
        decimal itemRate,
        decimal amount,
        string destination)
    {
        SourceRecord = sourceRecord;
        DocumentDate = documentDate;
        DocumentNumber = documentNumber ?? string.Empty;
        PartyName = partyName ?? string.Empty;
        CurrentItemName = currentItemName ?? string.Empty;
        Quantity = quantity;
        Unit = unit ?? string.Empty;
        ItemRate = itemRate;
        Amount = amount;
        Destination = destination ?? string.Empty;
    }

    public object SourceRecord { get; }
    public DateTime? DocumentDate { get; }
    public string DateText => DocumentDate?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture) ?? string.Empty;
    public string DocumentNumber { get; }
    public string PartyName { get; }
    public string CurrentItemName { get; }
    public decimal Quantity { get; }
    public string Unit { get; }
    public decimal ItemRate { get; }
    public decimal Amount { get; }
    public string Destination { get; }
}
