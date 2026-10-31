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
    Check(trees.Count == 4, "Expected one physical branch tree per durable branch.");
    Check(trees.All(x => Directory.Exists(Path.Combine(x.FolderPath, "Banking")) &&
                         Directory.Exists(Path.Combine(x.FolderPath, "Purchase")) &&
                         Directory.Exists(Path.Combine(x.FolderPath, "Sales")) &&
                         Directory.Exists(Path.Combine(x.FolderPath, "Journal")) &&
                         Directory.Exists(Path.Combine(x.FolderPath, "Cash")) &&
                         Directory.Exists(Path.Combine(x.FolderPath, "GST"))),
        "Every branch must have Banking/Purchase/Sales/Journal/Cash/GST Auto Import folders.");
    Check(trees.All(x => x.Gstin.Length != 15 ||
          Directory.Exists(Path.Combine(x.FolderPath, "GST", x.Gstin, "GSTR-3B"))),
        "Branch-GSTIN special-folder structure missing.");
    var statement = Path.Combine(trees[0].FolderPath, "Banking", "test-bank.xlsx");
    File.WriteAllText(statement, "Synthetic branch-owned source; never imported");
    Check(ClientBranchAutoImportLayout.TryResolve(tempBranchesRoot, statement, out var found) &&
          found!.ClientId == plain.Id && found.BranchId == stableIds[0].BranchId,
        "Nested bank file did not resolve to branch ownership.");
    Check(!ClientBranchAutoImportLayout.TryResolve(tempBranchesRoot,
          Path.Combine(tempBranchesRoot, "Banking", "unscoped.xlsx"), out _),
        "Legacy client root must remain unscoped.");
    var rerun = ClientBranchAutoImportLayout.Ensure(tempBranchesRoot, plain.Id, stableIds);
    Check(rerun.Count == trees.Count && File.Exists(statement),
        "Restart-folder reconciliation deleted or moved existing branch bank source.");
    var empty = Path.Combine(Path.GetTempPath(), "AAH-NormalClientTest-" + Guid.NewGuid().ToString("N"));
    Check(ClientBranchAutoImportLayout.Ensure(empty, plain.Id, []).Count == 0 &&
          !Directory.Exists(empty), "Unbranched client must not create a Branches directory.");
    var unclaimed = Path.Combine(tempBranchesRoot, "Branches", "Unclaimed");
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
Console.WriteLine("PASS: branch tree in the left sidebar, opt-in private books, no unscoped parent accounting writes.");
Console.WriteLine("PASS: original Client/FY unchanged; state-derived branch names, durable identities, branch module folders, owner markers, path lookup, and no unscoped import.");
