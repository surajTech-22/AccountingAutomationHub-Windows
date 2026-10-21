using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Domain.Audit;
using AccountingAutomationHub.Infrastructure.Audit;

namespace AccountingAutomationHub.Desktop.Views;

public partial class AuditAccountingWorkbenchWindow : Window, INotifyPropertyChanged
{
    private readonly SqliteAuditRepository _repository;
    private readonly AuditClientProfile _client;
    private readonly string _auditModuleKey;
    private readonly string _storageModule;
    private readonly string[] _ledgers;
    private readonly Action _changed;
    private AuditRecordRow? _selectedRow;
    private string _status = string.Empty;

    public AuditAccountingWorkbenchWindow(
        SqliteAuditRepository repository,
        AuditClientProfile client,
        string auditModuleKey,
        string displayModule,
        string filterContext,
        IReadOnlyCollection<AuditRecordRow> rows,
        IReadOnlyList<string> ledgers,
        Action changed)
    {
        InitializeComponent();
        _repository = repository;
        _client = client;
        _auditModuleKey = auditModuleKey;
        _storageModule = StorageModuleFor(auditModuleKey);
        _ledgers = ledgers?.ToArray() ?? Array.Empty<string>();
        _changed = changed;
        Header = $"{displayModule} · Accounting Module";
        Subtitle = $"{client.ClientName} · {client.ClientCode} · FY {client.FinancialYear} · independent Audit data";
        FilterContext = filterContext;
        Rows = new ObservableCollection<AuditRecordRow>(rows.OrderBy(row => row.Date ?? DateTime.MinValue).ThenBy(row => row.Reference, StringComparer.OrdinalIgnoreCase));
        Status = $"{Rows.Count:N0} Audit row(s) in the current module/filter. Ctrl/Shift-select vouchers, then choose Edit Selected Vouchers.";
        DataContext = this;
    }

    public string Header { get; }
    public string Subtitle { get; }
    public string FilterContext { get; }
    public ObservableCollection<AuditRecordRow> Rows { get; }

    public AuditRecordRow? SelectedRow
    {
        get => _selectedRow;
        set { if (!ReferenceEquals(_selectedRow, value)) { _selectedRow = value; OnPropertyChanged(); } }
    }

    public string Status
    {
        get => _status;
        private set { if (_status != value) { _status = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var blank = AuditAdvancedVoucherWorkbenchModel.CreateBlankRow(DisplayModuleFor(_auditModuleKey));
        var dialog = new AuditAdvancedVoucherWorkbenchWindow(
            _repository, _client.Clone(), _auditModuleKey, DisplayModuleFor(_auditModuleKey),
            $"{FilterContext} · New Audit voucher", new[] { blank }, _ledgers, treatRowsAsNew: true) { Owner = this };
        dialog.ShowDialog();
        if (!dialog.HasSavedChanges) return;
        if (dialog.SavedRowsBySlot.TryGetValue(0, out var saved))
        {
            Rows.Add(Clone(saved));
            SelectedRow = Rows[^1];
            RowsGrid.SelectedItem = SelectedRow;
        }
        Status = "Created Audit-owned voucher in the Advanced Voucher Workbench.";
        _changed();
    }

    private void Modify_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRows();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "Select one or more vouchers to edit.", "Advanced Audit Voucher Workbench", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new AuditAdvancedVoucherWorkbenchWindow(
            _repository, _client.Clone(), _auditModuleKey, DisplayModuleFor(_auditModuleKey),
            FilterContext, selected, _ledgers) { Owner = this };
        dialog.ShowDialog();
        if (!dialog.HasSavedChanges) return;

        foreach (var pair in dialog.SavedRowsBySlot)
        {
            if (pair.Key < 0 || pair.Key >= selected.Length) continue;
            var original = selected[pair.Key];
            var index = Rows.IndexOf(original);
            if (index >= 0) Rows[index] = Clone(pair.Value);
        }
        Status = $"Saved Advanced Voucher Workbench changes for {dialog.SavedRowsBySlot.Count:N0} voucher(s). Source evidence remains immutable.";
        _changed();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRows();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "Select one or more vouchers to delete/suppress.", "Delete Audit Voucher", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"Remove {selected.Length:N0} selected voucher(s) from this Audit workbench?\n\nOriginal imported/Tally/statement evidence will not be deleted.", "Delete Selected Audit Vouchers", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var deleted = 0;
        foreach (var row in selected)
        {
            if (DeleteOne(row))
            {
                Rows.Remove(row);
                deleted++;
            }
        }
        SelectedRow = null;
        Status = $"Removed {deleted:N0} Audit voucher(s). Source-backed rows were suppressed non-destructively.";
        if (deleted > 0) _changed();
    }

    private bool DeleteOne(AuditRecordRow selected)
    {
        try
        {
            var records = _repository.GetRecords(_client.Id, _client.FinancialYear, _storageModule);
            var existing = selected.SourceRecordId > 0 ? records.FirstOrDefault(record => record.Id == selected.SourceRecordId) : null;
            var identity = AuditWorkspaceViewModel.AccountingWorkbenchSourceIdentity(selected);

            if (existing is not null && existing.OriginKind.Equals("AccountingWorkbenchCreate", StringComparison.OrdinalIgnoreCase))
            {
                _repository.DeleteRecord(_client.Id, existing.Id);
                _repository.SaveEntryAdjustment(new AuditEntryAdjustment
                {
                    AuditClientId = _client.Id, FinancialYear = _client.FinancialYear, Module = _storageModule,
                    SourceRecordId = existing.Id, Source = selected.Source, SourceIdentity = identity,
                    TallyMasterId = selected.TallyMasterId, TallyGuid = selected.TallyGuid,
                    Reason = "Advanced Voucher Workbench", Action = "Workbench Delete", Status = "Deleted Audit",
                    OriginalJson = JsonSerializer.Serialize(selected), ProposedJson = string.Empty
                });
                return true;
            }

            var key = existing is not null && !string.IsNullOrWhiteSpace(existing.OriginKey)
                ? existing.OriginKey.Trim()
                : identity;
            if (key.Length == 0)
            {
                MessageBox.Show(this, $"Voucher {selected.Reference} has no stable Audit identity and cannot be suppressed safely.", "Delete Audit Voucher", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var tombstone = BuildStoredRecord(selected, "AccountingWorkbenchDelete", key, 0);
            tombstone.Status = "Deleted in Audit accounting workbench";
            tombstone.PayloadJson = JsonSerializer.Serialize(selected);
            var saved = _repository.SaveRecord(tombstone);
            _repository.SaveEntryAdjustment(new AuditEntryAdjustment
            {
                AuditClientId = _client.Id, FinancialYear = _client.FinancialYear, Module = _storageModule,
                SourceRecordId = saved.Id, Source = selected.Source, SourceIdentity = identity.Length > 0 ? identity : key,
                TallyMasterId = selected.TallyMasterId, TallyGuid = selected.TallyGuid,
                Reason = "Advanced Voucher Workbench", Action = "Workbench Delete", Status = "Suppressed Audit",
                OriginalJson = JsonSerializer.Serialize(selected), ProposedJson = JsonSerializer.Serialize(new { Deleted = true, OriginKey = key })
            });
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Delete Audit Voucher", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private AuditRecordRow[] SelectedRows()
    {
        var rows = RowsGrid.SelectedItems.Cast<AuditRecordRow>().ToArray();
        if (rows.Length == 0 && SelectedRow is not null) rows = new[] { SelectedRow };
        return rows;
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

    private static AuditRecordRow Clone(AuditRecordRow row) => JsonSerializer.Deserialize<AuditRecordRow>(JsonSerializer.Serialize(row)) ?? new AuditRecordRow();

    private static string StorageModuleFor(string key) => key switch
    {
        "BankingFinance" or "Bank" or "OD" or "Loans" => "BankingFinance",
        _ => key
    };

    private static string DisplayModuleFor(string key) => key switch
    {
        "BankingFinance" or "Bank" or "OD" or "Loans" => "Banking",
        "DebtorsCreditors" => "Debtors & Creditors",
        _ => key
    };
}
