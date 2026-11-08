using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Tally;

internal static class TallyNativeAlterIdCheckpointTests
{
    public static void Run()
    {
        // Exact CL019 8-Nov-2026 scenario: original synchronized max 7879,
        // all 645 separately verified adaptive ALTER vouchers 7881..8525.
        // The normal link must read the ORIGINAL master+voucher delta before
        // advancing its company checkpoint. The Cash master may be 7880.
        var state = new TallyBankSyncState
        {
            ClientId = 19, FinancialYear = "2025-26", CompanyKey = "cl019-book",
            LastFullSyncUtc = DateTime.UtcNow,
            LastAlterId = 7879, LastFyVoucherCount = 645
        };
        var index = Enumerable.Range(1, 645).Select((n, i) =>
            new TallyVoucherIndexRecord
            {
                CompanyKey = "cl019-book", FinancialYear = "2025-26",
                MasterId = n.ToString(), Guid = "CL019-GUID-" + n,
                AlterId = (7881 + i).ToString(), VoucherDate = new DateTime(2025, 4, 1)
            }).ToArray();
        var full = index.Select(row => new TallyVoucherXmlSnapshot(
            row.MasterId, row.Guid, "Contra", row.VoucherDate,
            "<VOUCHER><MASTERID>" + row.MasterId +
            "</MASTERID><ALTERID>" + row.AlterId + "</ALTERID></VOUCHER>")
            { AlterId = row.AlterId }).ToArray();

        static void Check(bool condition, string why)
        {
            if (!condition) throw new InvalidOperationException(why);
        }
        Check(TallyNativeAlterIdCheckpointPolicy.GetUnconsumedVerifiedFyAlterId(
            state, index, full) == 8525,
            "CL019 645 verified ALTERs should awaken normal Master+Voucher delta even when marker is 7879.");
        TallyNativeAlterIdCheckpointPolicy.RequireDeltaCoversUnconsumedVouchers(
            index, index, state.LastAlterId);

        bool RejectedDelta(IReadOnlyList<TallyVoucherIndexRecord> delta)
        {
            try
            {
                TallyNativeAlterIdCheckpointPolicy.RequireDeltaCoversUnconsumedVouchers(
                    index, delta, state.LastAlterId);
                return false;
            }
            catch (InvalidOperationException) { return true; }
        }
        Check(RejectedDelta(index.Skip(1).ToArray()),
            "A missing verified Tally delta MASTERID must block checkpoint advancement.");
        var stale = index.Select(x => new TallyVoucherIndexRecord
        {
            MasterId = x.MasterId, AlterId = x.AlterId, Guid = x.Guid
        }).ToArray();
        stale[0].AlterId = "7879";
        Check(RejectedDelta(stale),
            "An incorrect native delta ALTERID must not advance the normal linked checkpoint.");
        var corrupt = full.Take(644).ToArray();
        Check(TallyNativeAlterIdCheckpointPolicy.GetUnconsumedVerifiedFyAlterId(
                  state, index, corrupt) == 0,
            "Incomplete native full XML cannot authorize a checkpoint.");
        var changedFull = full.Select(x => x).ToArray();
        changedFull[0] = new TallyVoucherXmlSnapshot(
            index[0].MasterId, index[0].Guid, "Contra",
            new DateTime(2025, 4, 1),
            "<VOUCHER><ALTERID>7879</ALTERID></VOUCHER>")
            { AlterId = "7879" };
        Check(TallyNativeAlterIdCheckpointPolicy.GetUnconsumedVerifiedFyAlterId(
                  state, index, changedFull) == 0,
            "Mismatched native full-XML ALTERID must not authorize a checkpoint.");
        state.LastAlterId = 8525;
        Check(TallyNativeAlterIdCheckpointPolicy.GetUnconsumedVerifiedFyAlterId(
                  state, index, full) == 0,
            "A fully consumed native ALTERID must not re-trigger delta reads.");
        Console.WriteLine("PASS: CL019 7879->8525 normal Tally marker consumes verified 645 voucher " +
            "changes via original Master+Voucher delta; incomplete/mismatched authority blocked.");
    }
}
