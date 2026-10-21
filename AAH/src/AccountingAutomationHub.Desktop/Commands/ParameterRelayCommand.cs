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

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(
            this,
            EventArgs.Empty);
}
