using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Infrastructure.Persistence;

namespace AccountingAutomationHub.Desktop.Controls;

public sealed class CachedPageHost : Grid
{
    private readonly Dictionary<object, ContentPresenter> _cache =
        new(ReferenceEqualityComparer.Instance);
    private object? _activePage;
    private ContentPresenter? _activePresenter;

    public static readonly DependencyProperty ContentProperty =
        DependencyProperty.Register(
            nameof(Content),
            typeof(object),
            typeof(CachedPageHost),
            new PropertyMetadata(null, OnContentChanged));

    public object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    // Fix137 R46 R9: shell shortcuts must inspect only the currently visible page.
    // CachedPageHost intentionally retains light page visual trees, so searching the whole
    // host can otherwise discover a hidden Dashboard search control from an older page.
    public DependencyObject? ActiveVisualRoot => _activePresenter;
    public object? ActivePage => _activePage;

    private static void OnContentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not CachedPageHost host) return;
        host.Show(args.NewValue);
    }

    private void Show(object? page)
    {
        var stopwatch = Stopwatch.StartNew();

        // Journal, GST and Purchase are the three heaviest accounting workspaces.
        // Keeping all three WPF visual trees alive at once retains DataGrid containers,
        // popup/search controls and their collection subscriptions.  The ViewModels are
        // already long-lived in MainWindowViewModel, so release only the old visual tree
        // when navigating away; data/edit state in the ViewModel remains authoritative.
        ReleaseTransientActiveVisualIfNeeded(page);

        foreach (var cached in _cache.Values)
            cached.Visibility = Visibility.Collapsed;

        _activePage = page;
        _activePresenter = null;

        if (page is null)
        {
            stopwatch.Stop();
            return;
        }

        if (!_cache.TryGetValue(page, out var presenter))
        {
            presenter = new ContentPresenter
            {
                Content = page,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Visibility = Visibility.Collapsed
            };
            _cache[page] = presenter;
            Children.Add(presenter);
        }

        _activePresenter = presenter;
        presenter.Visibility = Visibility.Visible;
        stopwatch.Stop();
        WriteTiming(page.GetType().Name, stopwatch.ElapsedMilliseconds, _cache.Count);
    }

    private void ReleaseTransientActiveVisualIfNeeded(object? nextPage)
    {
        if (_activePage is null || _activePresenter is null || ReferenceEquals(_activePage, nextPage))
            return;
        if (!IsTransientHeavyPage(_activePage))
            return;

        _activePresenter.Visibility = Visibility.Collapsed;
        Children.Remove(_activePresenter);
        _activePresenter.Content = null;
        _cache.Remove(_activePage);
        _activePresenter = null;
        _activePage = null;
    }

    private static bool IsTransientHeavyPage(object page) =>
        page is JournalViewModel or GstReconciliationViewModel or PurchaseImportViewModel;

    private static void WriteTiming(string pageName, long elapsedMilliseconds, int cachedPages)
    {
        try
        {
            var logFolder = Path.Combine(ApplicationDataPaths.RootFolder, "Logs");
            Directory.CreateDirectory(logFolder);
            var line = $"{DateTime.Now:O}\t{pageName}\t{elapsedMilliseconds} ms\tcache={cachedPages}{Environment.NewLine}";
            _ = Task.Run(() => File.AppendAllText(Path.Combine(logFolder, "navigation-performance.log"), line));
        }
        catch
        {
            // Navigation must never fail because diagnostic logging failed.
        }
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
