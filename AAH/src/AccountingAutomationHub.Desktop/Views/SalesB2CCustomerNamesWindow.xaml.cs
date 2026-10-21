using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Views;

public partial class SalesB2CCustomerNamesWindow : Window
{
    private readonly SalesImportViewModel _vm;
    private readonly Dictionary<string, string> _committedNames = new(StringComparer.OrdinalIgnoreCase);
    private bool _saving;

    public SalesB2CCustomerNamesWindow(SalesImportViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.B2CSearchText = string.Empty;
        vm.RefreshB2CCustomerPanelForDisplay();
        foreach (var row in vm.B2CVisibleCustomers)
            _committedNames[row.SourceKey] = row.CustomerName;
        vm.BeginB2CCustomerRenameSession();
        Closed += (_, _) => vm.EndB2CCustomerRenameSession();
    }

    private async void Name_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        await SaveAsync(sender as ComboBox);
        e.Handled = true;
    }

    private async void Name_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        await SaveAsync(sender as ComboBox);

    private async void Name_DropDownClosed(object sender, EventArgs e) =>
        await SaveAsync(sender as ComboBox);

    private async Task SaveAsync(ComboBox? combo)
    {
        if (_saving || combo?.Tag is not SalesB2CCustomerMatchRow row) return;
        var name = combo.Text?.Trim() ?? string.Empty;
        var committed = _committedNames.TryGetValue(row.SourceKey, out var saved)
            ? saved
            : row.CustomerName;
        if (name.Length == 0 || string.Equals(name, committed, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            _saving = true;
            if (await _vm.SaveB2CCustomerNameFromPanelAsync(row.SourceKey, name))
            {
                _committedNames[row.SourceKey] = name;
                combo.Text = name;
            }
            else
            {
                combo.Text = committed;
            }
        }
        finally
        {
            _saving = false;
        }
    }
}
