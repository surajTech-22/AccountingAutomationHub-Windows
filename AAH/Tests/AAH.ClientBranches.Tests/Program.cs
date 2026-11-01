using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Clients;

static void Check(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException(message);
}
static void Reject(string text, string messageFragment)
{
    try { ClientBranchCatalog.Parse(text); }
    catch (InvalidOperationException ex) when (ex.Message.Contains(messageFragment, StringComparison.OrdinalIgnoreCase)) { return; }
    throw new InvalidOperationException("Expected branch parsing rejection: " + text);
}

var plain = new ClientProfile
{
    Id = 1, ClientCode = "CL1", ClientName = "Same name",
    FinancialYear = "2026-27", Gstin = "29ABCDE1234F1Z5", Pan = "ABCDE1234F",
    AdditionalGstins = []
};
var originalName = plain.ClientName;
Check(ClientBranchCatalog.Resolve(plain, "").Count == 0, "Single GSTIN must have zero forced branches.");
Check(ClientBranchCatalog.Parse("").Count == 0, "No branch text must remain a normal client.");

var nameOnly = ClientBranchCatalog.Parse("Warehouse\nService Centre");
Check(nameOnly.Count == 2 && nameOnly.All(x => x.Gstin.Length == 0), "Name-only branches not accepted.");
Check(ClientBranchCatalog.AdditionalGstins(nameOnly, plain.Gstin, plain.Pan).Count == 0, "Name-only branches generated bogus GST registrations.");

var branchLines = "South Branch | 27ABCDE1234F1Z5\nNorth Office\n33ABCDE1234F2Z5\nHead Office | 29ABCDE1234F1Z5";
var branches = ClientBranchCatalog.Parse(branchLines);
Check(branches.Count == 4, "Branch count changed.");
var extras = ClientBranchCatalog.AdditionalGstins(branches, plain.Gstin, plain.Pan);
Check(extras.SequenceEqual(new[] { "27ABCDE1234F1Z5", "33ABCDE1234F2Z5" }), "Additional GSTIN import authority wrong.");
Check(ClientBranchCatalog.Parse(ClientBranchCatalog.Format(branches)).Count == branches.Count, "Branch editor round trip failed.");
plain.AdditionalGstins = extras;
var saved = ClientBranchCatalog.Serialize(branches);
var resolved = ClientBranchCatalog.Resolve(plain, saved);
Check(resolved.Count == 4 && resolved.Any(x => x.Name == "North Office" && x.Gstin.Length == 0),
    "Name-only branch was lost during JSON round trip.");
Check(resolved.Any(x => x.Name == "South Branch" && x.Gstin == "27ABCDE1234F1Z5"),
    "Branch name was not linked to original GSTIN.");
Check(plain.ClientName == originalName && plain.Id == 1, "Branch change should not rewrite the original Client identity.");

var legacy = new ClientProfile
{
    Id = 2, ClientName = "Legacy multi-GSTIN client", Gstin = "29ABCDE1234F1Z5",
    AdditionalGstins = ["27ABCDE1234F1Z5", "33ABCDE1234F2Z5"]
};
var migrated = ClientBranchCatalog.Resolve(legacy, "");
Check(migrated.Count == 3 && migrated.All(x => x.Gstin.Length == 15) &&
      migrated.Any(x => x.Gstin == legacy.Gstin && x.Name == "Karnataka") &&
      migrated.Any(x => x.Gstin == "27ABCDE1234F1Z5" && x.Name == "Maharashtra") &&
      migrated.Any(x => x.Gstin == "33ABCDE1234F2Z5" && x.Name == "Tamil Nadu"),
    "Existing GST registrations must become named branches including the primary GSTIN.");

var updated = new ClientProfile
{
    Id = plain.Id, Gstin = plain.Gstin, AdditionalGstins = ["27ABCDE1234F1Z5"],
    ClientName = plain.ClientName
};
var cleaned = ClientBranchCatalog.Resolve(updated, saved);
Check(cleaned.All(x => x.Gstin != "33ABCDE1234F2Z5"), "Deleted GSTIN still displayed after dashboard quick edit.");
Check(cleaned.Any(x => x.Name == "North Office"), "Name-only branch deleted during quick GSTIN edit.");

var autoNamed = ClientBranchCatalog.Parse("24ABCDE1234F1Z5\n29ABCDE1234F1Z5");
Check(autoNamed[0].Name == "Gujarat" && autoNamed[1].Name == "Karnataka",
    "GSTIN first two digits must determine automatic state branch name.");
var twoKarnataka = ClientBranchCatalog.Parse("29ABCDE1234F1Z5\n29ABCDE1234F2Z5");
Check(twoKarnataka.Count == 2 && twoKarnataka.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
    "Different GSTINs in the same state need unique branch labels.");
var withIds = ClientBranchCatalog.Reconcile(plain.Id, branches, []);
var stableIds = ClientBranchCatalog.Reconcile(plain.Id,
    ClientBranchCatalog.Parse("Renamed South | 27ABCDE1234F1Z5\nNorth Office\n33ABCDE1234F2Z5\nHead Office | 29ABCDE1234F1Z5"), withIds);
Check(stableIds[0].BranchId == withIds[0].BranchId, "Renaming an existing GSTIN branch changed accounting identity.");

var tempBranchesRoot = Path.Combine(Path.GetTempPath(), "AAH-BranchFolderTests-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(tempBranchesRoot);
    var trees = ClientBranchAutoImportLayout.Ensure(tempBranchesRoot, plain.Id, stableIds);
    Check(trees.Count == 4, "Expected one physical branch root per durable branch.");
    Check(trees.All(x => Path.GetDirectoryName(x.FolderPath)!.Equals(
              tempBranchesRoot, StringComparison.OrdinalIgnoreCase)) &&
          !Directory.Exists(Path.Combine(tempBranchesRoot, "Branches")),
          "XYZ must directly contain named branch folders; do not create XYZ/Branches.");
    Check(trees.All(x => Directory.Exists(x.FolderPath) &&
                         File.Exists(Path.Combine(x.FolderPath, ClientBranchAutoImportLayout.Marker)) &&
                         !Directory.Exists(Path.Combine(x.FolderPath, "Banking")) &&
                         !Directory.Exists(Path.Combine(x.FolderPath, "Purchase")) &&
                         !Directory.Exists(Path.Combine(x.FolderPath, "Sales")) &&
                         !Directory.Exists(Path.Combine(x.FolderPath, "GST"))),
        "Branch Auto Import must have a flat, marker-owned folder, not ten nested module directories.");
    if (OperatingSystem.IsWindows())
        Check(trees.All(x => (File.GetAttributes(Path.Combine(x.FolderPath,
              ClientBranchAutoImportLayout.Marker)) &
              (FileAttributes.Hidden | FileAttributes.System)) ==
              (FileAttributes.Hidden | FileAttributes.System)),
            "New branch JSON metadata must be hidden from normal Windows Explorer.");
    var statement = Path.Combine(trees[0].FolderPath, "test-bank.xlsx");
    File.WriteAllText(statement, "Synthetic branch-owned source; never imported");
    Check(ClientBranchAutoImportLayout.TryResolve(tempBranchesRoot, statement, out var found) &&
          found!.ClientId == plain.Id && found.BranchId == stableIds[0].BranchId &&
          ClientBranchAutoImportLayout.IsWithinBranchPath(tempBranchesRoot, statement),
        "Flat branch file did not resolve to its durable owner.");
    Check(!ClientBranchAutoImportLayout.TryResolve(tempBranchesRoot,
          Path.Combine(tempBranchesRoot, "Banking", "unscoped.xlsx"), out _),
        "Legacy client root must remain unscoped.");

    // v537 generated unnecessary directories. Delete empty directories only;
    // never delete, overwrite or relocate files the user already saved.
    var oldEmpty = Path.Combine(trees[0].FolderPath, "Cash");
    Directory.CreateDirectory(oldEmpty);
    var oldOccupied = Path.Combine(trees[0].FolderPath, "Sales");
    Directory.CreateDirectory(oldOccupied);
    var salesEvidence = Path.Combine(oldOccupied, "keep-sales.xlsx");
    File.WriteAllText(salesEvidence, "MUST KEEP");
    var oldGst = Path.Combine(trees[0].FolderPath, "GST", trees[0].Gstin, "GSTR-3B");
    Directory.CreateDirectory(oldGst);
    var rerun = ClientBranchAutoImportLayout.Ensure(tempBranchesRoot, plain.Id, stableIds);
    Check(rerun.Count == trees.Count && File.Exists(statement) &&
          !Directory.Exists(oldEmpty) && File.Exists(salesEvidence) && !Directory.Exists(oldGst),
        "Restart reconciliation must remove only empty v537 folders and retain all user files.");
    var empty = Path.Combine(Path.GetTempPath(), "AAH-NormalClientTest-" + Guid.NewGuid().ToString("N"));
    Check(ClientBranchAutoImportLayout.Ensure(empty, plain.Id, []).Count == 0 &&
          !Directory.Exists(empty), "Unbranched client must not create a Branches directory.");
    var unclaimed = Path.Combine(tempBranchesRoot, "Unclaimed");
    Directory.CreateDirectory(unclaimed);
    File.WriteAllText(Path.Combine(unclaimed, "source.xlsx"), "must not be claimed");
    try
    {
        ClientBranchAutoImportLayout.Ensure(tempBranchesRoot, plain.Id,
            [new ClientBranchRecord { BranchId = "B-test-unclaimed", Name = "Unclaimed" }]);
        throw new InvalidOperationException("Claimed an existing user branch folder without a marker.");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Unowned branch", StringComparison.OrdinalIgnoreCase)) { }
    Check(File.Exists(Path.Combine(unclaimed, "source.xlsx")), "Unclaimed folder contents were modified.");

    // Upgrade an existing v536-v538 nested branch without deleting any
    // user files, losing the marker, or claiming unrelated folders.
    var migrate = Path.Combine(tempBranchesRoot, "LegacyMigration");
    Directory.CreateDirectory(migrate);
    var legacyContainer = Path.Combine(migrate, "Branches");
    var oldBranch = Path.Combine(legacyContainer, "Old Branch");
    Directory.CreateDirectory(oldBranch);
    var migrateBranch = stableIds[0];
    File.WriteAllText(Path.Combine(oldBranch, ClientBranchAutoImportLayout.Marker),
        System.Text.Json.JsonSerializer.Serialize(new
        { ClientId = plain.Id, BranchId = migrateBranch.BranchId, Gstin = migrateBranch.Gstin }));
    File.WriteAllText(Path.Combine(oldBranch, "purchase.xlsx"), "KEEP");
    var moved = ClientBranchAutoImportLayout.Ensure(migrate, plain.Id,
        [new ClientBranchRecord { BranchId = migrateBranch.BranchId,
            Name = migrateBranch.Name, Gstin = migrateBranch.Gstin }]);
    Check(moved.Count == 1 &&
          Path.GetDirectoryName(moved[0].FolderPath)!.Equals(migrate, StringComparison.OrdinalIgnoreCase) &&
          File.Exists(Path.Combine(moved[0].FolderPath, "purchase.xlsx")) &&
          ClientBranchAutoImportLayout.TryResolve(migrate,
              Path.Combine(moved[0].FolderPath, "purchase.xlsx"), out var upgraded) &&
          upgraded!.BranchId == migrateBranch.BranchId,
          "Legacy marked branch folder was not moved safely to the XYZ root.");
    if (OperatingSystem.IsWindows())
        Check((File.GetAttributes(Path.Combine(moved[0].FolderPath,
              ClientBranchAutoImportLayout.Marker)) &
              (FileAttributes.Hidden | FileAttributes.System)) ==
              (FileAttributes.Hidden | FileAttributes.System),
              "Existing branch metadata must be hidden after safe folder migration.");
}
finally { try { Directory.Delete(tempBranchesRoot, recursive: true); } catch { } }

Reject("South | 27ABCDE1234F1Z5\nNorth | 27ABCDE1234F1Z5", "Duplicate branch GSTIN");
Reject("South\nsouth", "Duplicate branch name");
Reject("South | 29NOTGSTIN", "Invalid branch GSTIN");

var wrongPan = ClientBranchCatalog.Parse("Unrelated | 27PQRST1234A1Z5");
try
{
    ClientBranchCatalog.AdditionalGstins(wrongPan, plain.Gstin, plain.Pan);
    throw new InvalidOperationException("Different PAN accepted.");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("does not belong", StringComparison.Ordinal)) { }

DirectoryInfo? sourceRoot = new DirectoryInfo(AppContext.BaseDirectory);
while (sourceRoot != null && !Directory.Exists(Path.Combine(sourceRoot.FullName, "AAH", "src")))
    sourceRoot = sourceRoot.Parent;
if (sourceRoot is null) throw new InvalidOperationException("Repository root not found.");
var viewModel = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src", "AccountingAutomationHub.Desktop", "ViewModels", "ClientManagementViewModel.cs"));
var clientView = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src", "AccountingAutomationHub.Desktop", "Views", "ClientManagementView.xaml"));
var dashboard = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src", "AccountingAutomationHub.Desktop", "ViewModels", "DashboardViewModel.cs"));
var dashboardView = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src", "AccountingAutomationHub.Desktop", "Views", "DashboardView.xaml"));
var automationSource = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src", "AccountingAutomationHub.Desktop", "Services", "BackgroundAutomationService.cs"));
Check(viewModel.Contains("ClientBranchCatalog.Parse(BranchEntriesText)", StringComparison.Ordinal) &&
      viewModel.Contains("ClientBranchCatalog.Key(saved.Id)", StringComparison.Ordinal) &&
      clientView.Contains("Text=\"{Binding BranchEntriesText, UpdateSourceTrigger=PropertyChanged}\"", StringComparison.Ordinal) &&
      dashboard.Contains("ClientBranches = ClientBranchCatalog.Resolve(client", StringComparison.Ordinal) &&
      dashboardView.Contains("ItemsSource=\"{Binding ClientBranches}\"", StringComparison.Ordinal) &&
      dashboardView.Contains("Grid.Column=\"0\" Width=\"226\"", StringComparison.Ordinal),
      "Branch model missing editor persistence or dashboard left-side display.");
Check(automationSource.Contains("needsBranchReconciliation", StringComparison.Ordinal) &&
      automationSource.Contains("!needsBranchReconciliation &&", StringComparison.Ordinal),
      "A legacy parent handled-set marker must not skip the startup branch source scan.");
Check(automationSource.Contains("BRANCH-SALES-PURCHASE-PENDING", StringComparison.Ordinal) &&
      automationSource.Contains("QueueFile(targetFolder.FolderId, path", StringComparison.Ordinal) &&
      automationSource.Contains("QueueFile(privateFolder.FolderId, path", StringComparison.Ordinal) &&
      automationSource.Contains("sourceKind is not (AutomationFileKind.Sales or AutomationFileKind.Purchase)", StringComparison.Ordinal),
      "Source-proven Sales/Purchase branch routing or its Bank/Cash ownership safeguard is missing.");
Check(automationSource.Contains("EnsureBranchFoldersForClient(saved.Id", StringComparison.Ordinal) == false &&
      automationSource.Contains("ClientBranchAutoImportLayout.Ensure(", StringComparison.Ordinal) &&
      automationSource.Contains("IsWithinBranchContainer(folder.FolderPath, path)", StringComparison.Ordinal) &&
      automationSource.Contains("BRANCH-IMPORT-SCOPE-PENDING", StringComparison.Ordinal) &&
      automationSource.Contains("BRANCH-CENTRAL-SOURCE-DEFER", StringComparison.Ordinal) &&
      automationSource.Contains("TryProcessBankSpreadsheetCacheFirstAsync", StringComparison.Ordinal),
      "Unsafe branch files might be posted through parent generic, bank cache-first or central source import.");
Check(viewModel.Contains("EnsureBranchFoldersForClient(saved.Id, branches)", StringComparison.Ordinal),
      "Saving a branch did not trigger immediate auto-import folder layout.");
var shellView = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src", "AccountingAutomationHub.Desktop", "MainWindow.xaml"));
var shellModel = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src", "AccountingAutomationHub.Desktop", "ViewModels", "MainWindowViewModel.cs"));
Check(shellView.Contains("ItemsSource=\"{Binding SidebarBranchWorkspaces}\"", StringComparison.Ordinal) &&
      shellView.Contains("SelectedItem=\"{Binding SelectedSidebarBranchWorkspace, Mode=TwoWay}\"", StringComparison.Ordinal) &&
      shellView.Contains("Visibility=\"{Binding HasConfiguredBranchWorkspaces", StringComparison.Ordinal) &&
      !shellView.Contains("ComboBox ItemsSource=\"{Binding BranchWorkspaces}\"", StringComparison.Ordinal) &&
      shellModel.Contains("SidebarBranchWorkspaces.Add(scope)", StringComparison.Ordinal) &&
      shellModel.Contains("BranchAccountingWorkspaceCatalog.Build(parent, configuredBranches)", StringComparison.Ordinal) &&
      shellModel.Contains("var branches = separateBooksEnabled", StringComparison.Ordinal) &&
      shellView.Contains("IsEnabled=\"{Binding HasBranchWorkspaces}\"", StringComparison.Ordinal),
      "Branch hierarchy is not rendered in the left sidebar or posting safety was bypassed.");
Check(shellView.Contains("""<Setter Property="ToolTip" Value="{Binding DisplayName}"/>""", StringComparison.Ordinal),
    "Branch hover must display the friendly branch name, not the internal workspace record.");
var consolidatedView = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
    "AccountingAutomationHub.Desktop", "Views", "ConsolidatedBranchDashboardView.xaml"));
Check(shellModel.Contains("available.Add(_dashboardNavigationItem);", StringComparison.Ordinal) &&
      !shellModel.Contains("\"Dashboard\", \"Separate branch accounting overview\"", StringComparison.Ordinal) &&
      shellView.Contains("Text=\"{Binding WorkspacePageTitle}\"", StringComparison.Ordinal) &&
      consolidatedView.Contains("Text=\"Client Identity\"", StringComparison.Ordinal) &&
      consolidatedView.Contains("Text=\"Accounting Activity\"", StringComparison.Ordinal) &&
      consolidatedView.Contains("Text=\"Auto Import Activity\"", StringComparison.Ordinal) &&
      consolidatedView.Contains("Style=\"{StaticResource CardBorderStyle}\"", StringComparison.Ordinal),
    "Branch must use native Dashboard and Consolidated must keep the original dashboard design.");
var dashboardNative = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
    "AccountingAutomationHub.Desktop", "ViewModels", "DashboardViewModel.cs"));
var tallyCoordinator = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
    "AccountingAutomationHub.Desktop", "Services", "TallyLiveConnectionCoordinator.cs"));
Check(dashboardNative.Contains("public bool ShowTallyPanel => true;", StringComparison.Ordinal) &&
      dashboardNative.Contains("var link = _tallyCoordinator.GetCurrentClientLink();", StringComparison.Ordinal) &&
      dashboardNative.Contains("HasConfirmedTallyLink = link is not null;", StringComparison.Ordinal) &&
      dashboardNative.Contains("CanConfirmTallyLink = pending &&", StringComparison.Ordinal) &&
      dashboardNative.Contains("CanUnlinkTallyLink = link is not null", StringComparison.Ordinal) &&
      dashboardNative.Contains("if (_isConsolidatedTallyScope) return false;", StringComparison.Ordinal) &&
      dashboardNative.Contains("_isConsolidatedTallyScope || TallyAutoSyncMode.Equals", StringComparison.Ordinal) &&
      dashboardNative.Contains("if (_isConsolidatedTallyScope) return;", StringComparison.Ordinal) &&
      shellModel.Contains("_dashboardViewModel.SetConsolidatedTallyScope(", StringComparison.Ordinal) &&
      !dashboardNative.Contains("GetLinkForClient(branchOwner.ParentClientId)", StringComparison.Ordinal) &&
      !dashboardNative.Contains("if (_isPrivateBranchDashboard) return false;", StringComparison.Ordinal) &&
      !dashboardNative.Contains("if (_isPrivateBranchDashboard) return;", StringComparison.Ordinal) &&
      !dashboardNative.Contains("if (_isPrivateBranchDashboard) normalized = \"Off\";", StringComparison.Ordinal) &&
      dashboardView.Contains("Visibility=\"{Binding ShowTallyPanel", StringComparison.Ordinal) &&
      shellModel.Contains("_dashboardViewModel.SetPrivateBranchDashboard(_isPrivateBranchActive)", StringComparison.Ordinal),
    "Every private branch must use its own native dashboard Tally link/export/autosync.");
Check(automationSource.Contains("RebaseAutomationFolderPaths([", StringComparison.Ordinal) &&
      automationSource.Contains("ClientBranchAutoImportLayout.IsWithinBranchPath", StringComparison.Ordinal) &&
      automationSource.Contains("IsWithinBranchContainer(f.FolderPath, path)", StringComparison.Ordinal),
    "Flat branch import ownership/history or parent watcher exclusion is missing.");
Check(shellModel.Contains("new ConsolidatedBranchDashboardViewModel(client, _dashboardViewModel)", StringComparison.Ordinal) &&
      shellModel.Contains("Consolidated · existing Dashboard Tally link and export design", StringComparison.Ordinal) &&
      shellModel.Contains("OpenLinkedCompanyAsync(linkClient, link, _tallyCoordinator)", StringComparison.Ordinal) &&
      automationSource.Contains("SqliteGstBranchOwnershipMigration", StringComparison.Ordinal) &&
      automationSource.Contains("RelocateOwnedGstEvidence(", StringComparison.Ordinal),
      "Original Tally link or GSTIN ownership migration was disconnected.");
var dashboardTallyViewCode = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
    "AccountingAutomationHub.Desktop", "Views", "DashboardView.xaml.cs"));
var consolidatedDashboardModel = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
    "AccountingAutomationHub.Desktop", "ViewModels", "ConsolidatedBranchDashboardViewModel.cs"));
Check(consolidatedView.Contains("TallyOnlyMode=\"True\"", StringComparison.Ordinal) &&
      consolidatedView.Contains("ExistingDashboardWorkspace", StringComparison.Ordinal) &&
      dashboardView.Contains("x:Name=\"DashboardTallyPanel\"", StringComparison.Ordinal) &&
      dashboardView.Contains("Command=\"{Binding ConfirmTallyLinkCommand}\"", StringComparison.Ordinal) &&
      dashboardView.Contains("Command=\"{Binding ExportSelectedToTallyCommand}\"", StringComparison.Ordinal) &&
      dashboardTallyViewCode.Contains("if (TallyOnlyMode)", StringComparison.Ordinal) &&
      consolidatedDashboardModel.Contains("public DashboardViewModel ExistingDashboardWorkspace", StringComparison.Ordinal) &&
      !consolidatedDashboardModel.Contains("TallyConnectionText", StringComparison.Ordinal) &&
      !consolidatedView.Contains("ExistingTallyWorkspace", StringComparison.Ordinal) &&
      shellView.Contains("Content=\"{Binding TallyLinkActionLabel}\"", StringComparison.Ordinal) &&
      shellModel.Contains("public string TallyLinkActionLabel", StringComparison.Ordinal) &&
      tallyCoordinator.Contains("_repository.GetLinkForClient(client.Id)", StringComparison.Ordinal) &&
      tallyCoordinator.Contains("ValidateClientCompanyIdentity(client, company)", StringComparison.Ordinal) &&
      shellModel.Contains("var linkClient = client;", StringComparison.Ordinal) &&
      shellModel.Contains("_tallyRepository.GetLinkForClient(current.Id)", StringComparison.Ordinal) &&
      shellModel.Contains("ConfirmTopTallyLinkAsync", StringComparison.Ordinal) &&
      !shellModel.Contains("GetLinkForClient(_privateBranchParentClientId)", StringComparison.Ordinal) &&
      !shellModel.Contains("Branch Tally linking is disabled", StringComparison.Ordinal) &&
      !shellModel.Contains("if (!_isPrivateBranchActive && !NetworkClientRuntime.IsNetworkClient && CanViewModule(GovernancePermissions.TallyView))", StringComparison.Ordinal),
      "Consolidated and each branch must have an independent current-Client/FY Tally link and native export path.");
// A heartbeat must never clear/re-add the same Tally companies, emit
// a fake selection, requeue Live XML, or reconstruct the same dashboard.
var tallyApplyMethod = tallyCoordinator.Split("private void ApplyCompanyList(", StringSplitOptions.None)[1]
    .Split("private void EnrichCompanyLinks(", StringSplitOptions.None)[0];
var tallySelectionMethod = tallyCoordinator.Split("private void SetSelectionFromRememberedLink(", StringSplitOptions.None)[1]
    .Split("private void OnAccountingRefreshed(", StringSplitOptions.None)[0];
Check(tallyApplyMethod.Contains("OpenCompanies.Count == refreshed.Length", StringComparison.Ordinal) &&
      tallyApplyMethod.Contains("current.DisplayText.Equals(refreshed[i].DisplayText", StringComparison.Ordinal) &&
      tallyApplyMethod.IndexOf("OpenCompanies.Count == refreshed.Length", StringComparison.Ordinal) <
      tallyApplyMethod.IndexOf("OpenCompanies.Clear();", StringComparison.Ordinal) &&
      tallySelectionMethod.Contains("ReferenceEquals(_selectedCompany, company) && !_manualSelectionPending", StringComparison.Ordinal) &&
      dashboardNative.Contains("if (TallyMetrics.SequenceEqual(updated)) return;", StringComparison.Ordinal) &&
      shellModel.Contains("_consolidatedDashboardPage = new ConsolidatedBranchDashboardViewModel(client, _dashboardViewModel)", StringComparison.Ordinal) &&
       shellModel.Contains("ReferenceEquals(CurrentPage, expected.PageFactory())", StringComparison.Ordinal) &&
      consolidatedDashboardModel.Contains("public long ParentClientId => _parentClientId;", StringComparison.Ordinal) &&
      consolidatedDashboardModel.Contains("public void RefreshAfterCommittedAccountingChange()", StringComparison.Ordinal) &&
      shellModel.Contains("consolidated.RefreshAfterCommittedAccountingChange();", StringComparison.Ordinal) &&
      shellModel.Contains("_privateBranchAccounts.FindByChild(change.ClientId)?.ParentClientId", StringComparison.Ordinal) &&
      shellModel.Contains("if ((change.Area is AccountingLiveArea.Banking or AccountingLiveArea.Cash or", StringComparison.Ordinal) &&
      !shellModel.Contains("RefreshAfterCommittedAccountingChange();\n        // Fix118", StringComparison.Ordinal),
    "Unchanged Tally pulses must not reconstruct company selection, metric cards or Consolidated Dashboard.");
var tallyPageModel = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
    "AccountingAutomationHub.Desktop", "ViewModels", "ConsolidatedTallyImportViewModel.cs"));
var tallyPageView = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
    "AccountingAutomationHub.Desktop", "Views", "ConsolidatedTallyImportView.xaml"));
var appTemplates = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
    "AccountingAutomationHub.Desktop", "App.xaml"));
Check(shellModel.Contains("new ConsolidatedTallyImportViewModel(", StringComparison.Ordinal) &&
       shellModel.Contains("client.Id, client.FinancialYear, _dashboardViewModel,", StringComparison.Ordinal) &&
       shellModel.Contains("ImportConsolidatedMastersAsync(client.Id, client.FinancialYear, types, cash)", StringComparison.Ordinal) &&
       shellModel.Contains("GenerateConsolidatedPreparedXmlAsync(client.Id, client.FinancialYear, types, cash)", StringComparison.Ordinal) &&
      shellModel.Contains("new NavigationItem(\"Tally Import\"", StringComparison.Ordinal) &&
      shellModel.Contains("() => _consolidatedTallyImportPage!", StringComparison.Ordinal) &&
      shellModel.Contains("if (NavigationItems.Count == choices.Count", StringComparison.Ordinal) &&
      tallyPageModel.Contains("public DashboardViewModel ExistingDashboardWorkspace", StringComparison.Ordinal) &&
      tallyPageModel.Contains("public long ParentClientId", StringComparison.Ordinal) &&
      tallyPageView.Contains("TallyOnlyMode=\"True\"", StringComparison.Ordinal) &&
      tallyPageView.Contains("DataContext=\"{Binding ExistingDashboardWorkspace}\"", StringComparison.Ordinal) &&
      consolidatedView.Contains("TallyOnlyMode=\"True\"", StringComparison.Ordinal) &&
      appTemplates.Contains("DataType=\"{x:Type viewModels:ConsolidatedTallyImportViewModel}\"", StringComparison.Ordinal) &&
      !tallyPageView.Contains("TallyClientView", StringComparison.Ordinal),
    "Separate Consolidated Tally Import module must reuse the current Dashboard Tally design, not the recovery page.");
Console.WriteLine("PASS: original Consolidated Dashboard Tally card preserved and separate Tally Import module shares its UI and coordinator.");
Console.WriteLine("PASS: Tally heartbeat UI stability, stable dropdown selection and consolidated dashboard instance.");
// Run the ACTUAL production wheel calculation through .NET on all boundaries.
Check(ConsolidatedDashboardWheelRouting.CanScrollNested(45, 200, +120) &&
      ConsolidatedDashboardWheelRouting.CanScrollNested(45, 200, -120) &&
      !ConsolidatedDashboardWheelRouting.CanScrollNested(0, 200, +120) &&
      !ConsolidatedDashboardWheelRouting.CanScrollNested(200, 200, -120) &&
      !ConsolidatedDashboardWheelRouting.CanScrollNested(0, 0, -120),
    "Nested grids must scroll locally except at their top/bottom boundary.");
Check(ConsolidatedDashboardWheelRouting.OuterTarget(200, 900, 450, -120, 3) == 248 &&
      ConsolidatedDashboardWheelRouting.OuterTarget(200, 900, 450, +120, 3) == 152 &&
      ConsolidatedDashboardWheelRouting.OuterTarget(5, 900, 450, +120, 3) == 0 &&
      ConsolidatedDashboardWheelRouting.OuterTarget(895, 900, 450, -120, 3) == 900 &&
      ConsolidatedDashboardWheelRouting.OuterTarget(200, 900, 450, -120, 0) == 200 &&
      ConsolidatedDashboardWheelRouting.OuterTarget(200, 900, 450, -120, -1) == 650,
    "Consolidated wheel distance, Windows settings and viewport boundaries regressed.");
var consolidatedViewCode = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
    "AccountingAutomationHub.Desktop", "Views", "ConsolidatedBranchDashboardView.xaml.cs"));
Check(consolidatedView.Contains("PreviewMouseWheel=\"ConsolidatedScrollViewer_PreviewMouseWheel\"", StringComparison.Ordinal) &&
      consolidatedView.Contains("x:Name=\"ConsolidatedScrollViewer\"", StringComparison.Ordinal) &&
      consolidatedView.Contains("TallyOnlyMode=\"True\"", StringComparison.Ordinal) &&
      consolidatedViewCode.Contains("ConsolidatedDashboardWheelRouting.CanScrollNested(", StringComparison.Ordinal) &&
      consolidatedViewCode.Contains("ConsolidatedDashboardWheelRouting.OuterTarget(", StringComparison.Ordinal) &&
      consolidatedViewCode.Contains("VisualTreeHelper.GetParent(current)", StringComparison.Ordinal) &&
      consolidatedViewCode.Contains("outer.ScrollToVerticalOffset(target);", StringComparison.Ordinal) &&
      consolidatedViewCode.Contains("if (canScrollHere) return;", StringComparison.Ordinal) &&
      consolidatedViewCode.Contains("e.Handled = true;", StringComparison.Ordinal) &&
      !dashboardTallyViewCode.Contains("ConsolidatedScrollViewer_PreviewMouseWheel", StringComparison.Ordinal),
    "Consolidated must route nested mouse wheel to the outer page while preserving inner grids and normal Dashboard scrolling.");
// Shared Fix134/127/128 framework: committed module snapshots may refresh
// an active projection, but the same navigation MUST NEVER be destroyed/recreated.
var navigationRebuild = shellModel.Split("private void RebuildWorkspaceNavigation(", StringSplitOptions.None)[1]
    .Split("private bool CanViewModule(", StringSplitOptions.None)[0];
var moduleReadyHandler = shellModel.Split("private void OnWorkspaceModuleReady(", StringSplitOptions.None)[1]
    .Split("private void QueueAllSafeModulePresentations(", StringSplitOptions.None)[0];
Check(navigationRebuild.Contains("NavigationItems.SequenceEqual(available)", StringComparison.Ordinal) &&
      navigationRebuild.Contains("activeItem.Title.Equals(previousTitle", StringComparison.Ordinal) &&
      navigationRebuild.Contains("ReferenceEquals(CurrentPage, _selectedNavigationPage)", StringComparison.Ordinal) &&
      shellModel.Contains("_selectedNavigationPage = nextPage;", StringComparison.Ordinal) &&
      navigationRebuild.IndexOf("NavigationItems.SequenceEqual(available)", StringComparison.Ordinal) <
      navigationRebuild.LastIndexOf("NavigationItems.Clear();", StringComparison.Ordinal) &&
      !navigationRebuild.Contains("activeItem.PageFactory()", StringComparison.Ordinal) &&
      moduleReadyHandler.Contains("QueueHiddenModulePresentationWarm(update.Snapshot, update);", StringComparison.Ordinal) &&
      moduleReadyHandler.Contains("(update.Module is ClientWorkspaceModule.Purchase or", StringComparison.Ordinal) &&
      moduleReadyHandler.Contains("ClientWorkspaceModule.Actions))", StringComparison.Ordinal),
    "A module cache completion must not destroy the existing WPF navigation frame or trigger a page factory unless the menu changes.");
Check(dashboardNative.Contains("UpdateAccountingMetricsIfChanged(cached.DashboardAccountingMetrics);", StringComparison.Ordinal) &&
      dashboardNative.Contains("UpdateGstr3bTilesIfChanged(tiles);", StringComparison.Ordinal) &&
      dashboardNative.Contains("AccountingSummaryMetrics.Count == updated.Count", StringComparison.Ordinal) &&
      dashboardNative.Contains("Gstr3bMonthTiles.Count == updated.Count", StringComparison.Ordinal) &&
      dashboardNative.Contains("AutoImportSummaryMetrics.Count == updated.Count", StringComparison.Ordinal) &&
      dashboardNative.Contains("UpdateAutoImportMetricsIfChanged(newMetrics);", StringComparison.Ordinal) &&
      !dashboardNative.Contains("        AccountingSummaryMetrics.Clear();\n        Gstr3bMonthTiles.Clear();\n\n        if (!_workspaceStateCache.TryPeekReadOnly", StringComparison.Ordinal),
    "Dashboard must not clear/rebuild the same accounting cards and GST tiles on each prepared-cache notification.");
Console.WriteLine("PASS: shared module framework reuses navigation, active WPF frame and unchanged dashboard projections.");
Console.WriteLine("PASS: consolidated wheel boundaries, nested Tally panel, branch DataGrid and single outer scroll authority.");
Console.WriteLine("PASS: independent branch/Consolidated Tally links with native dashboard/export controls.");
Console.WriteLine("PASS: consolidated link remains usable; ordinary parent voucher export is blocked until group XML routing is verified.");
Console.WriteLine("PASS: native branch dashboards, original consolidated presentation, and flat branch folder migration.");
Console.WriteLine("PASS: branch tree in the left sidebar, opt-in private books, no unscoped parent accounting writes.");
Console.WriteLine("PASS: original Client/FY unchanged; state-derived branch names, durable identities, flat branch roots, owner markers, path lookup, and no unscoped import.");
