using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Domain.Audit;
using AccountingAutomationHub.Infrastructure.Audit;

namespace AccountingAutomationHub.Desktop.Views;

public partial class AuditAdvancedVoucherWorkbenchWindow : Window
{
    private readonly SqliteAuditRepository _repository;
    private readonly AuditClientProfile _client;
    private readonly string _auditModuleKey;
    private readonly string _storageModule;
    private readonly Dictionary<int, AuditRecordRow> _savedRowsBySlot = new();
    private readonly DispatcherTimer _voucherPreviewCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private bool _voucherPreviewPinned;

    public AuditAdvancedVoucherWorkbenchWindow(
        SqliteAuditRepository repository,
        AuditClientProfile client,
        string auditModuleKey,
        string displayModule,
        string filterContext,
        IReadOnlyCollection<AuditRecordRow> rows,
        IReadOnlyList<string> ledgers,
        bool treatRowsAsNew = false)
    {
        InitializeComponent();
        _repository = repository;
        _client = client;
        _auditModuleKey = auditModuleKey;
        _storageModule = StorageModuleFor(auditModuleKey);
        Model = new AuditAdvancedVoucherWorkbenchModel(displayModule, filterContext, rows, ledgers, treatRowsAsNew);
        Model.CurrentVoucherChanged += (_, _) => RefreshSavedRevisions();
        DataContext = Model;
        _voucherPreviewCloseTimer.Tick += (_, _) =>
        {
            _voucherPreviewCloseTimer.Stop();
            if (!_voucherPreviewPinned && !VoucherPreviewLayer.IsMouseOver && !VoucherPreviewButton.IsMouseOver)
                HideVoucherPreview();
        };
        Deactivated += (_, _) => HideVoucherPreview();
        Loaded += (_, _) => RefreshSavedRevisions();
    }

    public AuditAdvancedVoucherWorkbenchModel Model { get; }
    public bool HasSavedChanges { get; private set; }
    public IReadOnlyDictionary<int, AuditRecordRow> SavedRowsBySlot => _savedRowsBySlot;

    private void Previous_Click(object sender, RoutedEventArgs e) { Model.GoPrevious(); RefreshSavedRevisions(); }
    private void Next_Click(object sender, RoutedEventArgs e) { Model.GoNext(); RefreshSavedRevisions(); }
    private void Undo_Click(object sender, RoutedEventArgs e) { Model.Undo(); RefreshSavedRevisions(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { Model.Redo(); RefreshSavedRevisions(); }
    private void UndoCurrent_Click(object sender, RoutedEventArgs e) { Model.UndoCurrentVoucher(); RefreshSavedRevisions(); }

    private void MasterChanges_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AuditMasterChangesWindow(Model) { Owner = this };
        dialog.ShowDialog();
        RefreshSavedRevisions();
    }

    private void ViewAll_Click(object sender, RoutedEventArgs e)
    {
        _voucherPreviewCloseTimer.Stop();
        if (VoucherPreviewLayer.Visibility == Visibility.Visible && _voucherPreviewPinned)
        {
            HideVoucherPreview();
            return;
        }

        _voucherPreviewPinned = true;
        ShowVoucherPreview();
    }

    private void VoucherPreviewButton_MouseEnter(object sender, MouseEventArgs e)
    {
        _voucherPreviewCloseTimer.Stop();
        if (_voucherPreviewPinned) return;
        ShowVoucherPreview();
    }

    private void VoucherPreviewButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_voucherPreviewPinned) _voucherPreviewCloseTimer.Start();
    }

    private void VoucherPreviewHost_MouseEnter(object sender, MouseEventArgs e) => _voucherPreviewCloseTimer.Stop();

    private void VoucherPreviewHost_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_voucherPreviewPinned) _voucherPreviewCloseTimer.Start();
    }

    private void ShowVoucherPreview()
    {
        VoucherPreviewLayer.Visibility = Visibility.Visible;
    }

    private void HideVoucherPreview()
    {
        _voucherPreviewCloseTimer.Stop();
        _voucherPreviewPinned = false;
        VoucherPreviewLayer.Visibility = Visibility.Collapsed;
    }

    private void CloseVoucherPreview_Click(object sender, RoutedEventArgs e) => HideVoucherPreview();

    private void OpenPreviewVoucher_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is string sessionId && Model.SelectVoucherBySessionId(sessionId))
        {
            HideVoucherPreview();
            RefreshSavedRevisions();
            EditorTabs.Focus();
        }
    }

    private void AddLedgerLine_Click(object sender, RoutedEventArgs e) => Model.AddLedgerLine(Model.CurrentVoucher);
    private void DeleteLedgerLine_Click(object sender, RoutedEventArgs e) => Model.DeleteLedgerLine(Model.CurrentVoucher, LedgerLinesGrid.SelectedItem as AuditVoucherLedgerLineDraft);
    private void DuplicateLedgerLine_Click(object sender, RoutedEventArgs e) => Model.DuplicateLedgerLine(Model.CurrentVoucher, LedgerLinesGrid.SelectedItem as AuditVoucherLedgerLineDraft);
    private void AddInventoryLine_Click(object sender, RoutedEventArgs e) => Model.AddInventoryLine(Model.CurrentVoucher);
    private void DeleteInventoryLine_Click(object sender, RoutedEventArgs e) => Model.DeleteInventoryLine(Model.CurrentVoucher, InventoryGrid.SelectedItem as AuditInventoryLineDraft);
    private void AddBill_Click(object sender, RoutedEventArgs e) => Model.AddBillAllocation(Model.CurrentVoucher);
    private void DeleteBill_Click(object sender, RoutedEventArgs e) => Model.DeleteBillAllocation(Model.CurrentVoucher, BillGrid.SelectedItem as AuditBillAllocationDraft);
    private void AddCostCentre_Click(object sender, RoutedEventArgs e) => Model.AddCostCentreAllocation(Model.CurrentVoucher);
    private void DeleteCostCentre_Click(object sender, RoutedEventArgs e) => Model.DeleteCostCentreAllocation(Model.CurrentVoucher, CostCentreGrid.SelectedItem as AuditCostCentreAllocationDraft);
    private void AddBank_Click(object sender, RoutedEventArgs e) => Model.AddBankAllocation(Model.CurrentVoucher);
    private void DeleteBank_Click(object sender, RoutedEventArgs e) => Model.DeleteBankAllocation(Model.CurrentVoucher, BankGrid.SelectedItem as AuditBankAllocationDraft);

    private void RestoreRevision_Click(object sender, RoutedEventArgs e)
    {
        var revision = Model.SelectedSavedRevision;
        if (revision is null)
        {
            MessageBox.Show(this, "Select a saved Audit revision first.", "Audit Revision", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(revision.OriginalJson))
        {
            MessageBox.Show(this, "This revision does not have an earlier saved state to restore.", "Audit Revision", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Model.RestoreCurrentFromSavedRevision(revision);
    }

    private void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveAllModified()) return;
        MessageBox.Show(this, "All modified Audit vouchers were saved. Every saved voucher has an individual durable revision containing its previous and new state.", "Advanced Audit Voucher Workbench", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private bool SaveAllModified()
    {
        var validation = Model.ValidateForSave();
        if (!validation.CanSave)
        {
            var first = validation.InvalidVouchers.First();
            Model.SelectVoucherBySessionId(first.SessionId);
            MessageBox.Show(this, $"Cannot save yet. {validation.InvalidVouchers.Count:N0} voucher(s) have blocking validation errors.\n\n{first.VoucherNumberDisplay}: {first.ValidationMessage}", "Audit Voucher Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var modified = Model.Vouchers.Where(v => v.IsModified || v.IsNew).ToArray();
        if (modified.Length == 0)
        {
            MessageBox.Show(this, "There are no unsaved voucher changes.", "Advanced Audit Voucher Workbench", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }

        try
        {
            var records = _repository.GetRecords(_client.Id, _client.FinancialYear, _storageModule);
            foreach (var voucher in modified)
            {
                var wasNew = voucher.IsNew;
                var previousJson = voucher.OriginalRowJson;
                var proposed = voucher.ToRow();
                var existing = proposed.SourceRecordId > 0 ? records.FirstOrDefault(record => record.Id == proposed.SourceRecordId) : null;
                var originKind = string.Empty;
                var originKey = string.Empty;
                long id = 0;

                if (wasNew || (existing is null && proposed.Source.Equals("Audit Accounting Workbench", StringComparison.OrdinalIgnoreCase)))
                {
                    originKind = "AccountingWorkbenchCreate";
                    originKey = $"WORKBENCH:{voucher.SessionId}";
                    proposed.Source = "Audit Accounting Workbench";
                    proposed.Status = "Audit working entry";
                    proposed.EvidenceStatus = "Audit accounting workbench";
                    if (string.IsNullOrWhiteSpace(proposed.AuthorityId)) proposed.AuthorityId = $"AUDIT-WORK-{Guid.NewGuid():N}";
                }
                else if (existing is not null && existing.OriginKind.Equals("AccountingWorkbenchCreate", StringComparison.OrdinalIgnoreCase))
                {
                    originKind = existing.OriginKind;
                    originKey = existing.OriginKey;
                    id = existing.Id;
                    proposed.EvidenceStatus = "Audit accounting workbench";
                }
                else if (existing is not null && existing.OriginKind.Equals("AccountingWorkbenchOverride", StringComparison.OrdinalIgnoreCase))
                {
                    originKind = existing.OriginKind;
                    originKey = existing.OriginKey;
                    id = existing.Id;
                    proposed.EvidenceStatus = "Audit accounting workbench override";
                }
                else
                {
                    originKind = "AccountingWorkbenchOverride";
                    originKey = existing is not null && !string.IsNullOrWhiteSpace(existing.OriginKey)
                        ? existing.OriginKey.Trim()
                        : voucher.OriginalSourceIdentity;
                    if (originKey.Length == 0) originKey = AuditWorkspaceViewModel.AccountingWorkbenchSourceIdentity(proposed);
                    if (originKey.Length == 0) throw new InvalidOperationException($"Voucher {voucher.VoucherNumberDisplay} has no stable Audit source identity and cannot be overridden safely.");
                    var prior = records.Where(record => record.OriginKind.Equals("AccountingWorkbenchOverride", StringComparison.OrdinalIgnoreCase) && record.OriginKey.Equals(originKey, StringComparison.OrdinalIgnoreCase)).OrderBy(record => record.Id).LastOrDefault();
                    id = prior?.Id ?? 0;
                    proposed.EvidenceStatus = "Audit accounting workbench override";
                }

                var stored = BuildStoredRecord(proposed, originKind, originKey, id);
                var saved = _repository.SaveRecord(stored);
                proposed.SourceRecordId = saved.Id;

                var revisionIdentity = voucher.OriginalSourceIdentity.Length > 0 ? voucher.OriginalSourceIdentity : originKey;
                _repository.SaveEntryAdjustment(new AuditEntryAdjustment
                {
                    AuditClientId = _client.Id,
                    FinancialYear = _client.FinancialYear,
                    Module = _storageModule,
                    SourceRecordId = saved.Id,
                    Source = proposed.Source,
                    SourceIdentity = revisionIdentity,
                    TallyMasterId = proposed.TallyMasterId,
                    TallyGuid = proposed.TallyGuid,
                    Reason = "Advanced Voucher Workbench",
                    Action = wasNew ? "Workbench Create" : "Workbench Revision",
                    Status = "Saved Audit",
                    OriginalJson = previousJson,
                    ProposedJson = JsonSerializer.Serialize(proposed)
                });

                Model.MarkSaved(voucher, proposed);
                _savedRowsBySlot[voucher.OriginalSlot] = Clone(proposed);
                HasSavedChanges = true;

                records = records.Where(record => record.Id != saved.Id).Concat(new[] { saved }).ToArray();
            }
            RefreshSavedRevisions();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Advanced Audit Voucher Workbench", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private AuditStoredRecord BuildStoredRecord(AuditRecordRow row, string originKind, string originKey, long id) => new()
    {
        Id = id,
        AuditClientId = _client.Id,
        FinancialYear = _client.FinancialYear,
        Module = _storageModule,
        RecordDate = row.Date,
        Reference = row.Reference,
        VoucherType = row.VoucherType,
        AuthorityId = row.AuthorityId,
        PartyOrLedger = row.PartyOrLedger,
        Details = row.OriginalNarration.Length > 0 ? row.OriginalNarration : row.Details,
        Debit = row.Debit,
        Credit = row.Credit,
        Amount = row.Amount,
        Status = row.Status,
        Source = row.Source,
        OriginKind = originKind,
        OriginKey = originKey,
        PayloadJson = JsonSerializer.Serialize(row)
    };

    private void RefreshSavedRevisions()
    {
        try
        {
            var voucher = Model.CurrentVoucher;
            var identity = voucher.OriginalSourceIdentity;
            var rows = _repository.GetEntryAdjustments(_client.Id, _client.FinancialYear, _storageModule)
                .Where(item => item.Action.StartsWith("Workbench", StringComparison.OrdinalIgnoreCase))
                .Where(item => identity.Length == 0 || item.SourceIdentity.Equals(identity, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Model.RefreshSavedRevisions(rows);
        }
        catch
        {
            Model.RefreshSavedRevisions(Array.Empty<AuditEntryAdjustment>());
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Vouchers.Any(v => v.IsModified || v.IsNew))
        {
            var result = MessageBox.Show(this, "There are unsaved Audit voucher changes.\n\nYes = Save All Modified and close\nNo = discard unsaved in-memory changes\nCancel = return to the workbench", "Close Advanced Voucher Workbench", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel) return;
            if (result == MessageBoxResult.Yes && !SaveAllModified()) return;
        }
        DialogResult = HasSavedChanges;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // The explicit Close button owns the save/discard prompt. Window chrome close is fail-safe:
        // prevent accidental loss and ask the same question once.
        if (!DialogResult.HasValue && Model.Vouchers.Any(v => v.IsModified || v.IsNew))
        {
            var result = MessageBox.Show(this, "Unsaved Audit voucher changes are still in memory. Close without saving?", "Advanced Voucher Workbench", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
        base.OnClosing(e);
    }

    private static string StorageModuleFor(string key) => key switch
    {
        "BankingFinance" or "Bank" or "OD" or "Loans" => "BankingFinance",
        _ => key
    };

    private static AuditRecordRow Clone(AuditRecordRow row) => JsonSerializer.Deserialize<AuditRecordRow>(JsonSerializer.Serialize(row)) ?? new AuditRecordRow();
}
