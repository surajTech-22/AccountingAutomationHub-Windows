using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Views;

public partial class AuditWorkspaceView : UserControl
{
    private readonly AuditWorkspaceViewModel _viewModel;
    private UniversalCalculatorWindow? _calculatorWindow;
    private Window? _chatWindow;
    private TeamViewModel? _teamViewModel;
    private ActiveClientContext? _teamClientContext;

    public AuditWorkspaceView(AuditWorkspaceViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;

        // Fix104: generic Audit data modules and Sources & Links use the same retained
        // multi-option search/filter bar as Accounting (comma-AND, numeric operators,
        // field suggestions, sort commands and Excel-style column filters).
        GenericAuditSearchFilterBar.Attach(GenericAuditGrid);
        SourceAuditSearchFilterBar.Attach(SourceAuditGrid);
    }

    private Window? HostWindow => Window.GetWindow(this);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var host = HostWindow;
        if (host is null) return;
        if (e.ClickCount == 2)
        {
            host.WindowState = host.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed) host.DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (HostWindow is { } host) host.WindowState = WindowState.Minimized;
    }

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        if (HostWindow is not { } host) return;
        host.WindowState = host.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => HostWindow?.Close();
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
        // Fix97: Audit uses the persistent MainWindow calculator so the same live
        // DataGrid selection-order logic works in Accounting and Audit.
        if (HostWindow is MainWindow mainWindow)
        {
            mainWindow.OpenSharedCalculator();
            return;
        }
        if (_calculatorWindow is { IsLoaded: true })
        {
            if (_calculatorWindow.WindowState == WindowState.Minimized) _calculatorWindow.WindowState = WindowState.Normal;
            _calculatorWindow.Activate();
            return;
        }
        _calculatorWindow = new UniversalCalculatorWindow { Owner = HostWindow };
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

        _teamClientContext ??= new ActiveClientContext();
        _teamViewModel ??= new TeamViewModel(_teamClientContext);
        _chatWindow = new Window
        {
            Title = "Accounting Automation Hub · Team & Chat",
            Width = 1180,
            Height = 760,
            MinWidth = 900,
            MinHeight = 600,
            Owner = HostWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("AppBackgroundBrush"),
            Content = new TeamView { DataContext = _teamViewModel }
        };
        _chatWindow.Closed += (_, _) => _chatWindow = null;
        _chatWindow.Show();
    }

    private void AuditWorkspaceView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Fix104: nested DataGrids, horizontal tile strips and lookup panels must not trap
        // the wheel when they cannot scroll any farther. Keep the inner control authoritative
        // while it has vertical range; at an edge (or on a horizontal-only ScrollViewer),
        // hand the same wheel movement to the next vertical Audit page ScrollViewer.
        if (e.Handled || Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
            e.OriginalSource is not DependencyObject source) return;

        var inner = FindAncestorScrollViewer(source);
        if (inner is null) return;

        var canMoveInner = inner.ScrollableHeight > 0.5 &&
                           ((e.Delta > 0 && inner.VerticalOffset > 0.5) ||
                            (e.Delta < 0 && inner.VerticalOffset < inner.ScrollableHeight - 0.5));
        if (canMoveInner) return;

        var outer = FindAncestorScrollViewer(ParentOf(inner));
        while (outer is not null && outer.ScrollableHeight <= 0.5)
            outer = FindAncestorScrollViewer(ParentOf(outer));
        if (outer is null) return;

        var next = Math.Clamp(outer.VerticalOffset - e.Delta, 0d, outer.ScrollableHeight);
        outer.ScrollToVerticalOffset(next);
        e.Handled = true;
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject? start)
    {
        for (var current = start; current is not null; current = ParentOf(current))
            if (current is ScrollViewer scrollViewer) return scrollViewer;
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject current)
    {
        if (current is FrameworkContentElement frameworkContent) return frameworkContent.Parent;
        if (current is ContentElement content) return ContentOperations.GetParent(content);
        return VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
    }

    private void AuditStatisticGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not AuditWorkspaceViewModel viewModel || sender is not DataGrid grid || e.OriginalSource is not DependencyObject current) return;
        while (current is not null && current is not DataGridRow) current = VisualTreeHelper.GetParent(current);
        if (current is not DataGridRow row || row.DataContext is not AuditStatisticRow statistic) return;
        grid.SelectedItem = statistic;
        if (viewModel.OpenAuditStatisticCommand.CanExecute(statistic)) viewModel.OpenAuditStatisticCommand.Execute(statistic);
    }

    private void AuditWorkspaceView_PreviewKeyDown(object sender, KeyEventArgs e) => HandlePreviewKeyDown(e);

    internal void HandlePreviewKeyDown(KeyEventArgs e)
    {
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (DataContext is AuditWorkspaceViewModel viewModel && (viewModel.IsDebtors || viewModel.IsCreditors) &&
            e.Key == Key.F12)
        {
            if (viewModel.OpenDebtorCreditorSettingsCommand.CanExecute(null))
                viewModel.OpenDebtorCreditorSettingsCommand.Execute(null);
            e.Handled = true;
            return;
        }
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

    internal void CloseOwnedUtilityWindows()
    {
        try { _chatWindow?.Close(); } catch { }
        try { _calculatorWindow?.Close(); } catch { }
    }
}
