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
