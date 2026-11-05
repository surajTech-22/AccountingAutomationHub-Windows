using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Infrastructure.Masters;
using Microsoft.Data.Sqlite;

// R610 regression: a built-in AAH chart-of-accounts placeholder must not
// compete with the same verified Group/Ledger from the linked Tally book.
// No real Tally MASTERID is merged, archived, renamed, or written to Tally.
internal static class TallyStandardHierarchyDedupTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Standard hierarchy/Tally dedup: " + message);
    }

    private static MasterRecord Row(string name, string type, string company, string source,
        string masterId = "", string tallyGuid = "", decimal? opening = null) => new()
    {
        Name = name, Type = type, Parent = type == "Group" ? "Current Assets" : "Cash-in-Hand",
        Company = company, Source = source, TallyMasterId = masterId, TallyGuid = tallyGuid,
        OpeningBalance = opening
    };

    public static void Run()
    {
        var dbPath = Path.Combine(Path.GetTempPath(),
            "AAH-Standard-Hierarchy-" + Guid.NewGuid().ToString("N") + ".db");
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString();
        const string fy = "2025-26";
        const string localCompany = "Mitansh Technology India Private Limited";
        const string linkedCompany = "Mitansh Technology Pvt Ltd";
        const string otherCompany = "Other Tally Book";
        try
        {
            using (var sql = new SqliteConnection(cs))
            {
                sql.Open();
                using var setup = sql.CreateCommand();
                setup.CommandText =
                    """
                    CREATE TABLE Clients (Id INTEGER PRIMARY KEY);
                    INSERT INTO Clients(Id) VALUES (901),(902),(903);
                    """;
                setup.ExecuteNonQuery();
            }
            var repo = new SqliteMasterRepository(cs);
            repo.Initialize();
            // Tally link storage is optional at startup; do not make standard
            // Master generation depend on the Tally repository being initialized.
            repo.Upsert(901, fy,
                [Row("Bank Accounts", "Group", localCompany, "Standard hierarchy")]);
            Check(repo.Get(901, fy, false).Count == 1,
                "an unlinked standard Group was incorrectly hidden");

            using (var sql = new SqliteConnection(cs))
            {
                sql.Open();
                using var link = sql.CreateCommand();
                link.CommandText =
                    """
                    CREATE TABLE TallyClientLinks (
                      ClientId INTEGER NOT NULL, CompanyName TEXT NOT NULL,
                      FinancialYear TEXT NOT NULL
                    );
                    INSERT INTO TallyClientLinks(ClientId,CompanyName,FinancialYear)
                      VALUES (901,'Mitansh Technology Pvt Ltd','2025-26'),
                             (902,'Other Tally Book','2025-26');
                    """;
                link.ExecuteNonQuery();
            }

            // Same display name in a different book cannot retire the local
            // placeholder or masquerade as the currently linked Tally master.
            repo.Upsert(901, fy, [Row("Bank Accounts", "Group", otherCompany,
                "Live Tally", "201", "11111111-1111-1111-1111-111111111111-00000001")]);
            var before = repo.Get(901, fy, true);
            Check(before.Single(row => row.Source == "Standard hierarchy").IsArchived == false,
                "an unrelated Tally company's Group retired the standard hierarchy");

            repo.Upsert(901, fy, [Row("Bank Accounts", "Group", linkedCompany,
                "Live Tally", "349", "22222222-2222-2222-2222-222222222222-00000002")]);
            var after = repo.Get(901, fy, true);
            Check(after.Count(row => !row.IsArchived && row.Name == "Bank Accounts") == 2,
                "the old standard Group remains active or a real Tally book was deleted");
            Check(after.Single(row => row.Source == "Standard hierarchy").IsArchived,
                "redundant standard Group not archived after linked Tally readback");
            Check(after.Count(row => !row.IsArchived && row.TallyMasterId == "349") == 1 &&
                  after.Count(row => !row.IsArchived && row.TallyMasterId == "201") == 1,
                "one of the independently bound Tally Groups was changed");

            // Repeated AAH accounting refresh must NOT revive Standard hierarchy.
            repo.Upsert(901, fy, [Row("Bank Accounts", "Group", localCompany,
                "Standard hierarchy")]);
            after = repo.Get(901, fy, true);
            Check(after.Single(row => row.Source == "Standard hierarchy").IsArchived,
                "subsequent standard sync revived the duplicate Group");
            Check(after.Count(row => !row.IsArchived && row.Name == "Bank Accounts") == 2,
                "repeat sync created a new active duplicate");

            // Ledger placeholders have the same problem: Tally's Cash Ledger
            // wins, but only for the uniquely linked physical book.
            repo.Upsert(901, fy,
                [Row("Cash", "Ledger", localCompany, "Standard hierarchy")]);
            repo.Upsert(901, fy, [Row("Cash", "Ledger", linkedCompany,
                "Live Tally", "351", "22222222-2222-2222-2222-222222222222-00000003")]);
            Check(repo.Get(901, fy, false).Count(row => row.Type == "Ledger" && row.Name == "Cash") == 1,
                "Cash standard Ledger shadow not retired");

            // Two independently identified Tally books can even share a
            // company display name and numeric MASTERID. That is never one
            // uniquely verified linked-book object. Do not hide the standard
            // placeholder when that authority is ambiguous.
            const string sharedName = "Coincidentally Same Tally Name";
            repo.Upsert(903, fy,
                [Row("Sundry Debtors", "Group", localCompany, "Standard hierarchy")]);
            repo.Upsert(903, fy, [Row("Sundry Debtors", "Group", sharedName,
                "Live Tally", "440", "33333333-3333-3333-3333-333333333333-00000005")]);
            repo.Upsert(903, fy, [Row("Sundry Debtors", "Group", sharedName,
                "Live Tally", "440", "44444444-4444-4444-4444-444444444444-00000006")]);
            using (var sql = new SqliteConnection(cs))
            {
                sql.Open();
                using var link = sql.CreateCommand();
                link.CommandText =
                    "INSERT INTO TallyClientLinks(ClientId,CompanyName,FinancialYear) " +
                    "VALUES (903,'Coincidentally Same Tally Name','2025-26');";
                link.ExecuteNonQuery();
            }
            repo.Upsert(903, fy,
                [Row("Sundry Debtors", "Group", localCompany, "Standard hierarchy")]);
            var ambiguous = repo.Get(903, fy, false).Where(row => row.Name == "Sundry Debtors").ToArray();
            Check(ambiguous.Length == 3 &&
                  ambiguous.Count(row => row.TallyMasterId == "440") == 2 &&
                  ambiguous.Any(row => row.Source == "Standard hierarchy"),
                "ambiguous equal-numeric MASTERIDs from different books were silently collapsed");

            // User-owned Masters, meaningful opening balances and unrelated
            // Client/FY authorities must remain intact.
            repo.Upsert(901, fy, [Row("Special Group", "Group", localCompany, "Manual")]);
            repo.Upsert(901, fy, [Row("Protected Balance", "Ledger", localCompany,
                "Standard hierarchy", opening: 100m)]);
            repo.Upsert(901, fy, [Row("Protected Balance", "Ledger", linkedCompany,
                "Live Tally", "352", "22222222-2222-2222-2222-222222222222-00000004")]);
            repo.Upsert(902, fy,
                [Row("Bank Accounts", "Group", localCompany, "Standard hierarchy")]);
            repo.Upsert(901, "2024-25",
                [Row("Bank Accounts", "Group", localCompany, "Standard hierarchy")]);
            Check(repo.Get(901, fy, false).Any(row => row.Name == "Special Group" && row.Source == "Manual"),
                "a user-created Group was altered");
            Check(repo.Get(901, fy, false).Any(row =>
                row.Name == "Protected Balance" && row.Source == "Standard hierarchy" &&
                row.OpeningBalance == 100m), "a standard row with a meaningful balance was hidden");
            Check(repo.Get(902, fy, false).Any(row => row.Name == "Bank Accounts" &&
                row.Source == "Standard hierarchy"), "unrelated client's master was hidden");
            Check(repo.Get(901, "2024-25", false).Any(row => row.Name == "Bank Accounts" &&
                row.Source == "Standard hierarchy"), "unrelated FY's master was hidden");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch (IOException) { }
            try { File.Delete(dbPath + "-wal"); } catch (IOException) { }
            try { File.Delete(dbPath + "-shm"); } catch (IOException) { }
        }
        Console.WriteLine("PASS: verified linked Tally Masters retire only unbound Standard hierarchy shadows; no cross-book merges.");
    }
}
