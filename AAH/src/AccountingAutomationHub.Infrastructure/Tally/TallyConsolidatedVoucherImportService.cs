using System.Globalization;
using System.Text;
using System.Xml.Linq;
using AccountingAutomationHub.Domain.Tally;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// The only live consolidated voucher writer. Never posts the parent client
/// voucher set, and never infers accounting amounts from totals or GST rates.
/// Each branch-prepared XML voucher uses the SAME durable source binding,
/// previous-version and accepted-write protections as AAH's ordinary exporter.
/// No foreign branch Tally identity or second mutation policy is permitted.
/// </summary>
public sealed class TallyConsolidatedVoucherImportService(
    TallyHttpService tally, Action<string, string, string>? diagnosticExchange = null,
    Action? verifiedVoucher = null,
    SqliteTallyRepository? tallyRepository = null,
    Action? tallyAcknowledgedCreate = null)
{
    private const string SourceEntityType = "Consolidated Voucher";
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

    /// <summary>
    /// Only reconciles old AcceptedAwaitingVerification CREATE acknowledgements.
    /// This route NEVER sends an Import/CREATE/ALTER/DELETE request. CL019 had
    /// an accepted sales voucher at destination MASTERID=337, but a same-date,
    /// same-reference voucher from another branch confused the earlier search.
    /// Binding is confirmed only if that exact destination MASTERID and BOTH
    /// signed ledger sides match the original prepared branch XML.
    /// </summary>
    public async Task<int> ReconcileAcceptedReadOnlyAsync(
        TallyClientLink link, TallyCompanyInfo destination,
        TallyConsolidatedPreparedXmlResult prepared,
        CancellationToken cancellationToken = default,
        string? accountingFinancialYear = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(prepared);
        if (tallyRepository is null)
            throw new InvalidOperationException(
                "Original Tally source-binding repository is required for read-only reconciliation.");
        var financialYear = TallyBranchConsolidationImportService.RequireConfirmedDestination(
            link, destination, accountingFinancialYear);
        TallyConsolidatedPreparedXmlBuilder.RequireNoForeignTallyIdentity(
            prepared.VouchersXml, "read-only consolidated accepted-voucher reconciliation");
        var voucherDoc = TallyConsolidatedXmlTextSafety.ParseDocument(
            prepared.VouchersXml, "read-only consolidated accepted-voucher reconciliation");
        RequireDestination(voucherDoc, destination.Name);

        var pending = new List<(ExpectedVoucher Voucher, string AcknowledgedMasterId)>();
        var seen = new HashSet<string>(IgnoreCase);
        foreach (var voucher in Messages(voucherDoc).Select(x => x.Elements().Single()))
        {
            if (!Is(voucher, "VOUCHER") ||
                !Field(voucher, "ACTION").Equals("Create", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Read-only reconciliation requires the original branch-prepared CREATE voucher.");
            var type = Value(voucher, "VOUCHERTYPENAME");
            var number = Value(voucher, "VOUCHERNUMBER");
            if (type.Length == 0 || number.Length == 0)
                throw new InvalidOperationException("Source voucher identity is missing.");
            var anchor = "CONSOLIDATED|" + type + "|" + number;
            if (!seen.Add(anchor))
                throw new InvalidOperationException("Repeated original source anchor in prepared XML.");
            var state = tallyRepository.GetSourceBindingVersionState(
                link.ClientId, financialYear, link.CompanyKey, SourceEntityType, anchor);
            if (state is null ||
                !state.State.Equals("AcceptedAwaitingVerification", StringComparison.OrdinalIgnoreCase))
                continue; // Never touch verified, pending-create or deleted states.
            if (!DateTime.TryParseExact(Value(voucher, "DATE"), "yyyyMMdd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new InvalidOperationException("Accepted voucher has an invalid original date.");
            if (!long.TryParse(state.MasterId, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var approvedMasterId) ||
                approvedMasterId <= 0)
                throw new InvalidOperationException(
                    "Accepted consolidated source " + anchor +
                    " has no authoritative LASTVCHID. Read-only reconciliation is blocked.");
            var entries = ReadPostings(voucher);
            if (entries.Count == 0)
                throw new InvalidOperationException(
                    "Accepted voucher has no original DR/CR posting authority: " + anchor);
            var reference = Value(voucher, "REFERENCE");
            var exactPostings = string.Join("|", entries
                .OrderBy(e => e.Name, IgnoreCase).ThenBy(e => e.Amount)
                .Select(e => e.Name + ":" +
                    e.Amount.ToString("0.00", CultureInfo.InvariantCulture)));
            var contentHash = TallyVoucherFingerprint.BuildContentHash(
                date, type, reference, Value(voucher, "PARTYLEDGERNAME"),
                entries.Where(e => e.Amount < 0).Sum(e => Math.Abs(e.Amount)),
                Value(voucher, "NARRATION") + "|" + exactPostings);
            var version = "CONSOLIDATED|" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) +
                "|" + type + "|" + reference + "|" + exactPostings + "|" + contentHash;
            if (!IgnoreCase.Equals(state.PendingContentHash, contentHash) ||
                (state.PendingVersion.Length > 0 &&
                 !string.Equals(state.PendingVersion, version, StringComparison.Ordinal)))
                throw new InvalidOperationException(
                    "Accepted source " + anchor +
                    " changed its original accounting version; no silent rebind or Tally mutation.");
            pending.Add((new ExpectedVoucher(date, type, number, entries, voucher,
                reference, anchor, contentHash, version), state.MasterId));
        }

        if (pending.Count == 0) return 0;
        // The identical original link/destination guard used by normal imports
        // protects against accidentally reading another connected Tally company.
        var open = await tally.DiscoverCompaniesAsync(cancellationToken).ConfigureAwait(false);
        var liveDestination = TallyBranchConsolidationImportService.RequireUniqueLiveDestination(
            link, destination, open, financialYear);
        var verified = 0;
        foreach (var item in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = item.Voucher;
            var after = await tally.ExportVoucherAuthorityFreshAsync(
                liveDestination, expected.Date, expected.Date, cancellationToken)
                .ConfigureAwait(false); // Read-only Export, NEVER Import.
            var matches = after.Index.Where(candidate =>
                IgnoreCase.Equals(candidate.MasterId, item.AcknowledgedMasterId) &&
                FullVoucherMatches(expected, candidate, after.Full)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(
                    "Tally acceptance remains unverified for " + expected.SourceAnchor +
                    " at LASTVCHID=" + item.AcknowledgedMasterId +
                    ". Exact destination MASTERID and DR/CR readback still differ. " +
                    "No voucher was resent, changed or deleted.");
            var confirmed = matches[0];
            tallyRepository.ConfirmSourceBindingVersion(
                link.ClientId, financialYear, link.CompanyKey, SourceEntityType,
                expected.SourceAnchor, expected.SourceAnchor,
                confirmed.MasterId, confirmed.Guid,
                expected.ContentHash, expected.DesiredVersion,
                long.TryParse(confirmed.AlterId, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var liveAlterId) ? liveAlterId : 0,
                "Read-only recovery of Tally-accepted CREATE; full destination " +
                "MASTERID/DRCR verified without any new voucher POST.");
            tallyRepository.RecordImportFingerprints(
                link.ClientId, financialYear, link.CompanyKey, SourceEntityType,
                new[] { (expected.SourceAnchor, expected.ContentHash) },
                "Imported", "Read-only exact Tally acceptance verified; no resend.");
            verified++;
            try { verifiedVoucher?.Invoke(); } catch { }
        }
        return verified;
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
            var reference = Value(voucher, "REFERENCE");
            // The already-prepared branch Voucher Number contains the scoped
            // branch identity. The destination owns its eventual Tally number;
            // this stable SOURCE anchor is kept only in AAH's existing
            // Client/FY/CompanyKey source-binding table.
            var sourceAnchor = "CONSOLIDATED|" + type + "|" + number;
            var exactPostings = string.Join("|", entries
                .OrderBy(e => e.Name, IgnoreCase).ThenBy(e => e.Amount)
                .Select(e => e.Name + ":" +
                    e.Amount.ToString("0.00", CultureInfo.InvariantCulture)));
            var contentHash = TallyVoucherFingerprint.BuildContentHash(
                date, type, reference, Value(voucher, "PARTYLEDGERNAME"),
                entries.Where(e => e.Amount < 0).Sum(e => Math.Abs(e.Amount)),
                Value(voucher, "NARRATION") + "|" + exactPostings);
            // The existing repository retains this immutable desired version
            // for accepted-unverified and previous->desired audit tracking.
            var version = "CONSOLIDATED|" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) +
                "|" + type + "|" + reference + "|" + exactPostings + "|" + contentHash;
            source.Add(new ExpectedVoucher(date, type, number, entries, voucher,
                reference, sourceAnchor, contentHash, version));
        }
        // Already-prepared source XML can contain two independent voucher
        // numbers for the same original invoice. Reject exact repeated source
        // content before any master or voucher POST, rather than relying only
        // on Tally to detect a duplicate after the first CREATE.
        foreach (var group in source.Where(v => v.Reference.Length > 0)
                     .GroupBy(v => v.Type + "|" +
                         v.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) +
                         "|" + v.Reference, IgnoreCase))
        {
            var hashes = new HashSet<string>(IgnoreCase);
            foreach (var item in group)
            {
                if (!hashes.Add(item.ContentHash))
                    throw new InvalidOperationException(
                        "Prepared consolidated vouchers contain the same original " +
                        "source invoice and full DR/CR under different branch numbers: " +
                        group.Key + ". No Tally POST was sent.");
            }
        }

        // A failed live read means no mutations. No cached branch or destination
        // data may authorize a CREATE against a Tally company.
        var open = await tally.DiscoverCompaniesAsync(cancellationToken).ConfigureAwait(false);
        var liveDestination = TallyBranchConsolidationImportService.RequireUniqueLiveDestination(
            link, destination, open, financialYear);
        // Consolidated must use the same durable source binding repository as
        // ordinary Purchase/Sales/Journal/Bank export. It must never fall back
        // to the former stateless CREATE-only writer.
        if (tallyRepository is null)
            throw new InvalidOperationException(
                "The existing Tally source-binding/version repository was not supplied; " +
                "consolidated Voucher CREATE is blocked.");
        if (!int.TryParse(financialYear[..Math.Min(4, financialYear.Length)],
            NumberStyles.None, CultureInfo.InvariantCulture, out var firstYear) ||
            firstYear < 1900 || firstYear > 9998)
            throw new InvalidOperationException("Cannot confirm the destination financial-year voucher authority.");
        var from = new DateTime(firstYear, 4, 1);
        var to = new DateTime(firstYear + 1, 3, 31);
        if (source.Any(x => x.Date < from || x.Date > to))
            throw new InvalidOperationException("Prepared voucher is outside the linked financial year.");
        // Reuse the existing bounded full-company/FY authority path used by
        // the central Tally coordinator. Unlike the old consolidated one-shot
        // collection, it checks every month's Voucher MASTERID against its full
        // object before authorizing any destination CREATE.
        var before = await tally.ExportFullCompanySnapshotAsync(
            liveDestination, from, to, cancellationToken).ConfigureAwait(false);
        if (liveDestination.VoucherCount > 0 && before.Vouchers.Count == 0)
            throw new InvalidOperationException(
                "Tally reports existing saved Voucher(s) but the full-FY snapshot " +
                "returned zero. This is NOT evidence of an empty destination. " +
                "No consolidated CREATE is safe.");
        tallyRepository.ReplaceVoucherIndex(
            link.CompanyKey, financialYear, before.Vouchers);
        tallyRepository.UpsertVoucherXmlAuthority(
            link.CompanyKey, financialYear, before.FullVouchers);
        // Reuse Fix85's normal missing MASTERID detection. An externally
        // deleted prior Voucher remains held for explicit recovery, not CREATE.
        tallyRepository.ReconcileMissingVoucherBindingAuthority(
            link.ClientId, financialYear, link.CompanyKey,
            before.Vouchers.Select(v => v.MasterId));

        var skipped = new HashSet<string>(IgnoreCase);
        // A previously verified consolidated source never re-enters CREATE.
        // Its destination MASTERID is already owned by the SAME normal
        // TallySourceBindings authority used for ordinary vouchers. Changes
        // in an original branch ledger must go through Previous->Desired ALTER.
        var alterTargets = new Dictionary<string, TallyVoucherIndexRecord>(IgnoreCase);
        var claimedAlterMasterIds = new HashSet<string>(IgnoreCase);
        foreach (var planned in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = tallyRepository.GetSourceBindingVersionState(
                link.ClientId, financialYear, link.CompanyKey, SourceEntityType,
                planned.SourceAnchor);
            if (state is not null && state.State.Equals("Verified", StringComparison.OrdinalIgnoreCase))
            {
                if (state.MasterId.Length == 0)
                    throw new InvalidOperationException(
                        "Previously verified source " + planned.SourceAnchor +
                        " has no saved destination MASTERID. ALTER and CREATE are blocked.");
                var bound = before.Vouchers.Where(candidate =>
                    IgnoreCase.Equals(candidate.MasterId, state.MasterId)).ToArray();
                if (bound.Length != 1)
                    throw new InvalidOperationException(
                        "Previously verified source " + planned.SourceAnchor +
                        " lost its unique destination MASTERID " + state.MasterId +
                        ". Existing restore/rebind safety is required; no CREATE.");
                var liveBound = bound[0];
                if (state.VerifiedContentHash.Length > 0 &&
                    IgnoreCase.Equals(state.VerifiedContentHash, planned.ContentHash) &&
                    FullVoucherMatches(planned, liveBound, before.FullVouchers))
                {
                    skipped.Add(planned.SourceAnchor);
                    continue; // Same saved source AND exact live destination.
                }
                if (state.VerifiedContentHash.Length == 0 ||
                    IgnoreCase.Equals(state.VerifiedContentHash, planned.ContentHash) ||
                    !PreviousConsolidatedVersionMatches(liveBound, before.FullVouchers, state))
                    throw new InvalidOperationException(
                        "Consolidated source " + planned.SourceAnchor +
                        " has changed, but the bound Tally MASTERID/ALTERID and " +
                        "saved previous full DR/CR version do not match. " +
                        "Refusing both ALTER and CREATE until the existing previous-version " +
                        "reconciliation gate is satisfied.");
                if (!claimedAlterMasterIds.Add(liveBound.MasterId))
                    throw new InvalidOperationException(
                        "Multiple original branch sources claim one destination Tally MASTERID " +
                        liveBound.MasterId + "; no ALTER or CREATE is safe.");
                alterTargets.Add(planned.SourceAnchor, liveBound);
                continue;
            }
            // AAH's durable source anchor survives Tally auto-numbering.
            // Never equate a new Tally VOUCHERNUMBER with a new source.
            var candidates = before.Vouchers.Where(live =>
                IgnoreCase.Equals(live.VoucherType, planned.Type) &&
                ((planned.Reference.Length > 0 &&
                  IgnoreCase.Equals(live.Reference, planned.Reference)) ||
                 IgnoreCase.Equals(live.VoucherNumber, planned.Number) ||
                 (state?.MasterId.Length > 0 &&
                  IgnoreCase.Equals(live.MasterId, state.MasterId)))).ToArray();

            // CL019: B2C vouchers in two DIFFERENT branches may share the
            // same source REFERENCE/date/type. Reference alone is not a
            // destination identity and must never bind the wrong MASTERID.
            // Filter by the normal accounting strict classifier AND complete
            // live signed ledger XML before assessing the number of matches.
            var exactMatches = candidates.Where(candidate =>
                FullVoucherMatches(planned, candidate, before.FullVouchers)).ToArray();
            if (exactMatches.Length > 1)
                throw new InvalidOperationException(
                    "Consolidated source " + planned.SourceAnchor +
                    " matches more than one full DR/CR-identical destination " +
                    "MASTERID. No CREATE, ALTER or DELETE is permitted.");
            if (exactMatches.Length == 1)
            {
                var candidate = exactMatches[0];
                if (state?.MasterId.Length > 0 &&
                    !IgnoreCase.Equals(state.MasterId, candidate.MasterId))
                    throw new InvalidOperationException(
                        "Previously bound consolidated source " + planned.SourceAnchor +
                        " changed destination MASTERID. Exact rebind must be reviewed.");
                // An already-verified source cannot be silently reclassified
                // after its AAH accounting content changes. The normal
                // previous->desired ALTER protocol is mandatory; this
                // consolidated CREATE-only wrapper never invents a shortcut.
                if (state is not null &&
                    ((state.State.Equals("Verified", StringComparison.OrdinalIgnoreCase) &&
                      state.VerifiedContentHash.Length > 0 &&
                      !IgnoreCase.Equals(state.VerifiedContentHash, planned.ContentHash)) ||
                     (state.PendingContentHash.Length > 0 &&
                      !IgnoreCase.Equals(state.PendingContentHash, planned.ContentHash))))
                    throw new InvalidOperationException(
                        "Previously verified/accepted consolidated source " +
                        planned.SourceAnchor +
                        " has a different saved previous or pending accounting " +
                        "version. The established Previous->Desired/restore safety " +
                        "gate must review it; never silently adopt or CREATE.");
                // Reuse the normal exporter’s verified source-version commit.
                // An original first-time import that Tally already holds is
                // adopted by exact live authority without another Tally POST.
                tallyRepository.ConfirmSourceBindingVersion(
                    link.ClientId, financialYear, link.CompanyKey, SourceEntityType,
                    planned.SourceAnchor, planned.SourceAnchor,
                    candidate.MasterId, candidate.Guid, planned.ContentHash,
                    planned.DesiredVersion,
                    long.TryParse(candidate.AlterId, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var alterId) ? alterId : 0L,
                    "Existing consolidated source fully matched live Tally by " +
                    "type/date/reference/full DRCR; no import, ALTER or DELETE.");
                tallyRepository.RecordImportFingerprints(
                    link.ClientId, financialYear, link.CompanyKey, SourceEntityType,
                    new[] { (planned.SourceAnchor, planned.ContentHash) },
                    "Imported", "Existing live destination voucher verified; not resent.");
                skipped.Add(planned.SourceAnchor);
                continue;
            }
            // A foreign branch with the same B2C document reference is
            // not the same source. However a same-branch or unscoped live
            // reference that differs in DR/CR is a conflict: fail closed,
            // never CREATE a second version of that invoice.
            if (candidates.Any(candidate =>
                !HasDifferentPreparedBranchScopes(candidate.VoucherNumber, planned.Number)))
                throw new InvalidOperationException(
                    "Consolidated source " + planned.SourceAnchor +
                    " has a conflicting same-branch or unscoped Tally voucher " +
                    "with the same source reference or destination number. " +
                    "Review its exact DR/CR; no duplicate CREATE is permitted.");
            if (state is not null ||
                tallyRepository.HasImportedSourceFingerprint(
                    link.ClientId, financialYear, link.CompanyKey,
                    SourceEntityType, planned.SourceAnchor))
                throw new InvalidOperationException(
                    "Consolidated source " + planned.SourceAnchor +
                    " has previous accepted/pending/deleted/verified Tally evidence " +
                    "but no exact live matching MASTERID. The existing AAH " +
                    "source-binding and explicit Restore-to-Tally safety gates " +
                    "forbid an automatic second CREATE.");
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
        // One fresh read after master-first, before voucher writes. If the
        // source authority changed during master creation, never continue
        // from an earlier duplicate decision.
        var recheck = await tally.ExportVoucherAuthorityFreshAsync(
            destination, from, to, cancellationToken).ConfigureAwait(false);
        var originalIds = before.Vouchers.Select(v => v.MasterId)
            .ToHashSet(IgnoreCase);
        var currentIds = recheck.Index.Select(v => v.MasterId)
            .ToHashSet(IgnoreCase);
        if (!originalIds.SetEquals(currentIds))
            throw new InvalidOperationException(
                "Live Tally Voucher authority changed during consolidated " +
                "master-first verification. The import is blocked before CREATE; " +
                "refresh the normal duplicate/version snapshot.");
        // Only truly unbound original sources can be CREATE candidates.
        // An altered source already owns a durable destination MASTERID.
        var createCandidates = source.Where(v =>
            !skipped.Contains(v.SourceAnchor) &&
            !alterTargets.ContainsKey(v.SourceAnchor)).ToArray();
        RequireNoCollision(recheck.Index, createCandidates);
        foreach (var planned in createCandidates)
        {
            var concurrent = recheck.Index.FirstOrDefault(live =>
                IgnoreCase.Equals(live.VoucherType, planned.Type) &&
                planned.Reference.Length > 0 &&
                IgnoreCase.Equals(live.Reference, planned.Reference) &&
                !HasDifferentPreparedBranchScopes(live.VoucherNumber, planned.Number));
            if (concurrent is not null)
                throw new InvalidOperationException(
                    "Destination Tally gained a voucher for original source " +
                    planned.Reference + " while masters were being verified. " +
                    "The existing normal accounting duplicate authority must be " +
                    "refreshed; no consolidated CREATE or ALTER was sent.");
        }

        var imported = 0;
        var altered = 0;
        foreach (var expected in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (skipped.Contains(expected.SourceAnchor))
                continue;

            if (alterTargets.TryGetValue(expected.SourceAnchor, out var originalLive))
            {
                // Do not emit a second ALTER unless the normal source binding
                // is still Verified and its PREVIOUS exact version matches a
                // fresh live Tally MASTERID with unchanged ALTERID. A
                // concurrent user/Tally edit is a review stop, not CREATE.
                var previous = tallyRepository.GetSourceBindingVersionState(
                    link.ClientId, financialYear, link.CompanyKey, SourceEntityType,
                    expected.SourceAnchor);
                var liveJustBefore = await tally.ExportVoucherAuthorityFreshAsync(
                    destination, expected.Date, expected.Date, cancellationToken).ConfigureAwait(false);
                var sameId = liveJustBefore.Index.SingleOrDefault(candidate =>
                    IgnoreCase.Equals(candidate.MasterId, originalLive.MasterId));
                if (previous is null ||
                    !previous.State.Equals("Verified", StringComparison.OrdinalIgnoreCase) ||
                    !IgnoreCase.Equals(previous.MasterId, originalLive.MasterId) ||
                    sameId is null ||
                    !PreviousConsolidatedVersionMatches(sameId, liveJustBefore.Full, previous))
                    throw new InvalidOperationException(
                        "Bound consolidated voucher " + expected.SourceAnchor +
                        " changed before ALTER: the existing Previous->Desired " +
                        "MASTERID/ALTERID/DRCR gate did not pass. No write sent.");

                // Reuse the NORMAL accounting voucher writer's BeginVoucherAlter
                // and the ORIGINAL imported branch-prepared XML. Only the
                // destination MASTERID and Tally-owned Voucher Number change.
                var alterXml = TallyAccountingVoucherExportService
                    .BuildBoundConsolidatedVoucherAlterXml(
                        destination.Name, expected.Xml,
                        sameId.MasterId, sameId.VoucherNumber);

                tallyRepository.StageSourceBindingChange(
                    link.ClientId, financialYear, link.CompanyKey, SourceEntityType,
                    expected.SourceAnchor, expected.SourceAnchor,
                    sameId.MasterId, sameId.Guid, expected.ContentHash,
                    expected.DesiredVersion, "ALTER", "ModifyPending",
                    "The same original AAH source's previous Tally MASTERID, " +
                    "ALTERID and full signed DR/CR matched before desired ledger ALTER.");

                TallyImportResponse alterResponse;
                try
                {
                    alterResponse = await ImportWithEvidenceAsync(
                        "Voucher-ALTER-" + expected.Number, alterXml, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    tallyRepository.UpsertSourceBinding(
                        link.ClientId, financialYear, link.CompanyKey, SourceEntityType,
                        expected.SourceAnchor, expected.SourceAnchor,
                        sameId.MasterId, sameId.Guid, expected.ContentHash,
                        "AcceptedAwaitingVerification",
                        "Bound ALTER transport result uncertain, existing destination MASTERID " +
                        sameId.MasterId + ". Never resend until exact fresh live readback: " +
                        exception.Message);
                    throw;
                }

                // The normal accepted-write barrier is durable BEFORE any
                // post-write fresh Tally read. A late read failure cannot
                // reclassify this source as CREATE or send a second ALTER.
                tallyRepository.UpsertSourceBinding(
                    link.ClientId, financialYear, link.CompanyKey, SourceEntityType,
                    expected.SourceAnchor, expected.SourceAnchor,
                    sameId.MasterId, sameId.Guid, expected.ContentHash,
                    "AcceptedAwaitingVerification",
                    "Bound Tally ALTER response: altered=" + alterResponse.Altered +
                    ", created=" + alterResponse.Created + ", deleted=" +
                    alterResponse.Deleted + ", errors=" + alterResponse.Errors +
                    ". Exact live readback is required before another mutation.");
                if (alterResponse.Altered != 1 || alterResponse.Created != 0 ||
                    alterResponse.Deleted != 0 || alterResponse.Ignored != 0 ||
                    alterResponse.Errors != 0 || alterResponse.Exceptions != 0 ||
                    !string.IsNullOrWhiteSpace(alterResponse.LineError))
                    throw new InvalidOperationException(
                        "Consolidated ALTER was not acknowledged as exactly one bound " +
                        "voucher. The previous and pending versions remain saved. " +
                        "Do not resend: verify Tally MASTERID " + sameId.MasterId + ". " +
                        alterResponse.LineError);

                var afterAlter = await tally.ExportVoucherAuthorityFreshAsync(
                    destination, expected.Date, expected.Date, cancellationToken)
                    .ConfigureAwait(false);
                var verifiedAlter = afterAlter.Index.Where(candidate =>
                    IgnoreCase.Equals(candidate.MasterId, sameId.MasterId) &&
                    FullVoucherMatches(expected, candidate, afterAlter.Full)).ToArray();
                if (verifiedAlter.Length != 1)
                    throw new InvalidOperationException(
                        "Tally accepted ALTER for " + expected.SourceAnchor +
                        " but exact destination MASTERID " + sameId.MasterId +
                        " and the complete desired signed DR/CR cannot yet be read back. " +
                        "AcceptedAwaitingVerification prevents a second ALTER/CREATE.");

                var confirmedAlter = verifiedAlter[0];
                tallyRepository.ConfirmSourceBindingVersion(
                    link.ClientId, financialYear, link.CompanyKey, SourceEntityType,
                    expected.SourceAnchor, expected.SourceAnchor,
                    confirmedAlter.MasterId, confirmedAlter.Guid,
                    expected.ContentHash, expected.DesiredVersion,
                    long.TryParse(confirmedAlter.AlterId, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var newAlterId)
                            ? newAlterId : 0L,
                    "Existing Previous->Desired bound MASTERID ALTER fully verified " +
                    "from fresh Tally voucher XML; old source number not reused.");
                tallyRepository.RecordImportFingerprints(
                    link.ClientId, financialYear, link.CompanyKey, SourceEntityType,
                    new[] { (expected.SourceAnchor, expected.ContentHash) },
                    "Imported", "Tally ALTER accepted and exact full DR/CR readback confirmed.");
                imported++;
                altered++;
                try { verifiedVoucher?.Invoke(); } catch { }
                continue;
            }

            // Existing normal AAH source lifecycle: persist CreatePending
            // BEFORE the potentially mutating network request. A transport
            // failure or lost Tally response must never become an unbound new
            // source that is automatically sent again.
            tallyRepository.StageSourceBindingChange(
                link.ClientId, financialYear, link.CompanyKey, SourceEntityType,
                expected.SourceAnchor, expected.SourceAnchor,
                string.Empty, string.Empty, expected.ContentHash,
                expected.DesiredVersion, "CREATE", "CreatePending",
                "Full-FY live Tally duplicate and master-first checks passed. " +
                "Exactly one original branch voucher is about to be submitted.");

            // A single in-flight voucher is the only permissible import unit.
            // Failure after POST is ambiguous; never automatically retry.
            var sb = new StringBuilder();
            TallyExcelParityXmlWriter.BeginErp9DataImport(sb, destination.Name, "Vouchers");
            sb.AppendLine(new XElement("TALLYMESSAGE", new XElement(expected.Xml))
                .ToString(SaveOptions.DisableFormatting));
            TallyExcelParityXmlWriter.EndErp9DataImport(sb);
            TallyImportResponse response;
            try
            {
                response = await ImportWithEvidenceAsync(
                    "Voucher-" + expected.Number, sb.ToString(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                tallyRepository.UpsertSourceBinding(
                    link.ClientId, financialYear, link.CompanyKey, SourceEntityType,
                    expected.SourceAnchor, expected.SourceAnchor,
                    string.Empty, string.Empty, expected.ContentHash,
                    "AcceptedAwaitingMasterId",
                    "Tally transport result unknown after CREATE request: " + exception.Message);
                throw;
            }

            if (response.Created == 1)
            {
                // A successful Tally CREATE is an accounting event, even if
                // the later live readback fails (CL019: CREATED=1/LASTVCHID=2).
                // Commit its accepted state BEFORE asking Tally for another
                // read-only Voucher collection. Re-export cannot resend.
                tallyRepository.UpsertSourceBinding(
                    link.ClientId, financialYear, link.CompanyKey, SourceEntityType,
                    expected.SourceAnchor, expected.SourceAnchor,
                    response.LastVoucherId, string.Empty, expected.ContentHash,
                    "AcceptedAwaitingVerification",
                    "Tally CREATED=1; LASTVCHID=" + response.LastVoucherId +
                    "; awaiting exact full Voucher DR/CR readback.");
                try { tallyAcknowledgedCreate?.Invoke(); }
                catch { /* diagnostic observer must never change accounting state */ }
            }
            else
            {
                tallyRepository.UpsertSourceBinding(
                    link.ClientId, financialYear, link.CompanyKey, SourceEntityType,
                    expected.SourceAnchor, expected.SourceAnchor,
                    string.Empty, string.Empty, expected.ContentHash,
                    "AcceptedAwaitingMasterId",
                    "Tally did not confirm one CREATE; reply requires live reconciliation.");
            }
            if (response.Created != 1 || response.Altered != 0 || response.Deleted != 0 ||
                response.Ignored != 0 || response.Errors != 0 || response.Exceptions != 0 ||
                !string.IsNullOrWhiteSpace(response.LineError))
                throw new InvalidOperationException("Tally did not accept exactly one NEW consolidated voucher " +
                    expected.Number + ". Some preceding vouchers may already have been imported. " +
                    "Do not retry until the live Tally vouchers are reconciled. " + response.LineError);

            var after = await tally.ExportVoucherAuthorityFreshAsync(
                destination, expected.Date, expected.Date, cancellationToken).ConfigureAwait(false);
            // Exact Tally Reference is the source document identity; Tally
            // can assign a different VOUCHERNUMBER. Reuse the normal strict
            // accounting classifier and validate the same MASTERID in the
            // complete original full XML readback.
            var matches = after.Index.Where(x =>
                IgnoreCase.Equals(x.VoucherType, expected.Type) &&
                x.VoucherDate?.Date == expected.Date &&
                ((expected.Reference.Length > 0 &&
                  IgnoreCase.Equals(x.Reference, expected.Reference)) ||
                 IgnoreCase.Equals(x.VoucherNumber, expected.Number) ||
                 (response.LastVoucherId.Length > 0 &&
                  IgnoreCase.Equals(x.MasterId, response.LastVoucherId)))).ToArray();
            // LASTVCHID identifies the voucher Tally just CREATED. It must
            // win over any other branch's matching B2C REFERENCE, but only
            // AFTER full signed ledger DR/CR and all other fields match.
            // Never adopt the wrong earlier branch merely by document number.
            var verified = matches.Where(candidate =>
                (response.LastVoucherId.Length == 0 ||
                 IgnoreCase.Equals(candidate.MasterId, response.LastVoucherId)) &&
                FullVoucherMatches(expected, candidate, after.Full)).ToArray();
            if (verified.Length != 1)
                throw new InvalidOperationException(
                    "Tally CREATED consolidated source " + expected.SourceAnchor +
                    " (LASTVCHID=" + response.LastVoucherId +
                    ") but one exact matching destination MASTERID and full signed " +
                    "DR/CR could not be proven. The existing " +
                    "AcceptedAwaitingVerification state prevents resend.");
            var confirmed = verified[0];

            tallyRepository.ConfirmSourceBindingVersion(
                link.ClientId, financialYear, link.CompanyKey, SourceEntityType,
                expected.SourceAnchor, expected.SourceAnchor,
                confirmed.MasterId, confirmed.Guid,
                expected.ContentHash, expected.DesiredVersion,
                long.TryParse(confirmed.AlterId, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var verifiedAlterId)
                    ? verifiedAlterId : 0L,
                "Tally CREATE and full signed DR/CR were verified at destination " +
                "MASTERID " + confirmed.MasterId + "; auto-number preserved.");
            tallyRepository.RecordImportFingerprints(
                link.ClientId, financialYear, link.CompanyKey, SourceEntityType,
                new[] { (expected.SourceAnchor, expected.ContentHash) },
                "Imported", "Tally accepted and live MASTERID/DRCR confirmed.");
            imported++;
            // The caller needs an accurate partial-success count if a later
            // voucher fails. This fires only AFTER exact live MASTERID/DR/CR
            // readback and never authorizes any resend.
            try { verifiedVoucher?.Invoke(); }
            catch { /* diagnostic-only callback */ }
        }
        return new TallyConsolidatedVoucherImportResult(imported, createdMasters,
            "Consolidated import used existing AAH source bindings: verified CREATE " +
            (imported - altered).ToString(CultureInfo.InvariantCulture) +
            ", verified PREVIOUS->DESIRED ALTER " + altered.ToString(CultureInfo.InvariantCulture) +
            ", exact live duplicates skipped " + skipped.Count.ToString(CultureInfo.InvariantCulture) +
            ". Only explicitly changed branch-prepared ledger assignments were " +
            "ALTERed on their existing destination MASTERID. GST and DR/CR values " +
            "were copied from the branches unchanged; no automatic DELETE.");
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
    // Keep the existing AAH Previous->Desired safety contract. CL019's
    // prior verified version was saved with full original signed branch
    // postings and a source fingerprint. Reconstruct both strictly from
    // the CURRENT LIVE old destination voucher, not from changed branch
    // accounting rows. Only that exact earlier version (plus same ALTERID)
    // authorizes using the original AAH bound-MASTERID ALTER writer.
    internal static bool PreviousConsolidatedVersionMatches(
        TallyVoucherIndexRecord live,
        IReadOnlyList<TallyVoucherXmlSnapshot> fullAuthority,
        TallySourceBindingVersionState previous)
    {
        if (previous is null ||
            !previous.State.Equals("Verified", StringComparison.OrdinalIgnoreCase) ||
            !IgnoreCase.Equals(previous.MasterId, live.MasterId) ||
            previous.VerifiedContentHash.Length == 0 ||
            previous.VerifiedVersion.Length == 0 ||
            previous.LastVerifiedAlterId <= 0 ||
            !long.TryParse(live.AlterId, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var currentAlterId) ||
            currentAlterId != previous.LastVerifiedAlterId ||
            live.VoucherDate is not DateTime originalDate)
            return false;
        var full = fullAuthority.Where(item =>
            IgnoreCase.Equals(item.MasterId, live.MasterId)).ToArray();
        if (full.Length != 1) return false;
        var voucher = TallyConsolidatedXmlTextSafety.ParseDocument(
            full[0].OriginalXml, "original previous voucher version")
            .Root?.DescendantsAndSelf().FirstOrDefault(x => Is(x, "VOUCHER"));
        if (voucher is null) return false;
        var postings = ReadPostings(voucher);
        // The full XML and index must agree on both signed sides before
        // comparing to the previous snapshot. This is never a ledger
        // relabeling/amount recalculation.
        if (postings.Count == 0 || live.LedgerPostings.Count != postings.Count ||
            !SamePostings(postings, live.LedgerPostings.Select(p =>
                (p.LedgerName, p.SignedAmount)).ToArray()))
            return false;
        var originalParty = Value(voucher, "PARTYLEDGERNAME");
        var originalReference = Value(voucher, "REFERENCE");
        var originalType = Value(voucher, "VOUCHERTYPENAME");
        if (!IgnoreCase.Equals(originalType, live.VoucherType) ||
            !IgnoreCase.Equals(originalReference, live.Reference) ||
            !IgnoreCase.Equals(originalParty, live.PartyLedger))
            return false;
        var exactPostings = string.Join("|", postings
            .OrderBy(entry => entry.Name, IgnoreCase).ThenBy(entry => entry.Amount)
            .Select(entry => entry.Name + ":" +
                entry.Amount.ToString("0.00", CultureInfo.InvariantCulture)));
        var verifiedHash = TallyVoucherFingerprint.BuildContentHash(
            originalDate.Date, originalType, originalReference, originalParty,
            postings.Where(entry => entry.Amount < 0)
                .Sum(entry => Math.Abs(entry.Amount)),
            Value(voucher, "NARRATION") + "|" + exactPostings);
        var verifiedVersion =
            "CONSOLIDATED|" + originalDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture) +
            "|" + originalType + "|" + originalReference + "|" +
            exactPostings + "|" + verifiedHash;
        return IgnoreCase.Equals(previous.VerifiedContentHash, verifiedHash) &&
            string.Equals(previous.VerifiedVersion, verifiedVersion, StringComparison.Ordinal);
    }

    // Exact voucher matching reuses the established ordinary Tally
    // classifier, then independently confirms the full Tally XML posting
    // lines. Branch amounts/taxes are NEVER recalculated here.
    private static bool FullVoucherMatches(ExpectedVoucher expected,
        TallyVoucherIndexRecord live, IReadOnlyList<TallyVoucherXmlSnapshot> fullAuthority)
    {
        if (!TallyAccountingVoucherExportService.VoucherStrictlyMatchesConsolidatedPrepared(
                live, expected.Date, expected.Type, expected.Reference,
                Value(expected.Xml, "NARRATION"), expected.Postings))
            return false;
        var full = fullAuthority.Where(x =>
            IgnoreCase.Equals(x.MasterId, live.MasterId)).ToArray();
        if (full.Length != 1) return false;
        var voucher = TallyConsolidatedXmlTextSafety.ParseDocument(
            full[0].OriginalXml, "consolidated exact live posting readback")
            .Root?.DescendantsAndSelf().FirstOrDefault(x => Is(x, "VOUCHER"));
        return voucher is not null &&
            SamePostings(expected.Postings, ReadPostings(voucher));
    }

    // Branch-scope identity is the hash already embedded by the original
    // consolidated prepared XML builder. A shared original B2C reference
    // cannot imply that different branch books are duplicate vouchers.
    // A normal Tally auto-number (without this scoped prefix) is NOT enough
    // to establish two different branches; fail closed in that case.
    internal static bool HasDifferentPreparedBranchScopes(string liveNumber, string plannedNumber)
    {
        static string Scope(string number)
        {
            number = (number ?? string.Empty).Trim();
            if (!number.StartsWith("B-", StringComparison.OrdinalIgnoreCase)) return "";
            var end = number.IndexOf('-', 2);
            if (end != 26) return "";
            var hash = number.Substring(2, 24);
            return hash.All(Uri.IsHexDigit) ? hash : "";
        }
        var live = Scope(liveNumber);
        var planned = Scope(plannedNumber);
        return live.Length > 0 && planned.Length > 0 &&
            !IgnoreCase.Equals(live, planned);
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
        IReadOnlyList<(string Name, decimal Amount)> Postings, XElement Xml,
        string Reference, string SourceAnchor, string ContentHash, string DesiredVersion);
}
public sealed record TallyConsolidatedVoucherImportResult(
    int ImportedVouchers, int CreatedMasters, string Status);
