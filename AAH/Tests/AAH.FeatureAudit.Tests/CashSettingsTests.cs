using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Masters;
using Microsoft.Data.Sqlite;
internal static class CashSettingsTests
{
    public static void Run()
    {
        var path = Path.Combine(Path.GetTempPath(), "aah-cash-settings-" + Guid.NewGuid().ToString("N") + ".db");
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        try
        {
            // The production database always has Client rows before Banking/Master
            // repositories are used. Build the same parent identity in this isolated
            // regression fixture so foreign-key protected Master writes exercise the
            // real schema instead of failing on a missing Clients table.
            using (var bootstrap = new SqliteConnection(cs))
            {
                bootstrap.Open();
                using var command = bootstrap.CreateCommand();
                command.CommandText = "CREATE TABLE Clients(Id INTEGER PRIMARY KEY); INSERT INTO Clients(Id) VALUES(1),(2);";
                command.ExecuteNonQuery();
            }
            var banking = new SqliteBankingRepository(cs);
            var masters = new SqliteMasterRepository(cs);
            banking.Initialize(); masters.Initialize();
            var service = new CashEntryService(banking, masters);
            var initial = service.EnsureSettings(1, "2025-26");
            using var observer = new SqliteConnection(cs); observer.Open();
            long Revision() { using var c=observer.CreateCommand(); c.CommandText="PRAGMA data_version"; return Convert.ToInt64(c.ExecuteScalar()); }
            void Check(bool ok,string message) { if(!ok) throw new InvalidOperationException(message); }
            var revision = Revision();
            for(var i=0;i<20;i++)
            {
                var settings=service.EnsureSettings(1,"2025-26");
                Check(settings.UpdatedUtc==initial.UpdatedUtc,"Read changed the saved Cash timestamp.");
                banking.SaveCashSettings(settings);
            }
            Check(Revision()==revision,"Unchanged Cash hydration/settings writes triggered database refresh.");
            initial.OpeningBalance=123.45m; banking.SaveCashSettings(initial);
            Check(Revision()!=revision,"A real settings change was lost.");
            Check(banking.GetCashSettings(1,"2025-26").OpeningBalance==123.45m,"Opening balance not persisted.");
            var other=service.EnsureSettings(2,"2025-26");
            Check(other.OpeningBalance==0m,"Cash settings leaked between clients.");
            revision=Revision();
            service.EnsureSettings(2,"2025-26");
            Check(Revision()==revision,"New client hydration did not settle.");
            Console.WriteLine("PASS Cash settings initialization, repeated read/no-op save, actual edit and client isolation.");
        }
        finally { foreach(var suffix in new[]{"","-wal","-shm"}) if(File.Exists(path+suffix))File.Delete(path+suffix); }
    }
}
