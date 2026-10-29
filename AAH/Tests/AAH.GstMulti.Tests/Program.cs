using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Gst;
using AccountingAutomationHub.Infrastructure.Clients;
using AccountingAutomationHub.Infrastructure.Gst;
using Microsoft.Data.Sqlite;
using System.Reflection;

var dir = Path.Combine(Path.GetTempPath(), "AAH-GST-MULTI-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try
{
    var path = Path.Combine(dir, "gst-multi.sqlite");
    var clients = new SqliteClientRepository(path);
    clients.Initialize();
    var client = clients.Save(new ClientProfile
    {
        ClientCode="GST-MULTI-TEST", ClientName="Synthetic Multi-State Co",
        FinancialYear="2026-27", Gstin="29ABCDE1234F1Z5",
        Pan="ABCDE1234F", AdditionalGstins=["27ABCDE1234F1Z5"]
    });
    var gst = new SqliteGstRepository(path);
    gst.Initialize();

    const string a = "29ABCDE1234F1Z5", b = "27ABCDE1234F1Z5", fy="2026-27";
    Gstr3bReturnRecord Return(string registration, decimal tax) => new()
    {
        ClientId=client.Id, FinancialYear=fy, Gstin=registration,
        PeriodKey="202607", PeriodLabel="July 2026", FilingFrequency="Monthly",
        OutwardIgst=tax, NetItcCgst=tax/2m, ParseValid=true,
        Arn="ARN" + registration, SourcePath=Path.Combine(dir, registration+".pdf"),
        RawFields=new Dictionary<string,string>(), ImportedUtc=DateTime.UtcNow
    };

    gst.ReplaceReturn(Return(a, 100m));
    gst.ReplaceReturn(Return(b, 200m));
    var two=gst.GetReturns(client.Id, fy);
    if(two.Count!=2 || two.Select(x=>x.Gstin).Distinct().Count()!=2)
        throw new Exception("Same-period GSTR-3B records overwrote each other.");
    var merged=GstReconciliationService.AggregateReturns(two,"Monthly");
    if(merged.Count!=1 || merged[0].OutwardIgst!=300m || merged[0].NetItcCgst!=150m)
        throw new Exception("Existing GST module combined sums lost one registration.");
    gst.ReplaceReturn(Return(a, 105m));
    two=gst.GetReturns(client.Id,fy);
    if(two.Count!=2 || two.Single(r=>r.Gstin==b).OutwardIgst!=200m ||
        two.Single(r=>r.Gstin==a).OutwardIgst!=105m)
        throw new Exception("Reimport modified the other GSTIN's return.");

    GstLedgerPeriodBalance Ledger(string registration, decimal opening, decimal closing) => new()
    {
        ClientId=client.Id, FinancialYear=fy, Gstin=registration,
        LedgerType="Electronic Credit Ledger", PeriodKey="202607",
        PeriodLabel="July 2026", OpeningIgst=opening, ClosingIgst=closing,
        ImportedUtc=DateTime.UtcNow,SourcePath=Path.Combine(dir,registration+".csv")
    };
    gst.ReplaceLedgerPeriods(client.Id,fy,"Electronic Credit Ledger",[Ledger(a,10m,12m)],a);
    gst.ReplaceLedgerPeriods(client.Id,fy,"Electronic Credit Ledger",[Ledger(b,20m,23m)],b);
    var rawLedgers=gst.GetLedgerPeriods(client.Id,fy);
    if(rawLedgers.Count!=2)
        throw new Exception("One electronic ledger replaced another registration.");
    var aggregated=GstReconciliationService.AggregateLedgerPeriods(rawLedgers,"Monthly");
    if(aggregated.Count!=1 || aggregated[0].OpeningIgst!=30m ||
        aggregated[0].ClosingIgst!=35m)
        throw new Exception("GST module did not sum each GSTIN's own ledger balances.");
    gst.ReplaceLedgerPeriods(client.Id,fy,"Electronic Credit Ledger",[Ledger(b,20m,24m)],b);
    if(gst.GetLedgerPeriods(client.Id,fy).Count!=2 ||
       gst.GetLedgerPeriods(client.Id,fy).Single(x=>x.Gstin==a).ClosingIgst!=12m)
        throw new Exception("Secondary GSTIN ledger update altered primary ledger.");

    gst.Initialize();
    if(gst.GetReturns(client.Id,fy).Count!=2 || gst.GetLedgerPeriods(client.Id,fy).Count!=2)
        throw new Exception("GST initialization lost per-GSTIN records.");

    // Simulate the exact legacy one-period-per-client indexes and an older
    // ledger table without a Gstin column. The migration must retain existing
    // records and then allow a second GSTIN for the same period.
    var oldPath=Path.Combine(dir,"legacy.sqlite");
    var oldClients=new SqliteClientRepository(oldPath);
    oldClients.Initialize();
    var oldClient=oldClients.Save(new ClientProfile {
        ClientCode="LEGACY", ClientName="Existing Single-GSTIN",
        FinancialYear=fy, Gstin=a, Pan="ABCDE1234F"
    });
    var oldStore=new SqliteGstRepository(oldPath);
    oldStore.Initialize();
    oldStore.ReplaceReturn(new Gstr3bReturnRecord {
        ClientId=oldClient.Id, FinancialYear=fy, Gstin=a,PeriodKey="202607",
        PeriodLabel="July 2026",OutwardIgst=33m,SourcePath="old-return.pdf",
        ImportedUtc=DateTime.UtcNow
    });
    oldStore.ReplaceLedgerPeriods(oldClient.Id,fy,"Electronic Credit Ledger",
        [new GstLedgerPeriodBalance {
            ClientId=oldClient.Id,FinancialYear=fy,Gstin=a,
            LedgerType="Electronic Credit Ledger",PeriodKey="202607",
            ClosingIgst=50m,SourcePath="legacy-ledger.csv"
        }],a);
    using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{ DataSource=oldPath }.ToString()))
    {
        db.Open();
        using var command=db.CreateCommand();
        command.CommandText = """
            DROP INDEX IF EXISTS UX_Gstr3bReturns_ClientFyGstinPeriod;
            CREATE UNIQUE INDEX UX_Gstr3bReturns_ClientFyPeriod
                ON Gstr3bReturns(ClientId,FinancialYear,PeriodKey);
            DROP INDEX IF EXISTS UX_GstLedgerPeriods_ClientFyGstinTypePeriod;
            ALTER TABLE GstLedgerPeriods DROP COLUMN Gstin;
            CREATE UNIQUE INDEX UX_GstLedgerPeriods_ClientFyTypePeriod
                ON GstLedgerPeriods(ClientId,FinancialYear,LedgerType,PeriodKey);
            """;
        command.ExecuteNonQuery();
    }
    var migrate=typeof(SqliteGstRepository).GetMethod(
        "InitializeCore",BindingFlags.Instance|BindingFlags.NonPublic)
        ?? throw new Exception("GST repository migration method unavailable.");
    try { migrate.Invoke(oldStore,null); }
    catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    var migratedReturns=oldStore.GetReturns(oldClient.Id,fy);
    var migratedLedgers=oldStore.GetLedgerPeriods(oldClient.Id,fy);
    if(migratedReturns.Count!=1 || migratedReturns[0].OutwardIgst!=33m ||
        migratedLedgers.Count!=1 || migratedLedgers[0].ClosingIgst!=50m ||
        migratedLedgers[0].Gstin!=a)
        throw new Exception("Legacy GST table migration lost existing return or ledger rows.");
    oldStore.ReplaceReturn(new Gstr3bReturnRecord {
        ClientId=oldClient.Id,FinancialYear=fy,Gstin=b,PeriodKey="202607",
        PeriodLabel="July 2026",OutwardIgst=44m,SourcePath="new-return.pdf",
        ImportedUtc=DateTime.UtcNow
    });
    if(oldStore.GetReturns(oldClient.Id,fy).Count!=2)
        throw new Exception("Migrated GST index still blocks a second registration.");

    Console.WriteLine("PASS: 9/9 multi-GSTIN storage, legacy migration, and merged GST module checks.");
}
finally
{
    try { Directory.Delete(dir,true); }
    catch { /* temporary SQLite WAL files can remain open momentarily */ }
}
