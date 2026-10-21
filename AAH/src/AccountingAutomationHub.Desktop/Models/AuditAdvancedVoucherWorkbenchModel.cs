using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Domain.Audit;

namespace AccountingAutomationHub.Desktop.Models;

public sealed class AuditAdvancedVoucherWorkbenchModel : INotifyPropertyChanged
{
    private const int MaxUndoStates = 100;
    private readonly Stack<AuditWorkbenchStateChange> _undo = new();
    private readonly Stack<AuditWorkbenchStateChange> _redo = new();
    private readonly IReadOnlyList<string> _externalLedgers;
    private int _currentIndex;
    private bool _changeOnlyThisEntry;
    private bool _internalChange;
    private int _commonChangeActions;
    private string _statusText = string.Empty;
    private AuditEntryAdjustment? _selectedSavedRevision;

    public AuditAdvancedVoucherWorkbenchModel(
        string module,
        string filterContext,
        IReadOnlyCollection<AuditRecordRow> sourceRows,
        IReadOnlyList<string> ledgers,
        bool treatRowsAsNew = false)
    {
        Module = module;
        FilterContext = filterContext;
        _externalLedgers = ledgers ?? Array.Empty<string>();
        Vouchers = new ObservableCollection<AuditVoucherDraft>();
        SessionHistory = new ObservableCollection<AuditChangeHistoryItem>();
        MasterUsages = new ObservableCollection<AuditMasterUsageRow>();
        SavedRevisions = new ObservableCollection<AuditEntryAdjustment>();

        var slot = 0;
        foreach (var row in sourceRows)
        {
            var clone = CloneRow(row);
            EnsureAdvancedSeed(clone);
            Vouchers.Add(new AuditVoucherDraft(this, clone, slot, treatRowsAsNew));
            slot++;
        }
        if (Vouchers.Count == 0)
        {
            var blank = CreateBlankRow(module);
            EnsureAdvancedSeed(blank);
            Vouchers.Add(new AuditVoucherDraft(this, blank, 0, true));
        }

        _currentIndex = 0;
        RebuildMasterIndex();
        ValidateAll();
        RefreshOptionLists();
        StatusText = $"{Vouchers.Count:N0} selected voucher(s) loaded. Common synchronization is ON.";
    }

    public string Module { get; }
    public string HeaderText => $"{Module} · independent Audit accounting";
    public string FilterContext { get; }
    public ObservableCollection<AuditVoucherDraft> Vouchers { get; }
    public ObservableCollection<AuditChangeHistoryItem> SessionHistory { get; }
    public ObservableCollection<AuditMasterUsageRow> MasterUsages { get; }
    public ObservableCollection<AuditEntryAdjustment> SavedRevisions { get; }

    public IReadOnlyList<string> LedgerOptions { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> GroupOptions { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> StockItemOptions { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> UnitOptions { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> CostCentreOptions { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> MasterOptions { get; private set; } = Array.Empty<string>();

    public AuditVoucherDraft CurrentVoucher => Vouchers[Math.Clamp(_currentIndex, 0, Math.Max(0, Vouchers.Count - 1))];
    public int CurrentIndex
    {
        get => _currentIndex;
        set
        {
            if (Vouchers.Count == 0) return;
            var next = Math.Clamp(value, 0, Vouchers.Count - 1);
            if (_currentIndex == next) return;
            _currentIndex = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentVoucher));
            OnPropertyChanged(nameof(CurrentPositionText));
            OnPropertyChanged(nameof(OriginalSnapshotText));
            OnPropertyChanged(nameof(CanPrevious));
            OnPropertyChanged(nameof(CanNext));
        }
    }

    public string CurrentPositionText => $"Voucher {CurrentIndex + 1:N0} of {Vouchers.Count:N0}";
    public bool CanPrevious => CurrentIndex > 0;
    public bool CanNext => CurrentIndex + 1 < Vouchers.Count;

    public bool ChangeOnlyThisEntry
    {
        get => _changeOnlyThisEntry;
        set
        {
            if (_changeOnlyThisEntry == value) return;
            _changeOnlyThisEntry = value;
            OnPropertyChanged();
            StatusText = value
                ? "Change only this entry is ON. New edits affect the active voucher only."
                : "Common synchronization is ON. New edits propagate only to selected vouchers with the exact old value/master.";
        }
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool CanUndoCurrentVoucher => CurrentVoucher.PreviousStates.Count > 0;
    public int SelectedCount => Vouchers.Count;
    public int ModifiedCount => Vouchers.Count(v => v.IsModified);
    public int IndividualOverrideCount => Vouchers.Count(v => v.IndividualOverride);
    public int ValidCount => Vouchers.Count(v => v.ValidationStatus.Equals("Valid", StringComparison.OrdinalIgnoreCase));
    public int ReviewCount => Vouchers.Count(v => !v.ValidationStatus.Equals("Valid", StringComparison.OrdinalIgnoreCase));
    public int CommonChangeActions => _commonChangeActions;
    public string SummaryText => $"Selected: {SelectedCount:N0} | Modified: {ModifiedCount:N0} | Common changes: {CommonChangeActions:N0} | Individual overrides: {IndividualOverrideCount:N0} | Valid: {ValidCount:N0} | Review: {ReviewCount:N0}";
    public string OriginalSnapshotText => CurrentVoucher.OriginalRowJson;

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public AuditEntryAdjustment? SelectedSavedRevision
    {
        get => _selectedSavedRevision;
        set { if (!ReferenceEquals(_selectedSavedRevision, value)) { _selectedSavedRevision = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CurrentVoucherChanged;

    internal bool IsInternalChange => _internalChange;

    internal void ApplyScalarChange<T>(
        AuditVoucherDraft source,
        string propertyName,
        T oldValue,
        T newValue,
        Func<AuditVoucherDraft, T> getter,
        Action<AuditVoucherDraft, T> setter,
        string? displayName = null)
    {
        if (ValuesEqual(oldValue, newValue)) return;
        var affected = ChangeOnlyThisEntry
            ? new List<AuditVoucherDraft> { source }
            : Vouchers.Where(v => ValuesEquivalent(getter(v), oldValue)).ToList();
        if (!affected.Contains(source)) affected.Insert(0, source);

        ExecuteAction(
            $"{displayName ?? propertyName}: {DisplayValue(oldValue)} -> {DisplayValue(newValue)}",
            affected,
            () =>
            {
                foreach (var voucher in affected) setter(voucher, newValue);
            },
            affected.Count > 1);
    }

    internal void ApplyMasterChangeFromChild(
        string masterType,
        string oldValue,
        string newValue,
        AuditVoucherDraft source,
        string? groupHint = null)
    {
        ReplaceMaster(masterType, oldValue, newValue, source, groupHint);
    }

    public int ReplaceMaster(string masterType, string oldValue, string newValue, AuditVoucherDraft? sourceVoucher = null, string? groupHint = null)
    {
        oldValue = (oldValue ?? string.Empty).Trim();
        newValue = (newValue ?? string.Empty).Trim();
        if (oldValue.Length == 0 || newValue.Length == 0 || MasterEquals(oldValue, newValue)) return 0;

        var scope = ChangeOnlyThisEntry && sourceVoucher is not null
            ? new[] { sourceVoucher }
            : Vouchers.ToArray();
        var affected = scope.Where(v => CountMasterOccurrences(v, masterType, oldValue) > 0).ToList();
        if (affected.Count == 0) return 0;
        var occurrenceCount = affected.Sum(v => CountMasterOccurrences(v, masterType, oldValue));
        var resolvedGroup = ResolveGroup(masterType, newValue) ?? groupHint ?? string.Empty;

        ExecuteAction(
            $"{masterType}: {oldValue} -> {newValue} · {affected.Count:N0} voucher(s) / {occurrenceCount:N0} usage(s)",
            affected,
            () =>
            {
                foreach (var voucher in affected)
                    ReplaceMasterInVoucher(voucher, masterType, oldValue, newValue, resolvedGroup);
            },
            affected.Count > 1);

        RebuildMasterIndex();
        RefreshOptionLists();
        return occurrenceCount;
    }

    public int CountMatchingMasterVouchers(string masterType, string value) =>
        Vouchers.Count(v => CountMasterOccurrences(v, masterType, value) > 0);

    public int CountMatchingMasterLines(string masterType, string value) =>
        Vouchers.Sum(v => CountMasterOccurrences(v, masterType, value));

    public int CountMatchingMasterLinesInVoucher(AuditVoucherDraft voucher, string masterType, string value) =>
        voucher is null ? 0 : CountMasterOccurrences(voucher, masterType, value);

    public IReadOnlyList<AuditVoucherDraft> GetMatchingMasterVouchers(string masterType, string value) =>
        Vouchers.Where(v => CountMasterOccurrences(v, masterType, value) > 0).ToArray();

    public void AddLedgerLine(AuditVoucherDraft voucher)
    {
        var targets = ChangeOnlyThisEntry ? new[] { voucher } : Vouchers.ToArray();
        ExecuteAction("Add accounting line", targets,
            () =>
            {
                foreach (var item in targets)
                {
                    item.SetAccountingAuthoritativeLocal(true);
                    item.AddLedgerLineLocal(new AuditVoucherLedgerLineData { Role = "Other", Direction = "Dr" });
                }
            }, targets.Length > 1);
    }

    public void DeleteLedgerLine(AuditVoucherDraft voucher, AuditVoucherLedgerLineDraft? selected)
    {
        if (selected is null) return;
        var signatureLedger = selected.Ledger;
        var signatureDirection = selected.Direction;
        var signatureAmount = selected.Amount;
        var affected = new List<AuditVoucherDraft>();
        if (ChangeOnlyThisEntry)
        {
            affected.Add(voucher);
        }
        else
        {
            affected.AddRange(Vouchers.Where(v => v.LedgerLines.Any(line =>
                MasterEquals(line.Ledger, signatureLedger) &&
                string.Equals(line.Direction, signatureDirection, StringComparison.OrdinalIgnoreCase) &&
                line.Amount == signatureAmount)));
        }
        ExecuteAction("Delete matching accounting line", affected,
            () =>
            {
                foreach (var item in affected)
                {
                    item.SetAccountingAuthoritativeLocal(true);
                    if (ReferenceEquals(item, voucher))
                        item.RemoveLedgerLineLocal(selected.LineId);
                    else
                    {
                        var match = item.LedgerLines.FirstOrDefault(line =>
                            MasterEquals(line.Ledger, signatureLedger) &&
                            string.Equals(line.Direction, signatureDirection, StringComparison.OrdinalIgnoreCase) &&
                            line.Amount == signatureAmount);
                        if (match is not null) item.RemoveLedgerLineLocal(match.LineId);
                    }
                }
            }, affected.Count > 1);
    }

    public void DuplicateLedgerLine(AuditVoucherDraft voucher, AuditVoucherLedgerLineDraft? selected)
    {
        if (selected is null) return;
        var targets = ChangeOnlyThisEntry ? new[] { voucher } : Vouchers.ToArray();
        var sourceData = selected.ExportData();
        ExecuteAction("Duplicate accounting line", targets,
            () =>
            {
                foreach (var item in targets)
                {
                    item.SetAccountingAuthoritativeLocal(true);
                    var copy = Clone(sourceData);
                    copy.LineId = Guid.NewGuid().ToString("N");
                    item.AddLedgerLineLocal(copy);
                }
            }, targets.Length > 1);
    }

    public void AddInventoryLine(AuditVoucherDraft voucher)
    {
        var targets = ChangeOnlyThisEntry ? new[] { voucher } : Vouchers.ToArray();
        ExecuteAction("Add inventory line", targets,
            () =>
            {
                foreach (var item in targets)
                {
                    item.SetInventoryAuthoritativeLocal(true);
                    item.AddInventoryLineLocal(new AuditInventoryLineData());
                }
            }, targets.Length > 1);
    }

    public void DeleteInventoryLine(AuditVoucherDraft voucher, AuditInventoryLineDraft? selected)
    {
        if (selected is null) return;
        var stock = selected.StockItem;
        var hsn = selected.HsnSac;
        var qty = selected.Quantity;
        var affected = ChangeOnlyThisEntry
            ? new List<AuditVoucherDraft> { voucher }
            : Vouchers.Where(v => v.InventoryLines.Any(line => MasterEquals(line.StockItem, stock) && MasterEquals(line.HsnSac, hsn) && line.Quantity == qty)).ToList();
        ExecuteAction("Delete matching inventory line", affected,
            () =>
            {
                foreach (var item in affected)
                {
                    item.SetInventoryAuthoritativeLocal(true);
                    if (ReferenceEquals(item, voucher))
                        item.RemoveInventoryLineLocal(selected.LineId);
                    else
                    {
                        var match = item.InventoryLines.FirstOrDefault(line => MasterEquals(line.StockItem, stock) && MasterEquals(line.HsnSac, hsn) && line.Quantity == qty);
                        if (match is not null) item.RemoveInventoryLineLocal(match.LineId);
                    }
                }
            }, affected.Count > 1);
    }

    public void AddBillAllocation(AuditVoucherDraft voucher) => ExecuteVoucherLocalCollectionAction(voucher, "Add bill allocation", () => voucher.AddBillAllocationLocal(new AuditBillAllocationData()));
    public void DeleteBillAllocation(AuditVoucherDraft voucher, AuditBillAllocationDraft? selected) { if (selected is not null) ExecuteVoucherLocalCollectionAction(voucher, "Delete bill allocation", () => voucher.RemoveBillAllocationLocal(selected.LineId)); }
    public void AddCostCentreAllocation(AuditVoucherDraft voucher) => ExecuteVoucherLocalCollectionAction(voucher, "Add cost-centre allocation", () => voucher.AddCostCentreAllocationLocal(new AuditCostCentreAllocationData()));
    public void DeleteCostCentreAllocation(AuditVoucherDraft voucher, AuditCostCentreAllocationDraft? selected) { if (selected is not null) ExecuteVoucherLocalCollectionAction(voucher, "Delete cost-centre allocation", () => voucher.RemoveCostCentreAllocationLocal(selected.LineId)); }
    public void AddBankAllocation(AuditVoucherDraft voucher) => ExecuteVoucherLocalCollectionAction(voucher, "Add banking allocation", () => voucher.AddBankAllocationLocal(new AuditBankAllocationData()));
    public void DeleteBankAllocation(AuditVoucherDraft voucher, AuditBankAllocationDraft? selected) { if (selected is not null) ExecuteVoucherLocalCollectionAction(voucher, "Delete banking allocation", () => voucher.RemoveBankAllocationLocal(selected.LineId)); }

    internal void ApplyChildLocalChange<T>(AuditVoucherDraft voucher, string description, T oldValue, T newValue, Action<T> setter)
    {
        if (ValuesEqual(oldValue, newValue)) return;
        ExecuteAction(description, new[] { voucher }, () => setter(newValue), false, forceIndividual: true);
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var target = _undo.Pop();
        var current = CaptureState();
        _redo.Push(new AuditWorkbenchStateChange(current, target.Description));
        RestoreState(target.State);
        AddHistory($"UNDO · {target.Description}", 0, false);
        StatusText = $"Undid: {target.Description}";
        RaiseUndoRedoChanged();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var target = _redo.Pop();
        var current = CaptureState();
        _undo.Push(new AuditWorkbenchStateChange(current, target.Description));
        RestoreState(target.State);
        AddHistory($"REDO · {target.Description}", 0, false);
        StatusText = $"Redid: {target.Description}";
        RaiseUndoRedoChanged();
    }

    public void UndoCurrentVoucher()
    {
        var voucher = CurrentVoucher;
        if (voucher.PreviousStates.Count == 0) return;
        var previousIndex = voucher.PreviousStates.Count - 1;
        var previous = voucher.PreviousStates[previousIndex];
        AuditRecordRow? priorRow;
        try { priorRow = JsonSerializer.Deserialize<AuditRecordRow>(previous.DataJson); }
        catch { priorRow = null; }
        if (priorRow is null) return;

        // Per-voucher undo consumes exactly one stored previous state. The complete session
        // state is still pushed onto the global Undo stack, so Redo/global Undo remains safe.
        var before = CaptureState();
        _internalChange = true;
        try
        {
            voucher.PreviousStates.RemoveAt(previousIndex);
            voucher.ReplaceDataLocal(priorRow, preservePreviousStates: true);
            voucher.SetModifiedLocal(true);
            voucher.SetIndividualOverrideLocal(true);
        }
        finally { _internalChange = false; }

        _undo.Push(new AuditWorkbenchStateChange(before, $"Undo only voucher {voucher.VoucherNumberDisplay}"));
        TrimUndoStack();
        _redo.Clear();
        AddHistory($"UNDO VOUCHER · {previous.Description}", 1, false);
        RebuildMasterIndex();
        RefreshOptionLists();
        ValidateAll();
        NotifyAllVoucherBindings();
        RaiseUndoRedoChanged();
        StatusText = $"Restored the previous state of {voucher.VoucherNumberDisplay}. Other selected vouchers were not changed.";
    }

    public void RestoreCurrentFromSavedRevision(AuditEntryAdjustment revision)
    {
        if (revision is null || string.IsNullOrWhiteSpace(revision.OriginalJson)) return;
        AuditRecordRow? previous;
        try { previous = JsonSerializer.Deserialize<AuditRecordRow>(revision.OriginalJson); }
        catch { previous = null; }
        if (previous is null) return;
        var voucher = CurrentVoucher;
        ExecuteAction($"Restore saved revision {revision.Id}", new[] { voucher },
            () => voucher.ReplaceDataLocal(previous, preservePreviousStates: true), false, forceIndividual: true);
        StatusText = "Previous saved Audit version restored in memory. Use Save All Modified to persist it.";
    }

    public void GoPrevious() { if (CanPrevious) CurrentIndex--; }
    public void GoNext() { if (CanNext) CurrentIndex++; }
    public bool SelectVoucherBySessionId(string sessionId)
    {
        var index = Vouchers.ToList().FindIndex(v => v.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        CurrentIndex = index;
        CurrentVoucherChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void RebuildMasterIndex()
    {
        var map = new Dictionary<string, MasterAccumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var voucher in Vouchers)
        {
            var partyRepresentedByLine = voucher.LedgerLines.Any(line => line.Role.Contains("Party", StringComparison.OrdinalIgnoreCase) && MasterEquals(line.Ledger, voucher.PartyOrLedger));
            var businessRepresentedByLine = voucher.LedgerLines.Any(line => !line.Role.Contains("Party", StringComparison.OrdinalIgnoreCase) && !line.Role.Contains("GST", StringComparison.OrdinalIgnoreCase) && !line.Role.Contains("Tax", StringComparison.OrdinalIgnoreCase) && MasterEquals(line.Ledger, voucher.BusinessLedger));
            if (!partyRepresentedByLine) AddUsage(map, "Party", voucher.PartyOrLedger, voucher.CounterpartyGroup, voucher, 1);
            if (!businessRepresentedByLine) AddUsage(map, "Ledger", voucher.BusinessLedger, voucher.BusinessGroup, voucher, 1);
            AddUsage(map, "Group", voucher.BusinessGroup, string.Empty, voucher, 1);
            AddUsage(map, "Group", voucher.CounterpartyGroup, string.Empty, voucher, 1);
            if (!voucher.LedgerLines.Any(line => MasterEquals(line.CostCentre, voucher.CostCentre)) && !voucher.CostCentreAllocations.Any(line => MasterEquals(line.CostCentre, voucher.CostCentre))) AddUsage(map, "Cost Centre", voucher.CostCentre, string.Empty, voucher, 1);
            if (!voucher.InventoryLines.Any(line => MasterEquals(line.StockItem, voucher.ItemName))) AddUsage(map, "Stock Item", voucher.ItemName, string.Empty, voucher, 1);
            if (!voucher.InventoryLines.Any(line => MasterEquals(line.Unit, voucher.Unit))) AddUsage(map, "Unit", voucher.Unit, string.Empty, voucher, 1);
            if (!voucher.InventoryLines.Any(line => MasterEquals(line.HsnSac, voucher.HsnCode))) AddUsage(map, "HSN/SAC", voucher.HsnCode, string.Empty, voucher, 1);
            foreach (var gstLedger in SplitPipe(voucher.TaxLedgers).Where(gst => !voucher.LedgerLines.Any(line => (line.Role.Contains("GST", StringComparison.OrdinalIgnoreCase) || line.Role.Contains("Tax", StringComparison.OrdinalIgnoreCase)) && MasterEquals(line.Ledger, gst)))) AddUsage(map, "GST Ledger", gstLedger, string.Empty, voucher, 1);
            foreach (var line in voucher.LedgerLines)
            {
                var type = line.Role.Contains("Party", StringComparison.OrdinalIgnoreCase) ? "Party"
                    : line.Role.Contains("GST", StringComparison.OrdinalIgnoreCase) || line.Role.Contains("Tax", StringComparison.OrdinalIgnoreCase) ? "GST Ledger"
                    : "Ledger";
                AddUsage(map, type, line.Ledger, line.Group, voucher, 1);
                AddUsage(map, "Group", line.Group, string.Empty, voucher, 1);
                AddUsage(map, "Cost Centre", line.CostCentre, string.Empty, voucher, 1);
            }
            foreach (var line in voucher.InventoryLines)
            {
                AddUsage(map, "Stock Item", line.StockItem, line.Group, voucher, 1);
                AddUsage(map, "Group", line.Group, string.Empty, voucher, 1);
                AddUsage(map, "Unit", line.Unit, string.Empty, voucher, 1);
                AddUsage(map, "HSN/SAC", line.HsnSac, string.Empty, voucher, 1);
            }
            foreach (var line in voucher.BillAllocations) AddUsage(map, "Ledger", line.Ledger, string.Empty, voucher, 1);
            foreach (var line in voucher.CostCentreAllocations)
            {
                AddUsage(map, "Ledger", line.Ledger, string.Empty, voucher, 1);
                AddUsage(map, "Cost Centre", line.CostCentre, string.Empty, voucher, 1);
            }
            foreach (var line in voucher.BankAllocations) AddUsage(map, "Ledger", line.Ledger, string.Empty, voucher, 1);
            AddUsage(map, "TDS/TCS", voucher.TdsSection, string.Empty, voucher, 1);
            AddUsage(map, "TDS/TCS", voucher.TcsSection, string.Empty, voucher, 1);
        }

        MasterUsages.Clear();
        foreach (var entry in map.Values.OrderBy(v => v.Type, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Master, StringComparer.OrdinalIgnoreCase))
        {
            MasterUsages.Add(new AuditMasterUsageRow
            {
                Type = entry.Type,
                ExistingMaster = entry.Master,
                Group = entry.Group,
                VoucherCount = entry.VoucherIds.Count,
                LineCount = entry.LineCount,
                VoucherNumbers = string.Join(", ", entry.VoucherNumbers.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            });
        }
        OnPropertyChanged(nameof(MasterUsages));
    }

    public IReadOnlyList<string> MasterTypes => MasterUsages.Select(x => x.Type).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

    public void ValidateAll()
    {
        foreach (var voucher in Vouchers)
        {
            var issues = new List<string>();
            if (!voucher.Date.HasValue) issues.Add("Date is required.");
            if (string.IsNullOrWhiteSpace(voucher.VoucherType)) issues.Add("Voucher Type is required.");
            if (Module is "Sales" or "Purchase" or "Banking" or "Cash" && string.IsNullOrWhiteSpace(voucher.PartyOrLedger))
                issues.Add("Party / Counterparty is required.");
            if (voucher.AccountingLinesAuthoritative)
            {
                if (voucher.LedgerLines.Count < 2)
                    issues.Add("A standard accounting voucher requires at least two ledger lines.");
                if (voucher.LedgerLines.Any(line => string.IsNullOrWhiteSpace(line.Ledger)))
                    issues.Add("Every accounting line requires a Ledger.");
                if (voucher.LedgerLines.Any(line => line.Amount < 0m))
                    issues.Add("Accounting line amount cannot be negative.");
                if (Math.Abs(voucher.AccountingDifference) > 0.01m)
                    issues.Add($"Debit/Credit difference is {voucher.AccountingDifference:N2}.");
            }
            if (voucher.InventoryLines.Any(line => line.Quantity < 0m)) issues.Add("Inventory quantity cannot be negative.");
            voucher.SetValidationLocal(issues.Count == 0 ? "Valid" : "Review", string.Join(" ", issues));
        }

        var duplicateGroups = Vouchers.GroupBy(v => $"{v.Date:yyyyMMdd}|{NormalizeMaster(v.Reference)}|{NormalizeMaster(v.PartyOrLedger)}|{v.Amount:0.00}", StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1 && g.Key.Replace("|", string.Empty).Length > 0)
            .ToArray();
        foreach (var group in duplicateGroups)
        {
            foreach (var voucher in group)
                voucher.SetValidationLocal("Review", AppendMessage(voucher.ValidationMessage, "Possible duplicate within the selected vouchers."));
        }
        RaiseSummaryChanged();
    }

    public AuditWorkbenchSaveValidation ValidateForSave()
    {
        ValidateAll();
        var invalid = Vouchers.Where(v => v.ValidationStatus.Equals("Review", StringComparison.OrdinalIgnoreCase) &&
                                          (v.ValidationMessage.Contains("required", StringComparison.OrdinalIgnoreCase) ||
                                           v.ValidationMessage.Contains("difference", StringComparison.OrdinalIgnoreCase) ||
                                           v.ValidationMessage.Contains("requires at least two ledger lines", StringComparison.OrdinalIgnoreCase) ||
                                           v.ValidationMessage.Contains("requires a Ledger", StringComparison.OrdinalIgnoreCase) ||
                                           v.ValidationMessage.Contains("cannot be negative", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return new AuditWorkbenchSaveValidation(invalid.Length == 0, invalid);
    }

    public void MarkSaved(AuditVoucherDraft voucher, AuditRecordRow savedRow)
    {
        voucher.AcceptSavedLocal(savedRow);
        ValidateAll();
        RebuildMasterIndex();
        RefreshOptionLists();
        StatusText = "Audit voucher changes saved. Original source evidence remains immutable.";
    }

    public void RefreshSavedRevisions(IEnumerable<AuditEntryAdjustment> revisions)
    {
        SavedRevisions.Clear();
        foreach (var item in revisions.OrderByDescending(x => x.UpdatedUtc).ThenByDescending(x => x.Id)) SavedRevisions.Add(item);
        SelectedSavedRevision = SavedRevisions.FirstOrDefault();
    }

    private void ExecuteVoucherLocalCollectionAction(AuditVoucherDraft voucher, string description, Action action) =>
        ExecuteAction(description, new[] { voucher }, action, false, forceIndividual: true);

    private void ExecuteAction(string description, IReadOnlyCollection<AuditVoucherDraft> affected, Action action, bool common, bool forceIndividual = false)
    {
        if (affected.Count == 0) return;
        var before = CaptureState();
        foreach (var voucher in affected.Distinct()) voucher.RememberPrevious(description);
        _internalChange = true;
        try
        {
            action();
            foreach (var voucher in affected.Distinct())
            {
                voucher.SetModifiedLocal(true);
                if (forceIndividual || affected.Count == 1) voucher.SetIndividualOverrideLocal(true);
            }
        }
        finally { _internalChange = false; }

        _undo.Push(new AuditWorkbenchStateChange(before, description));
        TrimUndoStack();
        _redo.Clear();
        if (common && affected.Count > 1) _commonChangeActions++;
        AddHistory(description, affected.Count, common && affected.Count > 1);
        RebuildMasterIndex();
        RefreshOptionLists();
        ValidateAll();
        NotifyAllVoucherBindings();
        RaiseUndoRedoChanged();
        StatusText = affected.Count > 1
            ? $"{description} · changed only {affected.Count:N0} matching selected vouchers."
            : $"{description} · changed only the active/matching voucher.";
    }

    private void TrimUndoStack()
    {
        if (_undo.Count <= MaxUndoStates) return;
        var newestFirst = _undo.ToArray().Take(MaxUndoStates).ToArray();
        _undo.Clear();
        for (var index = newestFirst.Length - 1; index >= 0; index--) _undo.Push(newestFirst[index]);
    }

    private void RaiseUndoRedoChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(CanUndoCurrentVoucher));
        RaiseSummaryChanged();
    }

    private void RaiseSummaryChanged()
    {
        OnPropertyChanged(nameof(ModifiedCount));
        OnPropertyChanged(nameof(IndividualOverrideCount));
        OnPropertyChanged(nameof(ValidCount));
        OnPropertyChanged(nameof(ReviewCount));
        OnPropertyChanged(nameof(CommonChangeActions));
        OnPropertyChanged(nameof(SummaryText));
    }

    private void NotifyAllVoucherBindings()
    {
        foreach (var voucher in Vouchers) voucher.NotifyAll();
        OnPropertyChanged(nameof(CurrentVoucher));
        OnPropertyChanged(nameof(OriginalSnapshotText));
        CurrentVoucherChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddHistory(string description, int affectedCount, bool common)
    {
        SessionHistory.Insert(0, new AuditChangeHistoryItem
        {
            WhenLocal = DateTime.Now,
            Description = description,
            AffectedCount = affectedCount,
            Scope = common ? "Common" : "Individual/Exact"
        });
        while (SessionHistory.Count > 250) SessionHistory.RemoveAt(SessionHistory.Count - 1);
    }

    private AuditWorkbenchSessionState CaptureState() => new()
    {
        CurrentIndex = CurrentIndex,
        ChangeOnlyThisEntry = ChangeOnlyThisEntry,
        CommonChangeActions = _commonChangeActions,
        Vouchers = Vouchers.Select(v => v.ExportSnapshot()).ToList()
    };

    private void RestoreState(AuditWorkbenchSessionState state)
    {
        _internalChange = true;
        try
        {
            Vouchers.Clear();
            foreach (var item in state.Vouchers) Vouchers.Add(new AuditVoucherDraft(this, item));
            _currentIndex = Math.Clamp(state.CurrentIndex, 0, Math.Max(0, Vouchers.Count - 1));
            _changeOnlyThisEntry = state.ChangeOnlyThisEntry;
            _commonChangeActions = state.CommonChangeActions;
        }
        finally { _internalChange = false; }
        RebuildMasterIndex();
        RefreshOptionLists();
        ValidateAll();
        OnPropertyChanged(nameof(CurrentIndex));
        OnPropertyChanged(nameof(CurrentVoucher));
        OnPropertyChanged(nameof(CurrentPositionText));
        OnPropertyChanged(nameof(ChangeOnlyThisEntry));
        OnPropertyChanged(nameof(CanPrevious));
        OnPropertyChanged(nameof(CanNext));
        OnPropertyChanged(nameof(OriginalSnapshotText));
        NotifyAllVoucherBindings();
    }

    private int CountMasterOccurrences(AuditVoucherDraft voucher, string type, string oldValue)
    {
        var count = 0;
        switch (NormalizeType(type))
        {
            case "PARTY":
            {
                var lineCount = voucher.LedgerLines.Count(line => line.Role.Contains("Party", StringComparison.OrdinalIgnoreCase) && MasterEquals(line.Ledger, oldValue));
                count += lineCount;
                if (lineCount == 0 && MasterEquals(voucher.PartyOrLedger, oldValue)) count++;
                break;
            }
            case "LEDGER":
            {
                var lineCount = voucher.LedgerLines.Count(line => !line.Role.Contains("Party", StringComparison.OrdinalIgnoreCase) && !line.Role.Contains("GST", StringComparison.OrdinalIgnoreCase) && !line.Role.Contains("Tax", StringComparison.OrdinalIgnoreCase) && MasterEquals(line.Ledger, oldValue));
                count += lineCount;
                if (lineCount == 0 && MasterEquals(voucher.BusinessLedger, oldValue)) count++;
                count += voucher.BillAllocations.Count(line => MasterEquals(line.Ledger, oldValue));
                count += voucher.CostCentreAllocations.Count(line => MasterEquals(line.Ledger, oldValue));
                count += voucher.BankAllocations.Count(line => MasterEquals(line.Ledger, oldValue));
                break;
            }
            case "GST LEDGER":
            {
                var lineCount = voucher.LedgerLines.Count(line => (line.Role.Contains("GST", StringComparison.OrdinalIgnoreCase) || line.Role.Contains("Tax", StringComparison.OrdinalIgnoreCase)) && MasterEquals(line.Ledger, oldValue));
                count += lineCount;
                if (lineCount == 0) count += SplitPipe(voucher.TaxLedgers).Count(item => MasterEquals(item, oldValue));
                break;
            }
            case "STOCK ITEM":
            {
                var lineCount = voucher.InventoryLines.Count(line => MasterEquals(line.StockItem, oldValue));
                count += lineCount; if (lineCount == 0 && MasterEquals(voucher.ItemName, oldValue)) count++; break;
            }
            case "UNIT":
            {
                var lineCount = voucher.InventoryLines.Count(line => MasterEquals(line.Unit, oldValue));
                count += lineCount; if (lineCount == 0 && MasterEquals(voucher.Unit, oldValue)) count++; break;
            }
            case "COST CENTRE":
            {
                var lineCount = voucher.LedgerLines.Count(line => MasterEquals(line.CostCentre, oldValue)) + voucher.CostCentreAllocations.Count(line => MasterEquals(line.CostCentre, oldValue));
                count += lineCount; if (lineCount == 0 && MasterEquals(voucher.CostCentre, oldValue)) count++; break;
            }
            case "HSN/SAC":
            {
                var lineCount = voucher.InventoryLines.Count(line => MasterEquals(line.HsnSac, oldValue));
                count += lineCount; if (lineCount == 0 && MasterEquals(voucher.HsnCode, oldValue)) count++; break;
            }
            case "GROUP":
                if (MasterEquals(voucher.BusinessGroup, oldValue)) count++;
                if (MasterEquals(voucher.CounterpartyGroup, oldValue)) count++;
                count += voucher.LedgerLines.Count(line => MasterEquals(line.Group, oldValue));
                count += voucher.InventoryLines.Count(line => MasterEquals(line.Group, oldValue));
                break;
            case "TDS/TCS":
                if (MasterEquals(voucher.TdsSection, oldValue)) count++;
                if (MasterEquals(voucher.TcsSection, oldValue)) count++;
                break;
        }
        return count;
    }

    private void ReplaceMasterInVoucher(AuditVoucherDraft voucher, string type, string oldValue, string newValue, string resolvedGroup)
    {
        switch (NormalizeType(type))
        {
            case "PARTY":
                if (MasterEquals(voucher.PartyOrLedger, oldValue))
                {
                    voucher.SetPartyLocal(newValue);
                    if (resolvedGroup.Length > 0) voucher.SetCounterpartyGroupLocal(resolvedGroup);
                }
                foreach (var line in voucher.LedgerLines.Where(line => line.Role.Contains("Party", StringComparison.OrdinalIgnoreCase) && MasterEquals(line.Ledger, oldValue)))
                {
                    line.SetLedgerLocal(newValue);
                    if (resolvedGroup.Length > 0) line.SetGroupLocal(resolvedGroup);
                }
                break;
            case "LEDGER":
                if (MasterEquals(voucher.BusinessLedger, oldValue))
                {
                    voucher.SetBusinessLedgerLocal(newValue);
                    if (resolvedGroup.Length > 0) voucher.SetBusinessGroupLocal(resolvedGroup);
                }
                foreach (var line in voucher.LedgerLines.Where(line => !line.Role.Contains("Party", StringComparison.OrdinalIgnoreCase) && !line.Role.Contains("GST", StringComparison.OrdinalIgnoreCase) && !line.Role.Contains("Tax", StringComparison.OrdinalIgnoreCase) && MasterEquals(line.Ledger, oldValue)))
                {
                    line.SetLedgerLocal(newValue);
                    if (resolvedGroup.Length > 0) line.SetGroupLocal(resolvedGroup);
                }
                foreach (var line in voucher.BillAllocations.Where(line => MasterEquals(line.Ledger, oldValue))) line.SetLedgerLocal(newValue);
                foreach (var line in voucher.CostCentreAllocations.Where(line => MasterEquals(line.Ledger, oldValue))) line.SetLedgerLocal(newValue);
                foreach (var line in voucher.BankAllocations.Where(line => MasterEquals(line.Ledger, oldValue))) line.SetLedgerLocal(newValue);
                break;
            case "GST LEDGER":
                voucher.SetTaxLedgersLocal(string.Join(" | ", SplitPipe(voucher.TaxLedgers).Select(item => MasterEquals(item, oldValue) ? newValue : item)));
                foreach (var line in voucher.LedgerLines.Where(line => (line.Role.Contains("GST", StringComparison.OrdinalIgnoreCase) || line.Role.Contains("Tax", StringComparison.OrdinalIgnoreCase)) && MasterEquals(line.Ledger, oldValue)))
                {
                    line.SetLedgerLocal(newValue);
                    if (resolvedGroup.Length > 0) line.SetGroupLocal(resolvedGroup);
                }
                break;
            case "STOCK ITEM":
                if (MasterEquals(voucher.ItemName, oldValue)) voucher.SetItemNameLocal(newValue);
                foreach (var line in voucher.InventoryLines.Where(line => MasterEquals(line.StockItem, oldValue)))
                {
                    line.SetStockItemLocal(newValue);
                    if (resolvedGroup.Length > 0) line.SetGroupLocal(resolvedGroup);
                }
                break;
            case "UNIT":
                if (MasterEquals(voucher.Unit, oldValue)) voucher.SetUnitLocal(newValue);
                foreach (var line in voucher.InventoryLines.Where(line => MasterEquals(line.Unit, oldValue))) line.SetUnitLocal(newValue);
                break;
            case "COST CENTRE":
                if (MasterEquals(voucher.CostCentre, oldValue)) voucher.SetCostCentreLocal(newValue);
                foreach (var line in voucher.LedgerLines.Where(line => MasterEquals(line.CostCentre, oldValue))) line.SetCostCentreLocal(newValue);
                foreach (var line in voucher.CostCentreAllocations.Where(line => MasterEquals(line.CostCentre, oldValue))) line.SetCostCentreLocal(newValue);
                break;
            case "HSN/SAC":
                if (MasterEquals(voucher.HsnCode, oldValue)) voucher.SetHsnCodeLocal(newValue);
                foreach (var line in voucher.InventoryLines.Where(line => MasterEquals(line.HsnSac, oldValue))) line.SetHsnSacLocal(newValue);
                break;
            case "GROUP":
                if (MasterEquals(voucher.BusinessGroup, oldValue)) voucher.SetBusinessGroupLocal(newValue);
                if (MasterEquals(voucher.CounterpartyGroup, oldValue)) voucher.SetCounterpartyGroupLocal(newValue);
                foreach (var line in voucher.LedgerLines.Where(line => MasterEquals(line.Group, oldValue))) line.SetGroupLocal(newValue);
                foreach (var line in voucher.InventoryLines.Where(line => MasterEquals(line.Group, oldValue))) line.SetGroupLocal(newValue);
                break;
            case "TDS/TCS":
                if (MasterEquals(voucher.TdsSection, oldValue)) voucher.SetTdsSectionLocal(newValue);
                if (MasterEquals(voucher.TcsSection, oldValue)) voucher.SetTcsSectionLocal(newValue);
                break;
        }
    }

    private string? ResolveGroup(string type, string master)
    {
        var usage = MasterUsages.FirstOrDefault(row => row.Type.Equals(type, StringComparison.OrdinalIgnoreCase) && MasterEquals(row.ExistingMaster, master));
        return usage is null || string.IsNullOrWhiteSpace(usage.Group) ? null : usage.Group;
    }

    private void RefreshOptionLists()
    {
        LedgerOptions = _externalLedgers
            .Concat(MasterUsages.Where(x => x.Type is "Ledger" or "Party" or "GST Ledger").Select(x => x.ExistingMaster))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        GroupOptions = MasterUsages.Where(x => x.Type == "Group").Select(x => x.ExistingMaster)
            .Concat(MasterUsages.Select(x => x.Group)).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        StockItemOptions = MasterUsages.Where(x => x.Type == "Stock Item").Select(x => x.ExistingMaster).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        UnitOptions = MasterUsages.Where(x => x.Type == "Unit").Select(x => x.ExistingMaster).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        CostCentreOptions = MasterUsages.Where(x => x.Type == "Cost Centre").Select(x => x.ExistingMaster).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        MasterOptions = MasterUsages.Select(x => x.ExistingMaster).Concat(LedgerOptions).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        OnPropertyChanged(nameof(LedgerOptions)); OnPropertyChanged(nameof(GroupOptions)); OnPropertyChanged(nameof(StockItemOptions)); OnPropertyChanged(nameof(UnitOptions)); OnPropertyChanged(nameof(CostCentreOptions)); OnPropertyChanged(nameof(MasterOptions));
    }

    private static void AddUsage(Dictionary<string, MasterAccumulator> map, string type, string value, string group, AuditVoucherDraft voucher, int lineCount)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var normalized = NormalizeMaster(value);
        var key = $"{type.ToUpperInvariant()}|{normalized}";
        if (!map.TryGetValue(key, out var item))
        {
            item = new MasterAccumulator { Type = type, Master = value.Trim(), Group = (group ?? string.Empty).Trim() };
            map[key] = item;
        }
        if (item.Group.Length == 0 && !string.IsNullOrWhiteSpace(group)) item.Group = group.Trim();
        item.VoucherIds.Add(voucher.SessionId);
        item.VoucherNumbers.Add(voucher.VoucherNumberDisplay);
        item.LineCount += lineCount;
    }

    private static void EnsureAdvancedSeed(AuditRecordRow row)
    {
        row.AdvancedVoucher ??= new AuditVoucherAdvancedData();
        var advanced = row.AdvancedVoucher;
        if (advanced.LedgerLines is null) advanced.LedgerLines = [];
        if (advanced.InventoryLines is null) advanced.InventoryLines = [];
        if (advanced.BillAllocations is null) advanced.BillAllocations = [];
        if (advanced.CostCentreAllocations is null) advanced.CostCentreAllocations = [];
        if (advanced.BankAllocations is null) advanced.BankAllocations = [];
        if (row.IsRcm) advanced.IsReverseCharge = true;

        if (advanced.LedgerLines.Count == 0)
        {
            SeedCapturedAccountingLines(row, advanced);
        }

        if (advanced.InventoryLines.Count == 0 && (!string.IsNullOrWhiteSpace(row.ItemName) || row.Quantity != 0m || row.TaxableValue != 0m))
        {
            advanced.InventoryLines.Add(new AuditInventoryLineData
            {
                StockItem = row.ItemName, HsnSac = row.HsnCode, Quantity = row.Quantity, Unit = row.Unit,
                Rate = row.ItemRate, DiscountPercent = row.DiscountPercent, DiscountAmount = row.DiscountAmount,
                TaxableValue = row.TaxableValue, GstRate = row.GstRate, Igst = row.Igst, Cgst = row.Cgst, Sgst = row.Sgst,
                Cess = row.Cess, LineTotal = row.Amount
            });
        }
    }

    // Audit display seed is source-transparent. It may determine Dr/Cr presentation from the
    // source voucher type, but it never substitutes, repairs, rejects or invents a Ledger name.
    // Exact Tally ledger lines normally arrive pre-populated; this path is only for source rows
    // that expose scalar ledger fields. It remains non-authoritative until the user edits a line.
    private static void SeedCapturedAccountingLines(AuditRecordRow row, AuditVoucherAdvancedData advanced)
    {
        var sourceModule = NormalizeType(row.SourceModule);
        var voucherType = NormalizeType(row.VoucherType);
        var isSalesFamily = sourceModule == "SALES" || (sourceModule.Length == 0 && (voucherType == "SALES" || voucherType == "CREDIT NOTE"));
        var isPurchaseFamily = sourceModule == "PURCHASE" || (sourceModule.Length == 0 && (voucherType == "PURCHASE" || voucherType == "DEBIT NOTE"));

        if (isSalesFamily || isPurchaseFamily)
        {
            var normalSales = isSalesFamily && voucherType == "SALES";
            var normalPurchase = isPurchaseFamily && voucherType == "PURCHASE";
            var partyIsDebit = isSalesFamily ? normalSales : !normalPurchase;
            var partyDirection = partyIsDebit ? "Dr" : "Cr";
            var businessDirection = partyIsDebit ? "Cr" : "Dr";
            var taxLedgers = SplitPipe(row.TaxLedgers).ToArray();
            var sourceAmount = row.Amount != 0m ? Math.Abs(row.Amount) : Math.Max(Math.Abs(row.Debit), Math.Abs(row.Credit));
            var taxTotal = Math.Abs(row.Igst) + Math.Abs(row.Cgst) + Math.Abs(row.Sgst) + Math.Abs(row.Cess);
            var businessAmount = Math.Abs(row.TaxableValue);
            if (businessAmount <= 0.005m && sourceAmount > 0.005m)
                businessAmount = Math.Max(0m, sourceAmount - taxTotal - row.RoundOff);
            if (businessAmount <= 0.005m && sourceAmount > 0.005m && taxTotal <= 0.005m)
                businessAmount = sourceAmount;

            if (businessAmount > 0.005m || !string.IsNullOrWhiteSpace(row.BusinessLedger))
                AddStandardLedgerLine(advanced, row.BusinessLedger, row.BusinessGroup, businessDirection, businessAmount, "Business", row.CostCentre, row, "Taxable");

            AddTaxComponentLine(advanced, taxLedgers, "IGST", row.Igst, isSalesFamily, businessDirection, row);
            AddTaxComponentLine(advanced, taxLedgers, "CGST", row.Cgst, isSalesFamily, businessDirection, row);
            AddTaxComponentLine(advanced, taxLedgers, "SGST", row.Sgst, isSalesFamily, businessDirection, row);
            if (Math.Abs(row.Cess) > 0.005m)
            {
                var cessLedger = FindTaxLedger(taxLedgers, "CESS");
                if (cessLedger.Length > 0)
                    AddStandardLedgerLine(advanced, cessLedger, string.Empty, businessDirection, Math.Abs(row.Cess), "GST", row.CostCentre, row, string.Empty);
            }

            var debit = advanced.LedgerLines.Where(line => line.Direction.Equals("Dr", StringComparison.OrdinalIgnoreCase)).Sum(line => Math.Abs(line.Amount));
            var credit = advanced.LedgerLines.Where(line => line.Direction.Equals("Cr", StringComparison.OrdinalIgnoreCase)).Sum(line => Math.Abs(line.Amount));
            var partyAmount = partyIsDebit ? credit - debit : debit - credit;
            if (partyAmount <= 0.005m) partyAmount = sourceAmount;
            AddStandardLedgerLine(advanced, row.PartyOrLedger, row.CounterpartyGroup, partyDirection, Math.Abs(partyAmount), "Party", row.CostCentre, row, string.Empty, insertFirst: true);
            return;
        }

        var amount = row.Amount != 0m ? Math.Abs(row.Amount) : Math.Max(Math.Abs(row.Debit), Math.Abs(row.Credit));
        var partyIsDebitFallback = row.Debit > 0m && row.Credit <= 0m;
        if (voucherType == "PAYMENT") partyIsDebitFallback = true;
        else if (voucherType == "RECEIPT") partyIsDebitFallback = false;
        var partyDirectionFallback = partyIsDebitFallback ? "Dr" : "Cr";
        var balancingDirection = partyIsDebitFallback ? "Cr" : "Dr";

        if (!string.IsNullOrWhiteSpace(row.PartyOrLedger) || amount > 0m)
            AddStandardLedgerLine(advanced, row.PartyOrLedger, row.CounterpartyGroup, partyDirectionFallback, amount, "Party", row.CostCentre, row, string.Empty);
        if (!string.IsNullOrWhiteSpace(row.BusinessLedger))
            AddStandardLedgerLine(advanced, row.BusinessLedger, row.BusinessGroup, balancingDirection, amount, "Business", row.CostCentre, row, string.Empty);
        else if (amount > 0m)
            AddStandardLedgerLine(advanced, string.Empty, string.Empty, balancingDirection, amount, "Other", row.CostCentre, row, string.Empty);
    }

    private static void AddTaxComponentLine(
        AuditVoucherAdvancedData advanced,
        IReadOnlyList<string> taxLedgers,
        string component,
        decimal amount,
        bool salesFamily,
        string direction,
        AuditRecordRow row)
    {
        if (Math.Abs(amount) <= 0.005m) return;
        var ledger = FindTaxLedger(taxLedgers, component);
        if (ledger.Length == 0) return;
        AddStandardLedgerLine(advanced, ledger, string.Empty, direction, Math.Abs(amount), "GST", row.CostCentre, row, string.Empty);
    }

    private static void AddRcmTaxPair(
        AuditVoucherAdvancedData advanced,
        IReadOnlyList<string> taxLedgers,
        string component,
        decimal amount,
        decimal taxRate,
        AuditRecordRow row)
    {
        if (Math.Abs(amount) <= 0.005m) return;
        var inputLedger = FindTaxLedger(taxLedgers, component);
        if (inputLedger.Length == 0) inputLedger = component == "CESS" ? "Input Cess RCM" : $"Input {component} RCM";
        var payableLedger = taxRate > 0m
            ? $"{component} RCM Payable @ {FormatRateText(taxRate)}%"
            : $"{component} RCM Payable";
        AddStandardLedgerLine(advanced, inputLedger, "Duties & Taxes", "Dr", Math.Abs(amount), "GST", row.CostCentre, row, "RCM Input Tax");
        AddStandardLedgerLine(advanced, payableLedger, "Duties & Taxes", "Cr", Math.Abs(amount), "GST", row.CostCentre, row, "RCM Payable");
    }

    private static void RepairCommerceSeedBusinessLedger(AuditRecordRow row)
    {
        var sourceModule = NormalizeType(row.SourceModule);
        var voucherType = NormalizeType(row.VoucherType);
        var isSalesFamily = sourceModule == "SALES" || (sourceModule.Length == 0 && (voucherType == "SALES" || voucherType == "CREDIT NOTE"));
        var isPurchaseFamily = sourceModule == "PURCHASE" || (sourceModule.Length == 0 && (voucherType == "PURCHASE" || voucherType == "DEBIT NOTE"));
        if (!isSalesFamily && !isPurchaseFamily) return;

        var taxLedgers = SplitPipe(row.TaxLedgers).ToArray();
        var current = (row.BusinessLedger ?? string.Empty).Trim();
        var candidate = SplitPipe(current)
            .FirstOrDefault(ledger => IsValidCommerceSeedBusinessLedger(ledger, row.PartyOrLedger, row.BusinessGroup, taxLedgers))
            ?.Trim() ?? string.Empty;

        if (candidate.Length == 0)
        {
            candidate = isPurchaseFamily
                ? row.GstRate > 0m ? $"Purchase @ {FormatRateText(row.GstRate)}%" : "Exempted Purchase"
                : row.GstRate > 0m ? $"Sales @ {FormatRateText(row.GstRate)}%" : "Exempted Sales";
            row.BusinessGroup = isPurchaseFamily ? "Purchase Accounts" : "Sales Accounts";
        }
        else if (!candidate.Equals(current, StringComparison.OrdinalIgnoreCase) ||
                 string.IsNullOrWhiteSpace(row.BusinessGroup) ||
                 row.BusinessGroup.Equals("Duties & Taxes", StringComparison.OrdinalIgnoreCase))
        {
            // A repaired/split candidate must not inherit the Group of a rejected Round Off/tax ledger.
            row.BusinessGroup = isPurchaseFamily ? "Purchase Accounts" : "Sales Accounts";
        }

        row.BusinessLedger = candidate;
    }

    private static bool IsValidCommerceSeedBusinessLedger(
        string? ledger,
        string? party,
        string? businessGroup,
        IReadOnlyList<string> taxLedgers)
    {
        var value = (ledger ?? string.Empty).Trim();
        if (value.Length == 0) return false;
        if (MasterEquals(value, party ?? string.Empty)) return false;
        if (taxLedgers.Any(taxLedger => MasterEquals(value, taxLedger))) return false;
        if ((businessGroup ?? string.Empty).Trim().Equals("Duties & Taxes", StringComparison.OrdinalIgnoreCase)) return false;
        return !IsRoundOffLedgerName(value);
    }

    private static bool IsRoundOffLedgerName(string? ledger)
    {
        var normalized = new string((ledger ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        return normalized.Contains("ROUNDOFF", StringComparison.Ordinal) ||
               normalized.Contains("ROUNDINGOFF", StringComparison.Ordinal);
    }

    private static string FormatRateText(decimal rate) =>
        rate.ToString("0.####", CultureInfo.InvariantCulture);

    private static string FindTaxLedger(IReadOnlyList<string> ledgers, string component) =>
        ledgers.FirstOrDefault(name => name.Contains(component, StringComparison.OrdinalIgnoreCase))?.Trim() ?? string.Empty;

    private static void AddStandardLedgerLine(
        AuditVoucherAdvancedData advanced,
        string ledger,
        string group,
        string direction,
        decimal amount,
        string role,
        string costCentre,
        AuditRecordRow row,
        string gstTreatment,
        bool insertFirst = false)
    {
        var line = new AuditVoucherLedgerLineData
        {
            Ledger = (ledger ?? string.Empty).Trim(),
            Group = (group ?? string.Empty).Trim(),
            Direction = direction,
            Amount = Math.Abs(amount),
            GstTreatment = gstTreatment,
            CostCentre = (costCentre ?? string.Empty).Trim(),
            Narration = row.OriginalNarration.Length > 0 ? row.OriginalNarration : row.Details,
            Role = role
        };
        if (insertFirst) advanced.LedgerLines.Insert(0, line); else advanced.LedgerLines.Add(line);
    }

    public static AuditRecordRow CreateBlankRow(string module) => new()
    {
        Date = DateTime.Today,
        VoucherType = module switch
        {
            "Sales" => "Sales", "Purchase" => "Purchase", "Banking" => "Payment", "Cash" => "Payment", "Journal" => "Journal", _ => module
        },
        AuthorityId = $"AUDIT-WORK-{Guid.NewGuid():N}",
        Source = "Audit Accounting Workbench",
        Status = "Audit working entry",
        EvidenceStatus = "Audit accounting workbench",
        SourceModule = module,
        Category = module,
        TransactionClass = module,
        AdvancedVoucher = new AuditVoucherAdvancedData()
    };

    private static string AppendMessage(string existing, string next) => string.IsNullOrWhiteSpace(existing) ? next : existing.Trim() + " " + next;
    internal static bool MasterEquals(string? left, string? right) => string.Equals(NormalizeMaster(left), NormalizeMaster(right), StringComparison.OrdinalIgnoreCase);
    internal static string NormalizeMaster(string? value) => string.Join(" ", (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    private static string NormalizeType(string? value) => NormalizeMaster(value).ToUpperInvariant();
    private static IEnumerable<string> SplitPipe(string? value) => (value ?? string.Empty).Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length > 0);
    private static bool ValuesEqual<T>(T left, T right) => EqualityComparer<T>.Default.Equals(left, right);
    private static bool ValuesEquivalent<T>(T left, T right)
    {
        if (left is string ls && right is string rs) return MasterEquals(ls, rs);
        return ValuesEqual(left, right);
    }
    private static string DisplayValue<T>(T value) => value switch
    {
        null => "(blank)",
        DateTime dt => dt.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
        decimal number => number.ToString("0.##", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };
    private static AuditRecordRow CloneRow(AuditRecordRow row) => JsonSerializer.Deserialize<AuditRecordRow>(JsonSerializer.Serialize(row)) ?? new AuditRecordRow();
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value)) ?? throw new InvalidOperationException("Unable to clone Audit voucher data.");

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class MasterAccumulator
    {
        public string Type { get; set; } = string.Empty;
        public string Master { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public HashSet<string> VoucherIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> VoucherNumbers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int LineCount { get; set; }
    }
}

public sealed class AuditVoucherDraft : INotifyPropertyChanged
{
    private readonly AuditAdvancedVoucherWorkbenchModel _owner;
    private AuditRecordRow _data;
    private string _validationStatus = "Valid";
    private string _validationMessage = string.Empty;

    internal AuditVoucherDraft(AuditAdvancedVoucherWorkbenchModel owner, AuditRecordRow row, int originalSlot, bool isNew)
    {
        _owner = owner;
        _data = row;
        OriginalSlot = originalSlot;
        IsNew = isNew;
        SessionId = Guid.NewGuid().ToString("N");
        OriginalSourceRecordId = row.SourceRecordId;
        OriginalSourceIdentity = AuditWorkspaceViewModel.AccountingWorkbenchSourceIdentity(row);
        OriginalRowJson = isNew ? string.Empty : JsonSerializer.Serialize(row);
        PreviousStates = new ObservableCollection<AuditVoucherPreviousState>();
        InitializeChildren();
    }

    internal AuditVoucherDraft(AuditAdvancedVoucherWorkbenchModel owner, AuditVoucherSnapshotItem snapshot)
    {
        _owner = owner;
        _data = snapshot.Data ?? new AuditRecordRow();
        OriginalSlot = snapshot.OriginalSlot;
        IsNew = snapshot.IsNew;
        SessionId = snapshot.SessionId;
        OriginalSourceRecordId = snapshot.OriginalSourceRecordId;
        OriginalSourceIdentity = snapshot.OriginalSourceIdentity;
        OriginalRowJson = snapshot.OriginalRowJson;
        IsModified = snapshot.IsModified;
        IndividualOverride = snapshot.IndividualOverride;
        _validationStatus = snapshot.ValidationStatus;
        _validationMessage = snapshot.ValidationMessage;
        PreviousStates = new ObservableCollection<AuditVoucherPreviousState>(snapshot.PreviousStates ?? []);
        InitializeChildren();
    }

    public string SessionId { get; }
    public int OriginalSlot { get; }
    public bool IsNew { get; private set; }
    public bool IsModified { get; private set; }
    public bool IndividualOverride { get; private set; }
    public long OriginalSourceRecordId { get; private set; }
    public string OriginalSourceIdentity { get; private set; }
    public string OriginalRowJson { get; private set; }
    public ObservableCollection<AuditVoucherPreviousState> PreviousStates { get; }
    public ObservableCollection<AuditVoucherLedgerLineDraft> LedgerLines { get; private set; } = new();
    public ObservableCollection<AuditInventoryLineDraft> InventoryLines { get; private set; } = new();
    public ObservableCollection<AuditBillAllocationDraft> BillAllocations { get; private set; } = new();
    public ObservableCollection<AuditCostCentreAllocationDraft> CostCentreAllocations { get; private set; } = new();
    public ObservableCollection<AuditBankAllocationDraft> BankAllocations { get; private set; } = new();

    public string VoucherNumberDisplay => string.IsNullOrWhiteSpace(Reference) ? $"(no ref · {OriginalSlot + 1})" : Reference;
    public string Source => _data.Source;
    public string EvidenceStatus => _data.EvidenceStatus;
    public string TallyMasterId => _data.TallyMasterId;
    public string TallyGuid => _data.TallyGuid;

    public DateTime? Date { get => _data.Date; set => SetScalar(nameof(Date), _data.Date, value, v => v._data.Date, (v, x) => v.SetDateLocal(x), "Date"); }
    public DateTime? EffectiveDate { get => _data.AdvancedVoucher.EffectiveDate; set => SetScalar(nameof(EffectiveDate), _data.AdvancedVoucher.EffectiveDate, value, v => v._data.AdvancedVoucher.EffectiveDate, (v, x) => v.SetEffectiveDateLocal(x), "Effective Date"); }
    public string Reference { get => _data.Reference; set => SetScalar(nameof(Reference), _data.Reference, value ?? string.Empty, v => v._data.Reference, (v, x) => v.SetReferenceLocal(x), "Reference / Voucher No."); }
    public DateTime? ReferenceDate { get => _data.AdvancedVoucher.ReferenceDate; set => SetScalar(nameof(ReferenceDate), _data.AdvancedVoucher.ReferenceDate, value, v => v._data.AdvancedVoucher.ReferenceDate, (v, x) => v.SetReferenceDateLocal(x), "Reference Date"); }
    public string VoucherType { get => _data.VoucherType; set => SetScalar(nameof(VoucherType), _data.VoucherType, value ?? string.Empty, v => v._data.VoucherType, (v, x) => v.SetVoucherTypeLocal(x), "Voucher Type"); }
    public string PartyOrLedger { get => _data.PartyOrLedger; set => SetMasterScalar("Party", _data.PartyOrLedger, value ?? string.Empty, v => v.SetPartyLocal(value ?? string.Empty)); }
    public string CounterpartyGroup { get => _data.CounterpartyGroup; set => SetScalar(nameof(CounterpartyGroup), _data.CounterpartyGroup, value ?? string.Empty, v => v._data.CounterpartyGroup, (v, x) => v.SetCounterpartyGroupLocal(x), "Party Group"); }
    public string BusinessLedger { get => _data.BusinessLedger; set => SetMasterScalar("Ledger", _data.BusinessLedger, value ?? string.Empty, v => v.SetBusinessLedgerLocal(value ?? string.Empty)); }
    public string BusinessGroup { get => _data.BusinessGroup; set => SetScalar(nameof(BusinessGroup), _data.BusinessGroup, value ?? string.Empty, v => v._data.BusinessGroup, (v, x) => v.SetBusinessGroupLocal(x), "Posting Group"); }
    public string PlaceOfSupply { get => _data.PlaceOfSupply; set => SetScalar(nameof(PlaceOfSupply), _data.PlaceOfSupply, value ?? string.Empty, v => v._data.PlaceOfSupply, (v, x) => v.SetPlaceOfSupplyLocal(x), "Place of Supply"); }
    public string Gstin { get => _data.Gstin; set => SetScalar(nameof(Gstin), _data.Gstin, value ?? string.Empty, v => v._data.Gstin, (v, x) => v.SetGstinLocal(x), "GSTIN"); }
    public string Pan { get => _data.AdvancedVoucher.Pan; set => SetScalar(nameof(Pan), _data.AdvancedVoucher.Pan, value ?? string.Empty, v => v._data.AdvancedVoucher.Pan, (v, x) => v.SetPanLocal(x), "PAN"); }
    public string GstRegistrationType { get => _data.AdvancedVoucher.GstRegistrationType; set => SetScalar(nameof(GstRegistrationType), _data.AdvancedVoucher.GstRegistrationType, value ?? string.Empty, v => v._data.AdvancedVoucher.GstRegistrationType, (v, x) => v.SetGstRegistrationTypeLocal(x), "GST Registration Type"); }
    public string TransactionNature { get => _data.AdvancedVoucher.TransactionNature; set => SetScalar(nameof(TransactionNature), _data.AdvancedVoucher.TransactionNature, value ?? string.Empty, v => v._data.AdvancedVoucher.TransactionNature, (v, x) => v.SetTransactionNatureLocal(x), "Transaction Nature"); }
    public string InvoiceType { get => _data.InvoiceType; set => SetScalar(nameof(InvoiceType), _data.InvoiceType, value ?? string.Empty, v => v._data.InvoiceType, (v, x) => v.SetInvoiceTypeLocal(x), "Invoice Type"); }
    public decimal Debit { get => _data.Debit; set => SetScalar(nameof(Debit), _data.Debit, value, v => v._data.Debit, (v, x) => v.SetDebitLocal(x), "Debit"); }
    public decimal Credit { get => _data.Credit; set => SetScalar(nameof(Credit), _data.Credit, value, v => v._data.Credit, (v, x) => v.SetCreditLocal(x), "Credit"); }
    public decimal Amount { get => _data.Amount; set => SetScalar(nameof(Amount), _data.Amount, value, v => v._data.Amount, (v, x) => v.SetAmountLocal(x), "Amount"); }
    public decimal TaxableValue { get => _data.TaxableValue; set => SetScalar(nameof(TaxableValue), _data.TaxableValue, value, v => v._data.TaxableValue, (v, x) => v.SetTaxableValueLocal(x), "Taxable Value"); }
    public decimal GstRate { get => _data.GstRate; set => SetScalar(nameof(GstRate), _data.GstRate, value, v => v._data.GstRate, (v, x) => v.SetGstRateLocal(x), "GST Rate"); }
    public decimal Igst { get => _data.Igst; set => SetScalar(nameof(Igst), _data.Igst, value, v => v._data.Igst, (v, x) => v.SetIgstLocal(x), "IGST"); }
    public decimal Cgst { get => _data.Cgst; set => SetScalar(nameof(Cgst), _data.Cgst, value, v => v._data.Cgst, (v, x) => v.SetCgstLocal(x), "CGST"); }
    public decimal Sgst { get => _data.Sgst; set => SetScalar(nameof(Sgst), _data.Sgst, value, v => v._data.Sgst, (v, x) => v.SetSgstLocal(x), "SGST"); }
    public decimal Cess { get => _data.Cess; set => SetScalar(nameof(Cess), _data.Cess, value, v => v._data.Cess, (v, x) => v.SetCessLocal(x), "Cess"); }
    public string TaxLedgers { get => _data.TaxLedgers; set => SetScalar(nameof(TaxLedgers), _data.TaxLedgers, value ?? string.Empty, v => v._data.TaxLedgers, (v, x) => v.SetTaxLedgersLocal(x), "GST Ledgers"); }
    public string CostCentre { get => _data.CostCentre; set => SetMasterScalar("Cost Centre", _data.CostCentre, value ?? string.Empty, v => v.SetCostCentreLocal(value ?? string.Empty)); }
    public string Narration { get => _data.OriginalNarration.Length > 0 ? _data.OriginalNarration : _data.Details; set => SetScalar(nameof(Narration), Narration, value ?? string.Empty, v => v.Narration, (v, x) => v.SetNarrationLocal(x), "Narration"); }
    public string Notes { get => _data.AdvancedVoucher.Notes; set => SetScalar(nameof(Notes), _data.AdvancedVoucher.Notes, value ?? string.Empty, v => v._data.AdvancedVoucher.Notes, (v, x) => v.SetNotesLocal(x), "Notes"); }
    public bool IsReverseCharge { get => _data.AdvancedVoucher.IsReverseCharge; set => SetScalar(nameof(IsReverseCharge), _data.AdvancedVoucher.IsReverseCharge, value, v => v._data.AdvancedVoucher.IsReverseCharge, (v, x) => v.SetReverseChargeLocal(x), "Reverse Charge"); }
    public bool IsNilRated { get => _data.AdvancedVoucher.IsNilRated; set => SetScalar(nameof(IsNilRated), _data.AdvancedVoucher.IsNilRated, value, v => v._data.AdvancedVoucher.IsNilRated, (v, x) => v.SetNilRatedLocal(x), "Nil Rated"); }
    public bool IsExempt { get => _data.AdvancedVoucher.IsExempt; set => SetScalar(nameof(IsExempt), _data.AdvancedVoucher.IsExempt, value, v => v._data.AdvancedVoucher.IsExempt, (v, x) => v.SetExemptLocal(x), "Exempt"); }
    public bool IsNonGst { get => _data.AdvancedVoucher.IsNonGst; set => SetScalar(nameof(IsNonGst), _data.AdvancedVoucher.IsNonGst, value, v => v._data.AdvancedVoucher.IsNonGst, (v, x) => v.SetNonGstLocal(x), "Non-GST"); }
    public string TdsSection { get => _data.AdvancedVoucher.TdsSection; set => SetMasterScalar("TDS/TCS", _data.AdvancedVoucher.TdsSection, value ?? string.Empty, v => v.SetTdsSectionLocal(value ?? string.Empty)); }
    public decimal TdsRate { get => _data.AdvancedVoucher.TdsRate; set => SetScalar(nameof(TdsRate), _data.AdvancedVoucher.TdsRate, value, v => v._data.AdvancedVoucher.TdsRate, (v, x) => v.SetTdsRateLocal(x), "TDS Rate"); }
    public decimal TdsAmount { get => _data.AdvancedVoucher.TdsAmount; set => SetScalar(nameof(TdsAmount), _data.AdvancedVoucher.TdsAmount, value, v => v._data.AdvancedVoucher.TdsAmount, (v, x) => v.SetTdsAmountLocal(x), "TDS Amount"); }
    public string TcsSection { get => _data.AdvancedVoucher.TcsSection; set => SetMasterScalar("TDS/TCS", _data.AdvancedVoucher.TcsSection, value ?? string.Empty, v => v.SetTcsSectionLocal(value ?? string.Empty)); }
    public decimal TcsRate { get => _data.AdvancedVoucher.TcsRate; set => SetScalar(nameof(TcsRate), _data.AdvancedVoucher.TcsRate, value, v => v._data.AdvancedVoucher.TcsRate, (v, x) => v.SetTcsRateLocal(x), "TCS Rate"); }
    public decimal TcsAmount { get => _data.AdvancedVoucher.TcsAmount; set => SetScalar(nameof(TcsAmount), _data.AdvancedVoucher.TcsAmount, value, v => v._data.AdvancedVoucher.TcsAmount, (v, x) => v.SetTcsAmountLocal(x), "TCS Amount"); }
    public string ItemName { get => _data.ItemName; set => SetMasterScalar("Stock Item", _data.ItemName, value ?? string.Empty, v => v.SetItemNameLocal(value ?? string.Empty)); }
    public string HsnCode { get => _data.HsnCode; set => SetMasterScalar("HSN/SAC", _data.HsnCode, value ?? string.Empty, v => v.SetHsnCodeLocal(value ?? string.Empty)); }
    public string Unit { get => _data.Unit; set => SetMasterScalar("Unit", _data.Unit, value ?? string.Empty, v => v.SetUnitLocal(value ?? string.Empty)); }

    public bool AccountingLinesAuthoritative => _data.AdvancedVoucher.AccountingLinesAuthoritative;
    public bool InventoryLinesAuthoritative => _data.AdvancedVoucher.InventoryLinesAuthoritative;
    public decimal AccountingDebit => LedgerLines.Where(x => x.Direction.Equals("Dr", StringComparison.OrdinalIgnoreCase)).Sum(x => Math.Abs(x.Amount));
    public decimal AccountingCredit => LedgerLines.Where(x => x.Direction.Equals("Cr", StringComparison.OrdinalIgnoreCase)).Sum(x => Math.Abs(x.Amount));
    public decimal AccountingDifference => AccountingDebit - AccountingCredit;
    public decimal InventoryTaxableTotal => InventoryLines.Sum(x => x.TaxableValue);
    public decimal InventoryGstTotal => InventoryLines.Sum(x => x.Igst + x.Cgst + x.Sgst + x.Cess);
    public decimal InventoryGrandTotal => InventoryLines.Sum(x => x.LineTotal);

    public string ValidationStatus => _validationStatus;
    public string ValidationMessage => _validationMessage;
    public string ChangeStatus => IndividualOverride ? "Individual Override" : IsModified ? "Common/Modified" : "Unchanged";
    public int PreviousStateCount => PreviousStates.Count;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetScalar<T>(string property, T oldValue, T newValue, Func<AuditVoucherDraft, T> getter, Action<AuditVoucherDraft, T> setter, string display)
    {
        if (_owner.IsInternalChange) { setter(this, newValue); return; }
        _owner.ApplyScalarChange(this, property, oldValue, newValue, getter, setter, display);
    }

    private void SetMasterScalar(string type, string oldValue, string newValue, Action<AuditVoucherDraft> localSetter)
    {
        if (_owner.IsInternalChange) { localSetter(this); return; }
        if (AuditAdvancedVoucherWorkbenchModel.MasterEquals(oldValue, newValue)) return;
        if (string.IsNullOrWhiteSpace(oldValue))
        {
            // A blank old Master is intentionally local-only. Treating every blank field
            // in the selected set as one Master would be a destructive bulk edit.
            _owner.ApplyChildLocalChange(this, $"{type}: (blank) -> {newValue}", oldValue, newValue, _ => localSetter(this));
            return;
        }
        _owner.ReplaceMaster(type, oldValue, newValue, this);
    }

    internal void InitializeChildren()
    {
        _data.AdvancedVoucher ??= new AuditVoucherAdvancedData();
        _data.AdvancedVoucher.LedgerLines ??= [];
        _data.AdvancedVoucher.InventoryLines ??= [];
        _data.AdvancedVoucher.BillAllocations ??= [];
        _data.AdvancedVoucher.CostCentreAllocations ??= [];
        _data.AdvancedVoucher.BankAllocations ??= [];
        LedgerLines = new ObservableCollection<AuditVoucherLedgerLineDraft>(_data.AdvancedVoucher.LedgerLines.Select(x => new AuditVoucherLedgerLineDraft(_owner, this, x)));
        InventoryLines = new ObservableCollection<AuditInventoryLineDraft>(_data.AdvancedVoucher.InventoryLines.Select(x => new AuditInventoryLineDraft(_owner, this, x)));
        BillAllocations = new ObservableCollection<AuditBillAllocationDraft>(_data.AdvancedVoucher.BillAllocations.Select(x => new AuditBillAllocationDraft(_owner, this, x)));
        CostCentreAllocations = new ObservableCollection<AuditCostCentreAllocationDraft>(_data.AdvancedVoucher.CostCentreAllocations.Select(x => new AuditCostCentreAllocationDraft(_owner, this, x)));
        BankAllocations = new ObservableCollection<AuditBankAllocationDraft>(_data.AdvancedVoucher.BankAllocations.Select(x => new AuditBankAllocationDraft(_owner, this, x)));
    }

    public AuditRecordRow ToRow()
    {
        var row = JsonSerializer.Deserialize<AuditRecordRow>(JsonSerializer.Serialize(_data)) ?? new AuditRecordRow();
        if (row.AdvancedVoucher.AccountingLinesAuthoritative)
        {
            var dr = LedgerLines.Where(x => x.Direction.Equals("Dr", StringComparison.OrdinalIgnoreCase)).Sum(x => Math.Abs(x.Amount));
            var cr = LedgerLines.Where(x => x.Direction.Equals("Cr", StringComparison.OrdinalIgnoreCase)).Sum(x => Math.Abs(x.Amount));
            row.Debit = dr;
            row.Credit = cr;
            row.Amount = Math.Max(dr, cr);
            var business = LedgerLines.FirstOrDefault(x => x.Role.Equals("Business", StringComparison.OrdinalIgnoreCase))
                ?? LedgerLines.FirstOrDefault(x => !x.Role.Contains("Party", StringComparison.OrdinalIgnoreCase) && !x.Role.Contains("GST", StringComparison.OrdinalIgnoreCase) && !x.Role.Contains("Tax", StringComparison.OrdinalIgnoreCase));
            if (business is not null) { row.BusinessLedger = business.Ledger; row.BusinessGroup = business.Group; }
            var party = LedgerLines.FirstOrDefault(x => x.Role.Contains("Party", StringComparison.OrdinalIgnoreCase));
            if (party is not null) { row.PartyOrLedger = party.Ledger; row.CounterpartyGroup = party.Group; }
            row.CostCentre = LedgerLines.Select(x => x.CostCentre).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? row.CostCentre;
        }
        if (row.AdvancedVoucher.InventoryLinesAuthoritative && InventoryLines.Count > 0)
        {
            var first = InventoryLines[0];
            row.ItemName = first.StockItem; row.HsnCode = first.HsnSac; row.Quantity = first.Quantity; row.Unit = first.Unit; row.ItemRate = first.Rate;
            row.DiscountPercent = first.DiscountPercent; row.DiscountAmount = first.DiscountAmount;
            row.TaxableValue = InventoryLines.Sum(x => x.TaxableValue); row.Igst = InventoryLines.Sum(x => x.Igst); row.Cgst = InventoryLines.Sum(x => x.Cgst);
            row.Sgst = InventoryLines.Sum(x => x.Sgst); row.Cess = InventoryLines.Sum(x => x.Cess);
            var distinctRates = InventoryLines.Where(x => x.GstRate != 0m).Select(x => x.GstRate).Distinct().ToArray();
            row.GstRate = distinctRates.Length == 1 ? distinctRates[0] : row.GstRate;
            row.GstRateKnown = distinctRates.Length > 0;
        }
        row.Details = Narration;
        row.OriginalNarration = Narration;
        return row;
    }

    internal AuditVoucherSnapshotItem ExportSnapshot() => new()
    {
        SessionId = SessionId, OriginalSlot = OriginalSlot, IsNew = IsNew, IsModified = IsModified, IndividualOverride = IndividualOverride,
        OriginalSourceRecordId = OriginalSourceRecordId, OriginalSourceIdentity = OriginalSourceIdentity, OriginalRowJson = OriginalRowJson,
        ValidationStatus = ValidationStatus, ValidationMessage = ValidationMessage, Data = ToRow(), PreviousStates = PreviousStates.Select(x => x.Clone()).ToList()
    };

    internal void RememberPrevious(string description)
    {
        PreviousStates.Add(new AuditVoucherPreviousState { WhenLocal = DateTime.Now, Description = description, DataJson = JsonSerializer.Serialize(ToRow()) });
        while (PreviousStates.Count > 100) PreviousStates.RemoveAt(0);
        OnPropertyChanged(nameof(PreviousStateCount));
    }

    internal void ReplaceDataLocal(AuditRecordRow row, bool preservePreviousStates)
    {
        _data = JsonSerializer.Deserialize<AuditRecordRow>(JsonSerializer.Serialize(row)) ?? new AuditRecordRow();
        if (!preservePreviousStates) PreviousStates.Clear();
        InitializeChildren();
        NotifyAll();
    }

    internal void AcceptSavedLocal(AuditRecordRow saved)
    {
        _data = JsonSerializer.Deserialize<AuditRecordRow>(JsonSerializer.Serialize(saved)) ?? saved;
        OriginalSourceRecordId = saved.SourceRecordId;
        if (OriginalSourceIdentity.Length == 0) OriginalSourceIdentity = AuditWorkspaceViewModel.AccountingWorkbenchSourceIdentity(saved);
        OriginalRowJson = JsonSerializer.Serialize(saved);
        IsNew = false; IsModified = false; IndividualOverride = false;
        PreviousStates.Clear();
        InitializeChildren();
        NotifyAll();
    }

    internal void SetModifiedLocal(bool value) { IsModified = value; OnPropertyChanged(nameof(IsModified)); OnPropertyChanged(nameof(ChangeStatus)); }
    internal void SetIndividualOverrideLocal(bool value) { IndividualOverride = value; OnPropertyChanged(nameof(IndividualOverride)); OnPropertyChanged(nameof(ChangeStatus)); }
    internal void SetValidationLocal(string status, string message) { _validationStatus = status; _validationMessage = message; OnPropertyChanged(nameof(ValidationStatus)); OnPropertyChanged(nameof(ValidationMessage)); }
    internal void SetDateLocal(DateTime? value) { _data.Date = value; OnPropertyChanged(nameof(Date)); }
    internal void SetEffectiveDateLocal(DateTime? value) { _data.AdvancedVoucher.EffectiveDate = value; OnPropertyChanged(nameof(EffectiveDate)); }
    internal void SetReferenceLocal(string value) { _data.Reference = value.Trim(); OnPropertyChanged(nameof(Reference)); OnPropertyChanged(nameof(VoucherNumberDisplay)); }
    internal void SetReferenceDateLocal(DateTime? value) { _data.AdvancedVoucher.ReferenceDate = value; OnPropertyChanged(nameof(ReferenceDate)); }
    internal void SetVoucherTypeLocal(string value) { _data.VoucherType = value.Trim(); OnPropertyChanged(nameof(VoucherType)); }
    internal void SetPartyLocal(string value) { _data.PartyOrLedger = value.Trim(); OnPropertyChanged(nameof(PartyOrLedger)); }
    internal void SetCounterpartyGroupLocal(string value) { _data.CounterpartyGroup = value.Trim(); OnPropertyChanged(nameof(CounterpartyGroup)); }
    internal void SetBusinessLedgerLocal(string value) { _data.BusinessLedger = value.Trim(); OnPropertyChanged(nameof(BusinessLedger)); }
    internal void SetBusinessGroupLocal(string value) { _data.BusinessGroup = value.Trim(); OnPropertyChanged(nameof(BusinessGroup)); }
    internal void SetPlaceOfSupplyLocal(string value) { _data.PlaceOfSupply = value.Trim(); OnPropertyChanged(nameof(PlaceOfSupply)); }
    internal void SetGstinLocal(string value) { _data.Gstin = value.Trim(); OnPropertyChanged(nameof(Gstin)); }
    internal void SetPanLocal(string value) { _data.AdvancedVoucher.Pan = value.Trim(); OnPropertyChanged(nameof(Pan)); }
    internal void SetGstRegistrationTypeLocal(string value) { _data.AdvancedVoucher.GstRegistrationType = value.Trim(); OnPropertyChanged(nameof(GstRegistrationType)); }
    internal void SetTransactionNatureLocal(string value) { _data.AdvancedVoucher.TransactionNature = value.Trim(); OnPropertyChanged(nameof(TransactionNature)); }
    internal void SetInvoiceTypeLocal(string value) { _data.InvoiceType = value.Trim(); OnPropertyChanged(nameof(InvoiceType)); }
    internal void SetDebitLocal(decimal value) { _data.Debit = value; OnPropertyChanged(nameof(Debit)); }
    internal void SetCreditLocal(decimal value) { _data.Credit = value; OnPropertyChanged(nameof(Credit)); }
    internal void SetAmountLocal(decimal value) { _data.Amount = value; OnPropertyChanged(nameof(Amount)); }
    internal void SetTaxableValueLocal(decimal value) { _data.TaxableValue = value; OnPropertyChanged(nameof(TaxableValue)); }
    internal void SetGstRateLocal(decimal value) { _data.GstRate = value; _data.GstRateKnown = true; OnPropertyChanged(nameof(GstRate)); }
    internal void SetIgstLocal(decimal value) { _data.Igst = value; OnPropertyChanged(nameof(Igst)); }
    internal void SetCgstLocal(decimal value) { _data.Cgst = value; OnPropertyChanged(nameof(Cgst)); }
    internal void SetSgstLocal(decimal value) { _data.Sgst = value; OnPropertyChanged(nameof(Sgst)); }
    internal void SetCessLocal(decimal value) { _data.Cess = value; OnPropertyChanged(nameof(Cess)); }
    internal void SetTaxLedgersLocal(string value) { _data.TaxLedgers = value.Trim(); OnPropertyChanged(nameof(TaxLedgers)); }
    internal void SetCostCentreLocal(string value) { _data.CostCentre = value.Trim(); OnPropertyChanged(nameof(CostCentre)); }
    internal void SetNarrationLocal(string value) { _data.Details = value; _data.OriginalNarration = value; OnPropertyChanged(nameof(Narration)); }
    internal void SetNotesLocal(string value) { _data.AdvancedVoucher.Notes = value; OnPropertyChanged(nameof(Notes)); }
    internal void SetReverseChargeLocal(bool value) { _data.AdvancedVoucher.IsReverseCharge = value; _data.IsRcm = value; OnPropertyChanged(nameof(IsReverseCharge)); }
    internal void SetNilRatedLocal(bool value) { _data.AdvancedVoucher.IsNilRated = value; OnPropertyChanged(nameof(IsNilRated)); }
    internal void SetExemptLocal(bool value) { _data.AdvancedVoucher.IsExempt = value; OnPropertyChanged(nameof(IsExempt)); }
    internal void SetNonGstLocal(bool value) { _data.AdvancedVoucher.IsNonGst = value; OnPropertyChanged(nameof(IsNonGst)); }
    internal void SetTdsSectionLocal(string value) { _data.AdvancedVoucher.TdsSection = value.Trim(); OnPropertyChanged(nameof(TdsSection)); }
    internal void SetTdsRateLocal(decimal value) { _data.AdvancedVoucher.TdsRate = value; OnPropertyChanged(nameof(TdsRate)); }
    internal void SetTdsAmountLocal(decimal value) { _data.AdvancedVoucher.TdsAmount = value; OnPropertyChanged(nameof(TdsAmount)); }
    internal void SetTcsSectionLocal(string value) { _data.AdvancedVoucher.TcsSection = value.Trim(); OnPropertyChanged(nameof(TcsSection)); }
    internal void SetTcsRateLocal(decimal value) { _data.AdvancedVoucher.TcsRate = value; OnPropertyChanged(nameof(TcsRate)); }
    internal void SetTcsAmountLocal(decimal value) { _data.AdvancedVoucher.TcsAmount = value; OnPropertyChanged(nameof(TcsAmount)); }
    internal void SetItemNameLocal(string value) { _data.ItemName = value.Trim(); OnPropertyChanged(nameof(ItemName)); }
    internal void SetHsnCodeLocal(string value) { _data.HsnCode = value.Trim(); OnPropertyChanged(nameof(HsnCode)); }
    internal void SetUnitLocal(string value) { _data.Unit = value.Trim(); OnPropertyChanged(nameof(Unit)); }
    internal void SetAccountingAuthoritativeLocal(bool value) { _data.AdvancedVoucher.AccountingLinesAuthoritative = value; OnPropertyChanged(nameof(AccountingLinesAuthoritative)); }
    internal void SetInventoryAuthoritativeLocal(bool value) { _data.AdvancedVoucher.InventoryLinesAuthoritative = value; OnPropertyChanged(nameof(InventoryLinesAuthoritative)); }

    internal void AddLedgerLineLocal(AuditVoucherLedgerLineData data) { _data.AdvancedVoucher.LedgerLines.Add(data); LedgerLines.Add(new AuditVoucherLedgerLineDraft(_owner, this, data)); NotifyAccountingTotals(); }
    internal void RemoveLedgerLineLocal(string lineId) { var data = _data.AdvancedVoucher.LedgerLines.FirstOrDefault(x => x.LineId == lineId); if (data is not null) _data.AdvancedVoucher.LedgerLines.Remove(data); var vm = LedgerLines.FirstOrDefault(x => x.LineId == lineId); if (vm is not null) LedgerLines.Remove(vm); NotifyAccountingTotals(); }
    internal void AddInventoryLineLocal(AuditInventoryLineData data) { _data.AdvancedVoucher.InventoryLines.Add(data); InventoryLines.Add(new AuditInventoryLineDraft(_owner, this, data)); NotifyInventoryTotals(); }
    internal void RemoveInventoryLineLocal(string lineId) { var data = _data.AdvancedVoucher.InventoryLines.FirstOrDefault(x => x.LineId == lineId); if (data is not null) _data.AdvancedVoucher.InventoryLines.Remove(data); var vm = InventoryLines.FirstOrDefault(x => x.LineId == lineId); if (vm is not null) InventoryLines.Remove(vm); NotifyInventoryTotals(); }
    internal void AddBillAllocationLocal(AuditBillAllocationData data) { _data.AdvancedVoucher.BillAllocations.Add(data); BillAllocations.Add(new AuditBillAllocationDraft(_owner, this, data)); }
    internal void RemoveBillAllocationLocal(string id) { _data.AdvancedVoucher.BillAllocations.RemoveAll(x => x.LineId == id); var vm=BillAllocations.FirstOrDefault(x=>x.LineId==id); if(vm is not null) BillAllocations.Remove(vm); }
    internal void AddCostCentreAllocationLocal(AuditCostCentreAllocationData data) { _data.AdvancedVoucher.CostCentreAllocations.Add(data); CostCentreAllocations.Add(new AuditCostCentreAllocationDraft(_owner, this, data)); }
    internal void RemoveCostCentreAllocationLocal(string id) { _data.AdvancedVoucher.CostCentreAllocations.RemoveAll(x => x.LineId == id); var vm=CostCentreAllocations.FirstOrDefault(x=>x.LineId==id); if(vm is not null) CostCentreAllocations.Remove(vm); }
    internal void AddBankAllocationLocal(AuditBankAllocationData data) { _data.AdvancedVoucher.BankAllocations.Add(data); BankAllocations.Add(new AuditBankAllocationDraft(_owner, this, data)); }
    internal void RemoveBankAllocationLocal(string id) { _data.AdvancedVoucher.BankAllocations.RemoveAll(x => x.LineId == id); var vm=BankAllocations.FirstOrDefault(x=>x.LineId==id); if(vm is not null) BankAllocations.Remove(vm); }

    internal void NotifyAccountingTotals() { OnPropertyChanged(nameof(AccountingDebit)); OnPropertyChanged(nameof(AccountingCredit)); OnPropertyChanged(nameof(AccountingDifference)); }
    internal void NotifyInventoryTotals() { OnPropertyChanged(nameof(InventoryTaxableTotal)); OnPropertyChanged(nameof(InventoryGstTotal)); OnPropertyChanged(nameof(InventoryGrandTotal)); }

    internal void NotifyAll()
    {
        foreach (var name in new[] { nameof(Date),nameof(EffectiveDate),nameof(Reference),nameof(ReferenceDate),nameof(VoucherType),nameof(PartyOrLedger),nameof(CounterpartyGroup),nameof(BusinessLedger),nameof(BusinessGroup),nameof(PlaceOfSupply),nameof(Gstin),nameof(Pan),nameof(GstRegistrationType),nameof(TransactionNature),nameof(InvoiceType),nameof(Debit),nameof(Credit),nameof(Amount),nameof(TaxableValue),nameof(GstRate),nameof(Igst),nameof(Cgst),nameof(Sgst),nameof(Cess),nameof(TaxLedgers),nameof(CostCentre),nameof(Narration),nameof(Notes),nameof(IsReverseCharge),nameof(IsNilRated),nameof(IsExempt),nameof(IsNonGst),nameof(TdsSection),nameof(TdsRate),nameof(TdsAmount),nameof(TcsSection),nameof(TcsRate),nameof(TcsAmount),nameof(ItemName),nameof(HsnCode),nameof(Unit),nameof(AccountingLinesAuthoritative),nameof(InventoryLinesAuthoritative),nameof(AccountingDebit),nameof(AccountingCredit),nameof(AccountingDifference),nameof(InventoryTaxableTotal),nameof(InventoryGstTotal),nameof(InventoryGrandTotal),nameof(ValidationStatus),nameof(ValidationMessage),nameof(ChangeStatus),nameof(PreviousStateCount),nameof(VoucherNumberDisplay),nameof(Source),nameof(EvidenceStatus),nameof(TallyMasterId),nameof(TallyGuid) }) OnPropertyChanged(name);
        OnPropertyChanged(nameof(LedgerLines)); OnPropertyChanged(nameof(InventoryLines)); OnPropertyChanged(nameof(BillAllocations)); OnPropertyChanged(nameof(CostCentreAllocations)); OnPropertyChanged(nameof(BankAllocations));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class AuditVoucherLedgerLineDraft : INotifyPropertyChanged
{
    private readonly AuditAdvancedVoucherWorkbenchModel _owner; private readonly AuditVoucherDraft _voucher; private readonly AuditVoucherLedgerLineData _data;
    internal AuditVoucherLedgerLineDraft(AuditAdvancedVoucherWorkbenchModel owner, AuditVoucherDraft voucher, AuditVoucherLedgerLineData data) { _owner=owner; _voucher=voucher; _data=data; }
    public string LineId => _data.LineId;
    public string Ledger { get=>_data.Ledger; set { value??=string.Empty; if(AuditAdvancedVoucherWorkbenchModel.MasterEquals(_data.Ledger,value))return; if(_owner.IsInternalChange) SetLedgerLocal(value); else {if(string.IsNullOrWhiteSpace(_data.Ledger))_owner.ApplyChildLocalChange(_voucher,$"Accounting line Ledger: (blank) -> {value}",_data.Ledger,value,SetLedgerLocal);else _owner.ApplyMasterChangeFromChild(TypeForRole(),_data.Ledger,value,_voucher,_data.Group);_voucher.SetAccountingAuthoritativeLocal(true);} } }
    public string Group { get=>_data.Group; set { value??=string.Empty; if(AuditAdvancedVoucherWorkbenchModel.MasterEquals(_data.Group,value))return; if(_owner.IsInternalChange)SetGroupLocal(value); else {if(string.IsNullOrWhiteSpace(_data.Group))_owner.ApplyChildLocalChange(_voucher,$"Accounting line Group: (blank) -> {value}",_data.Group,value,SetGroupLocal);else _owner.ApplyMasterChangeFromChild("Group",_data.Group,value,_voucher);_voucher.SetAccountingAuthoritativeLocal(true);} } }
    public string Direction { get=>_data.Direction; set=>SetLocal(nameof(Direction),_data.Direction,value??"Dr",x=>{_data.Direction=x;OnPropertyChanged(nameof(Direction));_voucher.NotifyAccountingTotals();}); }
    public decimal Amount { get=>_data.Amount; set=>SetLocal(nameof(Amount),_data.Amount,value,x=>{_data.Amount=x;OnPropertyChanged(nameof(Amount));_voucher.NotifyAccountingTotals();}); }
    public string GstTreatment { get=>_data.GstTreatment; set=>SetLocal(nameof(GstTreatment),_data.GstTreatment,value??string.Empty,x=>{_data.GstTreatment=x;OnPropertyChanged(nameof(GstTreatment));}); }
    public string CostCentre { get=>_data.CostCentre; set { value??=string.Empty; if(AuditAdvancedVoucherWorkbenchModel.MasterEquals(_data.CostCentre,value))return; if(_owner.IsInternalChange)SetCostCentreLocal(value); else {if(string.IsNullOrWhiteSpace(_data.CostCentre))_owner.ApplyChildLocalChange(_voucher,$"Accounting line Cost Centre: (blank) -> {value}",_data.CostCentre,value,SetCostCentreLocal);else _owner.ApplyMasterChangeFromChild("Cost Centre",_data.CostCentre,value,_voucher);_voucher.SetAccountingAuthoritativeLocal(true);} } }
    public string Narration { get=>_data.Narration; set=>SetLocal(nameof(Narration),_data.Narration,value??string.Empty,x=>{_data.Narration=x;OnPropertyChanged(nameof(Narration));}); }
    public string Role { get=>_data.Role; set=>SetLocal(nameof(Role),_data.Role,value??"Other",x=>{_data.Role=x;OnPropertyChanged(nameof(Role));}); }
    public string TallyMasterId => _data.TallyMasterId;
    internal AuditVoucherLedgerLineData ExportData()=>JsonSerializer.Deserialize<AuditVoucherLedgerLineData>(JsonSerializer.Serialize(_data))??new AuditVoucherLedgerLineData();
    internal void SetLedgerLocal(string value){_data.Ledger=value.Trim();OnPropertyChanged(nameof(Ledger));}
    internal void SetGroupLocal(string value){_data.Group=value.Trim();OnPropertyChanged(nameof(Group));}
    internal void SetCostCentreLocal(string value){_data.CostCentre=value.Trim();OnPropertyChanged(nameof(CostCentre));}
    private string TypeForRole()=>Role.Contains("Party",StringComparison.OrdinalIgnoreCase)?"Party":Role.Contains("GST",StringComparison.OrdinalIgnoreCase)||Role.Contains("Tax",StringComparison.OrdinalIgnoreCase)?"GST Ledger":"Ledger";
    private void SetLocal<T>(string name,T oldValue,T newValue,Action<T> set){if(EqualityComparer<T>.Default.Equals(oldValue,newValue))return;if(_owner.IsInternalChange){set(newValue);return;}_owner.ApplyChildLocalChange(_voucher,$"Accounting line {name}: {oldValue} -> {newValue}",oldValue,newValue,set);_voucher.SetAccountingAuthoritativeLocal(true);}
    public event PropertyChangedEventHandler? PropertyChanged; private void OnPropertyChanged([CallerMemberName]string?name=null)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
}

public sealed class AuditInventoryLineDraft : INotifyPropertyChanged
{
    private readonly AuditAdvancedVoucherWorkbenchModel _owner; private readonly AuditVoucherDraft _voucher; private readonly AuditInventoryLineData _data;
    internal AuditInventoryLineDraft(AuditAdvancedVoucherWorkbenchModel owner,AuditVoucherDraft voucher,AuditInventoryLineData data){_owner=owner;_voucher=voucher;_data=data;}
    public string LineId=>_data.LineId;
    public string StockItem{get=>_data.StockItem;set{value??=string.Empty;if(AuditAdvancedVoucherWorkbenchModel.MasterEquals(_data.StockItem,value))return;if(_owner.IsInternalChange)SetStockItemLocal(value);else {if(string.IsNullOrWhiteSpace(_data.StockItem))_owner.ApplyChildLocalChange(_voucher,$"Inventory Stock Item: (blank) -> {value}",_data.StockItem,value,SetStockItemLocal);else _owner.ApplyMasterChangeFromChild("Stock Item",_data.StockItem,value,_voucher,_data.Group);_voucher.SetInventoryAuthoritativeLocal(true);}}}
    public string Group{get=>_data.Group;set{value??=string.Empty;if(AuditAdvancedVoucherWorkbenchModel.MasterEquals(_data.Group,value))return;if(_owner.IsInternalChange)SetGroupLocal(value);else {if(string.IsNullOrWhiteSpace(_data.Group))_owner.ApplyChildLocalChange(_voucher,$"Inventory Group: (blank) -> {value}",_data.Group,value,SetGroupLocal);else _owner.ApplyMasterChangeFromChild("Group",_data.Group,value,_voucher);_voucher.SetInventoryAuthoritativeLocal(true);}}}
    public string Description{get=>_data.Description;set=>SetLocal(nameof(Description),_data.Description,value??string.Empty,x=>{_data.Description=x;OnPropertyChanged(nameof(Description));});}
    public string HsnSac{get=>_data.HsnSac;set{value??=string.Empty;if(AuditAdvancedVoucherWorkbenchModel.MasterEquals(_data.HsnSac,value))return;if(_owner.IsInternalChange)SetHsnSacLocal(value);else {if(string.IsNullOrWhiteSpace(_data.HsnSac))_owner.ApplyChildLocalChange(_voucher,$"Inventory HSN/SAC: (blank) -> {value}",_data.HsnSac,value,SetHsnSacLocal);else _owner.ApplyMasterChangeFromChild("HSN/SAC",_data.HsnSac,value,_voucher);_voucher.SetInventoryAuthoritativeLocal(true);}}}
    public decimal Quantity{get=>_data.Quantity;set=>SetLocal(nameof(Quantity),_data.Quantity,value,x=>{_data.Quantity=x;OnPropertyChanged(nameof(Quantity));Recalc();});}
    public string Unit{get=>_data.Unit;set{value??=string.Empty;if(AuditAdvancedVoucherWorkbenchModel.MasterEquals(_data.Unit,value))return;if(_owner.IsInternalChange)SetUnitLocal(value);else {if(string.IsNullOrWhiteSpace(_data.Unit))_owner.ApplyChildLocalChange(_voucher,$"Inventory Unit: (blank) -> {value}",_data.Unit,value,SetUnitLocal);else _owner.ApplyMasterChangeFromChild("Unit",_data.Unit,value,_voucher);_voucher.SetInventoryAuthoritativeLocal(true);}}}
    public decimal Rate{get=>_data.Rate;set=>SetLocal(nameof(Rate),_data.Rate,value,x=>{_data.Rate=x;OnPropertyChanged(nameof(Rate));Recalc();});}
    public decimal DiscountPercent{get=>_data.DiscountPercent;set=>SetLocal(nameof(DiscountPercent),_data.DiscountPercent,value,x=>{_data.DiscountPercent=x;OnPropertyChanged(nameof(DiscountPercent));});}
    public decimal DiscountAmount{get=>_data.DiscountAmount;set=>SetLocal(nameof(DiscountAmount),_data.DiscountAmount,value,x=>{_data.DiscountAmount=x;OnPropertyChanged(nameof(DiscountAmount));});}
    public decimal TaxableValue{get=>_data.TaxableValue;set=>SetLocal(nameof(TaxableValue),_data.TaxableValue,value,x=>{_data.TaxableValue=x;OnPropertyChanged(nameof(TaxableValue));_voucher.NotifyInventoryTotals();});}
    public decimal GstRate{get=>_data.GstRate;set=>SetLocal(nameof(GstRate),_data.GstRate,value,x=>{_data.GstRate=x;OnPropertyChanged(nameof(GstRate));});}
    public decimal Igst{get=>_data.Igst;set=>SetLocal(nameof(Igst),_data.Igst,value,x=>{_data.Igst=x;OnPropertyChanged(nameof(Igst));_voucher.NotifyInventoryTotals();});}
    public decimal Cgst{get=>_data.Cgst;set=>SetLocal(nameof(Cgst),_data.Cgst,value,x=>{_data.Cgst=x;OnPropertyChanged(nameof(Cgst));_voucher.NotifyInventoryTotals();});}
    public decimal Sgst{get=>_data.Sgst;set=>SetLocal(nameof(Sgst),_data.Sgst,value,x=>{_data.Sgst=x;OnPropertyChanged(nameof(Sgst));_voucher.NotifyInventoryTotals();});}
    public decimal Cess{get=>_data.Cess;set=>SetLocal(nameof(Cess),_data.Cess,value,x=>{_data.Cess=x;OnPropertyChanged(nameof(Cess));_voucher.NotifyInventoryTotals();});}
    public decimal LineTotal{get=>_data.LineTotal;set=>SetLocal(nameof(LineTotal),_data.LineTotal,value,x=>{_data.LineTotal=x;OnPropertyChanged(nameof(LineTotal));_voucher.NotifyInventoryTotals();});}
    internal void SetStockItemLocal(string v){_data.StockItem=v.Trim();OnPropertyChanged(nameof(StockItem));}
    internal void SetGroupLocal(string v){_data.Group=v.Trim();OnPropertyChanged(nameof(Group));}
    internal void SetHsnSacLocal(string v){_data.HsnSac=v.Trim();OnPropertyChanged(nameof(HsnSac));}
    internal void SetUnitLocal(string v){_data.Unit=v.Trim();OnPropertyChanged(nameof(Unit));}
    private void Recalc(){if(_data.TaxableValue==0m&&_data.Quantity!=0m&&_data.Rate!=0m){_data.TaxableValue=_data.Quantity*_data.Rate-_data.DiscountAmount;OnPropertyChanged(nameof(TaxableValue));}if(_data.LineTotal==0m&&_data.TaxableValue!=0m){_data.LineTotal=_data.TaxableValue+_data.Igst+_data.Cgst+_data.Sgst+_data.Cess;OnPropertyChanged(nameof(LineTotal));}_voucher.NotifyInventoryTotals();}
    private void SetLocal<T>(string name,T oldValue,T newValue,Action<T>set){if(EqualityComparer<T>.Default.Equals(oldValue,newValue))return;if(_owner.IsInternalChange){set(newValue);return;}_owner.ApplyChildLocalChange(_voucher,$"Inventory line {name}: {oldValue} -> {newValue}",oldValue,newValue,set);_voucher.SetInventoryAuthoritativeLocal(true);}
    public event PropertyChangedEventHandler? PropertyChanged; private void OnPropertyChanged([CallerMemberName]string?name=null)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
}

public sealed class AuditBillAllocationDraft : INotifyPropertyChanged
{
    private readonly AuditAdvancedVoucherWorkbenchModel _owner; private readonly AuditVoucherDraft _voucher; private readonly AuditBillAllocationData _data;
    internal AuditBillAllocationDraft(AuditAdvancedVoucherWorkbenchModel owner,AuditVoucherDraft voucher,AuditBillAllocationData data){_owner=owner;_voucher=voucher;_data=data;}
    public string LineId=>_data.LineId; public string Ledger{get=>_data.Ledger;set{value??=string.Empty;if(AuditAdvancedVoucherWorkbenchModel.MasterEquals(_data.Ledger,value))return;if(_owner.IsInternalChange)SetLedgerLocal(value);else if(string.IsNullOrWhiteSpace(_data.Ledger))_owner.ApplyChildLocalChange(_voucher,$"Bill allocation Ledger: (blank) -> {value}",_data.Ledger,value,SetLedgerLocal);else _owner.ApplyMasterChangeFromChild("Ledger",_data.Ledger,value,_voucher);}} public string ReferenceType{get=>_data.ReferenceType;set=>Set(nameof(ReferenceType),_data.ReferenceType,value??string.Empty,x=>{_data.ReferenceType=x;OnPropertyChanged(nameof(ReferenceType));});} public string ReferenceNumber{get=>_data.ReferenceNumber;set=>Set(nameof(ReferenceNumber),_data.ReferenceNumber,value??string.Empty,x=>{_data.ReferenceNumber=x;OnPropertyChanged(nameof(ReferenceNumber));});} public DateTime? DueDate{get=>_data.DueDate;set=>Set(nameof(DueDate),_data.DueDate,value,x=>{_data.DueDate=x;OnPropertyChanged(nameof(DueDate));});} public decimal Amount{get=>_data.Amount;set=>Set(nameof(Amount),_data.Amount,value,x=>{_data.Amount=x;OnPropertyChanged(nameof(Amount));});}
    internal void SetLedgerLocal(string v){_data.Ledger=v.Trim();OnPropertyChanged(nameof(Ledger));}
    private void Set<T>(string n,T o,T v,Action<T>s){if(EqualityComparer<T>.Default.Equals(o,v))return;if(_owner.IsInternalChange){s(v);return;}_owner.ApplyChildLocalChange(_voucher,$"Bill allocation {n}: {o} -> {v}",o,v,s);} public event PropertyChangedEventHandler? PropertyChanged;private void OnPropertyChanged([CallerMemberName]string?name=null)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
}

public sealed class AuditCostCentreAllocationDraft : INotifyPropertyChanged
{
    private readonly AuditAdvancedVoucherWorkbenchModel _owner; private readonly AuditVoucherDraft _voucher; private readonly AuditCostCentreAllocationData _data;
    internal AuditCostCentreAllocationDraft(AuditAdvancedVoucherWorkbenchModel owner,AuditVoucherDraft voucher,AuditCostCentreAllocationData data){_owner=owner;_voucher=voucher;_data=data;}
    public string LineId=>_data.LineId; public string Ledger{get=>_data.Ledger;set{value??=string.Empty;if(AuditAdvancedVoucherWorkbenchModel.MasterEquals(_data.Ledger,value))return;if(_owner.IsInternalChange)SetLedgerLocal(value);else if(string.IsNullOrWhiteSpace(_data.Ledger))_owner.ApplyChildLocalChange(_voucher,$"Cost-centre allocation Ledger: (blank) -> {value}",_data.Ledger,value,SetLedgerLocal);else _owner.ApplyMasterChangeFromChild("Ledger",_data.Ledger,value,_voucher);}} public string CostCentre{get=>_data.CostCentre;set{value??=string.Empty;if(AuditAdvancedVoucherWorkbenchModel.MasterEquals(_data.CostCentre,value))return;if(_owner.IsInternalChange)SetCostCentreLocal(value);else if(string.IsNullOrWhiteSpace(_data.CostCentre))_owner.ApplyChildLocalChange(_voucher,$"Cost-centre allocation Cost Centre: (blank) -> {value}",_data.CostCentre,value,SetCostCentreLocal);else _owner.ApplyMasterChangeFromChild("Cost Centre",_data.CostCentre,value,_voucher);}} public string Direction{get=>_data.Direction;set=>Set(nameof(Direction),_data.Direction,value??"Dr",x=>{_data.Direction=x;OnPropertyChanged(nameof(Direction));});} public decimal Amount{get=>_data.Amount;set=>Set(nameof(Amount),_data.Amount,value,x=>{_data.Amount=x;OnPropertyChanged(nameof(Amount));});}
    internal void SetLedgerLocal(string v){_data.Ledger=v.Trim();OnPropertyChanged(nameof(Ledger));} internal void SetCostCentreLocal(string v){_data.CostCentre=v.Trim();OnPropertyChanged(nameof(CostCentre));}
    private void Set<T>(string n,T o,T v,Action<T>s){if(EqualityComparer<T>.Default.Equals(o,v))return;if(_owner.IsInternalChange){s(v);return;}_owner.ApplyChildLocalChange(_voucher,$"Cost-centre allocation {n}: {o} -> {v}",o,v,s);} public event PropertyChangedEventHandler? PropertyChanged;private void OnPropertyChanged([CallerMemberName]string?name=null)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
}

public sealed class AuditBankAllocationDraft : INotifyPropertyChanged
{
    private readonly AuditAdvancedVoucherWorkbenchModel _owner; private readonly AuditVoucherDraft _voucher; private readonly AuditBankAllocationData _data;
    internal AuditBankAllocationDraft(AuditAdvancedVoucherWorkbenchModel owner,AuditVoucherDraft voucher,AuditBankAllocationData data){_owner=owner;_voucher=voucher;_data=data;}
    public string LineId=>_data.LineId; public string Ledger{get=>_data.Ledger;set{value??=string.Empty;if(AuditAdvancedVoucherWorkbenchModel.MasterEquals(_data.Ledger,value))return;if(_owner.IsInternalChange)SetLedgerLocal(value);else if(string.IsNullOrWhiteSpace(_data.Ledger))_owner.ApplyChildLocalChange(_voucher,$"Bank allocation Ledger: (blank) -> {value}",_data.Ledger,value,SetLedgerLocal);else _owner.ApplyMasterChangeFromChild("Ledger",_data.Ledger,value,_voucher);}} public string TransactionType{get=>_data.TransactionType;set=>Set(nameof(TransactionType),_data.TransactionType,value??string.Empty,x=>{_data.TransactionType=x;OnPropertyChanged(nameof(TransactionType));});} public string InstrumentNumber{get=>_data.InstrumentNumber;set=>Set(nameof(InstrumentNumber),_data.InstrumentNumber,value??string.Empty,x=>{_data.InstrumentNumber=x;OnPropertyChanged(nameof(InstrumentNumber));});} public string Utr{get=>_data.Utr;set=>Set(nameof(Utr),_data.Utr,value??string.Empty,x=>{_data.Utr=x;OnPropertyChanged(nameof(Utr));});} public DateTime? BankDate{get=>_data.BankDate;set=>Set(nameof(BankDate),_data.BankDate,value,x=>{_data.BankDate=x;OnPropertyChanged(nameof(BankDate));});} public decimal Amount{get=>_data.Amount;set=>Set(nameof(Amount),_data.Amount,value,x=>{_data.Amount=x;OnPropertyChanged(nameof(Amount));});}
    internal void SetLedgerLocal(string v){_data.Ledger=v.Trim();OnPropertyChanged(nameof(Ledger));}
    private void Set<T>(string n,T o,T v,Action<T>s){if(EqualityComparer<T>.Default.Equals(o,v))return;if(_owner.IsInternalChange){s(v);return;}_owner.ApplyChildLocalChange(_voucher,$"Bank allocation {n}: {o} -> {v}",o,v,s);} public event PropertyChangedEventHandler? PropertyChanged;private void OnPropertyChanged([CallerMemberName]string?name=null)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
}

public sealed class AuditMasterUsageRow
{
    public string Type { get; set; } = string.Empty;
    public string ExistingMaster { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public int VoucherCount { get; set; }
    public int LineCount { get; set; }
    public string VoucherNumbers { get; set; } = string.Empty;
    public string ReplacementMaster { get; set; } = string.Empty;
}

public sealed class AuditChangeHistoryItem
{
    public DateTime WhenLocal { get; set; }
    public string Description { get; set; } = string.Empty;
    public int AffectedCount { get; set; }
    public string Scope { get; set; } = string.Empty;
}

public sealed class AuditVoucherPreviousState
{
    public DateTime WhenLocal { get; set; }
    public string Description { get; set; } = string.Empty;
    public string DataJson { get; set; } = string.Empty;
    public AuditVoucherPreviousState Clone() => new() { WhenLocal = WhenLocal, Description = Description, DataJson = DataJson };
}

public sealed class AuditWorkbenchSessionState
{
    public int CurrentIndex { get; set; }
    public bool ChangeOnlyThisEntry { get; set; }
    public int CommonChangeActions { get; set; }
    public List<AuditVoucherSnapshotItem> Vouchers { get; set; } = [];
}

public sealed class AuditVoucherSnapshotItem
{
    public string SessionId { get; set; } = string.Empty;
    public int OriginalSlot { get; set; }
    public bool IsNew { get; set; }
    public bool IsModified { get; set; }
    public bool IndividualOverride { get; set; }
    public long OriginalSourceRecordId { get; set; }
    public string OriginalSourceIdentity { get; set; } = string.Empty;
    public string OriginalRowJson { get; set; } = string.Empty;
    public string ValidationStatus { get; set; } = string.Empty;
    public string ValidationMessage { get; set; } = string.Empty;
    public AuditRecordRow? Data { get; set; }
    public List<AuditVoucherPreviousState> PreviousStates { get; set; } = [];
}

public sealed class AuditWorkbenchStateChange
{
    public AuditWorkbenchStateChange(AuditWorkbenchSessionState state, string description) { State = state; Description = description; }
    public AuditWorkbenchSessionState State { get; }
    public string Description { get; }
}

public sealed record AuditWorkbenchSaveValidation(bool CanSave, IReadOnlyList<AuditVoucherDraft> InvalidVouchers);
