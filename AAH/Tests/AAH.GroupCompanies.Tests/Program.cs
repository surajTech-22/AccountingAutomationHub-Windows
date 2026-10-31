using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Clients;

static void Require(bool value, string message)
{
    if (!value) throw new InvalidOperationException("GROUP COMPANIES REGRESSION: " + message);
}
static ClientProfile Client(long id, string name, string fy, string pan, string gstin = "") => new()
{
    Id = id, ClientCode = "CL" + id, ClientName = name,
    FinancialYear = fy, Pan = pan, Gstin = gstin, AdditionalGstins = []
};

var catalog = new ClientGroupCatalogData
{
    Folders =
    [
        new ClientGroupFolderRecord { Id="owner", Type=ClientGroupFolderTypes.CompanyGroup, Name="Owner Group" },
        new ClientGroupFolderRecord { Id="branch-accounts", Type=ClientGroupFolderTypes.Company, Name="Division Accounts", ParentId="owner" },
        new ClientGroupFolderRecord { Id="ca", Type=ClientGroupFolderTypes.Ca, Name="Other CA" }
    ],
    Assignments =
    [
        new ClientGroupAssignmentRecord { ClientId=10, FolderId="owner" },
        new ClientGroupAssignmentRecord { ClientId=11, FolderId="branch-accounts" },
        new ClientGroupAssignmentRecord { ClientId=12, FolderId="owner" },
        new ClientGroupAssignmentRecord { ClientId=13, FolderId="owner" },
        new ClientGroupAssignmentRecord { ClientId=14, FolderId="owner" },
        new ClientGroupAssignmentRecord { ClientId=15, FolderId="ca" }
    ]
};
var clients = new[]
{
    Client(10, "Karnataka Office", "2026-27", "ABCDE1234F", "29ABCDE1234F1Z5"),
    Client(11, "Maharashtra Office", "2026-27", "ABCDE1234F", "27ABCDE1234F1Z5"),
    Client(12, "Other PAN proprietor firm", "2026-27", "PQRST1234A", "27PQRST1234A1Z5"),
    Client(13, "Unknown PAN company", "2026-27", ""),
    Client(14, "Previous-year account", "2025-26", "ABCDE1234F", "29ABCDE1234F1Z5"),
    Client(15, "Unrelated CA client", "2026-27", "ABCDE1234F")
};
var before = clients.Select(x => x.Clone()).ToArray();
var snapshot = GroupCompanyAccountingPolicy.Build(catalog, "owner", clients);
Require(snapshot.Members.Count == 5, "nested group membership missing, or CA client included");
Require(snapshot.VerifiedPanCount == 2, "same-PAN branches must not count as different owners");
Require(snapshot.Members.Single(x => x.ClientId == 10).Relationship == "Same-PAN branch accounts",
    "Karnataka branch not recognized");
Require(snapshot.Members.Single(x => x.ClientId == 11).Relationship == "Same-PAN branch accounts",
    "nested Maharashtra branch not recognized");
Require(snapshot.Members.Single(x => x.ClientId == 12).Relationship == "Separate company / owner",
    "different PAN should stay a separate company under same owner");
Require(snapshot.Members.Single(x => x.ClientId == 13).Pan == "REVIEW" &&
        snapshot.Warnings.Count == 1,
    "unknown PAN must be flagged, never grouped by assumption");
Require(snapshot.Members.Single(x => x.ClientId == 14).Relationship == "Separate company / owner",
    "previous financial year must not merge with current-year branch books");
Require(clients.Zip(before).All(t => t.First.Pan == t.Second.Pan &&
    t.First.Id == t.Second.Id && t.First.Gstin == t.Second.Gstin),
    "snapshot must not modify any source client records");

var ka = snapshot.Members.Single(x => x.ClientId == 10);
var ma = snapshot.Members.Single(x => x.ClientId == 11);
var other = snapshot.Members.Single(x => x.ClientId == 12);
var invalid = snapshot.Members.Single(x => x.ClientId == 13);
var cash = GroupCompanyAccountingPolicy.PlanTransfer(ka, ma, GroupTransferKind.CashContra, true);
Require(cash.IsPolicyAllowed && cash.VoucherType == "Branch-wise Contra" &&
        cash.Detail.Contains("Separate cash ledger", StringComparison.Ordinal),
    "branch-wise cash contra must permit separate cash ledger option");
var cashShared = GroupCompanyAccountingPolicy.PlanTransfer(ka, ma, GroupTransferKind.CashContra, false);
Require(cashShared.IsPolicyAllowed && cashShared.Detail.Contains("Shared cash ledger", StringComparison.Ordinal),
    "shared cash ledger option missing");
var bank = GroupCompanyAccountingPolicy.PlanTransfer(ka, ma, GroupTransferKind.InterbankContra, true);
Require(bank.IsPolicyAllowed && bank.VoucherType == "Common Contra",
    "same PAN interbank transfer must use common Contra voucher type");
Require(!GroupCompanyAccountingPolicy.PlanTransfer(ka, other, GroupTransferKind.InterbankContra, false).IsPolicyAllowed,
    "different PAN was wrongly treated as common contra");
Require(!GroupCompanyAccountingPolicy.PlanTransfer(ka, other, GroupTransferKind.CashContra, true).IsPolicyAllowed,
    "different PAN was wrongly treated as branch cash contra");
Require(!GroupCompanyAccountingPolicy.PlanTransfer(ka, other, GroupTransferKind.InterCompany, true).IsPolicyAllowed,
    "intercompany postings must require verified two-sided Tally mapping");
Require(!GroupCompanyAccountingPolicy.PlanTransfer(ka, invalid, GroupTransferKind.CashContra, true).IsPolicyAllowed,
    "invalid PAN was allowed in branch contra");
Require(!GroupCompanyAccountingPolicy.PlanTransfer(ka, snapshot.Members.Single(x => x.ClientId == 14),
    GroupTransferKind.CashContra, true).IsPolicyAllowed, "cross-financial-year contra was permitted");

try
{
    GroupCompanyAccountingPolicy.Build(catalog, "ca", clients);
    throw new InvalidOperationException("Ordinary CA group activated company accounting");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("only for an explicit Company Group", StringComparison.Ordinal)) { }

var conflict = Client(22, "Conflicting PAN", "2026-27", "ABCDE1234F", "27PQRST1234A1Z5");
catalog.Assignments.Add(new ClientGroupAssignmentRecord { ClientId = 22, FolderId = "owner" });
var conflictResult = GroupCompanyAccountingPolicy.Build(catalog, "owner", clients.Append(conflict));
Require(conflictResult.Members.Single(x => x.ClientId == 22).Pan == "REVIEW",
    "contradictory PAN and GSTIN must be quarantined from owner classification");


var unbranched = Client(90, "Normal Client", "2026-27", "ABCDE1234F", "29ABCDE1234F1Z5");
var normalWorkspace = BranchAccountingWorkspaceCatalog.Build(unbranched, []);
Require(normalWorkspace.Count == 1 && normalWorkspace[0].ScopeId == "NORMAL" &&
        normalWorkspace[0].AllowedModules.Contains("Purchase") &&
        normalWorkspace[0].CanUseExistingClientImporter,
    "no-branch client must retain existing full accounting workflow");
normalWorkspace[0].RequireVerifiedImporter();

unbranched.AdditionalGstins = ["27ABCDE1234F1Z5"];
var registrations = ClientBranchCatalog.Resolve(unbranched, "");
Require(registrations.Count == 2 &&
        registrations.Any(x => x.Name == "Karnataka" && x.Gstin == unbranched.Gstin) &&
        registrations.Any(x => x.Name == "Maharashtra" && x.Gstin == "27ABCDE1234F1Z5"),
    "unnamed GSTIN branches must use state registration names, including primary GSTIN");
var workspaces = BranchAccountingWorkspaceCatalog.Build(unbranched, registrations);
Require(workspaces.Count == 3 && workspaces[0].IsConsolidated &&
        workspaces[0].AllowedModules.SequenceEqual(["Dashboard", "Tally Import"]) &&
        workspaces.Skip(1).All(x => !x.IsConsolidated &&
            x.AllowedModules.Contains("Purchase") && x.AllowedModules.Contains("Journal") &&
            x.AllowedModules.Contains("Ledger Master") && x.AllowedModules.Contains("GST")),
    "parent Consolidated must have Dashboard+Tally only; branches need their full independent module menus");
Require(workspaces.Skip(1).All(x => x.ClientId == unbranched.Id &&
        x.StorageScopeKey.Contains(x.ScopeId, StringComparison.Ordinal) &&
        x.ScopeId != "CONSOLIDATED"),
    "branch workspaces must retain parent Client ID and distinct durable scope IDs");
Require(workspaces.All(x => !x.CanUseExistingClientImporter),
    "branched parent or unscoped branches cannot silently route through old Client/FY importer");
foreach (var workspace in workspaces)
{
    try { workspace.RequireVerifiedImporter(); throw new Exception("Unscoped posting allowed."); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("verified scope-specific storage")) { }
}
var mismatchedGstin = registrations.Select(x => new ClientBranchRecord
{
    BranchId = x.BranchId, Name = x.Name,
    Gstin = x.Gstin == "27ABCDE1234F1Z5" ? "27PQRST1234A1Z5" : x.Gstin
}).ToArray();
try
{
    BranchAccountingWorkspaceCatalog.Build(unbranched, mismatchedGstin);
    throw new Exception("Cross-company branch PAN silently joined parent.");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("must match the individual Client")) { }
Require(unbranched.Id == 90 && unbranched.AdditionalGstins!.Count == 1,
    "workspace creation modified original Client or GST registration.");


var verifiedGroup = GroupCompanyAccountingPolicy.Build(catalog, "owner",
    clients.Where(x => x.Id != 13));
var readbackRows = new[]
{
    new VerifiedMemberLedgerBalance(10, "2026-27", "master-asset", "tally-readback-001", 100m, 0m),
    new VerifiedMemberLedgerBalance(11, "2026-27", "master-expense", "tally-readback-002", 20m, 0m),
    new VerifiedMemberLedgerBalance(12, "2026-27", "master-liability", "tally-readback-003", 0m, 120m),
    new VerifiedMemberLedgerBalance(14, "2025-26", "master-asset", "tally-readback-004", 50m, 0m)
};
var consolidated = GroupCompanyConsolidationPolicy.Consolidate(verifiedGroup, readbackRows);
Require(consolidated.Count == 2, "financial years mixed in owner summary");
var currentSummary = consolidated.Single(x => x.FinancialYear == "2026-27");
Require(currentSummary.TotalDebit == 120m && currentSummary.TotalCredit == 120m &&
        currentSummary.Members.Count == 3,
    "owner balances not sourced from all verified member books");
Require(consolidated.Single(x => x.FinancialYear == "2025-26").TotalDebit == 50m,
    "other-year entry was omitted or merged into current year");
Require(currentSummary.ConsolidationDisclaimer.Contains("elimination", StringComparison.OrdinalIgnoreCase),
    "unverified intercompany elimination might be implied");
try
{
    GroupCompanyConsolidationPolicy.Consolidate(snapshot, readbackRows);
    throw new Exception("Group summary ignored an unresolved PAN.");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("PAN/GST identity", StringComparison.Ordinal)) { }
try
{
    GroupCompanyConsolidationPolicy.Consolidate(verifiedGroup, readbackRows.Append(readbackRows[0]));
    throw new Exception("Duplicate Tally read-back was double counted.");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("Duplicate verified balance", StringComparison.Ordinal)) { }
try
{
    GroupCompanyConsolidationPolicy.Consolidate(verifiedGroup,
        readbackRows.Append(new VerifiedMemberLedgerBalance(11, "2025-26", "bad", "source", 1m, 0m)));
    throw new Exception("Wrong financial year was permitted.");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("exact financial year", StringComparison.Ordinal)) { }

DirectoryInfo? root = new DirectoryInfo(AppContext.BaseDirectory);
while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "AAH", "src")))
    root = root.Parent;
Require(root is not null, "repository root unavailable");
var app = Path.Combine(root!.FullName, "AAH", "src", "AccountingAutomationHub.Desktop");
var view = File.ReadAllText(Path.Combine(app, "Views", "ClientManagementView.xaml"));
var vm = File.ReadAllText(Path.Combine(app, "ViewModels", "ClientManagementViewModel.cs"));
var autom = File.ReadAllText(Path.Combine(app, "Services", "BackgroundAutomationService.cs"));
Require(view.Contains("Group of Companies Dashboard", StringComparison.Ordinal) &&
        view.Contains("GroupCompanyDashboardMembers", StringComparison.Ordinal) &&
        vm.Contains("GroupCompanyAccountingPolicy.Build(", StringComparison.Ordinal) &&
        vm.Contains("IsSelectedGroupCompanyFolder", StringComparison.Ordinal),
    "group dashboard button or read-only PAN assessment not wired into group management");
Require(autom.Contains("BRANCH-IMPORT-SCOPE-PENDING", StringComparison.Ordinal) &&
        autom.Contains("BRANCH-CENTRAL-SOURCE-DEFER", StringComparison.Ordinal),
    "branch files could be posted through the parent importer before scope isolation");


// Assert WPF pages parse as XML and the active shell never offers the
// legacy unscoped module menu under Consolidated/Branch selection.
var mainShellView = File.ReadAllText(Path.Combine(app, "MainWindow.xaml"));
var scopeView = File.ReadAllText(Path.Combine(app, "Views", "BranchScopePreviewView.xaml"));
var shellVm = File.ReadAllText(Path.Combine(app, "ViewModels", "MainWindowViewModel.cs"));
var applicationXaml = File.ReadAllText(Path.Combine(app, "App.xaml"));
System.Xml.Linq.XDocument.Parse(mainShellView);
System.Xml.Linq.XDocument.Parse(scopeView);
System.Xml.Linq.XDocument.Parse(applicationXaml);
Require(mainShellView.Contains("SelectedBranchWorkspace, Mode=TwoWay", StringComparison.Ordinal) &&
        scopeView.Contains("SafetyNote", StringComparison.Ordinal) &&
        applicationXaml.Contains("viewModels:BranchScopePreviewViewModel", StringComparison.Ordinal) &&
        shellVm.Contains("if (HasBranchWorkspaces) return; // never warm/display unscoped", StringComparison.Ordinal) &&
        shellVm.Contains("if (client is null || HasBranchWorkspaces) return false;", StringComparison.Ordinal) &&
        shellVm.Contains("BranchAccountingWorkspaceCatalog.Build(client, branches)", StringComparison.Ordinal),
    "Consolidated/branch menu did not block the shared parent accounting data page.");


// End-to-end SQLite ownership regression: the same bank importer still uses
// ClientId/FY, but branch bank rows never become parent or sibling rows.
// The hidden rows must not become duplicate top-level Directory/GST owners.
var privateDbFolder = Path.Combine(Path.GetTempPath(), "aah-private-branch-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(privateDbFolder);
var privateDbPath = Path.Combine(privateDbFolder, "test.db");
try
{
    var mainClients = new AccountingAutomationHub.Infrastructure.Clients.SqliteClientRepository(privateDbPath);
    mainClients.Initialize();
    var persistedParent = mainClients.Save(new ClientProfile
    {
        ClientCode = "PARENT27", ClientName = "Parent Branch Company",
        FinancialYear = "2026-27", Pan = "ABCDE1234F", Gstin = "29ABCDE1234F1Z5",
        AdditionalGstins = ["27ABCDE1234F1Z5"]
    });
    Require(persistedParent.Id > 0 && mainClients.GetAll(false).Count == 1,
        "a normal parent must remain in Client Directory before branch creation");
    var scoped = new AccountingAutomationHub.Infrastructure.Clients.SqliteInternalBranchAccountRepository(privateDbPath);
    var first = scoped.GetOrCreate(persistedParent.Id, "B-KA", "Karnataka", "29ABCDE1234F1Z5");
    var second = scoped.GetOrCreate(persistedParent.Id, "B-MH", "Maharashtra", "27ABCDE1234F1Z5");
    Require(first.Id > 0 && second.Id > 0 && first.Id != second.Id &&
        first.Id != persistedParent.Id && second.Id != persistedParent.Id,
        "branch books must have their own durable ClientId, not the parent accounting owner");
    Require(scoped.GetOrCreate(persistedParent.Id, "B-KA", "Karnataka", first.Gstin).Id == first.Id,
        "repeated branch creation must reuse the same persisted identity without duplicates");
    Require(mainClients.GetAll(false).Count == 1 && mainClients.GetAll(true).Count == 1,
        "private branch accounting IDs must not appear in Client Directory or GST source scans");
    Require(mainClients.GetById(first.Id)?.Gstin == first.Gstin &&
        mainClients.GetById(second.Id)?.Gstin == second.Gstin,
        "private branch GST registration must survive a database read-back");
    Require(scoped.FindByChild(first.Id)?.ParentClientId == persistedParent.Id &&
        scoped.FindByParent(persistedParent.Id).Count == 2,
        "parent/branch hierarchy must be durable and reversible by exact ID");
    var parentAgain = mainClients.GetById(persistedParent.Id)!;
    parentAgain.ClientName = "Parent Edited";
    parentAgain.AdditionalGstins = ["27ABCDE1234F1Z5"];
    mainClients.Save(parentAgain);
    Require(mainClients.GetAll(false).Count == 1,
        "editing a parent must not be blocked by its own hidden branch registrations");
    try { mainClients.Save(first); throw new Exception("Normal client editor changed a branch authority."); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Branch accounting Client profiles", StringComparison.Ordinal)) { }
    try { scoped.GetOrCreate(persistedParent.Id, "B-KA", "Karnataka", "27ABCDE1234F1Z5");
          throw new Exception("Existing branch changed GSTIN identity silently."); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("manual reconciliation", StringComparison.Ordinal)) { }
    try { scoped.GetOrCreate(persistedParent.Id, "B-OTHER", "Other", "29PQRST1234A1Z5");
          throw new Exception("Different-PAN GSTIN was admitted into branch authority."); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("PAN does not match", StringComparison.Ordinal)) { }

    var banking = new AccountingAutomationHub.Infrastructure.Banking.SqliteBankingRepository(
        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = privateDbPath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate
        }.ToString());
    banking.Initialize();
    using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = privateDbPath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate
        }.ToString()))
    {
        conn.Open();
        using var saveBank = conn.CreateCommand();
        saveBank.CommandText =
            "INSERT INTO BankAccounts(ClientId,FinancialYear,BankName,AccountNumber,AccountType,BranchName,CreatedUtc,UpdatedUtc) " +
            "VALUES($Client,$FY,'Test Bank','12345','Current','Karnataka',$UTC,$UTC);";
        saveBank.Parameters.AddWithValue("$Client", first.Id);
        saveBank.Parameters.AddWithValue("$FY", "2026-27");
        saveBank.Parameters.AddWithValue("$UTC", DateTime.UtcNow.ToString("O"));
        saveBank.ExecuteNonQuery();
    }
    Require(banking.GetAccounts(first.Id, "2026-27").Count == 1 &&
            banking.GetAccounts(second.Id, "2026-27").Count == 0 &&
            banking.GetAccounts(persistedParent.Id, "2026-27").Count == 0,
        "existing real bank repository mixed parent and sibling branch transactions");
}
finally
{
    try { Directory.Delete(privateDbFolder, true); } catch { }
}

Console.WriteLine("PASS: explicit company groups, nested assignments, same-PAN branch accounting, different-PAN owner groups, FY separation, missing/conflicting PAN review, cash/interbank contra plans, preserved client records and safe branch-import gates, separate normal/Consolidated/branch module scope contracts and read-back-only financial summaries.");
