namespace AccountingAutomationHub.Desktop.Services;

public static class ApplicationSettingKeys
{
    public const string StartWithWindows = "General.StartWithWindows";
    public const string CloseToTray = "General.CloseToTray";
    public const string NotificationsEnabled = "General.NotificationsEnabled";
    public const string OpenLastClient = "General.OpenLastClient";

    public const string AutomationEnabled = "Automation.Enabled";
    public const string AutomationFolderName = "Automation.FolderName";
    public const string AutomationRootMode = "Automation.RootMode";
    public const string AutomationRootFolder = "Automation.RootFolder";
    public const string AutomationScanExisting = "Automation.ScanExisting";
    public const string AutomationMonitorExcelCsv = "Automation.MonitorExcelCsv";
    public const string AutomationMonitorPdf = "Automation.MonitorPdf";
    public const string AutomationAutoImportKnown = "Automation.AutoImportKnown";
    public const string AutomationFileStableSeconds = "Automation.FileStableSeconds";
    public const string NotifyImportComplete = "Notifications.ImportComplete";
    public const string NotifyImportFailed = "Notifications.ImportFailed";
    public const string NotifyActionRequired = "Notifications.ActionRequired";
    public const string NotifyFolderRegistration = "Notifications.FolderRegistration";
    public const string NotifyTallyDisconnected = "Notifications.TallyDisconnected";

    public const string PdfPythonExecutable = "Pdf.PythonExecutable";
    public const string PdfTimeoutMinutes = "Pdf.TimeoutMinutes";
    public const string PdfDeleteGeneratedWorkbook = "Pdf.DeleteGeneratedWorkbook";
    public const string BankOcrExternalRoot = "BankOcr.ExternalRoot";
    public const string BankOcrTimeoutMinutes = "BankOcr.TimeoutMinutes";
    public const string ReviewExportOverwriteExisting = "ReviewExport.OverwriteExisting";

    public const string BankingAutoApplyExactMappings = "Banking.AutoApplyExactMappings";
    public const string BankingShowMappedDefault = "Banking.ShowMappedDefault";
    public const string BankingOpeningBalanceMode = "Banking.OpeningBalanceMode";

    public const string SalesUseSavedMappings = "Sales.UseSavedMappings";
    public const string SalesSaveConfirmedMappings = "Sales.SaveConfirmedMappings";
    public const string PurchaseUseSavedMappings = "Purchase.UseSavedMappings";
    public const string PurchaseSaveConfirmedMappings = "Purchase.SaveConfirmedMappings";
    public const string PurchaseAutoImportKnown = "Purchase.AutoImportKnown";

    public const string TallyHost = "Tally.Host";
    public const string TallyPort = "Tally.Port";
    public const string TallyAutoConnect = "Tally.AutoConnect";
    public const string TallySelectAllDefault = "Tally.SelectAllDefault";

    public const string SecurityLanEnabled = "Security.LanEnabled";
    public const string SecurityLanBindAddress = "Security.LanBindAddress";
    public const string SecurityLanPort = "Security.LanPort";
    public const string SecurityLanCertificateThumbprint = "Security.LanCertificateThumbprint";
    public const string SecurityLanCaCertificateThumbprint = "Security.LanCaCertificateThumbprint";
    public const string SecurityLanCertificateBindAddress = "Security.LanCertificateBindAddress";
    public const string SecurityServerMode = "Security.ServerMode";
    public const string SecurityServerRootFolder = "Security.ServerRootFolder";
    public const string SecurityPcExpiryDays = "Security.PcExpiryDays";
    public const string SecurityAndroidExpiryDays = "Security.AndroidExpiryDays";
    public const string SecurityFolderLinkExpiryDays = "Security.FolderLinkExpiryDays";
    public const string SecurityRequireMfa = "Security.RequireMfa";
    public const string SecurityExternalBackupFolder = "Security.ExternalBackupFolder";
    public const string SecurityServerSecretCertificateThumbprint = "Security.ServerSecretCertificateThumbprint";
    public const string SecurityNetworkClientPackagePath = "Security.NetworkClientPackagePath";
    public const string SecurityAndroidPackagePath = "Security.AndroidPackagePath";
    public const string SecurityMainServerAutoConfigVersion = "Security.MainServerAutoConfigVersion";
    public const string SecurityForceWindowsUpdateVersion = "Security.ForceWindowsUpdateVersion";

    public const string Theme = "Appearance.Theme";
    public const string AccentColor = "Appearance.AccentColor";
    public const string ActionRequiredColor = "Appearance.ActionRequiredColor";
    public const string TallySelectedPeriodColor = "Appearance.TallySelectedPeriodColor";
    public const string GridDensity = "Appearance.GridDensity";
    public const string RememberWindowPlacement = "Appearance.RememberWindowPlacement";
    public const string WindowLeft = "Window.Left";
    public const string WindowTop = "Window.Top";
    public const string WindowWidth = "Window.Width";
    public const string WindowHeight = "Window.Height";
    public const string WindowState = "Window.State";

    public const string ShortcutHelp = "Shortcuts.Help";
    public const string ShortcutRefresh = "Shortcuts.Refresh";
    public const string ShortcutSave = "Shortcuts.Save";
    public const string ShortcutClientSearch = "Shortcuts.ClientSearch";
    public const string ShortcutNewClient = "Shortcuts.NewClient";

    public static IReadOnlyDictionary<string, string> Defaults { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [StartWithWindows] = "True",
            [CloseToTray] = "True",
            [NotificationsEnabled] = "True",
            [OpenLastClient] = "False",
            [AutomationEnabled] = "True",
            [AutomationFolderName] = "XYZ",
            [AutomationRootMode] = "Anywhere",
            [AutomationRootFolder] = string.Empty,
            [AutomationScanExisting] = "True",
            [AutomationMonitorExcelCsv] = "True",
            [AutomationMonitorPdf] = "True",
            [AutomationAutoImportKnown] = "True",
            [AutomationFileStableSeconds] = "3",
            [NotifyImportComplete] = "True",
            [NotifyImportFailed] = "True",
            [NotifyActionRequired] = "True",
            [NotifyFolderRegistration] = "True",
            [NotifyTallyDisconnected] = "True",
            [PdfPythonExecutable] = string.Empty,
            [PdfTimeoutMinutes] = "30",
            [PdfDeleteGeneratedWorkbook] = "True",
            [BankOcrExternalRoot] = string.Empty,
            [BankOcrTimeoutMinutes] = "120",
            [ReviewExportOverwriteExisting] = "False",
            [BankingAutoApplyExactMappings] = "True",
            [BankingShowMappedDefault] = "False",
            [BankingOpeningBalanceMode] = "Review",
            [SalesUseSavedMappings] = "True",
            [SalesSaveConfirmedMappings] = "True",
            [PurchaseUseSavedMappings] = "True",
            [PurchaseSaveConfirmedMappings] = "True",
            [PurchaseAutoImportKnown] = "True",
            [TallyHost] = "127.0.0.1",
            [TallyPort] = "9000",
            [TallyAutoConnect] = "True",
            [TallySelectAllDefault] = "True",
            [SecurityLanEnabled] = "True",
            [SecurityLanBindAddress] = "127.0.0.1",
            [SecurityLanPort] = "47831",
            [SecurityLanCertificateThumbprint] = string.Empty,
            [SecurityLanCaCertificateThumbprint] = string.Empty,
            [SecurityLanCertificateBindAddress] = string.Empty,
            [SecurityServerMode] = "True",
            [SecurityServerRootFolder] = @"C:\Accounting Automation Hub Server",
            [SecurityPcExpiryDays] = "30",
            [SecurityAndroidExpiryDays] = "15",
            [SecurityFolderLinkExpiryDays] = "15",
            [SecurityRequireMfa] = "True",
            [SecurityExternalBackupFolder] = string.Empty,
            [SecurityServerSecretCertificateThumbprint] = string.Empty,
            [SecurityNetworkClientPackagePath] = string.Empty,
            [SecurityAndroidPackagePath] = string.Empty,
            [SecurityMainServerAutoConfigVersion] = string.Empty,
            [SecurityForceWindowsUpdateVersion] = string.Empty,
            [Theme] = "Light",
            [AccentColor] = "#2563EB",
            [ActionRequiredColor] = "#EF4444",
            [TallySelectedPeriodColor] = "#16A34A",
            [GridDensity] = "Comfortable",
            [RememberWindowPlacement] = "True",
            [WindowLeft] = string.Empty,
            [WindowTop] = string.Empty,
            [WindowWidth] = string.Empty,
            [WindowHeight] = string.Empty,
            [WindowState] = string.Empty,
            [ShortcutHelp] = "Ctrl+H",
            [ShortcutRefresh] = "Ctrl+R",
            [ShortcutSave] = "Ctrl+S",
            [ShortcutClientSearch] = "Ctrl+K",
            [ShortcutNewClient] = "Ctrl+N"
        };
}
