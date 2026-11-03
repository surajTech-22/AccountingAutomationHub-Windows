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
    Action? verifiedVoucher = null,
    SqliteTallyRepository? tallyRepository = null,
    Action? acknowledgedCreate = null)
{
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;
    private const string ConsolidatedSourceType = "Consolidated Voucher";
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
            // The normal AAH exporter also rejects duplicates among selected
            // local sources before checking Tally. Two branches must never
            // conceal one identical invoice behind different prefixed numbers.
            var sourceReference = Value(voucher, "REFERENCE");
            if (sourceReference.Length > 0 && source.Any(previous =>
                previous.Date.Date == date.Date &&
                IgnoreCase.Equals(previous.Type, type) &&
                IgnoreCase.Equals(Value(previous.Xml, "REFERENCE"), sourceReference)))
                throw new InvalidOperationException(
                    "Consolidated branch XML repeats original reference '" +
                    sourceReference + "' with the same date/type, either as an " +
                    "exact duplicate or with changed DR/CR. The original AAH " +
                    "voucher rules require review before any CREATE/ALTER; no " +
                    "Tally voucher was sent.");
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
        // Reuse the SAME full-company snapshot, ALTERID pulse and saved
        // destination authority that the ordinary Tally export framework owns.
        // Do not infer an empty Tally destination from an empty FY XML reply.
        var repository = tallyRepository ??
            throw new InvalidOperationException(
                "The original Tally source-binding repository is required for consolidated export.");
        var liveBefore = await tally.ExportFullCompanySnapshotAsync(
            destination, from, to, cancellationToken).ConfigureAwait(false);
        var marker = await tally.GetVoucherChangeMarkerAsync(
            destination, cancellationToken).ConfigureAwait(false);
        if (marker.CompanyName.Length > 0 &&
            !IgnoreCase.Equals(marker.CompanyName, destination.Name))
            throw new InvalidOperationException(
                "Tally changed its active company during consolidated duplicate preflight.");
        // Native count is company-wide while the full XML is FY-bounded.
        // A mismatch is an uncertain authority, NOT permission to create.
        if (marker.VoucherCount != liveBefore.Vouchers.Count ||
            (destination.VoucherCount > 0 && liveBefore.Vouchers.Count == 0))
            throw new InvalidOperationException(
                "Consolidated duplicate safety STOP: original Tally full-company authority " +
                "returned " + liveBefore.Vouchers.Count + " FY voucher(s) but the live " +
                "Tally marker reports " + marker.VoucherCount +
                " company voucher(s). Review the original shared authority; no CREATE or ALTER sent.");
        repository.ReplaceVoucherIndex(
            destination.CompanyKey, financialYear, liveBefore.Vouchers);
        repository.ReplaceVoucherXmlAuthority(
            destination.CompanyKey, financialYear, liveBefore.FullVouchers);

        // Classify the entire source BEFORE creating any additional masters,
        // using the normal voucher comparator + existing source-binding state.
        var toPost = new List<ExpectedVoucher>();
        var duplicateCount = 0;
        foreach (var expected in source)
        {
            var matching = liveBefore.Vouchers.Where(live =>
                MatchesSourceIdentity(live, expected)).ToArray();
            if (matching.Length > 1)
                throw new InvalidOperationException(
                    "Multiple live destination Tally vouchers match consolidated source " +
                    Value(expected.Xml, "REFERENCE") + ". Manual duplicate review required.");
            if (matching.Length == 1)
            {
                if (!MatchesNormalAccountingVoucher(matching[0], expected))
                    throw new InvalidOperationException(
                        "Original Tally duplicate check found the same source reference/number " +
                        "with DIFFERENT DR/CR or party metadata: " +
                        Value(expected.Xml, "REFERENCE") +
                        ". Neither CREATE nor ALTER is permitted.");
                duplicateCount++;
                // The existing source binding is the single durable owner of
                // previous/version/MASTERID state; no branch Tally ID is copied.
                ConfirmVerifiedSource(repository, link, financialYear, expected,
                    matching[0], "Exact duplicate adopted from verified original Tally authority.");
                continue;
            }
            var state = repository.GetSourceBindingVersionState(link.ClientId,
                financialYear, destination.CompanyKey, ConsolidatedSourceType, expected.Number);
            if (state is not null)
                throw new InvalidOperationException(
                    "Consolidated source " + expected.Number + " already has Tally source-binding state '" +
                    state.State + "' (native MASTERID " + state.MasterId +
                    "). Its prior CREATE may have succeeded. No automatic resend or DELETE is safe.");
            toPost.Add(expected);
        }

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
        // Same pre-write fresh authority used by the normal exporter. If a
        // Tally user changed the destination during master-first preparation,
        // fail closed rather than use the earlier candidate classification.
        var recheck = await tally.ExportVoucherAuthorityFreshAsync(
            destination, from, to, cancellationToken).ConfigureAwait(false);
        var originalIds = liveBefore.Vouchers.Select(v => v.MasterId.Trim())
            .ToHashSet(IgnoreCase);
        var recheckIds = recheck.Index.Select(v => v.MasterId.Trim())
            .ToHashSet(IgnoreCase);
        if (!originalIds.SetEquals(recheckIds))
            throw new InvalidOperationException(
                "Fresh Tally voucher authority changed during master-first import. " +
                "No consolidated Voucher CREATE is authorized until the normal complete-FY " +
                "authority is refreshed again.");
        RequireNoCollision(recheck.Index, toPost);

        var imported = 0;
        foreach (var expected in toPost)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The normal accounting exporter writes a durable CreatePending
            // source version before the POST, and retains AcceptedAwaitingMasterId
            // if Tally accepts the write but readback cannot find its ID.
            // Reuse EXACTLY that existing persistent repository and states here.
            var originalHash = SourceHash(expected);
            repository.StageSourceBindingChange(
                link.ClientId, financialYear, destination.CompanyKey,
                ConsolidatedSourceType, expected.Number, expected.Number,
                string.Empty, string.Empty, originalHash,
                expected.Xml.ToString(SaveOptions.DisableFormatting),
                "CREATE", "CreatePending",
                "Consolidated original branch XML queued for Tally CREATE. " +
                "Do not resend if acknowledgement/readback is lost.");
            var sb = new StringBuilder();
            TallyExcelParityXmlWriter.BeginErp9DataImport(sb, destination.Name, "Vouchers");
            sb.AppendLine(new XElement("TALLYMESSAGE", new XElement(expected.Xml))
                .ToString(SaveOptions.DisableFormatting));
            TallyExcelParityXmlWriter.EndErp9DataImport(sb);
            var response = await ImportWithEvidenceAsync(
                "Voucher-" + expected.Number, sb.ToString(), cancellationToken).ConfigureAwait(false);
            if (response.Created == 1)
            {
                // Tally has already mutated the destination. Record that fact
                // BEFORE any postwrite lookup can throw or return an empty index.
                repository.UpsertSourceBinding(
                    link.ClientId, financialYear, destination.CompanyKey,
                    ConsolidatedSourceType, expected.Number, expected.Number,
                    string.Empty, string.Empty, originalHash,
                    "AcceptedAwaitingMasterId", "Tally CREATED=1; LASTVCHID=" +
                    response.LastVoucherId + "; strict readback still pending.");
                try { acknowledgedCreate?.Invoke(); }
                catch { /* diagnostic callback only */ }
            }
            if (response.Created != 1 || response.Altered != 0 || response.Deleted != 0 ||
                response.Ignored != 0 || response.Errors != 0 || response.Exceptions != 0 ||
                !string.IsNullOrWhiteSpace(response.LineError))
                throw new InvalidOperationException("Tally did not accept exactly one NEW consolidated voucher " +
                    expected.Number + ". Some preceding vouchers may already have been imported. " +
                    "Do not retry until the live Tally vouchers are reconciled. " + response.LineError);

            // Read back through the original Tally service's *existing*
            // exact-MASTERID verification path, shared with safe Ledger Merge.
            // This handles Tally auto-numbering and a lagging date collection.
            var nativeId = response.LastVoucherId.Trim();
            if (!long.TryParse(nativeId, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var parsedId) || parsedId <= 0)
                throw new InvalidOperationException(
                    "Tally accepted CREATE but supplied no positive LASTVCHID for " +
                    expected.Number + ". The original AAH source binding is HELD; no resend.");
            var exact = await tally.ExportVoucherXmlSnapshotsByKnownMasterIdsForVerificationAsync(
                destination, new[] { nativeId }, expected.Date, expected.Date,
                cancellationToken).ConfigureAwait(false);
            if (exact.Count != 1 || !IgnoreCase.Equals(exact[0].MasterId, nativeId))
                throw new InvalidOperationException(
                    "Exact Tally MASTERID readback did not confirm the one CREATED voucher " +
                    expected.Number + ". The accepted source remains HELD, not requeued.");
            var actual = TallyConsolidatedXmlTextSafety.ParseDocument(
                exact[0].OriginalXml, "native exact Voucher readback").Root;
            var written = actual?.DescendantsAndSelf().FirstOrDefault(x => Is(x, "VOUCHER"));
            if (written is null ||
                !IgnoreCase.Equals(Value(written, "VOUCHERTYPENAME"), expected.Type) ||
                !IgnoreCase.Equals(Value(written, "REFERENCE"), Value(expected.Xml, "REFERENCE")) ||
                !DateTime.TryParseExact(Value(written, "DATE"), "yyyyMMdd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var liveDate) ||
                liveDate.Date != expected.Date.Date ||
                !SamePostings(expected.Postings, ReadPostings(written)))
                throw new InvalidOperationException(
                    "Tally accepted consolidated source " + expected.Number +
                    " but the exact native MASTERID DR/CR, date, type or reference does not " +
                    "match the original branch voucher. Accepted source remains HELD; no resend.");
            repository.ConfirmSourceBindingVersion(
                link.ClientId, financialYear, destination.CompanyKey,
                ConsolidatedSourceType, expected.Number, expected.Number,
                nativeId, exact[0].Guid, originalHash,
                exact[0].OriginalXml,
                long.TryParse(exact[0].AlterId, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var liveAlterId) ? liveAlterId : 0L,
                "Exact original Tally MASTERID readback verified before source binding.");
            imported++;
            // The caller needs an accurate partial-success count if a later
            // voucher fails. This fires only AFTER exact live MASTERID/DR/CR
            // readback and never authorizes any resend.
            try { verifiedVoucher?.Invoke(); }
            catch { /* diagnostic-only callback */ }
        }
        return new TallyConsolidatedVoucherImportResult(imported, createdMasters,
            "Tally confirmed " + imported + " new consolidated voucher(s), skipped " +
            duplicateCount + " exact live duplicates through the original accounting " +
            "voucher comparator, and read back every posted native MASTERID. " +
            "No branch DR/CR or GST journal was recalculated.");
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
    // The original normal Tally accounting classifier owns duplicate
    // equivalence. This wrapper only supplies its original parsed XML fields
    // and never copies branch Tally MASTERID/ALTERID/GUID to the destination.
    private static bool MatchesNormalAccountingVoucher(
        TallyVoucherIndexRecord live, ExpectedVoucher source) =>
        TallyAccountingVoucherExportService.VoucherStrictlyMatchesConsolidatedSource(
            live, source.Date, source.Type, Value(source.Xml, "REFERENCE"),
            Value(source.Xml, "PARTYLEDGERNAME"), Value(source.Xml, "NARRATION"),
            source.Postings);

    // Same original Tally source reference is an identity candidate even
    // when Tally has assigned a different Voucher Number or changed its date.
    // The existing normal accounting comparator distinguishes exact duplicates
    // from protected changed-content conflicts.
    private static bool MatchesSourceIdentity(
        TallyVoucherIndexRecord live, ExpectedVoucher source)
    {
        if (!IgnoreCase.Equals(live.VoucherType, source.Type)) return false;
        var reference = Value(source.Xml, "REFERENCE");
        return (reference.Length > 0 && IgnoreCase.Equals(live.Reference, reference)) ||
               IgnoreCase.Equals(live.VoucherNumber, source.Number);
    }

    // Uses the existing AAH fingerprint + source binding table. The original
    // signed postings become content evidence; this never drives recalculation.
    private static string SourceHash(ExpectedVoucher source)
    {
        var postings = string.Join("|", source.Postings.Select(posting =>
            posting.Name + ":" + posting.Amount.ToString("0.00", CultureInfo.InvariantCulture)));
        var amount = source.Postings.Where(p => p.Amount < 0m).Sum(p => Math.Abs(p.Amount));
        return TallyVoucherFingerprint.BuildContentHash(
            source.Date, source.Type, Value(source.Xml, "REFERENCE"),
            Value(source.Xml, "PARTYLEDGERNAME"), amount,
            Value(source.Xml, "NARRATION") + "|" + postings);
    }

    private static void ConfirmVerifiedSource(
        SqliteTallyRepository repository, TallyClientLink link,
        string financialYear, ExpectedVoucher source,
        TallyVoucherIndexRecord live, string note)
    {
        if (string.IsNullOrWhiteSpace(live.MasterId))
            throw new InvalidOperationException(
                "Original Tally duplicate check returned an unbound live voucher; " +
                "no consolidated import is allowed.");
        repository.ConfirmSourceBindingVersion(
            link.ClientId, financialYear, link.CompanyKey,
            ConsolidatedSourceType, source.Number, source.Number,
            live.MasterId, live.Guid, SourceHash(source),
            source.Xml.ToString(SaveOptions.DisableFormatting),
            long.TryParse(live.AlterId, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var alterId) ? alterId : 0L,
            note);
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
