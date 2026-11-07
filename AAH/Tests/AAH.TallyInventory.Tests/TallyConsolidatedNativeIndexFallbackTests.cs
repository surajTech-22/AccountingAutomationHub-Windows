using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AccountingAutomationHub.Domain.Tally;
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
        // The normal RefreshNowAsync heartbeat UI may catch and log an
        // unsuccessful baseline rather than throw. Its OLD checkpoint is not
        // sufficient evidence for consolidated CREATE/ALTER even if the
        // native Tally connection remains available.
        var prior = new TallyBankSyncState
        {
            LastFullSyncUtc = new DateTime(2026, 11, 7, 3, 20, 0, DateTimeKind.Utc),
            LastAlterId = 672,
            LastFyVoucherCount = 645,
            LastStatus = "Manual refresh: complete FY baseline synchronized at ALTERID 672"
        };
        var advanced = new TallyBankSyncState
        {
            LastFullSyncUtc = prior.LastFullSyncUtc.AddMinutes(1),
            LastAlterId = 800,
            LastFyVoucherCount = 645,
            LastStatus = "Manual refresh: complete FY baseline synchronized at ALTERID 800"
        };
        TallyConsolidatedPreImportAuthorityWrapper.RequireFreshNormalBaseline(prior, advanced);
        bool BaselineRejected(TallyBankSyncState? after)
        {
            try
            {
                TallyConsolidatedPreImportAuthorityWrapper.RequireFreshNormalBaseline(prior, after);
                return false;
            }
            catch (InvalidOperationException) { return true; }
        }
        Check(BaselineRejected(null), "missing normal sync checkpoint must block import");
        Check(BaselineRejected(prior), "unchanged old FY baseline must block import");
        Check(BaselineRejected(new TallyBankSyncState
            {
                LastFullSyncUtc = advanced.LastFullSyncUtc,
                LastAlterId = advanced.LastAlterId,
                LastFyVoucherCount = advanced.LastFyVoucherCount,
                LastStatus = "Manual refresh failed; retaining previous status"
            }), "a newer time without a complete FY baseline must block import");
        Check(BaselineRejected(new TallyBankSyncState
            {
                LastFullSyncUtc = advanced.LastFullSyncUtc,
                LastAlterId = advanced.LastAlterId,
                LastFyVoucherCount = -1,
                LastStatus = advanced.LastStatus
            }), "unverified FY Voucher count must block import");
        Console.WriteLine("PASS: consolidated rejects stale/failed normal linked-Tally baseline before interpreting saved voucher MASTERIDs.");

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
    }
}
