using System.Windows;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Views;

public partial class AuditEntryModifyWindow : Window
{
    public AuditEntryModifyWindow(AuditRecordRow original, bool canApplyAah, bool canApplyTally)
    {
        InitializeComponent();
        Model = new AuditEntryModifyDialogModel(original, canApplyAah, canApplyTally);
        DataContext = Model;
    }

    public AuditEntryModifyDialogModel Model { get; }
    public AuditEntryEditDraft Draft => Model.Draft;

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        Draft.Action = element.Tag?.ToString() ?? "Suggestion";
        if (string.IsNullOrWhiteSpace(Draft.Reason))
        {
            MessageBox.Show(this, "Select a reason for the audit modification.", "Audit Entry Modification", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}

public sealed class AuditEntryModifyDialogModel
{
    public AuditEntryModifyDialogModel(AuditRecordRow original, bool canApplyAah, bool canApplyTally)
    {
        Original = original;
        Draft = AuditEntryEditDraft.From(original);
        CanApplyAah = canApplyAah;
        CanApplyTally = canApplyTally;
    }
    public AuditRecordRow Original { get; }
    public AuditEntryEditDraft Draft { get; }
    public string OriginalNarration => Original.OriginalNarration.Length > 0 ? Original.OriginalNarration : Original.Details;
    public IReadOnlyList<string> Reasons => AuditEntryEditDraft.Reasons;
    public bool CanApplyAah { get; }
    public bool CanApplyTally { get; }
}
