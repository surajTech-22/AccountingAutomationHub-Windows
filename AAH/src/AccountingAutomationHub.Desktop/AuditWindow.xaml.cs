using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Desktop.Views;

namespace AccountingAutomationHub.Desktop;

public partial class AuditWindow : Window
{
    private readonly AuditWorkspaceViewModel _viewModel;
    private bool _allowClose;
    private UniversalCalculatorWindow? _calculatorWindow;
    private Window? _chatWindow;
    private TeamViewModel? _teamViewModel;
    private ActiveClientContext? _teamClientContext;

    public AuditWindow(AuditWorkspaceViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
    }

    internal void CloseForApplicationExit()
    {
        _allowClose = true;
        try { _chatWindow?.Close(); } catch { }
        try { _calculatorWindow?.Close(); } catch { }
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeRestore_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Undo_Click(object sender, RoutedEventArgs e) => UndoCurrentEdit();

    private static void UndoCurrentEdit()
    {
        var target = Keyboard.FocusedElement as IInputElement;
        if (target is not null && ApplicationCommands.Undo.CanExecute(null, target))
            ApplicationCommands.Undo.Execute(null, target);
    }

    private void Calculator_Click(object sender, RoutedEventArgs e) => OpenCalculator();

    private void OpenCalculator()
    {
        if (_calculatorWindow is { IsLoaded: true })
        {
            if (_calculatorWindow.WindowState == WindowState.Minimized) _calculatorWindow.WindowState = WindowState.Normal;
            _calculatorWindow.Activate();
            return;
        }
        _calculatorWindow = new UniversalCalculatorWindow { Owner = this };
        _calculatorWindow.Closed += (_, _) => _calculatorWindow = null;
        _calculatorWindow.Show();
    }

    private void Chat_Click(object sender, RoutedEventArgs e)
    {
        if (_chatWindow is { IsLoaded: true })
        {
            if (_chatWindow.WindowState == WindowState.Minimized) _chatWindow.WindowState = WindowState.Normal;
            _chatWindow.Activate();
            return;
        }

        // Fix96 shell parity: Team & Chat is a general AAH utility. Audit does not feed its
        // client identity into Accounting chat keys; global/private chat remains available
        // while Audit-specific client conversations can be defined with the later Audit module rules.
        _teamClientContext ??= new ActiveClientContext();
        _teamViewModel ??= new TeamViewModel(_teamClientContext);
        _chatWindow = new Window
        {
            Title = "Accounting Automation Hub · Team & Chat",
            Width = 1180,
            Height = 760,
            MinWidth = 900,
            MinHeight = 600,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("AppBackgroundBrush"),
            Content = new TeamView { DataContext = _teamViewModel }
        };
        _chatWindow.Closed += (_, _) => _chatWindow = null;
        _chatWindow.Show();
    }

    private void AuditWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (control && alt && e.Key == Key.C)
        {
            OpenCalculator();
            e.Handled = true;
            return;
        }
        if (control && !alt && e.Key == Key.Z)
        {
            UndoCurrentEdit();
            e.Handled = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose && System.Windows.Application.Current is App app)
            app.HandleAuditWindowClosing(e);
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
