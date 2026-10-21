using AccountingAutomationHub.Infrastructure.Persistence;
using AccountingAutomationHub.Infrastructure.Masters;
using Microsoft.Data.Sqlite;
internal static class ResetStructureTests
{
    public static void Run()
    {
        var connectionString = $"Data Source=reset-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        void Execute(string sql) { using var c=connection.CreateCommand();c.CommandText=sql;c.ExecuteNonQuery(); }
        long Count(string sql) { using var c=connection.CreateCommand();c.CommandText=sql;return Convert.ToInt64(c.ExecuteScalar()); }
        void Check(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
        Execute("""
            CREATE TABLE Masters(Id INTEGER PRIMARY KEY,ClientId INTEGER,FinancialYear TEXT,Name TEXT,Parent TEXT,Type TEXT,Company TEXT,Source TEXT,OpeningBalance REAL,IsArchived INTEGER,IsUserEdited INTEGER,TallyMasterId TEXT,TallyGuid TEXT,TallyAlterId INTEGER,CreatedUtc TEXT,UpdatedUtc TEXT);
            CREATE TABLE BankLedgerMappingMembers(MappingId INTEGER);
            CREATE TABLE BankLedgerMappings(Id INTEGER,ClientId INTEGER,FinancialYear TEXT);
            CREATE TABLE JournalLines(VoucherId INTEGER);
            CREATE TABLE JournalVouchers(Id INTEGER,ClientId INTEGER,FinancialYear TEXT);
            CREATE TABLE ImportJobAttempts(JobId TEXT);
            CREATE TABLE ImportJobEntities(JobId TEXT);
            CREATE TABLE ImportJobs(JobId TEXT,ClientId INTEGER,FinancialYear TEXT);
            CREATE TABLE SalesRecords(ClientId INTEGER,FinancialYear TEXT);
            CREATE TABLE ClientPreferences(ClientId INTEGER,FinancialYear TEXT,Value TEXT);
            INSERT INTO SalesRecords VALUES(1,'2025-26'),(2,'2025-26'),(1,'2024-25');
            INSERT INTO ClientPreferences VALUES(1,'2025-26','preserve');
            INSERT INTO Masters(ClientId,FinancialYear,Name,Type) VALUES(1,'2025-26','Custom','Ledger'),(2,'2025-26','Other client','Ledger'),(1,'2024-25','Other FY','Ledger');
            """);
        var service=new ClientYearDataResetService(connectionString);
        var result=service.Clear(1,"2025-26");
        Check(result.StandardMastersRestored==30,"Standard group/ledger count is wrong.");
        Check(Count("SELECT COUNT(*) FROM Masters WHERE ClientId=1 AND FinancialYear='2025-26'")==30,"Reset did not restore the complete standard structure.");
        foreach(var master in MasterSynchronizationService.StandardStructure(""))
        {
            using var c=connection.CreateCommand();
            c.CommandText="SELECT COUNT(*) FROM Masters WHERE ClientId=1 AND FinancialYear='2025-26' AND Name=$n AND Parent=$p AND Type=$t AND Source='Standard hierarchy' AND TallyMasterId='' AND TallyGuid='' AND OpeningBalance=0";
            c.Parameters.AddWithValue("$n",master.Name);c.Parameters.AddWithValue("$p",master.Parent);c.Parameters.AddWithValue("$t",master.Type);
            Check(Convert.ToInt64(c.ExecuteScalar())==1,"Incorrect standard Master or invented Tally identity.");
        }
        Check(Count("SELECT COUNT(*) FROM Masters WHERE Name IN ('Other client','Other FY')")==2,"Reset crossed Client/FY boundaries.");
        Check(Count("SELECT COUNT(*) FROM SalesRecords")==2,"Wrong Sales scope cleared.");
        Check(Count("SELECT COUNT(*) FROM ClientPreferences WHERE Value='preserve'")==1,"Client preference lost.");
        service.Clear(1,"2025-26");
        Check(Count("SELECT COUNT(*) FROM Masters WHERE ClientId=1 AND FinancialYear='2025-26'")==30,"Repeated reset duplicated standard Masters.");
        Execute("INSERT INTO SalesRecords VALUES(1,'2025-26'); CREATE TRIGGER FailSeed BEFORE INSERT ON Masters WHEN NEW.Name='Cash' BEGIN SELECT RAISE(ABORT,'seed failure'); END;");
        bool failed=false;
        try { service.Clear(1,"2025-26"); } catch(SqliteException) { failed=true; }
        Check(failed && Count("SELECT COUNT(*) FROM SalesRecords WHERE ClientId=1 AND FinancialYear='2025-26'")==1,"Failed reseed did not roll back the reset.");
        Check(Count("SELECT COUNT(*) FROM Masters WHERE ClientId=1 AND FinancialYear='2025-26'")==30,"Failed reseed lost earlier Masters.");
        Console.WriteLine("PASS reset standard hierarchy, no invented Tally IDs, Client/FY isolation, repeated reset and atomic rollback.");
    }
}
