using System.Windows.Input;

namespace AccountingAutomationHub.Desktop.Commands;

public sealed class ParameterRelayCommand(
    Action<object?> execute,
    Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(
        object? parameter) =>
        canExecute?.Invoke(parameter) ?? true;

    public void Execute(
        object? parameter) =>
        execute(parameter);

    public void RaiseCanExecuteChanged()
    {
        var handler = CanExecuteChanged;
        if (handler is null) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            handler(this, EventArgs.Empty);
        else
            _ = dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.DataBind,
                new Action(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty)));
    }
}
