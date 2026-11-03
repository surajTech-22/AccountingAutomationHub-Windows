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
public sealed class TallyConsolidatedVoucherImportService(
    TallyHttpService tally, Action<string, string, string>? diagnosticExchange = null,
    Action? verifiedVoucher = null, Action? acknowledgedVoucherCreate = null)
{
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;
    private static readonly string[] PriorMasters =
        ["GROUP", "VOUCHERTYPE"];
    private static readonly string[] MasterOrder =
        ["UNIT", "STOCKCATEGORY", "STOCKGROUP", "GODOWN",
         "COSTCENTRE", "LEDGER", "STOCKITEM"];

    // Capture the REAL Tally request and reply for the original detailed
    // AAH_TALLY_DIAGNOSTIC pack. The callback is diagnostic-only; never let a
    // file/log failure alter an accounting POST or initiate any retry.
    private async Task<TallyImportResponse> ImportWithEvidenceAsync(
        string stage, string xml, CancellationToken token)
    {
        string response;
        try
        {
            response = await tally.ImportXmlAsync(xml, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try { diagnosticExchange?.Invoke(stage, xml, "Transport exception: " + ex); }
            catch { /* never affect live Tally write semantics */ }
            throw;
        }
        try { diagnosticExchange?.Invoke(stage, xml, response); }
        catch { /* never affect live Tally write semantics */ }
        return TallyHttpService.ParseImportResponse(response);
    }

    // Use the original Tally reader and capture the ACTUAL HTTP read-only
    // exchange in the existing detailed diagnostic ZIP. No replacement reader,
    // cached snapshot or blind duplicate bypass is permitted.
    private Action<string, string>? Trace(string stage) =>
        diagnosticExchange is null ? null :
        (request, response) =>
        {
            try { diagnosticExchange(stage, request, response); }
            catch { /* diagnostic observer only */ }
        };

    private async Task<(IReadOnlyList<TallyVoucherIndexRecord> Index,
        IReadOnlyList<TallyVoucherXmlSnapshot> Full)> ReadFreshConsolidatedAuthorityAsync(
        TallyCompanyInfo destination, DateTime from, DateTime to,
        string stage, CancellationToken cancellationToken,
        bool requireVisibleVoucherAfterCreate = false)
    {
        var primary = await tally.ExportVoucherAuthorityFreshAsync(
            destination, from, to, cancellationToken, Trace(stage + "-FullAuthority"))
            .ConfigureAwait(false);
        if (primary.Index.Count > 0)
            return primary;

        // A zero-row response is not proof of a duplicate-free Tally company.
        // An independent, lighter Voucher collection is needed when there
        // is a known live voucher or Tally has acknowledged one new CREATE.
        if (!requireVisibleVoucherAfterCreate && destination.VoucherCount <= 0)
            return primary;
        var alternate = await tally.ExportVoucherIndexFreshAsync(
            destination, from, to, cancellationToken, Trace(stage + "-IndependentIndex"))
            .ConfigureAwait(false);
        if (alternate.Count > 0)
            throw new InvalidOperationException(
                "Consolidated Tally duplicate authority is inconsistent: the full Voucher reader " +
                "returned zero but an independent live Voucher index returned " +
                alternate.Count + " row(s). The source XML was NOT posted again. " +
                "Inspect both original HTTP readbacks inside the detailed diagnostic ZIP.");
        if (!requireVisibleVoucherAfterCreate && destination.VoucherCount > 0)
            throw new InvalidOperationException(
                "Consolidated duplicate check cannot establish an empty financial-year Voucher " +
                "collection: live Tally reports " + destination.VoucherCount +
                " existing voucher(s), but both fresh export readers returned zero. " +
                "Import stopped BEFORE any voucher POST; do not assume duplicates are absent.");
        return primary;
    }

    private static void RequireExactPostedVoucher(
        TallyVoucherXmlSnapshot actual, string nativeMasterId, ExpectedVoucher expected)
    {
        if (!IgnoreCase.Equals(actual.MasterId, nativeMasterId) ||
            actual.VoucherDate?.Date != expected.Date.Date ||
            !IgnoreCase.Equals(actual.VoucherType, expected.Type))
            throw new InvalidOperationException(
                "Tally LASTVCHID " + nativeMasterId +
                " identified a different type/date/master than the acknowledged CREATE. " +
                "Do not retry without checking the target company.");

        var voucher = TallyConsolidatedXmlTextSafety.ParseDocument(
            actual.OriginalXml, "exact LASTVCHID post-import readback")
            .DescendantsAndSelf().FirstOrDefault(x => Is(x, "VOUCHER"));
        if (voucher is null)
            throw new InvalidOperationException("Exact Tally LASTVCHID did not return voucher XML.");
        var actualNumber = Value(voucher, "VOUCHERNUMBER");
        if (!IgnoreCase.Equals(actualNumber, expected.Number))
            throw new InvalidOperationException(
                "Tally CREATED the source but stored Voucher Number '" + actualNumber +
                "' instead of '" + expected.Number + "' for target MASTERID " + nativeMasterId +
                ". Auto-numbering/identity requires explicit reconciliation; no automatic resend.");
        var sourceReference = Value(expected.Xml, "REFERENCE");
        if (sourceReference.Length > 0 &&
            !IgnoreCase.Equals(Value(voucher, "REFERENCE"), sourceReference))
            throw new InvalidOperationException(
                "Tally stored a different Reference for native LASTVCHID " + nativeMasterId +
                "; import stopped to protect duplicate ownership.");
        if (!SamePostings(expected.Postings, ReadPostings(voucher)))
            throw new InvalidOperationException(
                "Tally target MASTERID " + nativeMasterId +
                " does not match the original branch DR/CR; import stopped without retry.");
    }

    public async Task<TallyConsolidatedVoucherImportResult> ImportAsync(
        TallyClientLink link, TallyCompanyInfo destination,
        TallyConsolidatedPreparedXmlResult prepared,
        CancellationToken cancellationToken = default,
        string? accountingFinancialYear = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(prepared);
        var financialYear = TallyBranchConsolidationImportService.RequireConfirmedDestination(
            link, destination, accountingFinancialYear);

        // Source company ID fields must never reach the destination Tally,
        // including nested inventory/ledger identities in master XML.
        // Destination creates/matches masters independently and reads back its
        // OWN MASTERID before a single voucher is posted.
        TallyConsolidatedPreparedXmlBuilder.RequireNoForeignTallyIdentity(
            prepared.MastersXml, "staged consolidated master import");
        TallyConsolidatedPreparedXmlBuilder.RequireNoForeignTallyIdentity(
            prepared.VouchersXml, "staged consolidated voucher import");

        var masterDoc = TallyConsolidatedXmlTextSafety.ParseDocument(
            prepared.MastersXml, "staged consolidated masters");
        var voucherDoc = TallyConsolidatedXmlTextSafety.ParseDocument(
            prepared.VouchersXml, "staged consolidated vouchers");
        RequireDestination(masterDoc, destination.Name);
        RequireDestination(voucherDoc, destination.Name);
        var masters = Messages(masterDoc).Select(x => x.Elements().Single()).ToArray();
        // Fail BEFORE any Tally request for legacy preview XML: its original
        // generic Ledger parity template may include an unresolved tax
        // classification named 'Not Applicable'. Tally rejects that object
        // with Exceptions=1; retrying would skip/duplicate partial masters.
        var badClassification = masters
            .Where(x => Is(x, "LEDGER"))
            .FirstOrDefault(x => x.Elements().Any(field =>
                Is(field, "TAXCLASSIFICATIONNAME") &&
                field.Value.Trim().TrimStart('\u0004').Trim()
                    .Equals("Not Applicable", StringComparison.OrdinalIgnoreCase)));
        if (badClassification is not null)
            throw new InvalidOperationException(
                "Prepared consolidated ledger '" + MasterName(badClassification) +
                "' still contains the unresolved Tax Classification 'Not Applicable'. " +
                "Regenerate consolidated XML with the corrected AAH release and verify " +
                "Tally's existing masters before retrying; no Tally POST was sent.");
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
        TallyBranchConsolidationImportService.RequireUniqueLiveDestination(
            link, destination, open, financialYear);
        if (!int.TryParse(financialYear[..Math.Min(4, financialYear.Length)],
            NumberStyles.None, CultureInfo.InvariantCulture, out var firstYear) ||
            firstYear < 1900 || firstYear > 9998)
            throw new InvalidOperationException("Cannot confirm the destination financial-year voucher authority.");
        var from = new DateTime(firstYear, 4, 1);
        var to = new DateTime(firstYear + 1, 3, 31);
        if (source.Any(x => x.Date < from || x.Date > to))
            throw new InvalidOperationException("Prepared voucher is outside the linked financial year.");
        // The linked destination can be empty, but a success-shaped empty
        // XML collection is not reliable proof when Tally reports other vouchers.
        // Reuse the existing read-only export, and fail closed on contradictory
        // collection results rather than inventing a missing duplicate count.
        var liveDestination = open.FirstOrDefault(company =>
            IgnoreCase.Equals(company.Name, destination.Name) &&
            IgnoreCase.Equals(company.CompanyKey, destination.CompanyKey)) ?? destination;
        var before = await ReadFreshConsolidatedAuthorityAsync(
            liveDestination, from, to, "Consolidated-FY-Before", cancellationToken)
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
            var response = await ImportWithEvidenceAsync(
                "Master-" + type + "-" + name, sb.ToString(), cancellationToken).ConfigureAwait(false);
            TallyBranchConsolidationImportService.RequireOneCreate(response, name);
            available = await ReadMasterAuthorityAsync(destination, cancellationToken).ConfigureAwait(false);
            RequireMaster(available, element);
            createdMasters++;
        }

        // All masters must be live and parent-verified before the first voucher.
        foreach (var master in masters) RequireMaster(available, master);
        var recheck = await ReadFreshConsolidatedAuthorityAsync(
            liveDestination, from, to, "Consolidated-FY-PrePOST", cancellationToken)
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
            var response = await ImportWithEvidenceAsync(
                "Voucher-" + expected.Number, sb.ToString(), cancellationToken).ConfigureAwait(false);
            if (response.Created == 1)
            {
                try { acknowledgedVoucherCreate?.Invoke(); }
                catch { /* diagnostic only */ }
            }
            if (response.Created != 1 || response.Altered != 0 || response.Deleted != 0 ||
                response.Ignored != 0 || response.Errors != 0 || response.Exceptions != 0 ||
                !string.IsNullOrWhiteSpace(response.LineError))
                throw new InvalidOperationException("Tally did not accept exactly one NEW consolidated voucher " +
                    expected.Number + ". Some preceding vouchers may already have been imported. " +
                    "Do not retry until the live Tally vouchers are reconciled. " + response.LineError);

            // Tally's documented LASTVCHID is the destination-created native
            // Voucher MASTERID. Treat it as a CANDIDATE to read, never as proof
            // by itself. The voucher's date/type/number/reference/full DR/CR
            // must be compared with the original branch source.
            var nativeMasterId = response.LastVoucherId.Trim();
            var after = await ReadFreshConsolidatedAuthorityAsync(
                liveDestination, expected.Date, expected.Date,
                "Consolidated-After-" + expected.Number, cancellationToken,
                requireVisibleVoucherAfterCreate: true).ConfigureAwait(false);
            var matches = after.Index.Where(x => x.VoucherDate?.Date == expected.Date &&
                IgnoreCase.Equals(x.VoucherType, expected.Type) &&
                IgnoreCase.Equals(x.VoucherNumber, expected.Number)).ToArray();
            if (matches.Length > 1)
                throw new InvalidOperationException(
                    "Live Tally has multiple vouchers with the same created consolidated identity " +
                    expected.Type + "/" + expected.Number + "; no automatic retry.");
            if (matches.Length == 1 && string.IsNullOrWhiteSpace(matches[0].MasterId))
                throw new InvalidOperationException(
                    "Fresh Tally Voucher index found the created number but no durable MASTERID. " +
                    "No automatic resend is safe.");

            if (matches.Length == 1)
            {
                var matchedMasterId = matches[0].MasterId.Trim();
                if (nativeMasterId.Length > 0 && !IgnoreCase.Equals(nativeMasterId, matchedMasterId))
                    throw new InvalidOperationException(
                        "Tally CREATE LASTVCHID " + nativeMasterId +
                        " disagrees with the fresh Voucher-index MASTERID " + matchedMasterId +
                        " for " + expected.Number + ". Do not retry.");
                var full = after.Full.Where(x => IgnoreCase.Equals(x.MasterId, matchedMasterId)).ToArray();
                if (full.Length != 1)
                    throw new InvalidOperationException(
                        "Tally created " + expected.Number +
                        " but the full signed ledger XML did not match one Voucher MASTERID. No automatic resend.");
                RequireExactPostedVoucher(full[0], matchedMasterId, expected);
            }
            else
            {
                // Crucial recovery for TallyPrime auto-numbering/collection
                // discrepancies: independently read the exact newly-created
                // destination MASTERID, without any second import POST.
                // The original v578 error hid Tally CREATED=1/LASTVCHID=2
                // and incorrectly reported zero created in the summary.
                var exact = await tally.ExportVoucherByKnownMasterIdForConsolidatedReadbackAsync(
                    liveDestination, nativeMasterId, expected.Date, cancellationToken,
                    Trace("Consolidated-Exact-LASTVCHID-" + nativeMasterId))
                    .ConfigureAwait(false);
                RequireExactPostedVoucher(exact, nativeMasterId, expected);
                // Even a proven exact voucher cannot authorize the next CREATE
                // if the normal financial-year reader cannot see it. This is
                // an index consistency defect, NOT a duplicate-free target.
                try { verifiedVoucher?.Invoke(); }
                catch { /* diagnostic only */ }
                throw new InvalidOperationException(
                    "Tally CREATED and exact LASTVCHID=" + nativeMasterId +
                    " readback VERIFIED voucher " + expected.Number +
                    ", but the normal fresh financial-year Voucher collection did not return " +
                    "that saved voucher. Further consolidated CREATEs are blocked until the " +
                    "live duplicate authority is repaired. No automatic resend.");
            }
            imported++;
            // The caller needs an accurate partial-success count if a later
            // voucher fails. This fires only AFTER exact live MASTERID/DR/CR
            // readback and never authorizes any resend.
            try { verifiedVoucher?.Invoke(); }
            catch { /* diagnostic-only callback */ }
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

        // Tally voucher types may auto-assign a DIFFERENT voucher number even
        // when the branch source supplied one. A rerun must also reject a
        // strongly matching live original-reference + entire signed DR/CR
        // identity. Never skip/alter the row merely on a weak reference match.
        foreach (var planned in expected)
        {
            var reference = Value(planned.Xml, "REFERENCE");
            if (reference.Length == 0) continue;
            var matching = actual.FirstOrDefault(live =>
                live.VoucherDate?.Date == planned.Date.Date &&
                IgnoreCase.Equals(live.VoucherType, planned.Type) &&
                IgnoreCase.Equals(live.Reference, reference) &&
                live.LedgerPostings.Count == planned.Postings.Count &&
                SamePostings(planned.Postings, live.LedgerPostings
                    .Select(x => (x.LedgerName, x.SignedAmount)).ToArray()));
            if (matching is not null)
                throw new InvalidOperationException(
                    "Destination Tally already contains a matching source Reference " +
                    reference + " / type " + planned.Type + " / complete DR/CR " +
                    "under Voucher Number '" + matching.VoucherNumber +
                    "' and native MASTERID " + matching.MasterId +
                    ". Possible Tally auto-numbering; reconcile, never blindly CREATE or ALTER.");
        }
    }

    private async Task<Dictionary<string, XElement>> ReadMasterAuthorityAsync(
        TallyCompanyInfo destination, CancellationToken token)
    {
        var masterXml = await tally.ExportMastersAsync(destination, token).ConfigureAwait(false);
        var stockXml = await tally.ExportStockMastersAsync(destination, token).ConfigureAwait(false);
        var result = new Dictionary<string, XElement>(IgnoreCase);
        foreach (var raw in new[] { masterXml, stockXml })
        foreach (var item in TallyConsolidatedXmlTextSafety.ParseDocument(
            raw, "live consolidated master/stock authority").Descendants())
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
