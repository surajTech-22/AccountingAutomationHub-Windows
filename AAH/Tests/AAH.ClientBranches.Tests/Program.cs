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
Check(migrated.Count == 2 && migrated.All(x => x.Gstin.Length == 15),
    "Existing AdditionalGstins were not automatically represented as internal branches.");

var updated = new ClientProfile
{
    Id = plain.Id, Gstin = plain.Gstin, AdditionalGstins = ["27ABCDE1234F1Z5"],
    ClientName = plain.ClientName
};
var cleaned = ClientBranchCatalog.Resolve(updated, saved);
Check(cleaned.All(x => x.Gstin != "33ABCDE1234F2Z5"), "Deleted GSTIN still displayed after dashboard quick edit.");
Check(cleaned.Any(x => x.Name == "North Office"), "Name-only branch deleted during quick GSTIN edit.");

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
Check(viewModel.Contains("ClientBranchCatalog.Parse(BranchEntriesText)", StringComparison.Ordinal) &&
      viewModel.Contains("ClientBranchCatalog.Key(saved.Id)", StringComparison.Ordinal) &&
      clientView.Contains("Text=\"{Binding BranchEntriesText, UpdateSourceTrigger=PropertyChanged}\"", StringComparison.Ordinal) &&
      dashboard.Contains("ClientBranches = ClientBranchCatalog.Resolve(client", StringComparison.Ordinal) &&
      dashboardView.Contains("ItemsSource=\"{Binding ClientBranches}\"", StringComparison.Ordinal) &&
      dashboardView.Contains("Grid.Column=\"0\" Width=\"226\"", StringComparison.Ordinal),
      "Branch model missing editor persistence or dashboard left-side display.");
Console.WriteLine("PASS: single-GSTIN unchanged, name-only branches, named GSTIN branches, same-PAN sync, prior registrations auto-branches, quick-panel deletion, validation, editor/dashboard wiring.");
