using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Views;

public partial class AuditAccountingEntryWindow : Window
{
    public AuditAccountingEntryWindow(string module, AuditRecordRow? source, IReadOnlyList<string> ledgers)
    {
        InitializeComponent();
        Model = new AuditAccountingEntryModel(module, source, ledgers);
        DataContext = Model;
    }

    public AuditAccountingEntryModel Model { get; }
    public AuditEntryEditDraft Draft => Model.Draft;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Draft.Date.HasValue)
        {
            MessageBox.Show(this, "Select the entry date.", "Accounting Entry", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(Draft.PartyOrLedger))
        {
            MessageBox.Show(this, "Select or enter the Party / Counterparty.", "Accounting Entry", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if ((Model.Module is "Sales" or "Purchase" or "Banking" or "Cash" or "Journal") && string.IsNullOrWhiteSpace(Draft.BusinessLedger))
        {
            MessageBox.Show(this, "Select or enter the Posting / Business Ledger.", "Accounting Entry", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (Draft.Amount == 0m && Draft.Debit == 0m && Draft.Credit == 0m && Draft.TaxableValue == 0m)
        {
            MessageBox.Show(this, "Enter an amount, debit/credit or taxable value.", "Accounting Entry", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}

public sealed class AuditAccountingEntryModel
{
    public AuditAccountingEntryModel(string module, AuditRecordRow? source, IReadOnlyList<string> ledgers)
    {
        Module = module;
        Source = source;
        Draft = source is null ? NewDraft(module) : AuditEntryEditDraft.From(source);
        Ledgers = ledgers ?? Array.Empty<string>();
        VoucherTypes = VoucherTypesFor(module, source?.VoucherType);
    }

    public string Module { get; }
    public AuditRecordRow? Source { get; }
    public AuditEntryEditDraft Draft { get; }
    public IReadOnlyList<string> Ledgers { get; }
    public IReadOnlyList<string> VoucherTypes { get; }
    public string Title => Source is null ? $"Create {Module} Entry" : $"Modify {Module} Entry";

    private static AuditEntryEditDraft NewDraft(string module) => new()
    {
        Date = DateTime.Today,
        VoucherType = module switch
        {
            "Sales" => "Sales",
            "Purchase" => "Purchase",
            "Banking" => "Payment",
            "Cash" => "Payment",
            "Journal" => "Journal",
            _ => module
        },
        Reason = "Audit Adjustment",
        Action = "AuditWorkbench"
    };

    private static string[] VoucherTypesFor(string module, string? current)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(current)) values.Add(current.Trim());
        foreach (var value in module switch
        {
            "Sales" => new[] { "Sales", "Credit Note" },
            "Purchase" => new[] { "Purchase", "Debit Note" },
            "Banking" => new[] { "Payment", "Receipt", "Contra" },
            "Cash" => new[] { "Payment", "Receipt" },
            "Journal" => new[] { "Journal" },
            _ => new[] { module }
        }) values.Add(value);
        return values.ToArray();
    }
}
