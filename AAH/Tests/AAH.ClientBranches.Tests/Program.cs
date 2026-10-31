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
Check(dashboardNative.Contains("public bool ShowTallyPanel => true;", StringComparison.Ordinal) &&
      dashboardNative.Contains("GetLinkForClient(branchOwner.ParentClientId)", StringComparison.Ordinal) &&
      dashboardNative.Contains("HasConfirmedTallyLink = !_isPrivateBranchDashboard", StringComparison.Ordinal) &&
      dashboardNative.Contains("if (_isPrivateBranchDashboard) return false;", StringComparison.Ordinal) &&
      dashboardView.Contains("Visibility=\"{Binding ShowTallyPanel", StringComparison.Ordinal) &&
      shellModel.Contains("_dashboardViewModel.SetPrivateBranchDashboard(_isPrivateBranchActive)", StringComparison.Ordinal),
    "Native branch Dashboard must not enable Tally export/autosync before verified branch-company authority.");
Check(automationSource.Contains("RebaseAutomationFolderPaths([", StringComparison.Ordinal) &&
      automationSource.Contains("ClientBranchAutoImportLayout.IsWithinBranchPath", StringComparison.Ordinal) &&
      automationSource.Contains("IsWithinBranchContainer(f.FolderPath, path)", StringComparison.Ordinal),
    "Flat branch import ownership/history or parent watcher exclusion is missing.");
Console.WriteLine("PASS: native branch dashboards, original consolidated presentation, and flat branch folder migration.");
Console.WriteLine("PASS: branch tree in the left sidebar, opt-in private books, no unscoped parent accounting writes.");
Console.WriteLine("PASS: original Client/FY unchanged; state-derived branch names, durable identities, flat branch roots, owner markers, path lookup, and no unscoped import.");
