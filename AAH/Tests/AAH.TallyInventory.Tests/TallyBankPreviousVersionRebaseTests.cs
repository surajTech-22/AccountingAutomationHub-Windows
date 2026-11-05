using System.Reflection;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Tally;

internal static class TallyBankPreviousVersionRebaseTests
{
    private static void Check(bool ok, string why)
    {
        if (!ok) throw new InvalidOperationException("Bank previous version contract: " + why);
    }

    public static void Run()
    {
        var method = typeof(TallyBankVoucherImportService).GetMethod(
            "CanRebaseBoundBankPreviousVersion", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Original Bank bound previous-version gate missing.");

        var saved = new TallySourceBindingVersionState
        {
            State="Verified", MasterId="354",
            VerifiedContentHash=new string('A',64),
            VerifiedVersion="DATE=20250521|TYPE=RECEIPT|BANKMID=1972|COUNTERMID=90",
            LastVerifiedAlterId=377
        };
        var live = new TallyVoucherIndexRecord
        {
            MasterId="354", AlterId="377", VoucherType="Receipt"
        };
        bool Allowed(bool staged=false) => (bool)method.Invoke(null,
            [saved, "354", live, staged])!;
        Check(Allowed(), "verified same MASTERID/ALTERID cannot reconstruct previous version");
        saved.LastVerifiedAlterId=376;
        Check(!Allowed(), "changed live ALTERID authorized mutation");
        saved.LastVerifiedAlterId=377;
        live.MasterId="355";
        Check(!Allowed(), "different voucher MASTERID authorized ledger switch");
        live.MasterId="354";
        saved.State="AcceptedAwaitingVerification";
        Check(!Allowed(true), "unverified accepted write authorized another ALTER");
        saved.State="ModifyPending";
        Check(!Allowed(), "staged voucher was treated as already verified during initial classification");
        Check(Allowed(true), "same staged voucher was blocked at final pre-ALTER gate");
        saved.VerifiedContentHash="";
        Check(!Allowed(true), "missing original verified fingerprint permitted ALTER");
        saved.VerifiedContentHash=new string('A',64);
        saved.LastVerifiedAlterId=0;
        Check(!Allowed(true), "missing saved ALTERID permitted ALTER");
        Console.WriteLine("PASS: individual Bank Voucher previous-version rebase requires original verified MASTERID and unchanged Tally ALTERID.");
    }
}
