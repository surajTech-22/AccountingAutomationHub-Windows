using AccountingAutomationHub.Domain.Masters;

namespace AccountingAutomationHub.Infrastructure.Masters;

/// <summary>
/// One Tally MASTERID belongs to one Tally company, not merely to a Client/FY.
/// Branch and consolidated Tally books can each contain identically named ledgers
/// (and can independently reuse the same numeric MASTERID).
/// Company GUID prefix outranks a mutable Tally display name.
/// This is only an AAH local identity boundary; it never emits Tally XML.
/// </summary>
public static class TallyCompanyMasterScope
{
    public static string GuidBookKey(string? tallyGuid)
    {
        var value = (tallyGuid ?? string.Empty).Trim();
        // Tally's object GUID format: <company-guid>-<object-hex-id>.
        // Only the fixed company GUID prefix is stable across object changes.
        if (value.Length < 38 || value[36] != '-' ||
            !Guid.TryParseExact(value[..36], "D", out var companyGuid))
            return string.Empty;
        return companyGuid.ToString("D").ToUpperInvariant();
    }

    public static string BookKey(MasterRecord record)
    {
        var guid = GuidBookKey(record.TallyGuid);
        return guid.Length > 0 ? "GUID:" + guid
            : "NAME:" + record.Company.Trim().ToUpperInvariant();
    }

    public static bool CanMatch(MasterRecord stored, MasterRecord incoming)
    {
        var oldBook = GuidBookKey(stored.TallyGuid);
        var newBook = GuidBookKey(incoming.TallyGuid);
        // Two complete company GUIDs are definitive, including when a
        // branch and consolidated ledger have the SAME name or MASTERID.
        if (oldBook.Length > 0 && newBook.Length > 0)
            return oldBook.Equals(newBook, StringComparison.OrdinalIgnoreCase);

        var oldCompany = stored.Company.Trim();
        var newCompany = incoming.Company.Trim();
        if (oldCompany.Length > 0 && newCompany.Length > 0)
            return oldCompany.Equals(newCompany, StringComparison.OrdinalIgnoreCase);

        // A previously Tally-bound object from an unknown company must not
        // be claimed by another company's live snapshot using only MASTERID/name.
        if (stored.TallyMasterId.Trim().Length > 0 ||
            stored.TallyGuid.Trim().Length > 0)
            return false;

        // An unbound legacy/manual row may be adopted on first verified sync.
        // Never move an explicitly company-owned unbound row to another book.
        return oldCompany.Length == 0;
    }

    public static bool BelongsToSnapshot(
        MasterRecord existing, string companyName, string? liveBookGuid)
    {
        var probe = new MasterRecord
        {
            Company = companyName,
            TallyGuid = liveBookGuid ?? string.Empty
        };
        return CanMatch(existing, probe);
    }
}
