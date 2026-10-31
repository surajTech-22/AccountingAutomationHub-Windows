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

Console.WriteLine("PASS: explicit company groups, nested assignments, same-PAN branch accounting, different-PAN owner groups, FY separation, missing/conflicting PAN review, cash/interbank contra plans, preserved client records and safe branch-import gates.");
