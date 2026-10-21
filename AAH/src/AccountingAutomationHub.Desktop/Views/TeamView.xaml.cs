using System.Windows.Controls;
using System.Windows.Input;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Views;

public partial class TeamView : UserControl
{
    public TeamView() => InitializeComponent();

    private void MessageComposer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        if (DataContext is not TeamViewModel viewModel || !viewModel.SendCommand.CanExecute(null)) return;
        e.Handled = true;
        viewModel.SendCommand.Execute(null);
    }

    private void UsersList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not TeamViewModel viewModel || !viewModel.StartPrivateChatCommand.CanExecute(null)) return;
        viewModel.StartPrivateChatCommand.Execute(null);
        MessageComposer.Focus();
    }
}
