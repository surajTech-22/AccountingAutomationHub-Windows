using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using AccountingAutomationHub.Application.Masters;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Sales;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Infrastructure.Sales;

namespace AccountingAutomationHub.Desktop.Views;

public partial class SalesStockBulkReviewWindow : Window, INotifyPropertyChanged
{
    private string _validationText = string.Empty;
    private string _scopeLabel = "Sales/Credit Note";
    private IReadOnlyList<StockItemAliasRecord> _existingAliases = Array.Empty<StockItemAliasRecord>();
    private bool _isApplying;
    public Func<IReadOnlyList<StockItemMappingSelection>, Task<StockItemMappingResult>>? ApplyItemMappingsAsync { get; set; }


    public SalesStockBulkReviewWindow(
        string mode,
        IEnumerable<SalesImportRecord> salesRows,
        IEnumerable<StockItemRecord> stockItems,
        IEnumerable<string> units,
        IEnumerable<StockItemAliasRecord>? aliases = null)
    {
        InitializeFor(mode, salesRows.Select(ToReviewSource), stockItems, units, "Sales/Credit Note", aliases);
    }

    // Fix94: Purchase uses the exact same Stock Item / Unit / quantity-conversion
    // review UI and the same durable Stock Master mapping tables as Sales.
    public SalesStockBulkReviewWindow(
        string mode,
        IEnumerable<PurchaseImportRecord> purchaseRows,
        IEnumerable<StockItemRecord> stockItems,
        IEnumerable<string> units,
        IEnumerable<StockItemAliasRecord>? aliases = null)
    {
        InitializeFor(mode, purchaseRows.Select(ToReviewSource), stockItems, units, "Purchase/Debit Note", aliases);
    }

    private void InitializeFor(
        string mode,
        IEnumerable<InventoryReviewSource> sourceRows,
        IEnumerable<StockItemRecord> stockItems,
        IEnumerable<string> units,
        string scopeLabel,
        IEnumerable<StockItemAliasRecord>? aliases)
    {
        InitializeComponent();
        _existingAliases = aliases?.ToArray() ?? Array.Empty<StockItemAliasRecord>();
        Mode = mode;
        _scopeLabel = scopeLabel;
        var items = stockItems.Where(item => !string.IsNullOrWhiteSpace(item.Name)).ToArray();
        StockItems = items.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToArray();
        Units = units.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToArray();
        FinalValues = mode == SalesStockToolsWindow.ItemAliasMode ? StockItems : Units;
        FinalChoices = mode == SalesStockToolsWindow.ItemAliasMode
            ? items.GroupBy(item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => HasTallyMasterId(item)).First())
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => new StockReviewChoice(item.Name.Trim(), ResolveAuthority(item), HasTallyMasterId(item) ? $"MASTERID {item.TallyMasterId}" : "AAH local master"))
                .ToArray()
            : Units.Select(unit => new StockReviewChoice(unit, "AAH Unit", string.Empty)).ToArray();

        // Fix121: one bulk window serves Item rename, Unit mapping and Quantity
        // conversion, but each mode must present the exact columns/action controls the
        // user expects. Final Value is an always-visible chooser in XAML.
        CurrentValueColumn.Header = mode == SalesStockToolsWindow.ItemAliasMode ? "Current Stock Item" : "Stock Item";
        ImportedValueColumn.Header = mode == SalesStockToolsWindow.ItemAliasMode ? "Imported Item Name" : "Imported Unit";
        FinalValueColumn.Header = mode == SalesStockToolsWindow.ItemAliasMode ? "Final Stock Item" : "Final Unit";
        FactorColumn.Visibility = mode == SalesStockToolsWindow.QuantityConversionMode ? Visibility.Visible : Visibility.Collapsed;

        BuildRows(sourceRows.ToArray(), items);
        DataContext = this;
    }

    public string Mode { get; private set; } = string.Empty;
    public ObservableCollection<SalesStockBulkReviewRow> Rows { get; } = [];
    public IReadOnlyList<string> StockItems { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> Units { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> FinalValues { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<StockReviewChoice> FinalChoices { get; private set; } = Array.Empty<StockReviewChoice>();

    public string Heading => Mode switch
    {
        SalesStockToolsWindow.ItemAliasMode => "Item Name Mapping — Review All",
        SalesStockToolsWindow.UnitMappingMode => "Item-specific Unit Mapping — Review All",
        _ => "Quantity Conversion — Review All"
    };

    public string Explanation => Mode switch
    {
        SalesStockToolsWindow.ItemAliasMode => $"One row is shown for each UNIQUE imported Item Name. Choose an existing Stock Master or type a new Final Stock Item; all matching {_scopeLabel} rows are updated together.",
        SalesStockToolsWindow.UnitMappingMode => "One row is shown for each Stock Item + imported Unit combination. Units is only for equivalent/canonical Unit names (factor 1), such as Qtls → QTL. Any real quantity change must go through Quantity Conversion confirmation.",
        _ => "Only Stock Item + imported Unit combinations where the imported Unit differs from the Stock Master's Final Unit are shown. Confirm the Final Unit and conversion factor; Quantity, billed quantity and rate are then recalculated from the original imported values."
    };

    public string SummaryText => Mode == SalesStockToolsWindow.QuantityConversionMode
        ? Rows.Count == 0
            ? "No quantity conversion is required: every imported Unit already matches its Stock Master Final Unit."
            : $"{Rows.Count:N0} Unit difference(s) require conversion confirmation. Enter/confirm the factor, then Confirm & Apply."
        : $"{Rows.Count:N0} unique review combination(s). Blank Final Value rows are left unchanged.";

    public string ApplyButtonText => Mode == SalesStockToolsWindow.QuantityConversionMode ? "Confirm & Apply" : "Apply All";

    public string ValidationText
    {
        get => _validationText;
        private set { _validationText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ValidationText))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void BuildRows(IReadOnlyCollection<InventoryReviewSource> salesRows, IReadOnlyCollection<StockItemRecord> stockItems)
    {
        if (Mode == SalesStockToolsWindow.ItemAliasMode)
        {
            var exact = stockItems.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var group in salesRows
                         .Where(row => row.HasInventoryEvidence)
                         .GroupBy(row =>
                         {
                             var imported = string.IsNullOrWhiteSpace(row.OriginalItemName) ? row.ItemName.Trim() : row.OriginalItemName.Trim();
                             return imported.Length == 0 ? "(Blank Item Name)" : AccountingText.CleanWhitespace(imported);
                         }, StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                var currentItem = group.Select(row => row.ItemName.Trim()).FirstOrDefault(value => value.Length > 0) ?? string.Empty;
                var currentMaster = stockItems.FirstOrDefault(item => item.Name.Equals(currentItem, StringComparison.OrdinalIgnoreCase));
                Rows.Add(new SalesStockBulkReviewRow
                {
                    Count = group.Count(),
                    SourceValue = group.Key,
                    StockItemName = currentItem,
                    MasterSource = ResolveAuthority(currentMaster),
                    SourceRecords = group.Select(row => new StockItemMappingSourceRecord(
                        row.PersistentId, row.ItemName, row.StockItemPersistentId, row.TallyStockMasterId)).ToArray(),
                    ExpectedAlias = _existingAliases
                        .Where(alias => AccountingText.CleanWhitespace(alias.ImportedAlias).Equals(group.Key, StringComparison.OrdinalIgnoreCase))
                        .Select(alias => new StockItemMappingAliasRevision(alias.StockItemName, alias.Source, alias.UpdatedUtc))
                        .FirstOrDefault(),
                    // Show the current resolved Stock Item as the editable starting point.
                    // The visible editable ComboBox also permits typing a NEW Stock Item;
                    // the module ViewModel creates it once and maps every matching source
                    // item together.
                    FinalValue = currentMaster?.Name ??
                        (exact.Contains(group.Key) ? stockItems.FirstOrDefault(item => item.Name.Equals(group.Key, StringComparison.OrdinalIgnoreCase))?.Name ?? currentItem : currentItem),
                    FactorText = string.Empty,
                    Remember = false
                });
            }
            return;
        }

        foreach (var group in salesRows.Where(row => !string.IsNullOrWhiteSpace(row.ItemName) && !string.IsNullOrWhiteSpace(row.Unit))
                     .GroupBy(row => new
                     {
                         Item = row.ItemName.Trim(),
                         Unit = string.IsNullOrWhiteSpace(row.OriginalUnit) ? row.Unit.Trim() : row.OriginalUnit.Trim()
                     })
                     .OrderByDescending(group => group.Count()).ThenBy(group => group.Key.Item, StringComparer.OrdinalIgnoreCase).ThenBy(group => group.Key.Unit, StringComparer.OrdinalIgnoreCase))
        {
            var master = stockItems.FirstOrDefault(item => item.Name.Equals(group.Key.Item, StringComparison.OrdinalIgnoreCase));
            var finalUnit = AahUnitCatalog.TryGetDefinition(master?.BaseUnit, out var masterUnit)
                ? masterUnit.Code
                : string.Empty;

            // Fix123: Quantity Conversion is an exception/review screen, not a list of
            // every imported Unit. If the imported Unit already resolves to the Stock
            // Master's Base/Inventory Unit, there is nothing to convert and nothing to
            // confirm.
            if (Mode == SalesStockToolsWindow.QuantityConversionMode &&
                finalUnit.Length > 0 &&
                UnitEquivalent(group.Key.Unit, finalUnit))
                continue;

            var factorText = string.Empty;
            if (Mode == SalesStockToolsWindow.QuantityConversionMode &&
                finalUnit.Length > 0 &&
                SalesStockNormalizationService.TryUniversalFactor(group.Key.Unit, finalUnit, out var suggested))
                factorText = suggested.ToString("0.########", CultureInfo.InvariantCulture);

            Rows.Add(new SalesStockBulkReviewRow
            {
                Count = group.Count(),
                StockItemName = group.Key.Item,
                MasterSource = ResolveAuthority(master),
                SourceValue = group.Key.Unit,
                FinalValue = finalUnit,
                // Fix123: non-deterministic conversions start blank. AAH never invents
                // package contents by silently treating 1 QTL as 1 PCS.
                FactorText = Mode == SalesStockToolsWindow.QuantityConversionMode ? factorText : string.Empty,
                Remember = true // compatibility field; confirmed conversions are always persisted.
            });
        }
    }

    private static bool HasTallyMasterId(StockItemRecord? item)
    {
        // Reuse the Domain's MASTERID-first authority rule. TallyMasterId is a string.
        return item?.AuthoritySource == "Tally";
    }

    private static string ResolveAuthority(StockItemRecord? item)
    {
        if (item is null) return "Unresolved";
        if (HasTallyMasterId(item))
            return "Tally";
        return "Manual";
    }

    private static bool UnitEquivalent(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        return AahUnitCatalog.TryResolvePreferred(a, out var aCanonical, out var aFactor) &&
               AahUnitCatalog.TryResolvePreferred(b, out var bCanonical, out var bFactor) &&
               aFactor == 1m && bFactor == 1m &&
               aCanonical.Equals(bCanonical, StringComparison.OrdinalIgnoreCase);
    }

    private static InventoryReviewSource ToReviewSource(SalesImportRecord row) =>
        new(row.ItemName, row.OriginalItemName, row.Unit, row.OriginalUnit,
            row.PersistentId, row.StockItemPersistentId, row.TallyStockMasterId,
            HasInventoryEvidence(row.ItemName, row.Unit, row.ActualQuantity, row.BilledQuantity, row.ItemRate, row.ItemAmount));

    private static InventoryReviewSource ToReviewSource(PurchaseImportRecord row) =>
        new(row.ItemName, row.OriginalItemName, row.Unit, row.OriginalUnit,
            row.PersistentId, row.StockItemPersistentId, row.TallyStockMasterId,
            HasInventoryEvidence(row.ItemName, row.Unit, row.ActualQuantity, row.BilledQuantity, row.ItemRate, row.ItemAmount));

    private static bool HasInventoryEvidence(string itemName, string unit, decimal actual, decimal billed, decimal rate, decimal amount) =>
        !string.IsNullOrWhiteSpace(itemName) || !string.IsNullOrWhiteSpace(unit) ||
        Math.Abs(actual) > 0.000001m || Math.Abs(billed) > 0.000001m ||
        Math.Abs(rate) > 0.000001m || Math.Abs(amount) > 0.005m;

    private sealed record InventoryReviewSource(string ItemName, string OriginalItemName, string Unit, string OriginalUnit,
        long PersistentId, long StockItemPersistentId, string TallyStockMasterId, bool HasInventoryEvidence);

    public void SetSourceRevisions(IReadOnlyDictionary<long, StockItemMappingSourceRevision> revisions)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        foreach (var row in Rows)
        {
            var sources = new List<StockItemMappingSourceRecord>();
            foreach (var source in row.SourceRecords)
            {
                if (source.PersistentId <= 0 || !revisions.TryGetValue(source.PersistentId, out var revision))
                    throw new InvalidOperationException("The Stock review contains an unsaved or missing source revision. Save the import and reopen the review.");
                sources.Add(source with { ExpectedRevision = revision });
            }
            row.SourceRecords = sources;
        }
    }

    public IReadOnlyList<StockItemMappingSelection> GetItemMappingSelections() =>
        Rows.Where(row => !string.IsNullOrWhiteSpace(row.FinalValue))
            .Select(row => new StockItemMappingSelection(row.SourceValue, row.StockItemName,
                row.FinalValue.Trim(), row.SourceRecords, row.ExpectedAlias)).ToArray();

    private bool ValidateRows()
    {
        foreach (var row in Rows.Where(row => !string.IsNullOrWhiteSpace(row.FinalValue)))
        {
            if (Mode == SalesStockToolsWindow.ItemAliasMode && row.FinalValue.Trim().Length == 0)
            {
                ValidationText = "Enter or select the Final Stock Item. A new name is allowed and will be created once for this Client + FY.";
                return false;
            }
            if (Mode != SalesStockToolsWindow.ItemAliasMode && !Units.Contains(row.FinalValue.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                ValidationText = $"'{row.FinalValue}' is not an AAH standard Unit. Select one of the controlled GST/Tally unit codes.";
                return false;
            }
            if (Mode == SalesStockToolsWindow.UnitMappingMode &&
                (!AahUnitCatalog.TryGetFactor(row.SourceValue, row.FinalValue, out var unitAliasFactor) || unitAliasFactor != 1m))
            {
                ValidationText = $"{row.SourceValue} → {row.FinalValue} changes quantity. Units is only for equivalent/canonical names (factor 1). Use Quantity Conversion to confirm the factor before any quantity or rate is changed.";
                return false;
            }
            if (Mode == SalesStockToolsWindow.QuantityConversionMode)
            {
                if (AahUnitCatalog.TryGetFactor(row.SourceValue, row.FinalValue, out var authoritativeFactor))
                    row.FactorText = authoritativeFactor.ToString("0.########", CultureInfo.InvariantCulture);
                if (row.Factor <= 0m)
                {
                    ValidationText = $"Enter a conversion factor greater than zero for {row.StockItemName} / {row.SourceValue}. AAH fills deterministic physical conversions automatically; package contents must be specified explicitly.";
                    return false;
                }
            }
        }
        if (Mode == SalesStockToolsWindow.QuantityConversionMode && Rows.Count == 0)
        {
            ValidationText = "No conversion is required because the imported Units already match the Stock Master Units.";
            return false;
        }
        if (!Rows.Any(row => !string.IsNullOrWhiteSpace(row.FinalValue)))
        {
            ValidationText = "Enter at least one Final Value before Apply All.";
            return false;
        }
        ValidationText = string.Empty;
        return true;
    }

    private async void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplying) return;
        // A failed editor commit is a validation failure, not permission to close.
        if (!ReviewGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !ReviewGrid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            ValidationText = "Complete the current Stock Item edit before applying.";
            return;
        }
        Keyboard.ClearFocus();
        if (!ValidateRows()) return;
        if (Mode == SalesStockToolsWindow.ItemAliasMode && ApplyItemMappingsAsync is null)
        {
            ValidationText = "The Stock mapping save service is unavailable. Nothing was saved; reopen the module and retry.";
            return;
        }
        if (Mode != SalesStockToolsWindow.ItemAliasMode)
        {
            // Preserve the existing Unit Mapping / Quantity Conversion workflows.
            DialogResult = true;
            Close();
            return;
        }

        _isApplying = true;
        ApplyAllButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        ReviewGrid.IsEnabled = false;
        ValidationText = "Saving and verifying every selected Stock Item mapping...";
        IReadOnlyList<StockItemMappingSelection> pendingSelections = Array.Empty<StockItemMappingSelection>();
        try
        {
            var selections = GetItemMappingSelections();
            pendingSelections = selections;
            var save = ApplyItemMappingsAsync
                ?? throw new InvalidOperationException("The Stock mapping save service is unavailable.");
            var result = await save(selections);
            ValidationText = result.Message;
            _isApplying = false;
            if (result.Warning.Length > 0)
                MessageBox.Show(this, result.Message, "Stock mapping saved — refresh required", MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = true;
            Close();
        }
        catch (StockItemMappingAliasConflictException conflict)
        {
            // A deliberate replacement must be explicitly confirmed. A retry is
            // bound to the latest alias revision and rechecks every source row.
            try
            {
                var save = ApplyItemMappingsAsync
                    ?? throw new InvalidOperationException("The Stock mapping save service is unavailable.");
                var result = await ResolveAliasConflictsAsync(save, pendingSelections, conflict);
                if (result is not null)
                    CompleteMappingSave(result);
            }
            catch (Exception exception)
            {
                ValidationText = exception.Message;
            }
        }
        catch (Exception exception)
        {
            // No false success: the dialog stays open with the actual persistence error.
            ValidationText = exception.Message;
        }
        finally
        {
            _isApplying = false;
            ApplyAllButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            ReviewGrid.IsEnabled = true;
        }
    }

    private async Task<StockItemMappingResult?> ResolveAliasConflictsAsync(
        Func<IReadOnlyList<StockItemMappingSelection>, Task<StockItemMappingResult>> save,
        IReadOnlyList<StockItemMappingSelection> selections,
        StockItemMappingAliasConflictException conflict)
    {
        // Each confirmation is scoped to one imported name and one saved alias
        // revision. Never automatically adopt a different user-selected target.
        var maxAttempts = Math.Min(20, Math.Max(3, selections.Count + 2));
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var current = conflict.CurrentAlias?.StockItemName ?? "(No saved mapping)";
            var answer = MessageBox.Show(this,
                $"Imported Item: {conflict.ImportedName}\n" +
                $"Current saved mapping: {current}\n" +
                $"Your selected Stock Item: {conflict.RequestedName}\n\n" +
                "Yes — replace the current alias with my selected Stock Item.\n" +
                "No — keep the saved mapping and return to the review.\n\n" +
                "This will update the selected inventory identities only. Existing Stock Masters, " +
                "Tally MASTERIDs, accounting/GST values, quantities and original source evidence remain protected.",
                "Confirm Stock Item mapping", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                ValidationText = "No mapping was committed. Your selections are retained. " + conflict.Message;
                return null;
            }
            selections = conflict.ConfirmSelections(selections);
            ValidationText = "Saving your confirmed Stock Item mapping and verifying the latest data...";
            try
            {
                return await save(selections);
            }
            catch (StockItemMappingAliasConflictException next)
            {
                // A different writer may have changed the alias again. A previous
                // confirmation cannot authorize that new revision.
                conflict = next;
            }
        }
        throw new InvalidOperationException("The Stock mapping changed repeatedly while saving. No additional mapping was committed. Your selections remain available; retry after the other writer finishes.");
    }

    private void CompleteMappingSave(StockItemMappingResult result)
    {
        ValidationText = result.Message;
        _isApplying = false;
        if (result.Warning.Length > 0)
            MessageBox.Show(this, result.Message, "Stock mapping saved — refresh required", MessageBoxButton.OK, MessageBoxImage.Warning);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplying) return;
        DialogResult = false;
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isApplying) e.Cancel = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (_isApplying) return;
        DialogResult = false;
        Close();
    }
}

public sealed class SalesStockBulkReviewRow : INotifyPropertyChanged
{
    private string _sourceValue = string.Empty;
    private string _finalValue = string.Empty;
    private string _factorText = string.Empty;

    public int Count { get; set; }
    public string StockItemName { get; set; } = string.Empty;
    public string MasterSource { get; set; } = string.Empty;
    public IReadOnlyList<StockItemMappingSourceRecord> SourceRecords { get; set; } = Array.Empty<StockItemMappingSourceRecord>();
    public StockItemMappingAliasRevision? ExpectedAlias { get; set; }
    public string SourceValue
    {
        get => _sourceValue;
        set
        {
            _sourceValue = value ?? string.Empty;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourceValue)));
            RefreshAuthoritativeFactor();
        }
    }
    public string FinalValue
    {
        get => _finalValue;
        set
        {
            _finalValue = value ?? string.Empty;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FinalValue)));
            RefreshAuthoritativeFactor();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDeterministicFactor)));
        }
    }
    public string FactorText
    {
        get => _factorText;
        set
        {
            _factorText = value ?? string.Empty;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FactorText)));
        }
    }
    public bool Remember { get; set; }
    public bool IsDeterministicFactor =>
        AahUnitCatalog.TryGetFactor(SourceValue, FinalValue, out _);
    public decimal Factor => decimal.TryParse(FactorText, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) ? value :
        decimal.TryParse(FactorText, NumberStyles.Number, CultureInfo.InvariantCulture, out value) ? value : 0m;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RefreshAuthoritativeFactor()
    {
        if (AahUnitCatalog.TryGetFactor(SourceValue, FinalValue, out var factor))
            FactorText = factor.ToString("0.########", CultureInfo.InvariantCulture);
    }
}

public sealed record StockReviewChoice(string Name, string Authority, string Detail);
