using System.Globalization;
using System.Text;
using System.Xml.Linq;
using AccountingAutomationHub.Domain.Tally;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// The only live consolidated voucher writer. Never posts the parent client
/// voucher set, and never infers accounting amounts from totals or GST rates.
/// Each prepared branch voucher is CREATE-only, protected by a fresh target
/// voucher identity snapshot and a separate post-write Tally readback.
/// </summary>
public sealed class TallyConsolidatedVoucherImportService(TallyHttpService tally)
{
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;
    private static readonly string[] PriorMasters =
        ["GROUP", "VOUCHERTYPE"];
    private static readonly string[] MasterOrder =
        ["UNIT", "STOCKCATEGORY", "STOCKGROUP", "GODOWN",
         "COSTCENTRE", "LEDGER", "STOCKITEM"];

    public async Task<TallyConsolidatedVoucherImportResult> ImportAsync(
        TallyClientLink link, TallyCompanyInfo destination,
        TallyConsolidatedPreparedXmlResult prepared,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(prepared);
        if (link.ClientId <= 0 ||
            !IgnoreCase.Equals(link.CompanyKey, destination.CompanyKey) ||
            !IgnoreCase.Equals(link.CompanyName, destination.Name) ||
            !IgnoreCase.Equals(link.FinancialYear, destination.FinancialYear))
            throw new InvalidOperationException("Consolidated destination Tally link or financial year changed.");

        var masterDoc = XDocument.Parse(prepared.MastersXml, LoadOptions.None);
        var voucherDoc = XDocument.Parse(prepared.VouchersXml, LoadOptions.None);
        RequireDestination(masterDoc, destination.Name);
        RequireDestination(voucherDoc, destination.Name);
        var masters = Messages(masterDoc).Select(x => x.Elements().Single()).ToArray();
        var vouchers = Messages(voucherDoc).Select(x => x.Elements().Single()).ToArray();
        if (vouchers.Length == 0 || vouchers.Length != prepared.FinalVouchers ||
            !vouchers.All(x => Is(x, "VOUCHER")))
            throw new InvalidOperationException("Prepared consolidated voucher count/shape is not authoritative.");

        var distinct = new HashSet<string>(IgnoreCase);
        var source = new List<ExpectedVoucher>();
        foreach (var voucher in vouchers)
        {
            if (!string.Equals(Field(voucher, "ACTION"), "Create", StringComparison.OrdinalIgnoreCase) ||
                Field(voucher, "TAGNAME").Length > 0 || Field(voucher, "TAGVALUE").Length > 0 ||
                voucher.DescendantsAndSelf().Any(x =>
                    Is(x, "MASTERID") || Is(x, "GUID") || Is(x, "ALTERID") ||
                    x.Attributes().Any(attribute =>
                        attribute.Name.LocalName is "GUID" or "MASTERID" or "ALTERID" or "TAGNAME" or "TAGVALUE")))
                throw new InvalidOperationException("Consolidated target cannot replay a foreign ALTER, MASTERID or GUID.");
            var dateText = Value(voucher, "DATE");
            if (!DateTime.TryParseExact(dateText, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
                throw new InvalidOperationException("Consolidated voucher has an invalid date.");
            var type = Value(voucher, "VOUCHERTYPENAME");
            var number = Value(voucher, "VOUCHERNUMBER");
            if (type.Length == 0 || number.Length == 0 ||
                !distinct.Add(Key(date, type, number)))
                throw new InvalidOperationException("Consolidated voucher identity is missing or repeated.");
            var entries = ReadPostings(voucher);
            if (entries.Count == 0)
                throw new InvalidOperationException("Prepared voucher has no ledger posting authority: " + number);
            source.Add(new ExpectedVoucher(date, type, number, entries, voucher));
        }

        // A failed live read means no mutations. No cached branch or destination
        // data may authorize a CREATE against a Tally company.
        var open = await tally.DiscoverCompaniesAsync(cancellationToken).ConfigureAwait(false);
        if (open.Count(x => IgnoreCase.Equals(x.CompanyKey, destination.CompanyKey) &&
                            IgnoreCase.Equals(x.Name, destination.Name)) != 1)
            throw new InvalidOperationException("The exact linked consolidated Tally company is not uniquely open.");
        if (!int.TryParse(destination.FinancialYear[..Math.Min(4, destination.FinancialYear.Length)],
            NumberStyles.None, CultureInfo.InvariantCulture, out var firstYear) ||
            firstYear < 1900 || firstYear > 9998)
            throw new InvalidOperationException("Cannot confirm the destination financial-year voucher authority.");
        var from = new DateTime(firstYear, 4, 1);
        var to = new DateTime(firstYear + 1, 3, 31);
        if (source.Any(x => x.Date < from || x.Date > to))
            throw new InvalidOperationException("Prepared voucher is outside the linked financial year.");
        var before = await tally.ExportVoucherAuthorityFreshAsync(destination, from, to, cancellationToken)
            .ConfigureAwait(false);
        RequireNoCollision(before.Index, source);

        var available = await ReadMasterAuthorityAsync(destination, cancellationToken).ConfigureAwait(false);
        // The compulsory branch groups and optional voucher types must have
        // passed the existing separate master-first UI. Never recreate them here.
        foreach (var element in masters.Where(x => PriorMasters.Contains(x.Name.LocalName)))
            RequireMaster(available, element);
        foreach (var element in masters.Where(x => !PriorMasters.Contains(x.Name.LocalName) &&
                                                   !MasterOrder.Contains(x.Name.LocalName)))
            throw new InvalidOperationException("Unsupported consolidated Master needs explicit verification: " +
                                                element.Name.LocalName);

        var createdMasters = 0;
        foreach (var type in MasterOrder)
        foreach (var element in masters.Where(x => Is(x, type)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = MasterName(element);
            var key = MasterKey(type, name);
            if (available.TryGetValue(key, out var known))
            {
                RequireCompatible(known, element);
                continue;
            }

            // One object per CREATE, never a bulk-master replay. If Tally
            // accepts a request and the readback fails, halt without retry.
            var sb = new StringBuilder();
            TallyExcelParityXmlWriter.BeginAllMastersImport(sb, destination.Name);
            sb.AppendLine(new XElement("TALLYMESSAGE", new XElement(element))
                .ToString(SaveOptions.DisableFormatting));
            TallyExcelParityXmlWriter.EndAllMastersImport(sb);
            var response = TallyHttpService.ParseImportResponse(
                await tally.ImportXmlAsync(sb.ToString(), cancellationToken).ConfigureAwait(false));
            TallyBranchConsolidationImportService.RequireOneCreate(response, name);
            available = await ReadMasterAuthorityAsync(destination, cancellationToken).ConfigureAwait(false);
            RequireMaster(available, element);
            createdMasters++;
        }

        // All masters must be live and parent-verified before the first voucher.
        foreach (var master in masters) RequireMaster(available, master);
        var recheck = await tally.ExportVoucherAuthorityFreshAsync(destination, from, to, cancellationToken)
            .ConfigureAwait(false);
        RequireNoCollision(recheck.Index, source);

        var imported = 0;
        foreach (var expected in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A single in-flight voucher is the only permissible import unit.
            // Failure after POST is ambiguous; never automatically retry.
            var sb = new StringBuilder();
            TallyExcelParityXmlWriter.BeginErp9DataImport(sb, destination.Name, "Vouchers");
            sb.AppendLine(new XElement("TALLYMESSAGE", new XElement(expected.Xml))
                .ToString(SaveOptions.DisableFormatting));
            TallyExcelParityXmlWriter.EndErp9DataImport(sb);
            var response = TallyHttpService.ParseImportResponse(
                await tally.ImportXmlAsync(sb.ToString(), cancellationToken).ConfigureAwait(false));
            if (response.Created != 1 || response.Altered != 0 || response.Deleted != 0 ||
                response.Ignored != 0 || response.Errors != 0 || response.Exceptions != 0 ||
                !string.IsNullOrWhiteSpace(response.LineError))
                throw new InvalidOperationException("Tally did not accept exactly one NEW consolidated voucher " +
                    expected.Number + ". Some preceding vouchers may already have been imported. " +
                    "Do not retry until the live Tally vouchers are reconciled. " + response.LineError);

            var after = await tally.ExportVoucherAuthorityFreshAsync(
                destination, expected.Date, expected.Date, cancellationToken).ConfigureAwait(false);
            var matches = after.Index.Where(x => x.VoucherDate?.Date == expected.Date &&
                IgnoreCase.Equals(x.VoucherType, expected.Type) &&
                IgnoreCase.Equals(x.VoucherNumber, expected.Number)).ToArray();
            if (matches.Length != 1 || string.IsNullOrWhiteSpace(matches[0].MasterId))
                throw new InvalidOperationException("Tally did not read back one durable MASTERID for " +
                    expected.Number + ". No automatic retry is safe.");
            var full = after.Full.Where(x => IgnoreCase.Equals(x.MasterId, matches[0].MasterId)).ToArray();
            if (full.Length != 1 || !SamePostings(expected.Postings,
                    ReadPostings(XDocument.Parse(full[0].OriginalXml).Root!
                        .DescendantsAndSelf().First(x => Is(x, "VOUCHER")))))
                throw new InvalidOperationException("Live Tally DR/CR postings differ from branch XML for " +
                    expected.Number + ". Import stopped; inspect posted voucher before continuing.");
            imported++;
        }
        return new TallyConsolidatedVoucherImportResult(imported, createdMasters,
            "Tally confirmed and read back each consolidated voucher with its original DR/CR entries. " +
            "No branch amounts or GST journals were recalculated.");
    }

    private static void RequireDestination(XDocument doc, string name)
    {
        if (!Is(doc.Root, "ENVELOPE") ||
            !IgnoreCase.Equals(doc.Descendants().FirstOrDefault(x => Is(x, "SVCURRENTCOMPANY"))?.Value.Trim(), name))
            throw new InvalidOperationException("Prepared XML is not addressed to the linked consolidated company.");
    }
    private static XElement[] Messages(XDocument doc) =>
        doc.Descendants().Where(x => Is(x, "TALLYMESSAGE")).ToArray();
    private static bool Is(XElement? x, string name) =>
        x is not null && IgnoreCase.Equals(x.Name.LocalName, name);
    private static string Field(XElement x, string name) =>
        x.Attributes().FirstOrDefault(a => IgnoreCase.Equals(a.Name.LocalName, name))?.Value.Trim() ?? "";
    private static string Value(XElement x, string name) =>
        x.Elements().FirstOrDefault(a => Is(a, name))?.Value.Trim() ?? "";
    private static string MasterName(XElement x) =>
        Field(x, "NAME") is { Length: > 0 } name ? name : Value(x, "NAME");
    private static string MasterKey(string type, string name) =>
        type.ToUpperInvariant() + "|" + name.Trim().ToUpperInvariant();
    private static string Key(DateTime date, string type, string number) =>
        type.Trim() + "|" + number.Trim(); // Voucher numbering is FY-scoped, not day-scoped.

    private static IReadOnlyList<(string Name, decimal Amount)> ReadPostings(XElement voucher)
    {
        var entries = voucher.Descendants().Where(x =>
            Is(x, "ALLLEDGERENTRIES.LIST") || Is(x, "LEDGERENTRIES.LIST")).ToArray();
        var result = new List<(string Name, decimal Amount)>();
        foreach (var entry in entries)
        {
            var name = Value(entry, "LEDGERNAME");
            var amount = Value(entry, "AMOUNT");
            if (name.Length == 0 ||
                !decimal.TryParse(amount, NumberStyles.AllowLeadingSign |
                    NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture, out var parsed))
                throw new InvalidOperationException("Incomplete Tally ledger entry prevents DR/CR verification.");
            result.Add((name, parsed));
        }
        return result;
    }
    private static bool SamePostings(IReadOnlyList<(string Name, decimal Amount)> a,
        IReadOnlyList<(string Name, decimal Amount)> b)
    {
        if (a.Count != b.Count) return false;
        var left = a.OrderBy(x => x.Name, IgnoreCase).ThenBy(x => x.Amount).ToArray();
        var right = b.OrderBy(x => x.Name, IgnoreCase).ThenBy(x => x.Amount).ToArray();
        return left.Zip(right).All(x => IgnoreCase.Equals(x.First.Name, x.Second.Name) &&
                                        x.First.Amount == x.Second.Amount);
    }
    private static void RequireNoCollision(
        IReadOnlyList<TallyVoucherIndexRecord> actual, IReadOnlyList<ExpectedVoucher> expected)
    {
        var anchors = expected.Select(x => Key(x.Date, x.Type, x.Number))
            .ToHashSet(IgnoreCase);
        var collision = actual.FirstOrDefault(x => x.VoucherDate is DateTime d &&
            anchors.Contains(Key(d, x.VoucherType, x.VoucherNumber)));
        if (collision is not null)
            throw new InvalidOperationException("Destination already has voucher " +
                collision.VoucherType + "/" + collision.VoucherNumber + " (" +
                collision.VoucherDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                "). No blind CREATE or ALTER: reconcile the original and destination first.");
    }

    private async Task<Dictionary<string, XElement>> ReadMasterAuthorityAsync(
        TallyCompanyInfo destination, CancellationToken token)
    {
        var masterXml = await tally.ExportMastersAsync(destination, token).ConfigureAwait(false);
        var stockXml = await tally.ExportStockMastersAsync(destination, token).ConfigureAwait(false);
        var result = new Dictionary<string, XElement>(IgnoreCase);
        foreach (var raw in new[] { masterXml, stockXml })
        foreach (var item in XDocument.Parse(raw, LoadOptions.None).Descendants())
        {
            if (!PriorMasters.Contains(item.Name.LocalName) &&
                !MasterOrder.Contains(item.Name.LocalName)) continue;
            var name = MasterName(item);
            if (name.Length == 0) continue;
            var key = MasterKey(item.Name.LocalName, name);
            if (result.TryGetValue(key, out var existing))
            {
                var before = Value(existing, "PARENT");
                var after = Value(item, "PARENT");
                if (before.Length > 0 && after.Length > 0 && !IgnoreCase.Equals(before, after))
                    throw new InvalidOperationException("Live Tally returned conflicting Master parents: " + name);
                if (item.Descendants().Count() > existing.Descendants().Count())
                    result[key] = new XElement(item);
            }
            else result.Add(key, new XElement(item));
        }
        if (result.Count == 0)
            throw new InvalidOperationException("Live Tally Master authority is empty.");
        return result;
    }
    private static void RequireCompatible(XElement live, XElement prepared)
    {
        var plannedParent = Value(prepared, "PARENT");
        if (plannedParent.Length > 0 &&
            !IgnoreCase.Equals(Value(live, "PARENT"), plannedParent))
            throw new InvalidOperationException("Destination Tally master parent mismatch for " +
                                                MasterName(prepared));
        // Do not silently replace branch GST registration with an unrelated
        // destination ledger of the same name.
        var expectedGstin = prepared.Descendants().Where(x => Is(x, "GSTIN"))
            .Select(x => x.Value.Trim()).Where(x => x.Length > 0).Distinct(IgnoreCase).ToArray();
        var actualGstin = live.Descendants().Where(x => Is(x, "GSTIN"))
            .Select(x => x.Value.Trim()).Where(x => x.Length > 0).Distinct(IgnoreCase).ToArray();
        if (expectedGstin.Except(actualGstin, IgnoreCase).Any())
            throw new InvalidOperationException("Destination GST master metadata is not identical for " +
                                                MasterName(prepared));
    }
    private static void RequireMaster(Dictionary<string, XElement> available, XElement source)
    {
        var name = MasterName(source);
        if (!available.TryGetValue(MasterKey(source.Name.LocalName, name), out var live))
            throw new InvalidOperationException("Master-first Tally readback missing " +
                source.Name.LocalName + " '" + name + "'.");
        RequireCompatible(live, source);
        if (source.Name.LocalName is "LEDGER" or "GROUP" or "VOUCHERTYPE" or "STOCKITEM")
        {
            var masterId = Value(live, "MASTERID");
            if (masterId.Length == 0)
                throw new InvalidOperationException("Live Tally did not return an authoritative MASTERID for " +
                    source.Name.LocalName + " '" + name + "'. No voucher posting allowed.");
        }
    }
    private sealed record ExpectedVoucher(
        DateTime Date, string Type, string Number,
        IReadOnlyList<(string Name, decimal Amount)> Postings, XElement Xml);
}
public sealed record TallyConsolidatedVoucherImportResult(
    int ImportedVouchers, int CreatedMasters, string Status);
