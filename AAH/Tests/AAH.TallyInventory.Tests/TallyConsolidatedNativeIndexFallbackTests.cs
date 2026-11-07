using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Tally;
using AccountingAutomationHub.Desktop.Services;

internal static class TallyConsolidatedNativeIndexFallbackTests
{
    private static void Check(bool success, string failure)
    {
        if (!success) throw new InvalidOperationException("Consolidated native ALTERID fallback: " + failure);
    }

    private static TallyVoucherIndexRecord Record(string master, string alter, string hash) => new()
    {
        CompanyKey = "BOOK-42",
        FinancialYear = "2025-26",
        MasterId = master,
        AlterId = alter,
        Guid = "BOOK-42-" + master,
        VoucherDate = new DateTime(2025, 5, 15),
        VoucherType = "Sales",
        VoucherNumber = master,
        Reference = "REF-" + master,
        ContentHash = hash
    };

    private static void Invoke(MethodInfo method,
        IReadOnlyList<TallyVoucherIndexRecord> cached,
        IReadOnlyList<TallyVoucherIndexRecord> live, long maxAlterId)
    {
        method.Invoke(null, [cached, live, maxAlterId]);
    }

    private static bool Rejects(MethodInfo method,
        IReadOnlyList<TallyVoucherIndexRecord> cached,
        IReadOnlyList<TallyVoucherIndexRecord> live, long maxAlterId)
    {
        try { Invoke(method, cached, live, maxAlterId); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { return true; }
        return false;
    }

    private static bool RejectsInvalidNativeReadback(
        SqliteTallyRepository repo, TallyClientLink link,
        TallyVoucherChangeMarker marker,
        IReadOnlyList<TallyVoucherIndexRecord> rows,
        IReadOnlyList<TallyVoucherXmlSnapshot> xml)
    {
        try
        {
            TallyConsolidatedPreImportAuthorityWrapper.PersistVerifiedOriginalLiveVoucherAuthority(
                repo, repo.GetBankSyncState(link.ClientId, link.FinancialYear, link.CompanyKey)!,
                link, marker, rows, xml);
            return false;
        }
        catch (InvalidOperationException) { return true; }
    }

    public static void Run()
    {
        // Compile the REAL consolidated-only wrapper into this test project
        // with a no-op coordinator stub. This exercises its production native
        // Voucher-index proof, rather than duplicating the matching algorithm.
        var proof = typeof(TallyConsolidatedPreImportAuthorityWrapper)
            .GetMethod("RequireSameVoucherIndex", BindingFlags.NonPublic | BindingFlags.Static);
        Check(proof is not null, "production verified-index guard is absent");
        var cached = new[] { Record("35", "670", "HASH-A"), Record("36", "672", "HASH-B") };
        var exact = new[] { Record("36", "672", "HASH-B"), Record("35", "670", "HASH-A") };

        Invoke(proof!, cached, exact, 672);
        Check(Rejects(proof!, cached,
            [Record("35", "670", "HASH-A"), Record("36", "673", "HASH-B")], 672),
            "a live Voucher ALTERID exceeding saved 672 must stop before Tally posting");
        Check(Rejects(proof!, cached,
            [Record("35", "670", "HASH-A"), Record("37", "672", "HASH-B")], 672),
            "different MASTERID at same count and ALTERID must never be adopted");
        Check(Rejects(proof!, cached,
            [Record("35", "670", "HASH-A"), Record("35", "670", "HASH-A")], 672),
            "duplicate destination MASTERIDs must never be treated as complete");
        Check(Rejects(proof!, cached, [Record("35", "670", "HASH-A")], 672),
            "missing live voucher must not silently enter CREATE");
        Check(Rejects(proof!, cached,
            [Record("35", "670", "HASH-A"), Record("36", "672", "MODIFIED-DRCR")], 672),
            "changed voucher content hash with same ALTERID must be rejected");
        Check(Rejects(proof!, cached, exact, 671),
            "saved maximum ALTERID older than a live voucher must fail");
        // Individual export reads the full native Voucher object. A slim
        // Tally TDL index may serialize a display field differently despite
        // the SAME native MASTERID/GUID/ALTERID. Such a representation drift
        // must be verified against the original native source, not treated
        // as proof that the user changed the voucher.
        var months = typeof(TallyConsolidatedPreImportAuthorityWrapper)
            .GetMethod("FindNativeProjectionRecheckMonths",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Check(months is not null, "native individual-voucher projection check is missing");
        IReadOnlyList<DateTime> RecheckMonths(IReadOnlyList<TallyVoucherIndexRecord> live) =>
            (IReadOnlyList<DateTime>)months!.Invoke(null, [cached, live, 672L])!;
        Check(RecheckMonths([Record("35", "670", "THIN-TDL-REPRESENTATION"),
                             Record("36", "672", "HASH-B")]).Count == 1,
            "same MASTERID/ALTERID projection drift should be rechecked using native full voucher");
        Check(RecheckMonths(exact).Count == 0,
            "identical voucher index must not trigger expensive native recheck");
        Check(RecheckMonths([Record("35", "671", "THIN-TDL-REPRESENTATION"),
                             Record("36", "672", "HASH-B")]).Count == 0,
            "changed live ALTERID must NEVER receive a projection exception");
        Check(RecheckMonths([Record("37", "670", "THIN-TDL-REPRESENTATION"),
                             Record("36", "672", "HASH-B")]).Count == 0,
            "changed MASTERID must NEVER receive a projection exception");
        Check(Rejects(proof!, cached,
                [Record("35", "670", "REAL-LIVE-POSTING-CHANGE"),
                 Record("36", "672", "HASH-B")], 672),
            "a different native full Voucher content hash must still fail closed");
        // Actual isolated SQLite replay, not a source-string check. CL019's
        // old live index row 35/ALTERID37 must become NEW ledger/ALTERID39
        // in BOTH saved synchronized objects, without touching other rows.
        var tempDb = Path.Combine(Path.GetTempPath(),
            "AAH-Live-Tally-CL019-Readback-" + Guid.NewGuid().ToString("N") + ".db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = tempDb,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString();
        try
        {
            using (var db = new SqliteConnection(connectionString))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "CREATE TABLE Clients(Id INTEGER PRIMARY KEY); INSERT INTO Clients VALUES(901);";
                cmd.ExecuteNonQuery();
            }
            var repo = new SqliteTallyRepository(connectionString);
            repo.Initialize();
            var link = new TallyClientLink
            {
                ClientId = 901, FinancialYear = "2025-26",
                CompanyKey = "BOOK-42", CompanyName = "CL019 Consolidated"
            };
            TallyVoucherIndexRecord Actual(string id, string alter, string ledger) => new()
            {
                CompanyKey = link.CompanyKey, FinancialYear = link.FinancialYear,
                MasterId = id, AlterId = alter, Guid = "CL019-" + id,
                VoucherDate = new DateTime(2025, 4, 22),
                VoucherType = "Purchase", VoucherNumber = id,
                Reference = id == "35" ? "KC1000148" : "OTHER",
                PartyLedger = ledger,
                DebitLedgers = "Purchase", CreditLedgers = ledger,
                Amount = 40000m, ContentHash = "HASH-" + alter + "-" + ledger
            };
            TallyVoucherXmlSnapshot Xml(string id, string alter, string ledger) => new(
                id, "CL019-" + id, "Purchase", new DateTime(2025, 4, 22),
                "<VOUCHER><MASTERID>" + id + "</MASTERID><ALTERID>" + alter +
                "</ALTERID><VOUCHERTYPENAME>Purchase</VOUCHERTYPENAME>" +
                "<ALLLEDGERENTRIES.LIST><LEDGERNAME>Purchase</LEDGERNAME>" +
                "<AMOUNT>40000</AMOUNT></ALLLEDGERENTRIES.LIST>" +
                "<ALLLEDGERENTRIES.LIST><LEDGERNAME>" + ledger +
                "</LEDGERNAME><AMOUNT>-40000</AMOUNT></ALLLEDGERENTRIES.LIST></VOUCHER>")
            { AlterId = alter };
            var old = new[] { Actual("35", "37", "Kreativ Creative"),
                              Actual("36", "41", "Unchanged Supplier") };
            var oldXml = new[] { Xml("35", "37", "Kreativ Creative"),
                                 Xml("36", "41", "Unchanged Supplier") };
            repo.ReplaceVoucherIndex(link.CompanyKey, link.FinancialYear, old);
            repo.ReplaceVoucherXmlAuthority(link.CompanyKey, link.FinancialYear, oldXml);
            repo.UpsertBankSyncState(new TallyBankSyncState
            {
                ClientId = 901, CompanyKey = link.CompanyKey,
                FinancialYear = link.FinancialYear, LastAlterId = 672,
                LastFyVoucherCount = 2, LastVoucherCount = 2,
                LastTallyChangeToken = "unreliable-ERP9-pulse",
                LastFullSyncUtc = new DateTime(2026, 11, 7, 3, 10, 0, DateTimeKind.Utc)
            });
            var fresh = new[] { Actual("35", "39", "Kreativ and Creative"),
                                Actual("36", "41", "Unchanged Supplier") };
            var freshXml = new[] { Xml("35", "39", "Kreativ and Creative"),
                                   Xml("36", "41", "Unchanged Supplier") };
            var marker = new TallyVoucherChangeMarker
            {
                HasReliableAlterId = false,
                VoucherCount = 0,
                LastAlterId = 0
            };
            // A missing full signed Voucher object must fail before mutating
            // either saved table or its original normal FY checkpoint.
            Check(RejectsInvalidNativeReadback(repo, link, marker, fresh,
                    new[] { freshXml[1] }),
                "incomplete live XML was accepted as current authority");
            Check(repo.GetVoucherIndexRange(link.CompanyKey, link.FinancialYear,
                    new DateTime(2025, 4, 1), new DateTime(2026, 3, 31))
                .Single(row => row.MasterId == "35").PartyLedger == "Kreativ Creative",
                "failed native readback overwrote old unverified voucher");
            var before = repo.GetBankSyncState(901, link.FinancialYear, link.CompanyKey)!;
            TallyConsolidatedPreImportAuthorityWrapper.PersistVerifiedOriginalLiveVoucherAuthority(
                repo, before, link, marker, fresh, freshXml);
            var readback = repo.GetVoucherIndexRange(link.CompanyKey, link.FinancialYear,
                new DateTime(2025, 4, 1), new DateTime(2026, 3, 31));
            var changed = readback.Single(row => row.MasterId == "35");
            Check(changed.AlterId == "39" && changed.PartyLedger == "Kreativ and Creative",
                "new live MASTERID 35 ALTERID/ledger did not update SQLite voucher index");
            Check(readback.Single(row => row.MasterId == "36").PartyLedger == "Unchanged Supplier",
                "native refresh changed an unrelated voucher");
            var liveFull = repo.GetVoucherXmlAuthorityByMasterIds(
                link.CompanyKey, link.FinancialYear, new[] { "35", "36" });
            Check(liveFull.Single(row => row.MasterId == "35").AlterId == "39" &&
                  liveFull.Single(row => row.MasterId == "35").OriginalXml.Contains(
                      "Kreativ and Creative", StringComparison.Ordinal),
                "new live Voucher XML and ALTERID did not update SQLite full XML authority");
            var after = repo.GetBankSyncState(901, link.FinancialYear, link.CompanyKey)!;
            Check(after.LastFullSyncUtc == before.LastFullSyncUtc &&
                  after.LastFyVoucherCount == 2 &&
                  after.LastAlterId >= 672 &&
                  after.LastIncrementalSyncUtc > DateTime.MinValue,
                "native voucher refresh invented Master baseline or failed to record refreshed authority");
            Console.WriteLine("PASS: real isolated SQLite readback updates CL019 MASTERID 35 from Kreativ Creative to Kreativ and Creative in BOTH original Live Tally index and complete signed XML; old source and other vouchers preserved.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(tempDb + suffix); } catch (IOException) { }
        }

        Console.WriteLine("PASS: consolidated thin-index representation differences are rechecked ONLY through individual native Voucher authority; changed native content/ALTERID remains blocked.");
        Console.WriteLine("PASS: actual consolidated wrapper reuses cached ALTERID only when native live MASTERID/ALTERID and voucher identities match.");
    }
}

// Test-only stand-in for the ordinary Desktop coordinator: this is NOT used
// by production. The linked wrapper's pure private validator is tested
// without involving a Tally network session or any accounting database.
namespace AccountingAutomationHub.Desktop.Services
{
    public sealed class TallyLiveConnectionCoordinator
    {
        public TallyClientLink? GetCurrentClientLink() => null;
        public Task RefreshNowAsync(
            bool forceLinkedSynchronization,
            CancellationToken cancellationToken = default,
            bool isFiveSecondBackgroundPulse = false,
            bool forceFullBaseline = true) => Task.CompletedTask;
        internal Task RunWithLinkedTallyOperationGateAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default) => operation(cancellationToken);
    }
}
