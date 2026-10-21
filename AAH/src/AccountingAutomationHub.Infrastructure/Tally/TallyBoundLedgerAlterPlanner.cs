using System.Globalization;
using System.Security;
using System.Text;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Tally;

namespace AccountingAutomationHub.Infrastructure.Tally;

internal sealed record TallyBoundLedgerAlterPlan(
    long BankAccountId,
    string MasterId,
    string Guid,
    long PreviousAlterId,
    string CurrentName,
    string CurrentParent,
    string DesiredName,
    string DesiredParent);

internal static class TallyBoundLedgerAlterPlanner
{
    public static bool HasPendingEdits(
        IReadOnlyCollection<BankAccountRecord> bankAccounts,
        IReadOnlyCollection<MasterRecord> masters)
    {
        var ids = bankAccounts.Where(account => account.TallyLedgerMasterId.Trim().Length > 0)
            .Select(account => account.TallyLedgerMasterId.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return masters.Any(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                     master.IsUserEdited && master.TallyMasterId.Trim().Length > 0 &&
                                     ids.Contains(master.TallyMasterId.Trim()));
    }

    public static IReadOnlyList<TallyBoundLedgerAlterPlan> Build(
        IReadOnlyCollection<BankAccountRecord> bankAccounts,
        IReadOnlyCollection<MasterRecord> masters,
        IReadOnlyCollection<TallyLedgerNameAuthority> liveAuthorities)
    {
        var result = new List<TallyBoundLedgerAlterPlan>();
        foreach (var account in bankAccounts.Where(account => account.TallyLedgerMasterId.Trim().Length > 0))
        {
            var masterId = account.TallyLedgerMasterId.Trim();
            var localMatches = masters.Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                                        !master.IsArchived &&
                                                        master.TallyMasterId.Trim().Equals(masterId, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            if (localMatches.Length != 1 || !localMatches[0].IsUserEdited) continue;

            var liveMatches = liveAuthorities.Where(authority => authority.MasterId.Trim().Equals(masterId, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            if (liveMatches.Length != 1)
                throw new InvalidOperationException($"Tally Ledger MASTERID {masterId} resolved to {liveMatches.Length} live Ledgers while a bound AAH edit is pending.");

            var local = localMatches[0];
            var live = liveMatches[0];
            var desiredName = account.LedgerName.Trim().Length > 0 ? account.LedgerName.Trim() : local.Name.Trim();
            var desiredParent = string.IsNullOrWhiteSpace(local.Parent) ? "Primary" : local.Parent.Trim();
            var currentName = !string.IsNullOrEmpty(live.ExactName) ? live.ExactName : live.DisplayName.Trim();
            var currentParent = string.IsNullOrWhiteSpace(live.Parent) ? "Primary" : live.Parent.Trim();

            // Fix77: a changed Ledger NAME is a separate Master request, never a rename
            // of this existing MASTERID. The old Master stays active. Same-MASTERID ALTER
            // is retained only for a Parent/group change while the Ledger name is unchanged.
            if (!SameLedgerName(desiredName, currentName))
                continue;
            if (desiredParent.Equals(currentParent, StringComparison.OrdinalIgnoreCase))
                continue;

            // Previous-version safety: the pending AAH Parent edit is allowed to ALTER only if
            // Tally still exposes the exact ALTERID that was bound before the local edit.
            var savedAlter = account.TallyLedgerAlterId > 0 ? account.TallyLedgerAlterId : local.TallyAlterId;
            if (savedAlter <= 0 || live.AlterId <= 0 || savedAlter != live.AlterId ||
                (local.TallyAlterId > 0 && local.TallyAlterId != live.AlterId))
                throw new InvalidOperationException(
                    $"Ledger MASTERID {masterId} changed in Tally after AAH's previous verified version. " +
                    $"Saved ALTERID={savedAlter.ToString(CultureInfo.InvariantCulture)}, live ALTERID={live.AlterId.ToString(CultureInfo.InvariantCulture)}. " +
                    "The pending AAH Parent/group change was not sent; refresh/review the external Tally change first.");

            result.Add(new TallyBoundLedgerAlterPlan(
                account.Id, masterId, live.Guid.Trim(), live.AlterId,
                currentName, currentParent, desiredName, desiredParent));
        }
        return result;
    }

    public static string BuildXml(string companyName, IReadOnlyCollection<TallyBoundLedgerAlterPlan> plans)
    {
        var builder = new StringBuilder();
        TallyExcelParityXmlWriter.BeginAllMastersImport(builder, companyName);
        foreach (var plan in plans)
        {
            builder.AppendLine("  <TALLYMESSAGE xmlns:UDF=\"TallyUDF\">");
            builder.Append("    <LEDGER NAME=\"").Append(XmlAttribute(plan.CurrentName)).AppendLine("\" RESERVEDNAME=\"\" ACTION=\"Alter\">");
            Tag(builder, "MASTERID", plan.MasterId, 6);
            if (plan.Guid.Length > 0) Tag(builder, "GUID", plan.Guid, 6);
            Tag(builder, "NAME", plan.DesiredName, 6);
            Tag(builder, "PARENT", plan.DesiredParent, 6);
            builder.AppendLine("    </LEDGER>");
            builder.AppendLine("  </TALLYMESSAGE>");
        }
        TallyExcelParityXmlWriter.EndAllMastersImport(builder);
        return builder.ToString();
    }


    private static bool SameLedgerName(string? first, string? second) =>
        string.Equals((first ?? string.Empty).Trim(), (second ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static void Tag(StringBuilder builder, string name, string value, int indent) =>
        builder.Append(' ', indent).Append('<').Append(name).Append('>')
            .Append(SecurityElement.Escape(value) ?? string.Empty)
            .Append("</").Append(name).AppendLine(">");

    private static string XmlAttribute(string value) =>
        (SecurityElement.Escape(value) ?? string.Empty).Replace("'", "&apos;", StringComparison.Ordinal);
}
