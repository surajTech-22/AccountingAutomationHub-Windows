using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Infrastructure.Masters;
using Microsoft.Data.Sqlite;

// CL019 regression: the source branch and the consolidated Tally company are
// independent destinations. Equal ledger names (and even numeric MASTERIDs)
// must not cause a cross-company ALTER, rebind, archive, or duplicate warning.
internal static class TallyCompanyMasterScopeTests
{
    private static void Check(bool ok, string reason)
    {
        if (!ok) throw new InvalidOperationException("AAH book scope: " + reason);
    }

    private const string BranchGuid = "654e8ae1-6edd-45f9-b9a5-392311c612aa";
    private const string ConsolidatedGuid = "8784a1d3-991b-41fd-b05e-404565f76a87";
    private const long ClientId = 919;
    private const string Fy = "2025-26";

    private static MasterRecord Ledger(string company, string guidBook,
        string name, string parent, string masterId, int alterId) => new()
    {
        ClientId = ClientId, FinancialYear = Fy, Type = "Ledger",
        Company = company, Name = name, Parent = parent, Source = "Live Tally",
        TallyMasterId = masterId,
        TallyGuid = guidBook + "-" + int.Parse(masterId).ToString("x8"),
        TallyAlterId = alterId
    };

    public static void Run()
    {
        var branch = Ledger("Mitansh Branch Tally", BranchGuid, "JABBAR SINGH",
            "Sundry Debtors", "212", 214);
        var consolidated = Ledger("Mitansh Technology Pvt Ltd Consolidated",
            ConsolidatedGuid, "JABBAR SINGH", "Karnataka Debtors", "350", 317);

        Check(TallyCompanyMasterScope.GuidBookKey(branch.TallyGuid) == BranchGuid.ToUpperInvariant(),
            "The source Tally company GUID prefix was not parsed correctly.");
        Check(!TallyCompanyMasterScope.CanMatch(branch, consolidated) &&
              !TallyCompanyMasterScope.CanMatch(consolidated, branch),
            "Branch and consolidated same-name ledgers were treated as one Master.");
        Check(TallyCompanyMasterScope.CanMatch(branch, Ledger(
            "Renamed source Tally company", BranchGuid,
            "JABBAR SINGH NEW", "Sundry Debtors", "212", 215)),
            "A same-company rename was rejected despite its stable Tally company GUID.");
        Check(!TallyCompanyMasterScope.CanMatch(branch, Ledger(
            "Mitansh Branch Tally", ConsolidatedGuid,
            "JABBAR SINGH", "Sundry Debtors", "212", 214)),
            "Same company display name overrode a contradictory Tally book GUID.");
        Check(TallyCompanyMasterScope.CanMatch(new MasterRecord {
            Name = "JABBAR SINGH", Type = "Ledger", Company = "Mitansh Branch Tally"
        }, branch), "First-time unbound same-company ledger cannot gain verified authority.");
        Check(!TallyCompanyMasterScope.CanMatch(new MasterRecord {
            Name = "JABBAR SINGH", Type = "Ledger", Company = "Other Branch Tally"
        }, branch), "A differently company-owned unbound ledger was adopted.");
        Check(!TallyCompanyMasterScope.CanMatch(new MasterRecord {
            Name = "JABBAR SINGH", Type = "Ledger", TallyMasterId = "212"
        }, consolidated), "Unknown-company bound MASTERID was improperly claimed.");

        var dbPath = Path.Combine(Path.GetTempPath(),
            "AAH-Tally-Master-Books-" + Guid.NewGuid().ToString("N") + ".db");
        var connectionString = new SqliteConnectionStringBuilder {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString();
        try
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var createClient = connection.CreateCommand();
                createClient.CommandText =
                    "CREATE TABLE Clients(Id INTEGER PRIMARY KEY); INSERT INTO Clients(Id) VALUES(919);";
                createClient.ExecuteNonQuery();
            }

            var repo = new SqliteMasterRepository(connectionString);
            repo.Initialize();
            repo.Upsert(ClientId, Fy, [branch]);
            repo.Upsert(ClientId, Fy, [consolidated]);

            var records = repo.Get(ClientId, Fy, includeArchived: true)
                .Where(m => m.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Check(records.Length == 2 && records.All(m => !m.IsArchived),
                "Branch/consolidated JABBAR identities were merged or archived.");
            Check(records.Count(m => m.Name == "JABBAR SINGH") == 2 &&
                  records.Any(m => m.TallyMasterId == "212" &&
                      m.TallyGuid.StartsWith(BranchGuid, StringComparison.OrdinalIgnoreCase)) &&
                  records.Any(m => m.TallyMasterId == "350" &&
                      m.TallyGuid.StartsWith(ConsolidatedGuid, StringComparison.OrdinalIgnoreCase)),
                "JABBAR SINGH was not kept as two independent Tally book identities.");
            Check(!repo.GetNormalizedDuplicateGroups(ClientId, Fy)
                .Any(g => g.Any(m => m.Name == "JABBAR SINGH")),
                "Distinct branch and consolidated JABBAR ledgers were flagged as duplicates.");
            Check(repo.FindNormalizedDuplicateInCompany(
                ClientId, Fy, "Ledger", "JABBAR SINGH",
                tallyCompany: "Mitansh Branch Tally")?.TallyMasterId == "212",
                "Branch Master duplicate lookup selected consolidated Tally.");
            Check(repo.FindNormalizedDuplicateInCompany(
                ClientId, Fy, "Ledger", "JABBAR SINGH",
                tallyCompany: "Mitansh Technology Pvt Ltd Consolidated")?.TallyMasterId == "350",
                "Consolidated Master duplicate lookup selected branch Tally.");
            Check(repo.FindNormalizedDuplicateInCompany(
                ClientId, Fy, "Ledger", "JABBAR SINGH",
                tallyCompany: "Another completely different Tally") is null,
                "A foreign-company same-name Ledger was treated as an in-company duplicate.");
            repo.VerifyLiveLedgerMasterIdAuthority(ClientId, Fy, [branch]);
            repo.VerifyLiveLedgerMasterIdAuthority(ClientId, Fy, [consolidated]);

            // Both companies legitimately re-use numeric MASTERID=410.
            var sameNumberBranch = Ledger("Mitansh Branch Tally", BranchGuid,
                "COMMON PARTY", "Sundry Debtors", "410", 100);
            var sameNumberConsolidated = Ledger(
                "Mitansh Technology Pvt Ltd Consolidated", ConsolidatedGuid,
                "COMMON PARTY", "Karnataka Debtors", "410", 200);
            repo.Upsert(ClientId, Fy, [sameNumberBranch]);
            repo.Upsert(ClientId, Fy, [sameNumberConsolidated]);
            var sameNumber = repo.Get(ClientId, Fy, includeArchived: true)
                .Where(m => m.TallyMasterId == "410").ToArray();
            Check(sameNumber.Length == 2 &&
                  sameNumber.Select(m => TallyCompanyMasterScope.GuidBookKey(m.TallyGuid))
                      .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
                "Numeric MASTERID from the second book overwrote the first book.");
            repo.VerifyLiveLedgerMasterIdAuthority(ClientId, Fy, [sameNumberBranch]);
            repo.VerifyLiveLedgerMasterIdAuthority(ClientId, Fy, [sameNumberConsolidated]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch (IOException) { }
            try { File.Delete(dbPath + "-wal"); } catch (IOException) { }
            try { File.Delete(dbPath + "-shm"); } catch (IOException) { }
        }

        Console.WriteLine("PASS: CL019 branch and consolidated same-name/MasterID identities remain independent.");
    }
}
