using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Tally;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Read-only adapter from the already existing AAH live XML preparation to
/// the consolidated branch wrapper. No new Sales/Purchase/GST-journal generator.
/// The original AAH preview is authoritative for what is already saved.
/// </summary>
public static class TallyConsolidatedSourcePreviewAdapter
{
    public static TallyConsolidatedBranchXmlInput Extract(
        TallyBranchMasterIdentity branch,
        TallyLiveAccountingXmlSnapshot prepared,
        IReadOnlyCollection<MasterRecord> savedBranchMasters, string sourceState = "")
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(savedBranchMasters);
        if (prepared.Unsupported > 0 || prepared.MissingLedgers > 0 ||
            prepared.ConflictsBlocked > 0 || prepared.UnverifiedSourceRecordCount() > 0)
            throw new InvalidOperationException("Source branch has unresolved accounting/master conflicts; cannot silently omit them from Consolidated.");
        if (string.IsNullOrWhiteSpace(prepared.Xml))
            throw new InvalidOperationException("Branch accounting preview is unavailable.");

        XDocument doc;
        try { doc = XDocument.Parse(TallyXmlSanitizer.Sanitize(prepared.Xml)); }
        catch (Exception exception) when (exception is System.Xml.XmlException or ArgumentException)
        { throw new InvalidOperationException("Original branch Tally XML cannot be parsed safely.", exception); }

        var candidate = savedBranchMasters.Where(x =>
                !x.IsArchived && x.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);

        // The original exporter can synthesize required Ledger masters before
        // the destination has them. Their PARENT is an authoritative AAH mapping
        // for this source preview, not a guess from the ledger name.
        foreach (var ledger in doc.Descendants().Where(x => x.Name.LocalName.Equals("LEDGER", StringComparison.OrdinalIgnoreCase)))
        {
            var name = ledger.Elements().FirstOrDefault(x => x.Name.LocalName == "NAME")?.Value.Trim()
                       ?? ledger.Attribute("NAME")?.Value.Trim() ?? "";
            var parent = ledger.Elements().FirstOrDefault(x => x.Name.LocalName == "PARENT")?.Value.Trim() ?? "";
            if (name.Length == 0 || parent.Length == 0) continue;
            if (!candidate.ContainsKey(name))
                candidate[name] = [new MasterRecord { Name = name, Parent = parent, Type = "Ledger" }];
            else if (candidate[name].Length != 1 || !candidate[name][0].Parent.Equals(parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Saved and generated source ledger parents conflict: " + name);
        }

        var vouchers = new List<TallyConsolidatedSavedVoucher>();
        var usedLedgerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenXmlIdentity = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var voucher in doc.Descendants().Where(x => x.Name.LocalName == "VOUCHER"))
        {
            var type = voucher.Elements().FirstOrDefault(x => x.Name.LocalName == "VOUCHERTYPENAME")?.Value.Trim() ?? "";
            if (type.Length == 0)
                throw new InvalidOperationException("Saved branch voucher has no Tally voucher type.");
            foreach (var entry in voucher.Descendants().Where(x =>
                         x.Name.LocalName is "LEDGERNAME" or "PARTYLEDGERNAME" or "PARTYNAME" or "BASICBASEPARTYNAME"))
                if (entry.Value.Trim().Length > 0)
                    usedLedgerNames.Add(entry.Value.Trim());

            var xml = voucher.ToString(SaveOptions.DisableFormatting);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml)));
            if (!seenXmlIdentity.Add(hash))
                throw new InvalidOperationException("Two byte-identical vouchers were prepared by one branch. Exact source identity must be reviewed before consolidation.");

            var taxUnit = voucher.Elements().FirstOrDefault(x => x.Name.LocalName == "TAXUNITNAME")?.Value.Trim() ?? "";
            // SourceHash is a PREVIEW fingerprint only, not a replacement for
            // durable source anchor/version and Tally destination MASTERID.
            vouchers.Add(new TallyConsolidatedSavedVoucher(type, hash, xml,
                IsSavedSource: true, IsGstRelevant: type is "Sales" or "Purchase",
                VerifiedTaxUnitName: taxUnit));
        }

        var ledgers = usedLedgerNames.Select(name =>
        {
            if (!candidate.TryGetValue(name, out var saved) || saved.Length != 1 ||
                saved[0].Parent.Trim().Length == 0)
                throw new InvalidOperationException($"Branch '{branch.DisplayName}' ledger '{name}' has no unambiguous saved source parent.");
            return new TallyConsolidatedSourceLedger(name, saved[0].Parent.Trim());
        }).ToArray();

        return new TallyConsolidatedBranchXmlInput(branch, ledgers, vouchers, sourceState);
    }

    private static int UnverifiedSourceRecordCount(this TallyLiveAccountingXmlSnapshot prepared) =>
        prepared.Ready < 0 || prepared.Selected < prepared.Ready ? 1 : 0;
}
