using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Clients;

static void Check(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
}

var owner = new ClientGroupFolderRecord { Id = "owner", Name = "Synthetic Proprietor Owner", Type = ClientGroupFolderTypes.OwnerGroup };
var person = new ClientGroupFolderRecord { Id = "individual", ParentId = owner.Id, Name = "Individual Records", Type = ClientGroupFolderTypes.Individual };
var branch = new ClientGroupFolderRecord { Id = "branch", ParentId = owner.Id, Name = "Branch Accounts", Type = ClientGroupFolderTypes.CompanyGroup };
var conventional = new ClientGroupFolderRecord { Id = "auditor", Name = "Ordinary Auditor", Type = ClientGroupFolderTypes.Auditor };
var unrelatedOwner = new ClientGroupFolderRecord { Id = "owner2", Name = "Second Owner", Type = ClientGroupFolderTypes.OwnerGroup };

var grouping = new ClientGroupCatalogData
{
    Folders = [owner, person, branch, conventional, unrelatedOwner],
    Assignments =
    [
        new() { ClientId = 101, FolderId = person.Id },
        new() { ClientId = 102, FolderId = branch.Id },
        new() { ClientId = 103, FolderId = owner.Id },
        new() { ClientId = 104, FolderId = owner.Id },
        new() { ClientId = 105, FolderId = conventional.Id },
        new() { ClientId = 106, FolderId = unrelatedOwner.Id },
        new() { ClientId = 107, FolderId = owner.Id }
    ]
};

var roundTrip = ClientGroupingCatalog.Load(ClientGroupingCatalog.Save(grouping));
Check(roundTrip.Folders.Single(x => x.Id == owner.Id).Type == ClientGroupFolderTypes.OwnerGroup,
    "Owner Group lost during existing preferences JSON round-trip.");
Check(roundTrip.Folders.Single(x => x.Id == person.Id).Type == ClientGroupFolderTypes.Individual,
    "Individual record folder lost during existing preferences JSON round-trip.");
Check(roundTrip.Folders.Single(x => x.Id == conventional.Id).Type == ClientGroupFolderTypes.Auditor,
    "Ordinary auditor group unexpectedly changed.");

var clients = new[]
{
    new ClientProfile { Id = 101, ClientCode = "PERSON-A", ClientName = "Individual", Pan = "ABCDE1234F", FinancialYear = "2026-27" },
    new ClientProfile { Id = 102, ClientCode = "SHOP-A", ClientName = "Branch 1", Pan = "abcde1234f", FinancialYear = "2026-27" },
    new ClientProfile { Id = 103, ClientCode = "ENTITY-B", ClientName = "Another PAN Entity", Pan = "PQRST1234A", FinancialYear = "2026-27" },
    new ClientProfile { Id = 104, ClientCode = "NO-PAN", ClientName = "Missing PAN", Pan = "", FinancialYear = "2026-27" },
    new ClientProfile { Id = 105, ClientCode = "AUD", ClientName = "Audited Client", Pan = "ABCDE1234F", FinancialYear = "2026-27" },
    new ClientProfile { Id = 106, ClientCode = "OTHER-OWNER", ClientName = "Another owner", Pan = "ABCDE1234F", FinancialYear = "2026-27" },
    new ClientProfile { Id = 107, ClientCode = "OLD-YEAR", ClientName = "Previous FY", Pan = "ABCDE1234F", FinancialYear = "2025-26" },
};
var members = OwnerGroupAccountingPolicy.GetMembers(roundTrip, clients, "2026-27");
Check(members.Count == 5, "Unexpected owner membership count (ordinary grouping or old FY leaked).");
Check(members.All(x => x.ClientId != 105 && x.ClientId != 107),
    "Ordinary CA/Auditor groups or other FY must not be treated as current owner members.");
var byId = members.ToDictionary(x => x.ClientId);
Check(OwnerGroupAccountingPolicy.Classify(byId[101], byId[102]) == OwnerGroupRelationship.SamePanBranchAccounts,
    "Same PAN under owner group should be branch accounts.");
Check(OwnerGroupAccountingPolicy.Classify(byId[102], byId[103]) == OwnerGroupRelationship.SameOwnerDifferentPan,
    "Different PAN under explicit owner group must remain separate entities.");
Check(OwnerGroupAccountingPolicy.Classify(byId[101], byId[104]) == OwnerGroupRelationship.NeedsPanReview,
    "Missing PAN must not be guessed into branch accounts or separate PAN.");
Check(OwnerGroupAccountingPolicy.Classify(byId[101], byId[106]) == OwnerGroupRelationship.Unrelated,
    "Different owner groups must never be silently consolidated.");
Check(OwnerGroupAccountingPolicy.Classify(byId[101], byId[101]) == OwnerGroupRelationship.SameClient,
    "A client cannot be its own branch.");
Check(OwnerGroupAccountingPolicy.IsValidPan("abcde1234f") &&
    !OwnerGroupAccountingPolicy.IsValidPan("ABCDE123") &&
    !OwnerGroupAccountingPolicy.IsValidPan("29ABCDE1234F1Z5"),
    "PAN validation accepted GSTIN or rejected a valid PAN.");

var sameCompanyInterbank = OwnerGroupAccountingPolicy.ClassifyTransfer(
    "tally-company-A", "tally-company-A", "bank1", "bank2", cashInvolved: false);
Check(sameCompanyInterbank.Treatment == OwnerTransferTreatment.CommonInterbankContra &&
      !sameCompanyInterbank.UseBranchWiseCashContraVoucherType,
    "Within-company interbank transfer must use common Contra.");

var branchCash = OwnerGroupAccountingPolicy.ClassifyTransfer(
    "tally-company-A", "tally-company-A", "cash", "bank1",
    cashInvolved: true, separateCashLedgerRequested: true, branchId: "BR-01");
Check(branchCash.Treatment == OwnerTransferTreatment.CashContra &&
      branchCash.UseBranchWiseCashContraVoucherType && branchCash.UseSeparateCashLedger,
    "Optional branch-wise cash Contra and separate Cash Ledger were not retained.");

var normalCash = OwnerGroupAccountingPolicy.ClassifyTransfer(
    "tally-company-A", "tally-company-A", "cash", "bank1", cashInvolved: true);
Check(normalCash.Treatment == OwnerTransferTreatment.CashContra &&
      !normalCash.UseBranchWiseCashContraVoucherType && !normalCash.UseSeparateCashLedger,
    "Branch-specific cash treatment must remain optional.");

var differentBooksSamePan = OwnerGroupAccountingPolicy.ClassifyTransfer(
    "tally-company-A", "tally-company-B", "bank1", "bank2", cashInvolved: false);
Check(differentBooksSamePan.Treatment == OwnerTransferTreatment.MirroredIntercompanyEntriesRequired,
    "Transfers across separate books must not import as a single Contra.");
Check(OwnerGroupAccountingPolicy.ClassifyTransfer(
    "tally-company-A", "tally-company-A", "bank1", "bank1", cashInvolved: false)
    .Treatment == OwnerTransferTreatment.ReviewRequired,
    "Self-transfer must require review.");
Check(OwnerGroupAccountingPolicy.ClassifyTransfer(
    "", "tally-company-A", "bank1", "bank2", cashInvolved: false)
    .Treatment == OwnerTransferTreatment.ReviewRequired,
    "Missing Tally identity must block automatic routing.");

Console.WriteLine("PASS: owner and individual groups, per-FY relationships, PAN separation, normal-group isolation, cash/interbank routing, cross-books safety.");
