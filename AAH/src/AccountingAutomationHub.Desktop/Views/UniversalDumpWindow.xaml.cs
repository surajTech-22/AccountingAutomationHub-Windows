using System.Windows;
using Microsoft.Win32;
using AccountingAutomationHub.Desktop.Services;

namespace AccountingAutomationHub.Desktop.Views;

public partial class UniversalDumpWindow : Window
{
    private readonly long _clientId;
    private readonly string _financialYear;
    private readonly BackgroundAutomationService _automationService;
    private readonly List<string> _sources = [];

    public UniversalDumpWindow(long clientId, string financialYear, BackgroundAutomationService automationService)
    {
        InitializeComponent();
        _clientId = clientId;
        _financialYear = financialYear;
        _automationService = automationService;
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select accounting source files",
            Multiselect = true,
            Filter = "Supported accounting sources|*.pdf;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.webp;*.xlsx;*.xlsm;*.xls;*.csv;*.txt;*.tsv;*.xml;*.json;*.zip;*.mt940;*.940;*.sta;*.ofx;*.qbo;*.qif|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        AddSources(dialog.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select a folder to dump into Accounting Automation Hub",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        var owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var result = dialog.ShowDialog(new Win32Window(owner));
        if (result != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
        AddSources([dialog.SelectedPath]);
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        foreach (var selected in SourceList.SelectedItems.Cast<string>().ToArray())
            _sources.RemoveAll(item => item.Equals(selected, StringComparison.OrdinalIgnoreCase));
        RefreshList();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _sources.Clear();
        RefreshList();
    }

    // Invoice image/PDF review is independent of Universal Dump's existing classification
    // and import action. No child result is automatically posted to AAH or Tally.
    private void OpenInvoiceVision_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            InvoiceVisionChildLauncher.Open(_clientId, _financialYear, _sources);
            StatusText.Text = "InvoiceVision opened for review. Universal Dump import was not changed or started.";
        }
        catch (Exception error)
        {
            StatusText.Text = "InvoiceVision not available: " + error.Message;
            MessageBox.Show(error.Message, "InvoiceVision Child", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_sources.Count == 0)
        {
            StatusText.Text = "Add at least one file or folder first.";
            return;
        }
        StartButton.IsEnabled = false;
        try
        {
            var result = _automationService.StageUniversalDump(_clientId, _financialYear, _sources);
            StatusText.Text = result.Message;
            if (result.WasSuccessful)
            {
                MessageBox.Show(
                    $"{result.Message}\n\nOpen Finance Center → Import Jobs & Undo to follow classification, Review Required and completion.",
                    "Universal Import",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            MessageBox.Show(exception.Message, "Universal Import", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }

    private void AddSources(IEnumerable<string> sources)
    {
        foreach (var source in sources.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var normalized = source.Trim();
            if (!_sources.Contains(normalized, StringComparer.OrdinalIgnoreCase)) _sources.Add(normalized);
        }
        RefreshList();
    }

    private void RefreshList()
    {
        SourceList.ItemsSource = null;
        SourceList.ItemsSource = _sources.ToArray();
        SelectionCountText.Text = $"{_sources.Count:N0} source(s)";
    }

    private sealed class Win32Window(nint handle) : System.Windows.Forms.IWin32Window
    {
        public nint Handle { get; } = handle;
    }
}
