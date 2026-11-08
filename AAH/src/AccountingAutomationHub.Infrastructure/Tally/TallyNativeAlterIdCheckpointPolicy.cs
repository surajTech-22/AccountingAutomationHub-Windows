using System.Globalization;
using AccountingAutomationHub.Domain.Tally;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// The full native READBACK after a consolidated write updates Voucher Index/XML,
/// but it must not silently consume the normal linked-company sync checkpoint.
/// The ordinary link's existing Master+Voucher ALTERID delta reader remains
/// responsible for consuming ALL object changes, including a Cash master rename.
/// </summary>
public static class TallyNativeAlterIdCheckpointPolicy
{
    public static long GetUnconsumedVerifiedFyAlterId(
        TallyBankSyncState checkpoint,
        IReadOnlyList<TallyVoucherIndexRecord> savedIndex,
        IReadOnlyList<TallyVoucherXmlSnapshot> savedFull)
    {
        if (checkpoint is null || checkpoint.LastFullSyncUtc == DateTime.MinValue ||
            checkpoint.LastAlterId <= 0 || checkpoint.LastFyVoucherCount < 0 ||
            savedIndex.Count != checkpoint.LastFyVoucherCount ||
            savedFull.Count != savedIndex.Count ||
            checkpoint.CompanyKey.Trim().Length == 0)
            return 0;
        var fullById = new Dictionary<string, TallyVoucherXmlSnapshot>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in savedFull)
        {
            var id = snapshot.MasterId.Trim();
            if (id.Length == 0 || !fullById.TryAdd(id, snapshot) ||
                snapshot.OriginalXml.Trim().Length == 0)
                return 0;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var highest = checkpoint.LastAlterId;
        foreach (var row in savedIndex)
        {
            var id = row.MasterId.Trim();
            if (id.Length == 0 || !seen.Add(id) ||
                !row.CompanyKey.Equals(checkpoint.CompanyKey, StringComparison.OrdinalIgnoreCase) ||
                !long.TryParse(row.AlterId.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var alt) || alt <= 0 ||
                !fullById.TryGetValue(id, out var native) ||
                !string.Equals(row.AlterId.Trim(), native.AlterId.Trim(),
                    StringComparison.OrdinalIgnoreCase) ||
                (row.Guid.Trim().Length > 0 && native.Guid.Trim().Length > 0 &&
                 !row.Guid.Trim().Equals(native.Guid.Trim(),
                    StringComparison.OrdinalIgnoreCase)))
                return 0;
            highest = Math.Max(highest, alt);
        }
        return highest > checkpoint.LastAlterId ? highest : 0;
    }

    // The normal ALTERID delta must include EVERY already-native-verified
    // post-import row newer than the saved checkpoint. If ERP9/Prime returns
    // an incomplete delta, do not advance the checkpoint or accept new writes.
    public static void RequireDeltaCoversUnconsumedVouchers(
        IReadOnlyList<TallyVoucherIndexRecord> previouslyVerified,
        IReadOnlyList<TallyVoucherIndexRecord> nativeDelta,
        long previousCheckpoint)
    {
        var deltaById = nativeDelta.GroupBy(x => x.MasterId.Trim(),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var cached in previouslyVerified)
        {
            if (!long.TryParse(cached.AlterId.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var alterId) ||
                alterId <= previousCheckpoint)
                continue;
            if (!deltaById.TryGetValue(cached.MasterId.Trim(), out var matching) ||
                matching.Length != 1 ||
                !string.Equals(matching[0].AlterId.Trim(), cached.AlterId.Trim(),
                    StringComparison.OrdinalIgnoreCase) ||
                (cached.Guid.Trim().Length > 0 && matching[0].Guid.Trim().Length > 0 &&
                 !string.Equals(matching[0].Guid.Trim(), cached.Guid.Trim(),
                     StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "The normal Tally Master+Voucher ALTERID delta omitted or changed " +
                    "a previously native-verified consolidated voucher MASTERID " +
                    cached.MasterId + ". Saved linked checkpoint was NOT advanced.");
        }
    }
}
