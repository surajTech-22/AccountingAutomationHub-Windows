using System.Text;
using System.Xml.Linq;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Tally;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Executes only the first (master) stage of a same-PAN consolidated import.
/// Reuses the application's existing Tally HTTP transport and single-flight gate.
/// It deliberately never treats an XML preview or Tally's CREATED counter as
/// evidence of voucher success; that needs independently verified source rows.
/// </summary>
public sealed class TallyBranchConsolidationImportService(TallyHttpService tally)
{
    public async Task<TallyBranchMasterImportResult> ImportMastersAsync(
        TallyClientLink confirmedLink,
        TallyCompanyInfo destination,
        IReadOnlyCollection<TallyBranchMasterIdentity> branches,
        bool separateVoucherTypes = false,
        bool separateCashLedgers = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmedLink);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(branches);
        if (confirmedLink.ClientId <= 0 ||
            !confirmedLink.CompanyKey.Equals(destination.CompanyKey, StringComparison.OrdinalIgnoreCase) ||
            !confirmedLink.CompanyName.Equals(destination.Name, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(destination.CompanyKey) ||
            string.IsNullOrWhiteSpace(destination.FinancialYear) ||
            !confirmedLink.FinancialYear.Equals(destination.FinancialYear, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Confirm the exact writable consolidated Client/FY Tally link before master import.");

        // Check Tally itself, not a remembered dropdown item or branch company.
        var live = await tally.DiscoverCompaniesAsync(cancellationToken).ConfigureAwait(false);
        if (live.Count(x => x.CompanyKey.Equals(destination.CompanyKey, StringComparison.OrdinalIgnoreCase) &&
                            x.Name.Equals(destination.Name, StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidOperationException("The linked consolidated Tally company is not uniquely open; no master sent.");

        // Read live masters immediately before planning. Local SQLite name matches
        // are never permission to ALTER/overwrite a foreign Tally master.
        var before = ReadLiveMasters(await tally.ExportMastersAsync(destination, cancellationToken)
            .ConfigureAwait(false));
        var allVoucherTypes = before.Where(x => x.Type == "Voucher Type")
            .Select(x => x.Name)
            .Where(x => !branches.Any(b => x.EndsWith(" - " + b.DisplayName, StringComparison.OrdinalIgnoreCase))) // exclude already-generated branch types only
            .ToArray();
        var plan = TallyBranchConsolidationMasterPlanner.Build(
            destination.Name, destination.CompanyKey, destination.FinancialYear,
            branches, before, true, separateVoucherTypes, separateCashLedgers,
            separateVoucherTypes ? allVoucherTypes : null);
        TallyBranchConsolidationMasterPlanner.ValidatePlanXml(plan);

        // Fail before ANY writes when a base type or generated destination name
        // is unverified. Mandatory groups are independent of the two options.
        if (separateVoucherTypes && allVoucherTypes.Length == 0)
            throw new InvalidOperationException("Tally did not return a voucher type catalog.");
        var toCreate = plan.Groups.Where(x => x.Create)
            .Select(x => new MasterRecord { Type = "Group", Name = x.Name, Parent = x.Parent })
            .ToArray();
        var cash = plan.CashLedgers.Where(x => x.Create)
            .Select(x => new MasterRecord { Type = "Ledger", Name = x.Name, Parent = "Cash-in-Hand" })
            .ToArray();

        int createdGroups = 0, createdCash = 0, createdTypes = 0;
        // One-by-one existing Upsert validates Tally's response AND reads the
        // resulting parent from Tally. A failure halts the stage, never advances
        // to vouchers, and a retry re-plans from fresh live Masters.
        foreach (var group in toCreate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireOneCreate(await tally.UpsertAccountingMasterAsync(
                destination, group, "Create", cancellationToken).ConfigureAwait(false), group.Name);
            createdGroups++;
        }
        foreach (var ledger in cash)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireOneCreate(await tally.UpsertAccountingMasterAsync(
                destination, ledger, "Create", cancellationToken).ConfigureAwait(false), ledger.Name);
            createdCash++;
        }

        // Optional: use the ORIGINAL All Masters XML import transport, with
        // exactly one voucher type per request, and confirm each new MASTERID
        // and inherited base type with an independent fresh export.
        foreach (var requirement in plan.VoucherTypes.Where(x => x.Create))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var xml = SingleVoucherTypeXml(plan.VoucherTypeXml, destination.Name, requirement.Name);
            var response = TallyHttpService.ParseImportResponse(
                await tally.ImportXmlAsync(xml, cancellationToken).ConfigureAwait(false));
            RequireOneCreate(response, requirement.Name);
            var readback = ReadLiveMasters(await tally.ExportMastersAsync(destination, cancellationToken)
                .ConfigureAwait(false));
            RequireMaster(readback, "Voucher Type", requirement.Name, requirement.BaseType);
            createdTypes++;
        }

        // Final read-back confirms compulsory groups, optional masters and
        // durable Tally MASTERIDs even if Tally initially acknowledged a post.
        var after = ReadLiveMasters(await tally.ExportMastersAsync(destination, cancellationToken)
            .ConfigureAwait(false));
        foreach (var g in plan.Groups) RequireMaster(after, "Group", g.Name, g.Parent);
        foreach (var l in plan.CashLedgers) RequireMaster(after, "Ledger", l.Name, "Cash-in-Hand");
        foreach (var v in plan.VoucherTypes) RequireMaster(after, "Voucher Type", v.Name, v.BaseType);
        return new TallyBranchMasterImportResult(
            destination.CompanyKey, plan.Pan, plan.FinancialYear, plan.Groups.Count,
            createdGroups, createdCash, createdTypes,
            "Branch master read-back passed. No consolidated vouchers were posted. " +
            "Each branch GST ledger remains source-owned; no GST journal was regenerated.");
    }

    private static string SingleVoucherTypeXml(string batchXml, string company, string name)
    {
        var source = XDocument.Parse(batchXml);
        var nodes = source.Descendants("TALLYMESSAGE")
            .Where(x => x.Element("VOUCHERTYPE")?.Attribute("NAME")?.Value == name).ToArray();
        if (nodes.Length != 1)
            throw new InvalidOperationException("Voucher type XML must identify exactly one planned master.");
        var sb = new StringBuilder();
        TallyExcelParityXmlWriter.BeginAllMastersImport(sb, company);
        sb.AppendLine(nodes[0].ToString(SaveOptions.DisableFormatting));
        TallyExcelParityXmlWriter.EndAllMastersImport(sb);
        return sb.ToString();
    }

    public static void RequireOneCreate(TallyImportResponse response, string masterName)
    {
        if (response.Created != 1 || response.Errors != 0 || response.Exceptions != 0 ||
            response.Altered != 0 || response.Deleted != 0 || response.Ignored != 0 ||
            !string.IsNullOrWhiteSpace(response.LineError))
            throw new InvalidOperationException(
                "Tally did not confirm exactly one new master '" + masterName + "': " +
                $"Created={response.Created}, Altered={response.Altered}, Ignored={response.Ignored}, " +
                $"Exceptions={response.Exceptions}, Errors={response.Errors}. {response.LineError} " +
                "Reopen master import to re-read Tally before any retry.");
    }

    private static void RequireMaster(
        IReadOnlyCollection<MasterRecord> masters, string type, string name, string parent)
    {
        var matches = masters.Where(x => x.Type.Equals(type, StringComparison.OrdinalIgnoreCase) &&
                                         x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 ||
            !matches[0].Parent.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(matches[0].TallyMasterId))
            throw new InvalidOperationException(
                $"Tally read-back did not verify '{name}' under '{parent}' with a MASTERID. " +
                "No voucher import is allowed.");
    }

    /// <summary>Parse the existing Tally master export; never substitute AAH's local master cache.</summary>
    public static IReadOnlyList<MasterRecord> ReadLiveMasters(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new InvalidOperationException("No Tally master XML was returned.");
        var doc = XDocument.Parse(xml, LoadOptions.None);
        var masters = new List<MasterRecord>();
        foreach (var element in doc.Descendants().Where(x =>
                     x.Name.LocalName is "GROUP" or "LEDGER" or "VOUCHERTYPE"))
        {
            static string Field(XElement el, string key) =>
                el.Elements().FirstOrDefault(x => x.Name.LocalName.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value?.Trim()
                ?? el.Attributes().FirstOrDefault(x => x.Name.LocalName.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value?.Trim()
                ?? "";
            var name = Field(element, "NAME");
            if (name.Length == 0) continue;
            masters.Add(new MasterRecord
            {
                Type = element.Name.LocalName switch { "GROUP" => "Group", "LEDGER" => "Ledger", _ => "Voucher Type" },
                Name = name,
                Parent = Field(element, "PARENT"),
                TallyMasterId = Field(element, "MASTERID")
            });
        }
        if (masters.Count == 0)
            throw new InvalidOperationException("Tally returned no readable master objects.");

        // Multiple Tally collection reports may echo the same object. Do not
        // mistake an echo for another master, but reject conflicting identifiers.
        var distinct = new List<MasterRecord>();
        foreach (var group in masters.GroupBy(x => (x.Type.ToUpperInvariant(), x.Name.ToUpperInvariant())))
        {
            var entries = group.ToArray();
            var parents = entries.Select(x => x.Parent).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var ids = entries.Select(x => x.TallyMasterId).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (parents.Length > 1 || ids.Length > 1)
                throw new InvalidOperationException("Tally returned conflicting Master identities for " + entries[0].Name);
            distinct.Add(new MasterRecord
            {
                Type = entries[0].Type, Name = entries[0].Name,
                Parent = parents.FirstOrDefault() ?? "",
                TallyMasterId = ids.FirstOrDefault() ?? ""
            });
        }
        return distinct;
    }
}

public sealed record TallyBranchMasterImportResult(
    string DestinationCompanyKey, string Pan, string FinancialYear,
    int RequiredGroups, int CreatedGroups, int CreatedCashLedgers,
    int CreatedVoucherTypes, string Status);
