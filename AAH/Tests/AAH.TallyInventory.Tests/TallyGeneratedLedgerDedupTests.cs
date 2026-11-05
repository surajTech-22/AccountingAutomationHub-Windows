using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Infrastructure.Masters;
using Microsoft.Data.Sqlite;

internal static class TallyGeneratedLedgerDedupTests
{
    private static void Check(bool ok, string why)
    {
        if (!ok) throw new InvalidOperationException("Linked Tally Ledger source dedup: " + why);
    }

    private const string Fy = "2025-26";
    private const string HubCompany = "Mitansh Technology India Private Limited";
    private const string TallyCompany = "Mitansh Technology Pvt Ltd";
    private static MasterRecord Ledger(string name, string parent, string source,
        string company = HubCompany, string masterId = "", string guid = "",
        decimal? openingBalance = null) => new()
    {
        Type = "Ledger", Name = name, Parent = parent, Source = source, Company = company,
        TallyMasterId = masterId, TallyGuid = guid, OpeningBalance = openingBalance
    };
    private static MasterRecord Live(string name, string parent, string masterId) =>
        Ledger(name, parent, "Live Tally", TallyCompany, masterId,
            "22222222-2222-2222-2222-222222222222-" + masterId.PadLeft(8, '0'));

    public static void Run()
    {
        var path = Path.Combine(Path.GetTempPath(), "AAH-Linked-Ledgers-" + Guid.NewGuid().ToString("N") + ".db");
        var cs = new SqliteConnectionStringBuilder { DataSource=path,
            Mode=SqliteOpenMode.ReadWriteCreate, Cache=SqliteCacheMode.Private }.ToString();
        try
        {
            using (var db = new SqliteConnection(cs))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText =
                    """
                    CREATE TABLE Clients(Id INTEGER PRIMARY KEY);
                    INSERT INTO Clients(Id) VALUES(901),(902);
                    CREATE TABLE TallyClientLinks(ClientId INTEGER PRIMARY KEY,
                      CompanyName TEXT NOT NULL,FinancialYear TEXT NOT NULL);
                    INSERT INTO TallyClientLinks VALUES(901,'Mitansh Technology Pvt Ltd','2025-26');
                    INSERT INTO TallyClientLinks VALUES(902,'Other Company','2025-26');
                    CREATE TABLE BankAccounts(
                        ClientId INTEGER NOT NULL,FinancialYear TEXT NOT NULL,
                        LedgerName TEXT NOT NULL,TallyLedgerMasterId TEXT NOT NULL DEFAULT '');
                    CREATE TABLE PurchaseRecords(
                        ClientId INTEGER NOT NULL,FinancialYear TEXT NOT NULL,
                        PurchaseLedger TEXT NOT NULL,IsUserEdited INTEGER NOT NULL DEFAULT 0,
                        RecordJson TEXT NOT NULL DEFAULT '');
                    INSERT INTO PurchaseRecords
                        (ClientId,FinancialYear,PurchaseLedger,IsUserEdited,RecordJson)
                        VALUES (901,'2025-26','Input Cgst @9%',0,'original-import-json');
                    """;
                cmd.ExecuteNonQuery();
            }
            var repo = new SqliteMasterRepository(cs);
            repo.Initialize();
            var original = new[] {
                Ledger("HDFC Bank CA - 50200110437141", "Bank Accounts", "Bank Account"),
                Ledger("Cash", "Cash-in-Hand", "Ledger Mapping"),
                Ledger("Input Cgst @9%", "Duties & Taxes", "Purchase sync"),
                Ledger("Output IGST @ 18%", "Duties & Taxes", "Sales sync") };
            repo.Upsert(901,Fy,original);
            Check(repo.Get(901,Fy,false).Count==4, "local source masters did not import");
            // Import real current-book ledger authority, without the same AAH
            // legal name; these are the pairs visible in the user's screenshot.
            var tally = new[] {
                Live("HDFC Bank CA - 50200110437141", "Bank Accounts", "349"),
                Live("Cash", "Cash-in-Hand", "350"),
                Live("Input CGST @9%", "Duties & Taxes", "351"),
                Live("Output IGST @ 18%", "Duties & Taxes", "352") };
            repo.Upsert(901,Fy,tally);
            using (var db = new SqliteConnection(cs))
            {
                db.Open();
                using var query = db.CreateCommand();
                query.CommandText = "SELECT IsUserEdited,RecordJson FROM PurchaseRecords " +
                                    "WHERE ClientId=901 AND FinancialYear='2025-26';";
                using var result = query.ExecuteReader();
                Check(result.Read() && result.GetInt32(0)==0 &&
                      result.GetString(1)=="original-import-json",
                    "case-only GST ledger reconciliation must not rewrite original Purchase records");
            }
            var now = repo.Get(901,Fy,true).Where(x=>x.Type=="Ledger").ToArray();
            foreach (var pair in tally)
            {
                var same = now.Where(x => AccountingAutomationHub.Domain.Common.TallyMasterIdentity.Equivalent(x.Name,pair.Name)).ToArray();
                Check(same.Count(x=>!x.IsArchived)==1,"duplicate remained active for "+pair.Name);
                Check(same.Single(x=>!x.IsArchived).TallyMasterId==pair.TallyMasterId,
                    "the verified live Tally MASTERID was not retained for "+pair.Name);
                Check(same.Count(x=>x.IsArchived)==1 && same.Single(x=>x.IsArchived).TallyMasterId=="",
                    "the old local Master was deleted or still active for "+pair.Name);
            }
            repo.Upsert(901,Fy,original);
            Check(repo.Get(901,Fy,false).Count(x=>x.Type=="Ledger")==4,
                "local modules regenerated the retired duplicate at next sync");

            // A live same-name master in a different Tally book is not proof
            // of authority for the linked company's ledger.
            repo.Upsert(902,Fy,[Ledger("Unrelated Bank", "Bank Accounts","Bank Account")]);
            repo.Upsert(902,Fy,[Ledger("Unrelated Bank","Bank Accounts","Live Tally",
                company:"Different Company",masterId:"921",
                guid:"33333333-3333-3333-3333-333333333333-00000921")]);
            Check(repo.Get(902,Fy,false).Count(x=>x.Name=="Unrelated Bank")==2,
                "an unlinked/different book retired a local master");

            // A separate bank-account binding outranks same-name Tally claims.
            using (var db = new SqliteConnection(cs))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO BankAccounts VALUES(901,'2025-26','Bank With Changed Identity','77');";
                cmd.ExecuteNonQuery();
            }
            repo.Upsert(901,Fy,[Ledger("Bank With Changed Identity","Bank Accounts","Bank Account")]);
            repo.Upsert(901,Fy,[Live("Bank With Changed Identity","Bank Accounts","354")]);
            Check(repo.Get(901,Fy,false).Count(x=>x.Name=="Bank With Changed Identity")==2,
                "a different bound Bank MASTERID was overwritten by a name match");

            // A user-created local Ledger, a nonzero opening balance, and a
            // pending group edit are distinct objects until reviewed.
            repo.Upsert(901,Fy,[Ledger("Manual Name","Duties & Taxes","Manual")]);
            repo.Upsert(901,Fy,[Live("Manual Name","Duties & Taxes","355")]);
            Check(repo.Get(901,Fy,false).Count(x=>x.Name=="Manual Name")==2,
                "a user's Manual Ledger was silently retired");
            repo.Upsert(901,Fy,[Ledger("Balance Must Stay","Bank Accounts","Bank Account",
                openingBalance:250m)]);
            repo.Upsert(901,Fy,[Live("Balance Must Stay","Bank Accounts","356")]);
            Check(repo.Get(901,Fy,false).Count(x=>x.Name=="Balance Must Stay")==2,
                "a meaningful opening balance was silently removed");
            repo.Upsert(901,Fy,[Ledger("Different Parent","Duties & Taxes","Purchase sync")]);
            repo.Upsert(901,Fy,[Live("Different Parent","Current Liabilities","357")]);
            Check(repo.Get(901,Fy,false).Count(x=>x.Name=="Different Parent")==2,
                "a pending local group edit was lost");

            // On unlink, original generated rows must be able to return from
            // archived history without accidentally retaining old MASTERID.
            using (var db = new SqliteConnection(cs))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "DELETE FROM TallyClientLinks WHERE ClientId=901;";
                cmd.ExecuteNonQuery();
            }
            repo.Upsert(901,Fy,original);
            var unlinked = repo.Get(901,Fy,false);
            Check(unlinked.Count(x=>x.Source=="Bank Account" && x.Name=="HDFC Bank CA - 50200110437141")==1,
                "unlinked original Bank Account ledger could not reactivate");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch (IOException) { }
            try { File.Delete(path+"-wal"); } catch (IOException) { }
            try { File.Delete(path+"-shm"); } catch (IOException) { }
        }
        Console.WriteLine("PASS: generated Bank/Cash/Purchase/Sales shadows retire only for unique linked Tally identity and safely reappear after unlink.");
    }
}
