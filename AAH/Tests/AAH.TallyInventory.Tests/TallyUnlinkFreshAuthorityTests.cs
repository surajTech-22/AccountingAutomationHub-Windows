using AccountingAutomationHub.Infrastructure.Tally;
using Microsoft.Data.Sqlite;

// A scoped unlink is a destructive operation against AAH's OLD Tally cache,
// not against Tally or the client's source accounting. Exercise the real
// SQLite reset and a same-book relink with a genuinely new authority snapshot.
internal static class TallyUnlinkFreshAuthorityTests
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException("Tally unlink/relink: " + message);
    }

    public static void Run()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "AAH-Tally-Unlink-" + Guid.NewGuid().ToString("N") + ".db");
        var cs = new SqliteConnectionStringBuilder {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString();
        try
        {
            using (var db = new SqliteConnection(cs))
            {
                db.Open();
                using var init = db.CreateCommand();
                init.CommandText =
                    "CREATE TABLE Clients(Id INTEGER PRIMARY KEY); " +
                    "INSERT INTO Clients(Id) VALUES (901),(902);";
                init.ExecuteNonQuery();
            }
            var repo = new SqliteTallyRepository(cs);
            repo.Initialize();
            using var connection = new SqliteConnection(cs);
            connection.Open();
            void Exec(string sql)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            int Count(string sql)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }

            Exec(
                """
                CREATE TABLE AahOriginalAccounting(ClientId INTEGER, FinancialYear TEXT, SourceRef TEXT);
                INSERT INTO AahOriginalAccounting VALUES (901,'2025-26','BANK-56032');
                CREATE TABLE Masters(ClientId INTEGER,FinancialYear TEXT,Company TEXT,
                    Name TEXT, Source TEXT, IsUserEdited INTEGER, TallyMasterId TEXT,
                    TallyGuid TEXT, TallyAlterId INTEGER, UpdatedUtc TEXT);
                CREATE TABLE StockItems(ClientId INTEGER,FinancialYear TEXT,Company TEXT,
                    Name TEXT, Source TEXT, IsArchived INTEGER, IsUserEdited INTEGER,
                    TallyMasterId TEXT,TallyGuid TEXT,TallyAlterId INTEGER,UpdatedUtc TEXT);
                CREATE TABLE StockUnits(ClientId INTEGER,FinancialYear TEXT,Company TEXT,
                    Name TEXT, Source TEXT, IsArchived INTEGER, IsUserEdited INTEGER,UpdatedUtc TEXT);
                CREATE TABLE BankAccounts(ClientId INTEGER,FinancialYear TEXT,LedgerName TEXT,
                    TallyLedgerMasterId TEXT,TallyLedgerGuid TEXT,TallyLedgerAlterId INTEGER,
                    TallyLedgerVerifiedName TEXT);
                CREATE TABLE BankTallyExportLinks(ClientId INTEGER,FinancialYear TEXT,CompanyKey TEXT,TallyMasterId TEXT);
                CREATE TABLE BankPendingTallyDuplicateDeletes(ClientId INTEGER,FinancialYear TEXT,KnownTallyMasterId TEXT);
                CREATE TABLE PendingTallyMasterMerges(ClientId INTEGER,FinancialYear TEXT,CompanyKey TEXT,
                    Status TEXT,LastError TEXT,UpdatedUtc TEXT);

                INSERT INTO Masters VALUES
                    (901,'2025-26','BOOK A','Imported creditor','Live Tally',0,'777','G-A',5,''),
                    (901,'2025-26','BOOK A','Edited creditor','Live Tally',1,'778','G-A',5,''),
                    (901,'2025-26','BOOK A','Local sales','Manual',1,'','','0',''),
                    (902,'2025-26','BOOK B','Other book creditor','Live Tally',0,'777','G-B',5,'');
                INSERT INTO StockItems VALUES
                    (901,'2025-26','BOOK A','Imported stock','Live Tally',0,0,'88','G-A',8,''),
                    (901,'2025-26','BOOK A','Edited stock','Live Tally',0,1,'89','G-A',8,''),
                    (902,'2025-26','BOOK B','Other stock','Live Tally',0,0,'88','G-B',8,'');
                INSERT INTO StockUnits VALUES
                    (901,'2025-26','BOOK A','Old unit','Live Tally',0,0,''),
                    (902,'2025-26','BOOK B','Other unit','Live Tally',0,0,'');
                INSERT INTO BankAccounts VALUES
                    (901,'2025-26','HDFC Bank','349','G-A',12,'HDFC Bank'),
                    (902,'2025-26','Other Bank','349','G-B',12,'Other Bank');
                INSERT INTO BankTallyExportLinks VALUES
                    (901,'2025-26','COMPANY-A','354'),
                    (902,'2025-26','COMPANY-B','354');
                INSERT INTO BankPendingTallyDuplicateDeletes VALUES
                    (901,'2025-26','354'),(902,'2025-26','354');
                INSERT INTO PendingTallyMasterMerges VALUES
                    (901,'2025-26','COMPANY-A','Watching','',''),
                    (902,'2025-26','COMPANY-B','Watching','','');

                INSERT INTO TallyClientLinks
                    (ClientId,CompanyKey,CompanyName,FinancialYear,LinkedUtc,UpdatedUtc)
                VALUES (901,'COMPANY-A','BOOK A','2025-26','now','now'),
                       (902,'COMPANY-B','BOOK B','2025-26','now','now');
                INSERT INTO TallyBankSyncState(ClientId,FinancialYear,CompanyKey,LastFullSyncUtc)
                VALUES (901,'2025-26','COMPANY-A','yesterday'),
                       (902,'2025-26','COMPANY-B','yesterday');
                INSERT INTO TallySourceBindings
                    (ClientId,FinancialYear,CompanyKey,EntityType,SourceAnchor,SourceKey,
                     TallyMasterId,LastContentHash,LastVerifiedAlterId,State,UpdatedUtc)
                VALUES
                    (901,'2025-26','COMPANY-A','Consolidated Voucher','BANK-56032','BANK-56032',
                     '354','OLD-HASH',377,'Verified','now'),
                    (902,'2025-26','COMPANY-B','Consolidated Voucher','OTHER','OTHER',
                     '354','OTHER-HASH',377,'Verified','now');
                INSERT INTO TallyImportFingerprints
                    (ClientId,FinancialYear,CompanyKey,EntityType,SourceKey,ContentHash,FirstSeenUtc,LastSeenUtc)
                VALUES
                    (901,'2025-26','COMPANY-A','Consolidated Voucher','BANK-56032','OLD-HASH','now','now'),
                    (902,'2025-26','COMPANY-B','Consolidated Voucher','OTHER','OTHER-HASH','now','now');
                INSERT INTO TallyObjectVersionHistory
                    (ClientId,FinancialYear,CompanyKey,EntityType,SourceAnchor,CreatedUtc)
                VALUES (901,'2025-26','COMPANY-A','Bank Voucher','BANK-56032','now'),
                       (902,'2025-26','COMPANY-B','Bank Voucher','OTHER','now');
                INSERT INTO TallyVoucherIndex
                    (CompanyKey,FinancialYear,MasterId,AlterId,LastSeenUtc)
                VALUES ('COMPANY-A','2025-26','354','377','now'),
                       ('COMPANY-B','2025-26','354','377','now');
                INSERT INTO TallyVoucherXmlAuthority
                    (CompanyKey,FinancialYear,MasterId,AlterId,LastSeenUtc)
                VALUES ('COMPANY-A','2025-26','354','377','now'),
                       ('COMPANY-B','2025-26','354','377','now');
                INSERT INTO TallyReportAuthority
                    (CompanyKey,FinancialYear,ReportName,FromDate,ToDate,RefreshedUtc)
                VALUES ('COMPANY-A','2025-26','Balance Sheet','2025-04-01','2026-03-31','now'),
                       ('COMPANY-B','2025-26','Balance Sheet','2025-04-01','2026-03-31','now');
                """);

            try
            {
                repo.UnlinkClientAndResetAuthority(901, "2025-26", "WRONG-COMPANY");
                throw new InvalidOperationException("wrong-company unlink was accepted");
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("confirmed Tally link changed", StringComparison.Ordinal))
            {
                Check(Count("SELECT COUNT(*) FROM TallyClientLinks WHERE ClientId=901")==1,
                    "wrong-company guard changed a link");
            }

            repo.UnlinkClientAndResetAuthority(901, "2025-26", "COMPANY-A");
            foreach (var table in new[] { "TallyClientLinks", "TallySourceBindings",
                         "TallyImportFingerprints", "TallyObjectVersionHistory",
                         "TallyBankSyncState", "BankTallyExportLinks" })
            {
                Check(Count($"SELECT COUNT(*) FROM {table} WHERE ClientId=901") == 0,
                    table + " retained the detached client's old authority");
                Check(Count($"SELECT COUNT(*) FROM {table} WHERE ClientId=902") == 1,
                    table + " deleted a different client or Tally company");
            }
            foreach (var table in new[] { "TallyVoucherIndex", "TallyVoucherXmlAuthority",
                         "TallyReportAuthority" })
            {
                Check(Count($"SELECT COUNT(*) FROM {table} WHERE CompanyKey='COMPANY-A'")==0,
                    table + " retained old Tally snapshot");
                Check(Count($"SELECT COUNT(*) FROM {table} WHERE CompanyKey='COMPANY-B'")==1,
                    table + " cleared unrelated company snapshot");
            }
            Check(Count("SELECT COUNT(*) FROM AahOriginalAccounting WHERE ClientId=901")==1,
                "unlink deleted user AAH accounting");
            Check(Count("SELECT COUNT(*) FROM Masters WHERE Name='Imported creditor'")==0,
                "old Tally-only ledger remained active");
            Check(Count("SELECT COUNT(*) FROM Masters WHERE Name='Edited creditor' AND Source='Manual' AND TallyMasterId=''")==1,
                "edited ledger was deleted instead of preserving local master");
            Check(Count("SELECT COUNT(*) FROM Masters WHERE Name='Local sales' AND Source='Manual'")==1,
                "local AAH master changed");
            Check(Count("SELECT COUNT(*) FROM StockItems WHERE Name='Imported stock' AND IsArchived=1 AND TallyMasterId=''")==1,
                "old Tally stock identity survived");
            Check(Count("SELECT COUNT(*) FROM StockItems WHERE Name='Edited stock' AND IsArchived=0 AND Source='Manual'")==1,
                "user's edited inventory master was lost");
            Check(Count("SELECT COUNT(*) FROM BankAccounts WHERE ClientId=901 AND LedgerName='HDFC Bank' AND TallyLedgerMasterId=''")==1,
                "bank statement account was removed or kept stale Tally identity");
            Check(Count("SELECT COUNT(*) FROM BankAccounts WHERE ClientId=902 AND TallyLedgerMasterId='349'")==1,
                "other client's Bank ledger changed");
            Check(Count("SELECT COUNT(*) FROM BankPendingTallyDuplicateDeletes WHERE ClientId=901")==0,
                "pending old Tally delete retained");
            Check(Count("SELECT COUNT(*) FROM PendingTallyMasterMerges WHERE ClientId=901 AND Status='Cancelled'")==1,
                "old Tally master merge can still execute");
            Check(repo.GetBankSyncState(901, "2025-26", "COMPANY-A") is null,
                "relink may reuse the pre-unlink full sync token");

            // Same-company relink must require fresh baseline and must not
            // trust removed prior MASTERID=354 or previous ALTERID=377.
            Exec(
                """
                INSERT INTO TallyClientLinks
                    (ClientId,CompanyKey,CompanyName,FinancialYear,LinkedUtc,UpdatedUtc)
                VALUES (901,'COMPANY-A','BOOK A','2025-26','new','new');
                INSERT INTO TallyVoucherIndex
                    (CompanyKey,FinancialYear,MasterId,AlterId,LastSeenUtc)
                VALUES ('COMPANY-A','2025-26','999','1000','new');
                """);
            Check(repo.GetSourceBindingVersionState(901, "2025-26", "COMPANY-A",
                "Consolidated Voucher", "BANK-56032") is null,
                "fresh relink silently adopted old imported voucher binding");
            Check(Count("SELECT COUNT(*) FROM TallyVoucherIndex WHERE CompanyKey='COMPANY-A' AND MasterId='999'")==1,
                "freshly fetched destination authority was not accepted");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch (IOException) { }
            try { File.Delete(path + "-wal"); } catch (IOException) { }
            try { File.Delete(path + "-shm"); } catch (IOException) { }
        }
        Console.WriteLine("PASS: unlink clears one old Tally book; relink requires new snapshot; original accounting and other books intact.");
    }
}
