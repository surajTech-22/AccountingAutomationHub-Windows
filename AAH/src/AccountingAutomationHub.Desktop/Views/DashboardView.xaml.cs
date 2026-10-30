using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Desktop.Models.Tally;
using AccountingAutomationHub.Desktop.ViewModels;
using Microsoft.Win32;
using DrawingIcon = System.Drawing.Icon;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Infrastructure.Tally;

namespace AccountingAutomationHub.Desktop.Views;

public partial class DashboardView : UserControl
{
    private readonly DispatcherTimer _allSearchDebounceTimer;
    private Window? _hostWindow;
    private Point? _lastTallySettingsAnchorScreenPoint;
    private bool _repositioningTallyAutoSyncSettingsPopup;
    private MainWindowViewModel? _tallyLogoOwner;
    private int _tallyLogoRequestVersion;
    private long _tallyLogoClientId = -1;
    private string _tallyLogoFinancialYear = string.Empty;
    private string _tallyLogoVerifiedIdentity = string.Empty;

    public DashboardView()
    {
        InitializeComponent();
        _allSearchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(170)
        };
        _allSearchDebounceTimer.Tick += (_, _) =>
        {
            _allSearchDebounceTimer.Stop();
            RefreshAllSearchSuggestions();
        };
        AllSearchBox.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(AllSearchTextChanged));
    }

    private DashboardViewModel? ViewModel => DataContext as DashboardViewModel;

    private void DashboardGstin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string gstin } ||
            string.IsNullOrWhiteSpace(gstin)) return;
        // Navigate through the existing shell and client-management editor.
        // Never change the selected Client/FY just to edit a registration.
        if (Window.GetWindow(this)?.DataContext is MainWindowViewModel main)
            main.OpenDashboardGstinManagement(gstin);
    }


    private void DashboardView_Loaded(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (!ReferenceEquals(_hostWindow, window))
        {
            if (_hostWindow is not null)
            {
                _hostWindow.PreviewMouseDown -= DashboardWindow_PreviewMouseDown;
                _hostWindow.Deactivated -= DashboardWindow_Deactivated;
            }
            _hostWindow = window;
            if (_hostWindow is not null)
            {
                _hostWindow.PreviewMouseDown += DashboardWindow_PreviewMouseDown;
                _hostWindow.Deactivated += DashboardWindow_Deactivated;
            }
        }
        if (window?.DataContext is not MainWindowViewModel main) return;
        BalanceSheetToggle.IsChecked = main.IsFinancialStatementEnabled("Balance Sheet");
        ProfitLossToggle.IsChecked = main.IsFinancialStatementEnabled("P&L");
        RefreshAllSearchSuggestions();
        if (!ReferenceEquals(_tallyLogoOwner, main))
        {
            DetachTallyLogoEvents();
            _tallyLogoOwner = main;
            _tallyLogoOwner.PropertyChanged += TallyLogoWorkspacePropertyChanged;
            _tallyLogoOwner.SharedAuthorityChanged += TallyLogoLinkChanged;
        }
        RefreshInstalledTallyLogo(main);
    }

    private void DetachTallyLogoEvents()
    {
        if (_tallyLogoOwner is null) return;
        _tallyLogoOwner.PropertyChanged -= TallyLogoWorkspacePropertyChanged;
        _tallyLogoOwner.SharedAuthorityChanged -= TallyLogoLinkChanged;
        _tallyLogoOwner = null;
    }

    private void TallyLogoWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // MainWindow notifies CurrentClient and CurrentFinancialYear on workspace
        // changes (not CurrentClientId). Re-evaluate even while Dashboard stays loaded.
        if ((e.PropertyName is nameof(MainWindowViewModel.CurrentClient) or
            nameof(MainWindowViewModel.CurrentFinancialYear)) &&
            IsLoaded && _tallyLogoOwner is not null)
            RefreshInstalledTallyLogo(_tallyLogoOwner);
    }

    private void TallyLogoLinkChanged(object? sender, EventArgs e)
    {
        // After confirming/rebinding a company its saved application family can change.
        if (IsLoaded && _tallyLogoOwner is not null)
            RefreshInstalledTallyLogo(_tallyLogoOwner);
    }

    private void RefreshInstalledTallyLogo(MainWindowViewModel main)
    {
        var request = ++_tallyLogoRequestVersion;
        var clientId = main.CurrentClientId;
        var financialYear = main.CurrentFinancialYear;
        // Keep the last verified icon visible during same-Client/FY authority updates.
        // Previously every five-second refresh cleared it to the plain 'Tally'
        // fallback before the asynchronous icon lookup had even completed.
        if (_tallyLogoClientId != clientId ||
            !_tallyLogoFinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase))
        {
            _tallyLogoClientId = clientId;
            _tallyLogoFinancialYear = financialYear;
            _tallyLogoVerifiedIdentity = string.Empty;
            TallyInstalledLogoImage.Source = null;
            TallyInstalledLogoImage.Visibility = Visibility.Collapsed;
            TallyLogoFallback.Visibility = Visibility.Visible;
        }
        if (clientId <= 0) return;

        // File/version queries are background-only: never delay the fast Dashboard paint.
        // Never fall back to an arbitrary running/registered Tally executable here:
        // that can display a Prime icon on an ERP9-linked Client (or vice versa).
        _ = Task.Run(() =>
        {
            try
            {
                var launcher = new TallyApplicationLaunchService();
                var profile = launcher.GetSavedLaunchProfile(clientId);
                if (profile is null || string.IsNullOrWhiteSpace(profile.CompanyKey) ||
                    string.IsNullOrWhiteSpace(profile.ExecutablePath)) return;
                var repository = new SqliteTallyRepository();
                repository.Initialize();
                var link = repository.GetLinkForClient(clientId);
                if (link is null || !profile.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase) ||
                    (link.FinancialYear.Length > 0 &&
                     !financialYear.Equals(link.FinancialYear, StringComparison.OrdinalIgnoreCase))) return;

                var path = profile.ExecutablePath;
                var identity = $"{clientId}|{financialYear}|{link.CompanyKey}|{path}";
                // No repeated icon file reads/repaints for the same validated company
                // as the five-second Tally authority notification is published.
                if (identity.Equals(_tallyLogoVerifiedIdentity, StringComparison.OrdinalIgnoreCase)) return;
                if (!File.Exists(path)) return;
                var product = TallyInstalledProductIdentityService.ReadExecutable(path);
                if (product is null ||
                    (profile.ApplicationProductFamily.Length > 0 &&
                     !product.Family.Equals(profile.ApplicationProductFamily, StringComparison.OrdinalIgnoreCase))) return;
                // Genuine icon belonging to the verified same-company Prime or ERP9 EXE.
                using var ico = DrawingIcon.ExtractAssociatedIcon(path);
                if (ico is null) return;
                var pixels = Imaging.CreateBitmapSourceFromHIcon(
                    ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                pixels.Freeze();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!IsLoaded || request != _tallyLogoRequestVersion ||
                        Window.GetWindow(this)?.DataContext is not MainWindowViewModel current ||
                        current.CurrentClientId != clientId ||
                        !current.CurrentFinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase)) return;
                    _tallyLogoVerifiedIdentity = identity;
                    TallyInstalledLogoImage.Source = pixels;
                    TallyInstalledLogoImage.Visibility = Visibility.Visible;
                    TallyLogoFallback.Visibility = Visibility.Collapsed;
                }), DispatcherPriority.Background);
            }
            catch
            {
                // Icon metadata is decorative; missing/untrusted files never block startup.
            }
        });
    }

    private void DashboardView_Unloaded(object sender, RoutedEventArgs e)
    {
        ++_tallyLogoRequestVersion; // invalidate any outstanding background icon lookup
        DetachTallyLogoEvents();
        if (_hostWindow is not null)
        {
            _hostWindow.PreviewMouseDown -= DashboardWindow_PreviewMouseDown;
            _hostWindow.Deactivated -= DashboardWindow_Deactivated;
            _hostWindow = null;
        }
        if (ViewModel is not null) ViewModel.IsTallyAutoSyncSettingsOpen = false;
        TallyLauncherInfoButton.IsChecked = false;
        _lastTallySettingsAnchorScreenPoint = null;
    }

    private void DashboardWindow_Deactivated(object? sender, EventArgs e)
    {
        // WPF Popup owns a separate HWND. Moving focus to an inner option can
        // deactivate the host Window: do not mistake that for a click outside.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsLoaded) return;
            if (!IsPointerOrFocusWithinTallySettings() && ViewModel is not null)
                ViewModel.IsTallyAutoSyncSettingsOpen = false;
            if (!IsPointerOrFocusWithinTallySettings()) TallyLauncherInfoButton.IsChecked = false;
        }));
    }

    private bool IsPointerOrFocusWithinTallySettings() =>
        TallyAutoSyncSettingsPopup.IsOpen &&
        (TallyAutoSyncSettingsPopup.Child?.IsMouseOver == true ||
         TallyAutoSyncSettingsPopup.Child?.IsKeyboardFocusWithin == true);

    private void TallyAutoSyncSettingsDone_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) ViewModel.IsTallyAutoSyncSettingsOpen = false;
    }

    private void DashboardWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel?.IsTallyAutoSyncSettingsOpen != true) return;
        if (IsClickOnTallySettingsButton(e.OriginalSource as DependencyObject) ||
            IsPointerOrFocusWithinTallySettings()) return;
        ViewModel.IsTallyAutoSyncSettingsOpen = false;
    }

    // Fix137 R46 R9 Dashboard All Search searches the prepared Client/FY accounting
    // authority itself: modules, Masters, Stock, Purchase/Sales rows, Journal vouchers,
    // Banking/Cash, GST, 26AS, statements and Action rows. It never forces hidden modules
    // to hydrate merely because the user typed in the Dashboard.
    public void FocusAllSearchBox(string? searchText = null)
    {
        if (searchText is not null) AllSearchBox.Text = searchText;
        AllSearchBox.Focus();
        if (AllSearchBox.Template.FindName("PART_EditableTextBox", AllSearchBox) is TextBox box) box.SelectAll();
        RefreshAllSearchSuggestions();
        AllSearchBox.IsDropDownOpen = AllSearchBox.Items.Count > 0;
    }

    private void AllSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _allSearchDebounceTimer.Stop();
        if (AllSearchBox.SelectedItem is GlobalSearchResult selected &&
            string.Equals(AllSearchBox.Text, selected.DisplayText, StringComparison.Ordinal))
            return;
        _allSearchDebounceTimer.Start();
    }

    private void RefreshAllSearchSuggestions()
    {
        if (Window.GetWindow(this)?.DataContext is not MainWindowViewModel main) return;
        var query = (AllSearchBox.Text ?? string.Empty).Trim();
        var results = main.SearchWorkspace(query, 100);
        AllSearchBox.ItemsSource = results;
        AllSearchBox.SelectedIndex = -1;
        if (AllSearchBox.IsKeyboardFocusWithin)
            AllSearchBox.IsDropDownOpen = results.Count > 0;
    }

    private void AllSearchGo_Click(object sender, RoutedEventArgs e) => NavigateAllSearch();

    private void AllSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClearAllSearchFromEscape();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        NavigateAllSearch();
        e.Handled = true;
    }

    // Fix137 R46 R14: Dashboard All Search follows the same Escape contract as module search.
    public void ClearAllSearchFromEscape()
    {
        _allSearchDebounceTimer.Stop();
        AllSearchBox.SelectedItem = null;
        AllSearchBox.Text = string.Empty;
        AllSearchBox.IsDropDownOpen = false;
        AllSearchBox.Focus();
    }

    private void NavigateAllSearch()
    {
        if (Window.GetWindow(this) is not MainWindow shell || shell.DataContext is not MainWindowViewModel main) return;
        var query = (AllSearchBox.Text ?? string.Empty).Trim();
        if (query.Length == 0 && AllSearchBox.SelectedItem is not GlobalSearchResult)
        {
            FocusAllSearchBox();
            return;
        }

        var result = AllSearchBox.SelectedItem as GlobalSearchResult
            ?? main.SearchWorkspace(query, 1).FirstOrDefault();
        if (result is null)
        {
            AllSearchBox.IsDropDownOpen = true;
            return;
        }

        shell.NavigateGlobalSearchResult(result, query);
    }

    private void FinancialStatementToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton toggle) return;
        if (Window.GetWindow(this)?.DataContext is not MainWindowViewModel main) return;
        var title = toggle.Tag?.ToString() ?? string.Empty;
        if (!main.SetFinancialStatementEnabled(title, toggle.IsChecked == true))
            toggle.IsChecked = false;
        e.Handled = true;
    }

    private void AccountingWorkbook_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || !viewModel.CanUseUniversalDump)
        {
            MessageBox.Show("Open a client before importing an Accounting Workbook.", "Accounting Workbook", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Import Accounting Workbook",
            Filter = "Accounting workbooks (*.xlsx;*.xlsm;*.xls)|*.xlsx;*.xlsm;*.xls|All files (*.*)|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var result = viewModel.StageUniversalDump([dialog.FileName]);
            MessageBox.Show(result.Message, "Accounting Workbook", MessageBoxButton.OK,
                result.WasSuccessful ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Accounting Workbook", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        e.Handled = true;
    }

    private void UniversalDumpTile_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && ViewModel?.CanUseUniversalDump == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void UniversalDumpTile_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel?.CanUseUniversalDump != true || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var sources = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        if (sources.Length == 0) return;
        try
        {
            var result = ViewModel.StageUniversalDump(sources);
            MessageBox.Show(result.Message, "Universal Dump", MessageBoxButton.OK, result.WasSuccessful ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Universal Dump", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        e.Handled = true;
    }

    private void UniversalDumpTile_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button) return;
        UniversalDump_Click(sender, e);
    }

    private void UniversalDump_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || !viewModel.CanUseUniversalDump)
        {
            MessageBox.Show("Open a client before using Universal Dump.", "Universal Import", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var automation = (Window.GetWindow(this)?.DataContext as MainWindowViewModel)?.AutomationService;
        if (automation is null)
        {
            MessageBox.Show("The background import service is not available.", "Universal Import", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var window = new UniversalDumpWindow(viewModel.ActiveClientId, viewModel.ActiveClientFinancialYear, automation)
        {
            Owner = Window.GetWindow(this)
        };
        window.ShowDialog();
    }

    private void AccountingMetric_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { DataContext: DashboardAccountingMetric metric } && metric.TargetPage.Length > 0)
            NavigateAccountingActivity(metric.TargetPage);
    }

    private void BankPendingMapping_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is not MainWindowViewModel main) return;
        if (!main.NavigateFromAccountingActivity("Banking")) return;
        if (main.CurrentPage is BankingViewModel banking)
            banking.OpenPendingLedgerMappingFromDashboard();
        e.Handled = true;
    }

    private void AccountingActionRequired_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DashboardAccountingMetric metric } &&
            Window.GetWindow(this)?.DataContext is MainWindowViewModel main)
        {
            main.OpenActionCenter(metric.TargetPage);
            e.Handled = true;
        }
    }

    private void Gstr3bMonth_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: DashboardGstr3bMonthTile tile }) return;
        Navigate("GST");
        if (Window.GetWindow(this)?.DataContext is MainWindowViewModel main &&
            main.CurrentPage is GstReconciliationViewModel gst)
            gst.SelectPeriod(tile.PeriodKey);
    }

    private void NavigateAccountingActivity(string page)
    {
        if (Window.GetWindow(this)?.DataContext is not MainWindowViewModel main) return;
        if (!main.NavigateFromAccountingActivity(page)) return;

        // The Bank Statement accounting tile means the Bank Statement sheet/tab,
        // not whichever Banking tab happened to be open last.
        if (page.Equals("Banking", StringComparison.OrdinalIgnoreCase) &&
            main.CurrentPage is BankingViewModel banking)
            banking.SelectedBankingTabIndex = 0;
    }

    private void Navigate(string page)
    {
        if (Window.GetWindow(this)?.DataContext is MainWindowViewModel main)
            main.NavigateTo(page);
    }

    private void TallyExportPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        ViewModel?.BeginTallyExportInteraction();
    }

    private void DashboardView_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel?.IsTallyAutoSyncSettingsOpen != true ||
            IsClickOnTallySettingsButton(e.OriginalSource as DependencyObject) ||
            IsPointerOrFocusWithinTallySettings()) return;
        ViewModel.IsTallyAutoSyncSettingsOpen = false;
    }

    private bool IsClickOnTallySettingsButton(DependencyObject? current)
    {
        while (current is not null)
        {
            if (ReferenceEquals(current, TallyAutoSyncSettingsButton) ||
                ReferenceEquals(current, TallyAutoSyncSettingsPopup.Child)) return true;
            current = current switch
            {
                Visual visual => VisualTreeHelper.GetParent(visual),
                System.Windows.Media.Media3D.Visual3D visual3D => VisualTreeHelper.GetParent(visual3D),
                FrameworkContentElement content => content.Parent,
                _ => LogicalTreeHelper.GetParent(current)
            };
        }
        return false;
    }

    private void TallyAutoSyncSettingsButton_Checked(object sender, RoutedEventArgs e)
    {
        AnimateTallySettingsGear(180d);
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(RepositionTallyAutoSyncSettingsPopup));
    }

    private void TallyAutoSyncSettingsButton_Unchecked(object sender, RoutedEventArgs e)
    {
        AnimateTallySettingsGear(0d);
        _lastTallySettingsAnchorScreenPoint = null;
    }

    private CustomPopupPlacement[] TallyAutoSyncSettingsPopup_CustomPopupPlacement(Size popupSize, Size targetSize, Point offset)
    {
        // Right-align the separate settings panel to the Cog and open directly below it.
        // The popup is explicitly repositioned while DashboardScrollViewer moves so it follows the Cog.
        var below = new Point(targetSize.Width - popupSize.Width, targetSize.Height + 4d);
        var above = new Point(targetSize.Width - popupSize.Width, -popupSize.Height - 4d);
        return
        [
            new CustomPopupPlacement(below, PopupPrimaryAxis.Vertical),
            new CustomPopupPlacement(above, PopupPrimaryAxis.Vertical)
        ];
    }

    private void TallyAutoSyncSettingsPopup_Opened(object? sender, EventArgs e)
    {
        _lastTallySettingsAnchorScreenPoint = GetTallySettingsAnchorScreenPoint();
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(RepositionTallyAutoSyncSettingsPopup));
    }

    private void TallyAutoSyncSettingsPopup_Closed(object? sender, EventArgs e)
        => _lastTallySettingsAnchorScreenPoint = null;

    private void DashboardScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ViewModel?.IsTallyAutoSyncSettingsOpen != true) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(RepositionTallyAutoSyncSettingsPopup));
    }

    private void TallyAutoSyncSettingsButton_LayoutUpdated(object? sender, EventArgs e)
    {
        if (ViewModel?.IsTallyAutoSyncSettingsOpen != true || _repositioningTallyAutoSyncSettingsPopup) return;
        var current = GetTallySettingsAnchorScreenPoint();
        if (current is null) return;
        if (_lastTallySettingsAnchorScreenPoint is Point previous &&
            Math.Abs(previous.X - current.Value.X) < 0.5d &&
            Math.Abs(previous.Y - current.Value.Y) < 0.5d) return;
        _lastTallySettingsAnchorScreenPoint = current;
        RepositionTallyAutoSyncSettingsPopup();
    }

    private Point? GetTallySettingsAnchorScreenPoint()
    {
        if (!TallyAutoSyncSettingsButton.IsVisible || TallyAutoSyncSettingsButton.ActualWidth <= 0d || TallyAutoSyncSettingsButton.ActualHeight <= 0d) return null;
        try { return TallyAutoSyncSettingsButton.PointToScreen(new Point(0d, 0d)); }
        catch (InvalidOperationException) { return null; }
    }

    private bool IsTallySettingsAnchorInsideDashboardViewport()
    {
        try
        {
            var bounds = TallyAutoSyncSettingsButton.TransformToAncestor(DashboardScrollViewer)
                .TransformBounds(new Rect(0d, 0d, TallyAutoSyncSettingsButton.ActualWidth, TallyAutoSyncSettingsButton.ActualHeight));
            var viewport = new Rect(0d, 0d, DashboardScrollViewer.ViewportWidth, DashboardScrollViewer.ViewportHeight);
            return bounds.IntersectsWith(viewport);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void RepositionTallyAutoSyncSettingsPopup()
    {
        if (_repositioningTallyAutoSyncSettingsPopup || !TallyAutoSyncSettingsPopup.IsOpen) return;
        if (!IsTallySettingsAnchorInsideDashboardViewport())
        {
            if (ViewModel is not null) ViewModel.IsTallyAutoSyncSettingsOpen = false;
            return;
        }

        try
        {
            _repositioningTallyAutoSyncSettingsPopup = true;
            var offset = TallyAutoSyncSettingsPopup.HorizontalOffset;
            TallyAutoSyncSettingsPopup.HorizontalOffset = offset + 0.01d;
            TallyAutoSyncSettingsPopup.HorizontalOffset = offset;
            _lastTallySettingsAnchorScreenPoint = GetTallySettingsAnchorScreenPoint();
        }
        finally
        {
            _repositioningTallyAutoSyncSettingsPopup = false;
        }
    }

    private void AnimateTallySettingsGear(double targetAngle)
    {
        var animation = new DoubleAnimation
        {
            To = targetAngle,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        TallyAutoSyncSettingsIconRotation.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    private void ExportMonth_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: TallyExportMonthOption option } border) return;
        var module = border.Tag?.ToString() ?? string.Empty;
        ViewModel?.BeginTallyExportInteraction();
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            ViewModel?.ToggleExportMonthShortcut(option.MonthIndex, module, shift: true);
        else
            ViewModel?.ToggleExportMonth(module, option.MonthIndex);
        e.Handled = true;
    }

    private void TallyExportPeriodMode_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not RadioButton radio) return;
        var tag = radio.Tag?.ToString() ?? string.Empty;
        var separator = tag.IndexOf('|');
        if (separator <= 0 || separator >= tag.Length - 1) return;
        ViewModel?.BeginTallyExportInteraction();
        ViewModel?.SetTallyExportPeriodMode(tag[..separator], tag[(separator + 1)..]);
        e.Handled = true;
    }

    private void TallyModuleMonthSelection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var tag = button.Tag?.ToString() ?? string.Empty;
        var separator = tag.IndexOf('|');
        if (separator <= 0 || separator >= tag.Length - 1) return;
        var module = tag[..separator];
        var action = tag[(separator + 1)..];
        ViewModel?.BeginTallyExportInteraction();
        ViewModel?.SetAllExportMonthsForModule(module, action.Equals("All", StringComparison.OrdinalIgnoreCase));
        e.Handled = true;
    }

    private void TallyReportGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        var scroll = FindVisualChild<ScrollViewer>(grid);
        if (scroll is null) return;
        var atTop = e.Delta > 0 && scroll.VerticalOffset <= 0;
        var atBottom = e.Delta < 0 && scroll.VerticalOffset >= scroll.ScrollableHeight;
        if (!atTop && !atBottom) return;

        var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = DashboardScrollViewer
        };
        DashboardScrollViewer.RaiseEvent(forwarded);
        e.Handled = true;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result) return result;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}
