using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Gst;
using AccountingAutomationHub.Infrastructure.Clients;
using AccountingAutomationHub.Infrastructure.Gst;

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

    Console.WriteLine("PASS: 7/7 isolated multi-GSTIN return, ledger, reimport, and merged totals checks.");
}
finally
{
    try { Directory.Delete(dir,true); }
    catch { /* temporary SQLite WAL files can remain open momentarily */ }
}
