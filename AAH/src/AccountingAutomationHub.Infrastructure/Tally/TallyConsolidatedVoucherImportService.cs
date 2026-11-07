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
    Action? tallyAcknowledgedCreate = null,
    Action? tallyAcknowledgedAlter = null,
    Func<TallyClientLink, TallyCompanyInfo, string, CancellationToken, Task<TallyVoucherChangeMarker>>?
        consolidatedVerifiedMarkerReader = null)
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


    // Normal and individual-client readers keep their ORIGINAL live Tally
    // change-marker semantics. Only the consolidated call-site can supply
    // a marker independently proved against a fresh live Voucher index when
    // the known Tally report returns unreliable zero/empty pulse values.
    // The original strict RequireMatchingSynchronizedMarker, duplicate,
    // CREATE/ALTER, readback and previous-version checks remain untouched.
    private Task<TallyVoucherChangeMarker> ReadTrustedMarkerAsync(
        TallyClientLink link, TallyCompanyInfo company, string financialYear,
        CancellationToken cancellationToken) =>
        consolidatedVerifiedMarkerReader is null
            ? tally.GetVoucherChangeMarkerAsync(company, cancellationToken)
            : consolidatedVerifiedMarkerReader(link, company, financialYear, cancellationToken);

    /// <summary>
    /// Reuse the exact full-year authority maintained by the existing Tally
    /// coordinator, including all saved MASTERIDs/ALTERIDs and complete XML.
    /// Never start a separate full-company snapshot from consolidated import.
    /// </summary>
    private async Task<(TallyFullCompanySnapshot Snapshot, TallyVoucherChangeMarker Marker)>
        ReadSynchronizedVoucherAuthorityAsync(
            TallyClientLink link, TallyCompanyInfo destination, string financialYear,
            DateTime from, DateTime to, CancellationToken token)
    {
        if (tallyRepository is null)
            throw new InvalidOperationException("Existing Tally source-binding repository is required.");
        var state = tallyRepository.GetBankSyncState(link.ClientId, financialYear, link.CompanyKey);
        if (state is null || state.LastFullSyncUtc == DateTime.MinValue ||
            state.LastAlterId <= 0 || state.LastFyVoucherCount < 0)
            throw new InvalidOperationException(
                "Normal linked-Tally FY authority has not completed its baseline. " +
                "Refresh the existing Tally link; consolidated will not take a second snapshot.");

        var marker = await ReadTrustedMarkerAsync(
            link, destination, financialYear, token).ConfigureAwait(false);
        RequireMatchingSynchronizedMarker(link, destination, state, marker);

        var index = tallyRepository.GetVoucherIndexRange(link.CompanyKey, financialYear, from, to);
        if (index.Count != state.LastFyVoucherCount ||
            index.Any(v => string.IsNullOrWhiteSpace(v.MasterId)) ||
            index.GroupBy(v => v.MasterId.Trim(), IgnoreCase).Any(g => g.Count() != 1) ||
            !tallyRepository.HasCompleteVoucherXmlAuthority(link.CompanyKey, financialYear, index.Count))
            throw new InvalidOperationException(
                "Synchronized Tally Voucher index/full XML is incomplete or has duplicate MASTERIDs. " +
                "Use the existing normal Tally refresh before CREATE or ALTER.");

        var full = tallyRepository.GetVoucherXmlAuthorityByMasterIds(
            link.CompanyKey, financialYear, index.Select(v => v.MasterId));
        var byId = full.GroupBy(v => v.MasterId.Trim(), IgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), IgnoreCase);
        if (full.Count != index.Count || index.Any(v =>
                !byId.TryGetValue(v.MasterId.Trim(), out var rows) ||
                rows.Length != 1 || string.IsNullOrWhiteSpace(rows[0].OriginalXml) ||
                !IgnoreCase.Equals(rows[0].AlterId, v.AlterId)))
            throw new InvalidOperationException(
                "Synchronized full Voucher XML/ALTERID differs from the existing MASTERID index. " +
                "Refresh the normal Tally authority; do not write from an incomplete copy.");

        // A concurrent normal delta may advance the durable checkpoint while
        // local SQLite rows are being read. Never mix two generations.
        var after = tallyRepository.GetBankSyncState(link.ClientId, financialYear, link.CompanyKey);
        if (after is null ||
            !string.Equals(after.LastTallyChangeToken, state.LastTallyChangeToken, StringComparison.Ordinal) ||
            after.LastAlterId != state.LastAlterId ||
            after.LastFyVoucherCount != state.LastFyVoucherCount)
            throw new InvalidOperationException(
                "Tally synchronized authority changed during consolidated preparation. Retry after the normal refresh.");

        // Tally's NORMAL FY synchronization saved the signed Voucher XML and
        // its index in two SQLite tables. The persisted index intentionally
        // does not serialize LedgerPostings. Rebuild those missing DR/CR rows
        // from the SAME synchronized full XML (no new Tally request, no
        // mutation), using the ORIGINAL normal Tally Ledger parser.
        var completeIndex = HydrateSynchronizedVoucherPostings(index, full);

        return (new TallyFullCompanySnapshot
        {
            CompanyKey = link.CompanyKey,
            FinancialYear = financialYear,
            FromDate = from,
            ToDate = to,
            Vouchers = completeIndex,
            FullVouchers = full,
            DeclaredVoucherCount = index.Count,
            AuthorityAlterId = state.LastAlterId,
            CapturedUtc = state.LastIncrementalSyncUtc > state.LastFullSyncUtc
                ? state.LastIncrementalSyncUtc : state.LastFullSyncUtc
        }, marker);
    }

    /// <summary>
    /// Match the individual import's signed Ledger Entry object projection,
    /// but use only complete full Voucher XML ALREADY captured by the normal
    /// Tally synchronization. A SQLite index record has no LedgerPostings
    /// column: without this hydration, 645 existing vouchers can all look
    /// different even when no AAH/Tally voucher changed.
    /// </summary>
    internal static IReadOnlyList<TallyVoucherIndexRecord> HydrateSynchronizedVoucherPostings(
        IReadOnlyList<TallyVoucherIndexRecord> index,
        IReadOnlyList<TallyVoucherXmlSnapshot> full)
    {
        if (index.Count != full.Count)
            throw new InvalidOperationException(
                "Normal Tally index and synchronized full Voucher XML have different counts. No consolidated CREATE/ALTER.");
        var byId = new Dictionary<string, TallyVoucherXmlSnapshot>(IgnoreCase);
        foreach (var snapshot in full)
        {
            if (string.IsNullOrWhiteSpace(snapshot.MasterId) ||
                !byId.TryAdd(snapshot.MasterId.Trim(), snapshot))
                throw new InvalidOperationException(
                    "Normal Tally full Voucher XML has missing or duplicate MASTERIDs. No consolidated CREATE/ALTER.");
        }
        var seen = new HashSet<string>(IgnoreCase);
        var result = new List<TallyVoucherIndexRecord>(index.Count);
        foreach (var saved in index)
        {
            var id = saved.MasterId.Trim();
            if (id.Length == 0 || !seen.Add(id) ||
                !byId.TryGetValue(id, out var source) ||
                string.IsNullOrWhiteSpace(source.OriginalXml) ||
                !IgnoreCase.Equals(source.AlterId.Trim(), saved.AlterId.Trim()) ||
                (saved.Guid.Trim().Length > 0 && source.Guid.Trim().Length > 0 &&
                 !IgnoreCase.Equals(source.Guid.Trim(), saved.Guid.Trim())))
                throw new InvalidOperationException(
                    "Normal synchronized Voucher MASTERID/ALTERID/GUID authority is incomplete for " +
                    id + ". No consolidated CREATE/ALTER.");

            var document = TallyConsolidatedXmlTextSafety.ParseDocument(
                source.OriginalXml, "normal synchronized Voucher full DR/CR");
            var voucher = document.Root?.DescendantsAndSelf()
                .FirstOrDefault(element => Is(element, "VOUCHER"));
            if (voucher is null)
                throw new InvalidOperationException(
                    "Normal synchronized full Voucher XML has no Voucher object for MASTERID " + id + ".");
            var xmlMasterId = Value(voucher, "MASTERID");
            var xmlAlterId = Value(voucher, "ALTERID");
            if ((xmlMasterId.Length > 0 && !IgnoreCase.Equals(xmlMasterId, id)) ||
                (xmlAlterId.Length > 0 &&
                 !IgnoreCase.Equals(xmlAlterId, saved.AlterId.Trim())))
                throw new InvalidOperationException(
                    "Normal synchronized full Voucher XML contains mismatching native MASTERID/ALTERID for " +
                    id + ". No consolidated CREATE/ALTER.");

            var posts = TallyHttpService.ReadVoucherLedgerPostings(voucher);
            if (posts.Count == 0 &&
                (saved.DebitLedgers.Length > 0 || saved.CreditLedgers.Length > 0 ||
                 saved.Amount != 0m))
                throw new InvalidOperationException(
                    "Normal synchronized full Voucher XML has missing signed DR/CR for MASTERID " +
                    id + ". No consolidated CREATE/ALTER.");
            saved.LedgerPostings = posts;
            result.Add(saved);
        }
        return result;
    }

    // Previous->Desired rule from the original individual exporter:
    // identity + same last verified ALTERID proves the destination has not
    // been altered. The original saved SOURCE fingerprint must ALSO match
    // today's prepared source before classifying it unchanged. Same
    // MASTERID/ALTERID alone is permission to inspect old DR/CR for ALTER,
    // never permission to ignore a newly changed AAH ledger or amount.
    internal static bool IsVerifiedUnchangedBoundSource(
        TallySourceBindingVersionState saved,
        TallyVoucherIndexRecord live, string preparedSourceHash,
        string? desiredVoucherType = null)
    {
        // Even an old legacy source binding with a previously accepted hash
        // cannot skip a changed destination voucher type. The desired new
        // branch subtype (or restoration to base) must use the original
        // Previous->Desired same-MASTERID ALTER writer instead.
        return (desiredVoucherType is null ||
                IgnoreCase.Equals(live.VoucherType, desiredVoucherType)) &&
               saved.State.Equals("Verified", StringComparison.OrdinalIgnoreCase) &&
               saved.MasterId.Trim().Length > 0 &&
               IgnoreCase.Equals(saved.MasterId.Trim(), live.MasterId.Trim()) &&
               saved.VerifiedContentHash.Length > 0 &&
               IgnoreCase.Equals(saved.VerifiedContentHash, preparedSourceHash) &&
               saved.LastVerifiedAlterId > 0 &&
               long.TryParse(live.AlterId.Trim(), NumberStyles.Integer,
                   CultureInfo.InvariantCulture, out var actualAlter) &&
               actualAlter == saved.LastVerifiedAlterId &&
               (saved.Guid.Trim().Length == 0 ||
                IgnoreCase.Equals(saved.Guid.Trim(), live.Guid.Trim()));
    }

    private static void RequireMatchingSynchronizedMarker(
        TallyClientLink link, TallyCompanyInfo destination,
        TallyBankSyncState state, TallyVoucherChangeMarker marker)
    {
        if (!marker.HasReliableAlterId || marker.LastAlterId <= 0 ||
            string.IsNullOrWhiteSpace(state.LastTallyChangeToken) ||
            !string.Equals(marker.Token, state.LastTallyChangeToken, StringComparison.Ordinal) ||
            marker.VoucherCount != state.LastVoucherCount ||
            (marker.CompanyName.Length > 0 && !IgnoreCase.Equals(marker.CompanyName, destination.Name)))
            throw new InvalidOperationException(
                "Live Tally is newer than the synchronized Client/FY/CompanyKey authority " +
                "(or its ALTERID is unreliable). Complete NORMAL Tally synchronization; " +
                "no second full-company snapshot will be taken.");
        if (!IgnoreCase.Equals(state.CompanyKey, link.CompanyKey) ||
            state.ClientId != link.ClientId)
            throw new InvalidOperationException(
                "Synchronized Tally authority belongs to a different client or company.");
    }

    private async Task RequireUnchangedSynchronizedAuthorityAsync(
        TallyClientLink link, TallyCompanyInfo destination, string financialYear,
        TallyVoucherChangeMarker initial, CancellationToken token)
    {
        // A tiny change-marker check, not another complete FY Voucher request.
        var marker = await ReadTrustedMarkerAsync(
            link, destination, financialYear, token).ConfigureAwait(false);
        var state = tallyRepository?.GetBankSyncState(link.ClientId, financialYear, link.CompanyKey)
            ?? throw new InvalidOperationException("Normal Tally authority checkpoint disappeared.");
        RequireMatchingSynchronizedMarker(link, destination, state, marker);
        if (!string.Equals(marker.Token, initial.Token, StringComparison.Ordinal) ||
            marker.LastAlterId != initial.LastAlterId)
            throw new InvalidOperationException(
                "Live Tally changed during consolidated preparation. Refresh normal Tally authority and regenerate.");
    }

    /// <summary>
    /// Produce REAL, manually importable Tally master and voucher envelopes,
    /// with destination-bound ALTER actions for changed vouchers. This is a
    /// strict read-only live preflight: it NEVER calls ImportXmlAsync, changes
    /// source-binding states, posts masters, or reserves an ALTER/CREATE.
    /// The original prepared CREATE XML remains the internal AAH import
    /// authority and MUST NOT be mistaken for this manual export.
    /// </summary>
    public async Task<TallyConsolidatedManualImportXmlResult> PrepareManualImportXmlAsync(
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
                "Manual XML needs AAH's original source->destination MASTERID bindings.");
        var financialYear = TallyBranchConsolidationImportService.RequireConfirmedDestination(
            link, destination, accountingFinancialYear);
        TallyConsolidatedPreparedXmlBuilder.RequireNoForeignTallyIdentity(
            prepared.MastersXml, "original consolidated manual master source");
        TallyConsolidatedPreparedXmlBuilder.RequireNoForeignTallyIdentity(
            prepared.VouchersXml, "original consolidated manual voucher source");
        var masterDoc = TallyConsolidatedXmlTextSafety.ParseDocument(
            prepared.MastersXml, "manual import original masters");
        var voucherDoc = TallyConsolidatedXmlTextSafety.ParseDocument(
            prepared.VouchersXml, "manual import original vouchers");
        RequireDestination(masterDoc, destination.Name);
        RequireDestination(voucherDoc, destination.Name);
        var masters = Messages(masterDoc).Select(x => x.Elements().Single()).ToArray();
        var vouchers = Messages(voucherDoc).Select(x => x.Elements().Single()).ToArray();
        if (vouchers.Length != prepared.FinalVouchers ||
            vouchers.Length == 0 || vouchers.Any(x => !Is(x, "VOUCHER")))
            throw new InvalidOperationException(
                "Manual export refused: consolidated voucher count/shape changed.");
        if (masters.Any(x => !PriorMasters.Contains(x.Name.LocalName) &&
                             !MasterOrder.Contains(x.Name.LocalName)))
            throw new InvalidOperationException(
                "Manual import contains a master type outside the original Tally importer.");

        // Only Tally EXPORT/read requests. Never use the cached voucher index
        // as proof of identity, and never copy a branch-company MASTERID.
        var open = await tally.DiscoverCompaniesAsync(cancellationToken).ConfigureAwait(false);
        var linked = TallyBranchConsolidationImportService.RequireUniqueLiveDestination(
            link, destination, open, financialYear);
        if (!int.TryParse(financialYear[..Math.Min(4, financialYear.Length)],
                NumberStyles.None, CultureInfo.InvariantCulture, out var startYear) ||
            startYear < 1900 || startYear > 9998)
            throw new InvalidOperationException("Manual import financial year cannot be verified.");
        var from = new DateTime(startYear, 4, 1);
        var to = new DateTime(startYear + 1, 3, 31);
        var (live, capturedMarker) = await ReadSynchronizedVoucherAuthorityAsync(
            link, linked, financialYear, from, to, cancellationToken).ConfigureAwait(false);
        // The full FY authority is already collected in bounded monthly slices.
        // Index its full signed Voucher XML ONCE by destination MASTERID so
        // verifying each prepared source does not scan all voucher XML again.
        // Keep duplicate IDs as multiple rows: the existing exact-one check
        // must still fail closed, never silently choose one.
        var fullByMasterId = live.FullVouchers
            .Where(item => item.MasterId.Trim().Length > 0)
            .GroupBy(item => item.MasterId.Trim(), IgnoreCase)
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<TallyVoucherXmlSnapshot>)group.ToArray(), IgnoreCase);
        // The normal coordinator's LastFyVoucherCount proves an empty target FY.
        // Earlier-year vouchers in the same company are not an incomplete FY
        // snapshot and must not block legitimate first-time consolidated XML.
        var available = await ReadMasterAuthorityAsync(
            linked, cancellationToken,
            vouchers.Select(v => Value(v, "VOUCHERTYPENAME"))
                .Where(prepared.VoucherTypeBaseByDestination.ContainsKey)
                .Distinct(IgnoreCase).ToArray(),
            allowMissingChildTypesInMasterFirstManualXml: true)
            .ConfigureAwait(false);
        RequireExistingDefaultCashForBankVouchers(vouchers, masters, available);

        // A real manual Master XML must contain ONLY missing masters, in the
        // same dependency order as the established automatic master-first
        // importer. Existing names must pass the original compatibility check;
        // otherwise a blind CREATE would incorrectly duplicate/reparent them.
        var missingMasters = new Dictionary<string, XElement>(IgnoreCase);
        foreach (var master in masters)
        {
            var name = MasterName(master);
            var key = MasterKey(master.Name.LocalName, name);
            if (name.Length == 0 || missingMasters.ContainsKey(key))
                throw new InvalidOperationException(
                    "Ambiguous or duplicate consolidated master in manual XML: " + name);
            if (available.TryGetValue(key, out var existing))
            {
                RequireMaster(available, master);
                continue;
            }
            missingMasters.Add(key, new XElement(master));
        }
        foreach (var (type, baseType) in prepared.VoucherTypeBaseByDestination)
        {
            if (!vouchers.Any(x => IgnoreCase.Equals(Value(x, "VOUCHERTYPENAME"), type)))
                continue;
            if (!available.TryGetValue(MasterKey("VOUCHERTYPE", baseType), out var baseMaster) ||
                !long.TryParse(Value(baseMaster, "MASTERID"), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var baseMasterId) || baseMasterId <= 0)
                throw new InvalidOperationException(
                    "The original parent voucher type '" + baseType +
                    "' is not a verified Tally master; no manual ALTER XML is safe.");
            if (available.TryGetValue(MasterKey("VOUCHERTYPE", type), out var child))
            {
                if (!IgnoreCase.Equals(Value(child, "PARENT"), baseType) ||
                    !long.TryParse(Value(child, "MASTERID"), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var childId) || childId <= 0)
                    throw new InvalidOperationException(
                        "Existing destination voucher type has the wrong parent or no MASTERID: " + type);
            }
            else if (!missingMasters.TryGetValue(MasterKey("VOUCHERTYPE", type), out var staged) ||
                     !IgnoreCase.Equals(Value(staged, "PARENT"), baseType))
                throw new InvalidOperationException(
                    "Required branch voucher type is neither verified live nor included in the manual master XML: " + type);
        }

        var ready = new List<XElement>();
        var proofs = new List<string>();
        var stableAnchors = new HashSet<string>(IgnoreCase);
        var uniqueVoucherNumbers = new HashSet<string>(IgnoreCase);
        var claimedIds = new HashSet<string>(IgnoreCase);
        var create = 0;
        var alter = 0;
        var unchanged = 0;
        foreach (var voucher in vouchers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Field(voucher, "ACTION").Equals("Create", StringComparison.OrdinalIgnoreCase) ||
                voucher.DescendantsAndSelf().Any(x =>
                    Is(x, "MASTERID") || Is(x, "GUID") || Is(x, "ALTERID") ||
                    x.Attributes().Any(a => a.Name.LocalName is
                        "MASTERID" or "GUID" or "ALTERID" or "TAGNAME" or "TAGVALUE")))
                throw new InvalidOperationException(
                    "Only original identity-free prepared vouchers can be materialized for manual Tally import.");
            var type = Value(voucher, "VOUCHERTYPENAME");
            var number = Value(voucher, "VOUCHERNUMBER");
            var reference = Value(voucher, "REFERENCE");
            if (!DateTime.TryParseExact(Value(voucher, "DATE"), "yyyyMMdd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                date < from || date > to || type.Length == 0 || number.Length == 0 ||
                !uniqueVoucherNumbers.Add(type + "|" + number))
                throw new InvalidOperationException(
                    "The prepared manual voucher has an invalid date, type or duplicate voucher number.");
            var anchor = BuildConsolidatedSourceAnchor(
                type, number, prepared.VoucherTypeBaseByDestination);
            if (!stableAnchors.Add(anchor))
                throw new InvalidOperationException(
                    "One original consolidated source appears twice in the manual voucher XML: " + anchor);
            var postings = ReadPostings(voucher);
            if (postings.Count == 0)
                throw new InvalidOperationException("Original signed DR/CR is missing: " + anchor);
            var (desiredHash, _) = ComputePreparedContentIdentity(voucher);
            var state = tallyRepository.GetSourceBindingVersionState(
                link.ClientId, financialYear, link.CompanyKey, SourceEntityType, anchor);
            if (state is not null)
            {
                // Pending/accepted/unverified/deleted are NOT safe offline
                // XML actions: Tally may already have accepted a write.
                if (!state.State.Equals("Verified", StringComparison.OrdinalIgnoreCase) ||
                    !long.TryParse(state.MasterId, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var ownedId) || ownedId <= 0)
                    throw new InvalidOperationException(
                        "Consolidated source has pending/unverified Tally ownership: " + anchor +
                        ". Reconcile with the existing AAH Tally safety gate first.");
                var bound = live.Vouchers.Where(x =>
                    IgnoreCase.Equals(x.MasterId, state.MasterId)).ToArray();
                if (bound.Length != 1 || !claimedIds.Add(state.MasterId))
                    throw new InvalidOperationException(
                        "Manual XML cannot find one unique bound destination MASTERID for " + anchor);
                var previous = bound[0];
                var boundFull = fullByMasterId.TryGetValue(previous.MasterId, out var savedXml)
                    ? savedXml : Array.Empty<TallyVoucherXmlSnapshot>();
                // This is the same decision order as the EXISTING automatic
                // consolidated importer: first prove the source's SAVED
                // verified content is identical to the FULL live Tally voucher.
                // An exact existing voucher requires NO CREATE/ALTER at all.
                // A legacy normal-client ModifyPending record can claim its
                // MASTERID, but that must not make a read-only NO-OP fail.
                // This does NOT release/transfer the other source's binding.
                if (FullVoucherMatches(
                        new ExpectedVoucher(date, type, number, postings, voucher,
                            reference, anchor, desiredHash, ""), previous, boundFull))
                {
                    unchanged++;
                    var competingUnchanged = tallyRepository.ReadTallyMasterIdCompetingClaims(
                        link.ClientId, financialYear, link.CompanyKey,
                        SourceEntityType, anchor, previous.MasterId);
                    proofs.Add("SKIP " + anchor + " | destination MASTERID=" +
                        previous.MasterId + " | ALTERID=" + previous.AlterId +
                        " | exact saved and live full DR/CR already matches; no XML action" +
                        (competingUnchanged.Count == 0 ? "" :
                            " | other AAH source claim(s) remain unresolved (read-only SKIP; " +
                            "no claim cleared, rebound or modified): " +
                            string.Join("; ", competingUnchanged.Select(other =>
                                $"ClientId={other.ClientId}, {other.EntityType}, " +
                                $"{other.SourceAnchor}, {other.State}"))));
                    continue;
                }

                // Reuse the SAME VERIFIED-BOUND decision as ImportAsync:
                // a saved consolidated source already owns this exact Tally
                // MASTERID. A normal-module historical Purchase ModifyPending
                // claim is not a second consolidated source and must not be an
                // extra manual-only veto on the established Previous->Desired
                // bound ALTER path. Never assign/clear/rebind either claim.
                // Changed vouchers STILL need the ORIGINAL saved previous
                // version, current live MASTERID + ALTERID, and full signed
                // DR/CR to agree; otherwise the standard ALTER remains blocked.
                // First-unbound sources continue through the existing strict
                // candidate/duplicate/ownership checks below (no blind CREATE).
                if (IgnoreCase.Equals(state.VerifiedContentHash, desiredHash) ||
                    !PreviousConsolidatedVersionMatches(
                        previous, boundFull, state))
                    throw new InvalidOperationException(
                        "Previous saved voucher version, destination MASTERID, ALTERID or signed DR/CR has changed: " +
                        anchor + ". A blind manual ALTER or CREATE is forbidden.");

                // EXACTLY the same bound voucher ALTER writer as automatic
                // Export Consolidated. It sets ACTION=Alter, TAGNAME=MASTER ID,
                // TAGVALUE=<live MASTERID>, actual live Voucher Number, and the
                // requested parent/child VCHTYPE without recalculating DR/CR.
                var alterXml = TallyAccountingVoucherExportService
                    .BuildBoundConsolidatedVoucherAlterXml(
                        destination.Name, voucher, previous.MasterId,
                        previous.VoucherNumber);
                var alterDoc = TallyConsolidatedXmlTextSafety.ParseDocument(
                    alterXml, "read-only bound manual voucher ALTER");
                var messages = Messages(alterDoc);
                if (messages.Length != 1 || messages[0].Elements().Count() != 1)
                    throw new InvalidOperationException(
                        "The standard bound Tally ALTER writer did not produce exactly one voucher.");
                var boundAlter = messages[0].Elements().Single();
                if (!Field(boundAlter, "ACTION").Equals("Alter", StringComparison.OrdinalIgnoreCase) ||
                    !Field(boundAlter, "TAGNAME").Equals("MASTER ID", StringComparison.OrdinalIgnoreCase) ||
                    !IgnoreCase.Equals(Field(boundAlter, "TAGVALUE"), previous.MasterId) ||
                    !IgnoreCase.Equals(Value(boundAlter, "VOUCHERTYPENAME"), type) ||
                    !IgnoreCase.Equals(Value(boundAlter, "VOUCHERNUMBER"), previous.VoucherNumber) ||
                    !SamePostings(postings, ReadPostings(boundAlter)))
                    throw new InvalidOperationException(
                        "The generated manual ALTER differs from the original prepared voucher: " + anchor);
                ready.Add(new XElement(messages[0]));
                alter++;
                proofs.Add("ALTER " + anchor + " | destination MASTERID=" +
                    previous.MasterId + " | last verified ALTERID=" +
                    previous.AlterId + " | previous type=" + previous.VoucherType +
                    " | desired type=" + type);
                continue;
            }
            if (tallyRepository.HasImportedSourceFingerprint(
                    link.ClientId, financialYear, link.CompanyKey,
                    SourceEntityType, anchor))
                throw new InvalidOperationException(
                    "Manual CREATE denied: source has historical Tally import evidence without a verified MASTERID: " + anchor);
            // A missing binding with a plausible same-document live voucher
            // must NEVER become a second CREATE. Shared B2C references across
            // branches are particularly ambiguous with Tally auto numbering.
            if (live.Vouchers.Any(item =>
                item.VoucherDate?.Date == date.Date &&
                (IgnoreCase.Equals(item.VoucherNumber, number) ||
                 (reference.Length > 0 && IgnoreCase.Equals(item.Reference, reference) &&
                  (IgnoreCase.Equals(item.VoucherType, type) ||
                   (prepared.VoucherTypeBaseByDestination.TryGetValue(type, out var baseType) &&
                    IgnoreCase.Equals(item.VoucherType, baseType)))) ||
                 // A missing/blank Tally reference or auto-number must not
                 // authorize re-CREATE of the same original DR/CR under a
                 // different type. Fail closed for ambiguous manual files.
                 SamePostings(postings, item.LedgerPostings
                     .Select(p => (p.LedgerName, p.SignedAmount)).ToArray()))))
                throw new InvalidOperationException(
                    "Manual CREATE denied: existing Tally voucher may own source " + anchor +
                    ". Resolve its unique MASTERID in AAH instead of duplicating it.");
            ready.Add(new XElement("TALLYMESSAGE", new XElement(voucher)));
            create++;
            proofs.Add("CREATE " + anchor + " | no existing AAH source binding or matching live Tally source");
        }
        // The original normal FY authority is already held in SQLite;
        // only its live change marker is checked again before writing files.
        await RequireUnchangedSynchronizedAuthorityAsync(
            link, linked, financialYear, capturedMarker, cancellationToken).ConfigureAwait(false);
        var masterBuilder = new StringBuilder();
        TallyExcelParityXmlWriter.BeginAllMastersImport(masterBuilder, destination.Name);
        foreach (var type in PriorMasters.Concat(MasterOrder))
        foreach (var master in missingMasters.Values.Where(m => Is(m, type)))
            masterBuilder.AppendLine(new XElement("TALLYMESSAGE", master)
                .ToString(SaveOptions.DisableFormatting));
        TallyExcelParityXmlWriter.EndAllMastersImport(masterBuilder);
        var voucherBuilder = new StringBuilder();
        TallyExcelParityXmlWriter.BeginErp9DataImport(voucherBuilder, destination.Name, "Vouchers");
        foreach (var message in ready)
            voucherBuilder.AppendLine(message.ToString(SaveOptions.DisableFormatting));
        TallyExcelParityXmlWriter.EndErp9DataImport(voucherBuilder);
        TallyConsolidatedXmlTextSafety.ParseDocument(
            masterBuilder.ToString(), "real manual Tally master XML");
        TallyConsolidatedXmlTextSafety.ParseDocument(
            voucherBuilder.ToString(), "real manual Tally voucher XML");
        var audit = "Generated from normal AAH synchronized full-FY Voucher authority with fresh change-marker checks." +
            Environment.NewLine + "Tally company: " + destination.Name +
            Environment.NewLine + "Financial year: " + financialYear +
            Environment.NewLine + "Required missing masters: " + missingMasters.Count +
            Environment.NewLine + "Ready CREATE: " + create +
            Environment.NewLine + "Ready ALTER: " + alter +
            Environment.NewLine + "Already identical (excluded): " + unchanged +
            Environment.NewLine + "IMPORT MASTERS FIRST if any are listed, then VOUCHERS." +
            Environment.NewLine + "Never import an old generated file after a Tally edit." +
            Environment.NewLine + "Manual Tally import bypasses AAH acceptance/readback tracking: " +
                "refresh/reconcile AAH source bindings after importing, and DO NOT import the same file twice." +
            Environment.NewLine + string.Join(Environment.NewLine, proofs);
        return new TallyConsolidatedManualImportXmlResult(
            masterBuilder.ToString(), voucherBuilder.ToString(),
            missingMasters.Count, create, alter, unchanged, audit);
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
            // The source identity is the ORIGINAL branch voucher type.
            // Selecting a separate destination type changes the DESIRED Tally
            // version, never its existing AAH source->MASTERID binding.
            var anchor = BuildConsolidatedSourceAnchor(
                type, number, prepared.VoucherTypeBaseByDestination);
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
            var (contentHash, exactPostings) = ComputePreparedContentIdentity(voucher);
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
            _ = RequireVerifiedLiveConsolidatedVersion(confirmed, after.Full);
            // Reuse the individual importer's fresh native readback persistence.
            // This is a fully verified date slice, not a new Tally read or a
            // fabricated company-wide ALTERID checkpoint.
            tallyRepository.ReplaceVoucherIndexRange(
                link.CompanyKey, financialYear, expected.Date, expected.Date, after.Index);
            tallyRepository.UpsertVoucherXmlAuthority(
                link.CompanyKey, financialYear, after.Full);
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
        string? accountingFinancialYear = null,
        bool voucherTypeOnlyAlter = false)
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
        var stableAnchors = new HashSet<string>(IgnoreCase);
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
            // Preserve legacy consolidated source anchors (e.g. Sales|KA-INV)
            // across the optional destination voucher-type toggle. Without
            // this mapping a previously verified MASTERID looked unbound,
            // which could incorrectly enter the CREATE path.
            var sourceAnchor = BuildConsolidatedSourceAnchor(
                type, number, prepared.VoucherTypeBaseByDestination);
            if (!stableAnchors.Add(sourceAnchor))
                throw new InvalidOperationException(
                    "Original Bank source was repeated with different voucher treatments: " +
                    sourceAnchor + ". One source cannot CREATE two Tally vouchers.");
            var (contentHash, exactPostings) = ComputePreparedContentIdentity(voucher);
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
        // Reuse the verified full-FY MASTERID and complete signed Voucher XML
        // ALREADY held by the ordinary Tally coordinator. No full-company
        // snapshot is needed to classify the consolidated destination.
        var (before, capturedMarker) = await ReadSynchronizedVoucherAuthorityAsync(
            link, liveDestination, financialYear, from, to, cancellationToken).ConfigureAwait(false);
        // An empty verified active FY is valid even when the same Tally
        // company has vouchers from earlier years. The synchronized FY count
        // was already verified by ReadSynchronizedVoucherAuthorityAsync.
        // Normal Tally synchronization already owns these exact SQLite rows.
        // The consolidated wrapper must not publish another baseline.
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
        var preflightBoundConflicts = new List<string>();
        var alterTargets = new Dictionary<string, TallyVoucherIndexRecord>(IgnoreCase);
        var claimedAlterMasterIds = new HashSet<string>(IgnoreCase);
        // A normal Tally company can auto-number the voucher and drop the
        // branch-scoped B-{hash} number. An unchanged shared B2C REFERENCE
        // therefore needs existing original source-to-MASTERID authority,
        // never a guess from the Tally-owned number or ledger-name suffix.
        bool IsAlreadyOwnedByOtherVerifiedBranch(
            TallyVoucherIndexRecord live, ExpectedVoucher current)
        {
            if (live.MasterId.Length == 0) return false;
            return source.Any(other =>
            {
                if (IgnoreCase.Equals(other.SourceAnchor, current.SourceAnchor) ||
                    !HasDifferentPreparedBranchScopes(other.Number, current.Number) ||
                    !IgnoreCase.Equals(other.Type, live.VoucherType) ||
                    !IgnoreCase.Equals(other.Reference, live.Reference) ||
                    other.Date != live.VoucherDate?.Date)
                    return false;
                var otherBinding = tallyRepository.GetSourceBindingVersionState(
                    link.ClientId, financialYear, link.CompanyKey,
                    SourceEntityType, other.SourceAnchor);
                return otherBinding is not null &&
                    (otherBinding.State.Equals("Verified", StringComparison.OrdinalIgnoreCase) ||
                     otherBinding.State.Equals("AcceptedAwaitingVerification", StringComparison.OrdinalIgnoreCase)) &&
                    IgnoreCase.Equals(otherBinding.MasterId, live.MasterId);
            });
        }
        foreach (var planned in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = tallyRepository.GetSourceBindingVersionState(
                link.ClientId, financialYear, link.CompanyKey, SourceEntityType,
                planned.SourceAnchor);
            // Only previously VERIFIED bound source vouchers are eligible for
            // a type-only ALTER. Never CREATE an unbound or pending row.
            if (voucherTypeOnlyAlter &&
                (state is null ||
                 !state.State.Equals("Verified", StringComparison.OrdinalIgnoreCase)))
            {
                skipped.Add(planned.SourceAnchor);
                continue;
            }
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
                if (voucherTypeOnlyAlter)
                {
                    // A branch type may replace only its verified original
                    // base type. All other source edits must use normal import.
                    if (IgnoreCase.Equals(liveBound.VoucherType, planned.Type))
                    {
                        skipped.Add(planned.SourceAnchor);
                        continue;
                    }
                    if (!prepared.VoucherTypeBaseByDestination.TryGetValue(
                            planned.Type, out var originalType) ||
                        !IgnoreCase.Equals(originalType, liveBound.VoucherType) ||
                        !MatchesTypeOnlyPreparedVoucher(
                            planned.Xml, liveBound, before.FullVouchers) ||
                        !PreviousConsolidatedVersionMatches(
                            liveBound, before.FullVouchers, state))
                    {
                        preflightBoundConflicts.Add(
                            planned.SourceAnchor + " | MASTERID " + liveBound.MasterId +
                            " has different original type, accounting data or " +
                            "saved previous ALTERID/DRCR. No type-only ALTER.");
                        continue;
                    }
                    if (!claimedAlterMasterIds.Add(liveBound.MasterId))
                        throw new InvalidOperationException(
                            "Multiple consolidated sources claim MASTERID " +
                            liveBound.MasterId + "; no type-only ALTER.");
                    alterTargets.Add(planned.SourceAnchor, liveBound);
                    continue;
                }
                // Identical to normal individual export's already-verified
                // no-op gate: same saved SOURCE hash + bound MASTERID +
                // positive last-verified ALTERID + native GUID. Tally's FY
                // full signed DR/CR is available for ALL vouchers above, but
                // this unchanged source does not need a second comparison.
                if (IsVerifiedUnchangedBoundSource(
                        state, liveBound, planned.ContentHash, planned.Type))
                {
                    skipped.Add(planned.SourceAnchor);
                    continue;
                }
                // Legacy verified bindings without a saved ALTERID may use
                // the original FULL previous DR/CR proof. Never bypass the
                // previous version just because an unverified source happens
                // to look similar to a live Voucher.
                if (IgnoreCase.Equals(state.VerifiedContentHash, planned.ContentHash) &&
                    FullVoucherMatches(planned, liveBound, before.FullVouchers) &&
                    PreviousConsolidatedVersionMatches(
                        liveBound, before.FullVouchers, state))
                {
                    skipped.Add(planned.SourceAnchor);
                    continue;
                }
                // Same MASTERID/ALTERID with a DIFFERENT desired source hash
                // is NOT an exact duplicate. Now compare the complete
                // previous signed DR/CR (hydrated from normal Tally sync),
                // then let the unchanged original bound ALTER writer proceed.
                if (state.VerifiedContentHash.Length == 0 ||
                    IgnoreCase.Equals(state.VerifiedContentHash, planned.ContentHash) ||
                    !PreviousConsolidatedVersionMatches(liveBound, before.FullVouchers, state))
                {
                    preflightBoundConflicts.Add(
                        planned.SourceAnchor + " | MASTERID " + state.MasterId +
                        " | saved ALTERID " + state.LastVerifiedAlterId +
                        ", live ALTERID " + liveBound.AlterId +
                        " | saved/current SOURCE hash same: " +
                        IgnoreCase.Equals(state.VerifiedContentHash, planned.ContentHash) +
                        " | " + ExplainPreviousFullVoucherMismatch(
                            liveBound, before.FullVouchers, state));
                    continue; // Collect other safe preflight failures; NO Tally mutation.
                }
                if (!claimedAlterMasterIds.Add(liveBound.MasterId))
                    throw new InvalidOperationException(
                        "Multiple original branch sources claim one destination Tally MASTERID " +
                        liveBound.MasterId + "; no ALTER or CREATE is safe.");
                alterTargets.Add(planned.SourceAnchor, liveBound);
                continue;
            }
            // A legacy already-imported voucher can exist under its
            // ORIGINAL base type even if its binding is missing/corrupt.
            // Never CREATE a second voucher merely because the destination
            // type was switched to/from a branch subtype. Another branch's
            // separately verified MASTERID is not this source's property.
            if (state is null && before.Vouchers.Any(live =>
                live.VoucherDate?.Date == planned.Date.Date &&
                !IgnoreCase.Equals(live.VoucherType, planned.Type) &&
                (live.VoucherType.StartsWith(planned.Type + " - ",
                     StringComparison.OrdinalIgnoreCase) ||
                 planned.Type.StartsWith(live.VoucherType + " - ",
                     StringComparison.OrdinalIgnoreCase)) &&
                !HasDifferentPreparedBranchScopes(live.VoucherNumber, planned.Number) &&
                !tallyRepository.IsTallyMasterIdClaimedByAnotherSource(
                    link.ClientId, financialYear, link.CompanyKey,
                    SourceEntityType, planned.SourceAnchor, live.MasterId) &&
                (IgnoreCase.Equals(live.VoucherNumber, planned.Number) ||
                 (planned.Reference.Length > 0 &&
                  IgnoreCase.Equals(live.Reference, planned.Reference) &&
                  SamePostings(planned.Postings, live.LedgerPostings
                      .Select(p => (p.LedgerName, p.SignedAmount)).ToArray())))))
                throw new InvalidOperationException(
                    "An existing destination voucher under the original/branch " +
                    "voucher type matches source " + planned.SourceAnchor +
                    ", but its saved MASTERID binding is missing. No duplicate " +
                    "CREATE or blind ALTER is allowed; reconcile original " +
                    "Tally source ownership first.");

            // AAH's durable source anchor survives Tally auto-numbering.
            // Never equate a new Tally VOUCHERNUMBER with a new source.
            var candidates = before.Vouchers.Where(live =>
                IgnoreCase.Equals(live.VoucherType, planned.Type) &&
                ((planned.Reference.Length > 0 &&
                  IgnoreCase.Equals(live.Reference, planned.Reference)) ||
                 IgnoreCase.Equals(live.VoucherNumber, planned.Number) ||
                 (state?.MasterId.Length > 0 &&
                  IgnoreCase.Equals(live.MasterId, state.MasterId)) ||
                 PotentialExistingUnboundBankVoucher(live, planned))).ToArray();

            // CL019: B2C vouchers in two DIFFERENT branches may share the
            // same source REFERENCE/date/type. Reference alone is not a
            // destination identity and must never bind the wrong MASTERID.
            // Filter by the normal accounting strict classifier AND complete
            // live signed ledger XML before assessing the number of matches.
            var exactMatches = candidates.Where(candidate =>
                FullVoucherMatches(planned, candidate, before.FullVouchers)).ToArray();
            if (exactMatches.Any(candidate =>
                    IsAlreadyOwnedByOtherVerifiedBranch(candidate, planned)))
                throw new InvalidOperationException(
                    "An identical destination voucher is already verified as a DIFFERENT " +
                    "branch's source. One Tally MASTERID cannot be rebound to a " +
                    "second branch merely because its date/reference/DRCR coincide.");
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
            // CL019 Nov 3: an early accepted Karnataka B2C Sales voucher has
            // live MASTERID=337/ALTERID=339, but no durable binding for its
            // CURRENT branch-scoped source anchor. Its old debtor Ledger was
            // "B2C Sales - 24 - Karnataka"; original branch XML now specifies
            // "B2C Sales - Karnataka". Both tax lines/amounts are identical.
            // A first-unbound ledger-only edit is an ALTER, not a second CREATE.
            // Recover the old version ONLY from full fresh Tally readback,
            // and only when no other AAH source has claimed this MASTERID.
            // Then the existing Previous->Desired / same-MASTERID ALTER path
            // below performs a SECOND fresh version+ALTERID check before POST.
            // The same source may instead be Tally-accepted but still
            // awaiting readback from the earlier cross-branch B2C collision.
            // That state owns a LASTVCHID and an immutable PENDING old version.
            // Recover only if the old live XML proves THAT pending version.
            var acceptedOldSource = state is not null &&
                state.State.Equals("AcceptedAwaitingVerification", StringComparison.OrdinalIgnoreCase) &&
                state.MasterId.Length > 0 &&
                state.PendingVersion.Length > 0 &&
                state.PendingContentHash.Length > 0;
            if (acceptedOldSource || (state is null &&
                !tallyRepository.HasImportedSourceFingerprint(
                    link.ClientId, financialYear, link.CompanyKey,
                    SourceEntityType, planned.SourceAnchor)))
            {
                var recoverable = candidates.Where(candidate =>
                    (!acceptedOldSource ||
                     IgnoreCase.Equals(candidate.MasterId, state!.MasterId)) &&
                    TryReadFirstUnboundB2cLedgerRename(
                        planned.Xml, candidate, before.FullVouchers,
                        out _, out _)).ToArray();
                if (recoverable.Length > 1)
                    throw new InvalidOperationException(
                        "Multiple live Tally MASTERIDs fit the unbound B2C ledger rename for " +
                        planned.SourceAnchor + ". Manual ownership review is required.");
                if (recoverable.Length == 1)
                {
                    var priorLive = recoverable[0];
                    if (tallyRepository.IsTallyMasterIdClaimedByAnotherSource(
                            link.ClientId, financialYear, link.CompanyKey,
                            SourceEntityType, planned.SourceAnchor, priorLive.MasterId) ||
                        source.Any(other =>
                            !IgnoreCase.Equals(other.SourceAnchor, planned.SourceAnchor) &&
                            TryReadFirstUnboundB2cLedgerRename(
                                other.Xml, priorLive, before.FullVouchers, out _, out _)) ||
                        !claimedAlterMasterIds.Add(priorLive.MasterId))
                        throw new InvalidOperationException(
                            "Existing Tally MASTERID " + priorLive.MasterId +
                            " is not uniquely owned by this unbound B2C branch source. " +
                            "No CREATE or ALTER is permitted.");
                    if (!TryReadFirstUnboundB2cLedgerRename(
                            planned.Xml, priorLive, before.FullVouchers,
                            out var oldHash, out var oldVersion) ||
                        !long.TryParse(priorLive.AlterId, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var oldAlterId) ||
                        oldAlterId <= 0)
                        throw new InvalidOperationException(
                            "Could not verify previous full B2C voucher version for " +
                            planned.SourceAnchor + "; no CREATE or ALTER is permitted.");
                    if (acceptedOldSource &&
                        (!IgnoreCase.Equals(state!.PendingContentHash, oldHash) ||
                         !string.Equals(state.PendingVersion, oldVersion,
                             StringComparison.Ordinal)))
                        throw new InvalidOperationException(
                            "Tally-accepted B2C source " + planned.SourceAnchor +
                            " has a different saved pending previous version. " +
                            "No second CREATE or unverified ALTER is allowed.");
                    var previousProof = new TallySourceBindingVersionState
                    {
                        State = "Verified", MasterId = priorLive.MasterId,
                        VerifiedContentHash = oldHash, VerifiedVersion = oldVersion,
                        LastVerifiedAlterId = oldAlterId
                    };
                    if (!PreviousConsolidatedVersionMatches(
                            priorLive, before.FullVouchers, previousProof))
                        throw new InvalidOperationException(
                            "Original Tally MASTERID/ALTERID and old full DR/CR failed " +
                            "the existing previous-version ALTER gate.");
                    // Baseline only: no Tally mutation. Preserve the ORIGINAL
                    // verified old live version; do not record desired DR/CR
                    // as verified until the ordinary ALTER is read back.
                    tallyRepository.ConfirmSourceBindingVersion(
                        link.ClientId, financialYear, link.CompanyKey,
                        SourceEntityType, planned.SourceAnchor, planned.SourceAnchor,
                        priorLive.MasterId, priorLive.Guid, oldHash, oldVersion,
                        oldAlterId,
                        "First-unbound or accepted-pending B2C ledger rename: " +
                        "unique saved/live MASTERID, same date/ref/narration/tax " +
                        "amounts, one party Ledger name correction; prior pending " +
                        "version if present and old full XML/ALTERID verified. " +
                        "No CREATE or ALTER sent during baseline recovery.");
                    alterTargets.Add(planned.SourceAnchor, priorLive);
                    continue;
                }
            }

            // A foreign branch with the same B2C document reference is
            // not the same source. However a same-branch or unscoped live
            // reference that differs in DR/CR is a conflict: fail closed,
            // never CREATE a second version of that invoice.
            if (candidates.Any(candidate =>
                !HasDifferentPreparedBranchScopes(candidate.VoucherNumber, planned.Number) &&
                !IsAlreadyOwnedByOtherVerifiedBranch(candidate, planned)))
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

        if (preflightBoundConflicts.Count > 0)
            throw new InvalidOperationException(
                "Consolidated full-FY PRE-WRITE authority audit found " +
                preflightBoundConflicts.Count +
                " previously verified source(s) requiring review. " +
                "No Tally Voucher CREATE or ALTER was sent. These are all " +
                "the bound previous-version conflicts found in this batch, not " +
                "only the first one: " +
                string.Join(" ; ", preflightBoundConflicts.Take(35)) +
                (preflightBoundConflicts.Count > 35
                    ? " ; plus " + (preflightBoundConflicts.Count - 35) +
                      " more. Preserve the detailed diagnostic ZIP."
                    : ""));

        var available = await ReadMasterAuthorityAsync(
            destination, cancellationToken,
            vouchers.Select(v => Value(v, "VOUCHERTYPENAME"))
                .Where(prepared.VoucherTypeBaseByDestination.ContainsKey)
                .Distinct(IgnoreCase).ToArray(),
            allowExistingNoneNumbering: voucherTypeOnlyAlter).ConfigureAwait(false);
        // Same rule as the ORIGINAL normal Bank/Contra exporter: the built-in
        // Cash ledger remains under Cash-in-Hand when optional branch cash
        // ledgers are not selected. Cash Entry module may be empty while 21+
        // BANK Contra vouchers still reference that Cash. Read the ACTUAL
        // destination Cash MASTERID and parent; never CREATE/reparent a Tally
        // built-in merely because a cash withdrawal uses that ledger.
        RequireExistingDefaultCashForBankVouchers(vouchers, masters, available);
        // Separate voucher types must ALREADY exist in the linked consolidated
        // Tally company and inherit the original base type with a positive
        // destination MASTERID. The existing master importer creates and
        // independently verifies them BEFORE the voucher ALTER stage.
        // Never create a voucher type after the first voucher was posted.
        foreach (var type in vouchers.Select(v => Value(v, "VOUCHERTYPENAME"))
                     .Where(prepared.VoucherTypeBaseByDestination.ContainsKey)
                     .Distinct(IgnoreCase))
        {
            var baseType = prepared.VoucherTypeBaseByDestination[type];
            if (!available.TryGetValue(MasterKey("VOUCHERTYPE", type), out var liveType) ||
                !IgnoreCase.Equals(Value(liveType, "PARENT"), baseType) ||
                !long.TryParse(Value(liveType, "MASTERID"),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var typeMasterId) ||
                typeMasterId <= 0)
                throw new InvalidOperationException(
                    "The consolidated branch voucher type '" + type +
                    "' has not been created and verified under '" + baseType +
                    "' in the linked destination company. No voucher CREATE/ALTER sent.");
        }
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
            if (voucherTypeOnlyAlter) continue; // No ledger/master creation.

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
        foreach (var master in masters.Where(x =>
                     !voucherTypeOnlyAlter || PriorMasters.Contains(x.Name.LocalName)))
            RequireMaster(available, master);
        // The normal synchronized index remains authoritative while Masters
        // were unchanged. If this import CREATED a Master, its own ALTERID
        // change requires a fresh INDEX-only pre-write check. Never request
        // another full-year graph of complete Voucher XML for this barrier.
        IReadOnlyList<TallyVoucherIndexRecord> recheckIndex;
        if (createdMasters == 0)
        {
            await RequireUnchangedSynchronizedAuthorityAsync(
                link, liveDestination, financialYear, capturedMarker, cancellationToken)
                .ConfigureAwait(false);
            recheckIndex = before.Vouchers;
        }
        else
        {
            recheckIndex = await tally.ExportVoucherIndexFreshAsync(
                destination, from, to, cancellationToken).ConfigureAwait(false);
        }
        var previousIds = before.Vouchers
            .ToDictionary(v => v.MasterId.Trim(), v => v.AlterId, IgnoreCase);
        var currentIds = recheckIndex.GroupBy(v => v.MasterId.Trim(), IgnoreCase)
            .ToDictionary(group => group.Key, group => group.Single().AlterId, IgnoreCase);
        if (previousIds.Count != currentIds.Count ||
            previousIds.Any(kv => !currentIds.TryGetValue(kv.Key, out var newAlterId) ||
                                  !IgnoreCase.Equals(kv.Value, newAlterId)))
            throw new InvalidOperationException(
                "Live Tally Voucher MASTERID/ALTERID changed during consolidated master-first verification. " +
                "No CREATE or ALTER is safe; refresh the normal authority.");
        // Only truly unbound original sources can be CREATE candidates.
        // An altered source already owns a durable destination MASTERID.
        var createCandidates = voucherTypeOnlyAlter
            ? Array.Empty<ExpectedVoucher>()
            : source.Where(v =>
                !skipped.Contains(v.SourceAnchor) &&
                !alterTargets.ContainsKey(v.SourceAnchor)).ToArray();
        RequireNoCollision(recheckIndex, createCandidates);
        foreach (var planned in createCandidates)
        {
            var concurrent = recheckIndex.FirstOrDefault(live =>
                IgnoreCase.Equals(live.VoucherType, planned.Type) &&
                planned.Reference.Length > 0 &&
                IgnoreCase.Equals(live.Reference, planned.Reference) &&
                !HasDifferentPreparedBranchScopes(live.VoucherNumber, planned.Number) &&
                !IsAlreadyOwnedByOtherVerifiedBranch(live, planned));
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
            if (skipped.Contains(expected.SourceAnchor) ||
                (voucherTypeOnlyAlter &&
                 !alterTargets.ContainsKey(expected.SourceAnchor)))
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
                    !PreviousConsolidatedVersionMatches(sameId, liveJustBefore.Full, previous) ||
                    (voucherTypeOnlyAlter &&
                     (!IgnoreCase.Equals(sameId.VoucherType, originalLive.VoucherType) ||
                      !MatchesTypeOnlyPreparedVoucher(
                          expected.Xml, sameId, liveJustBefore.Full))))
                    throw new InvalidOperationException(
                        "Bound consolidated voucher " + expected.SourceAnchor +
                        " changed before ALTER: the existing Previous->Desired " +
                        "MASTERID/ALTERID/DRCR/type-only gate failed. No write sent.");

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
                if (alterResponse.Altered == 1)
                {
                    // An ALTERED response is destination acceptance even if
                    // exact readback is delayed. It is NOT another CREATE and
                    // must be counted separately in the original report ZIP.
                    try { tallyAcknowledgedAlter?.Invoke(); }
                    catch { /* observer cannot affect the accounting POST */ }
                }
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
                if (voucherTypeOnlyAlter &&
                    !IgnoreCase.Equals(confirmedAlter.VoucherNumber, sameId.VoucherNumber))
                    throw new InvalidOperationException(
                        "Tally changed the voucher number while altering its type " +
                        "at MASTERID " + sameId.MasterId +
                        ". AcceptedAwaitingVerification prevents another ALTER.");
                _ = RequireVerifiedLiveConsolidatedVersion(confirmedAlter, afterAlter.Full);
                // Identical to the individual Tally post-write readback path:
                // persist BOTH native index and complete signed Voucher XML.
                // The normal coordinator alone advances the FY ALTERID marker
                // after its own synchronized baseline/delta succeeds.
                tallyRepository.ReplaceVoucherIndexRange(
                    link.CompanyKey, financialYear, expected.Date, expected.Date, afterAlter.Index);
                tallyRepository.UpsertVoucherXmlAuthority(
                    link.CompanyKey, financialYear, afterAlter.Full);
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
            var confirmedVersion = RequireVerifiedLiveConsolidatedVersion(confirmed, after.Full);
            // CREATE readback uses the same original index/full-XML cache as
            // individual import. Do not advance the normal global marker here.
            tallyRepository.ReplaceVoucherIndexRange(
                link.CompanyKey, financialYear, expected.Date, expected.Date, after.Index);
            tallyRepository.UpsertVoucherXmlAuthority(
                link.CompanyKey, financialYear, after.Full);

            tallyRepository.ConfirmSourceBindingVersion(
                link.ClientId, financialYear, link.CompanyKey, SourceEntityType,
                expected.SourceAnchor, expected.SourceAnchor,
                confirmed.MasterId, confirmed.Guid,
                confirmedVersion.Hash, confirmedVersion.Version,
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
            voucherTypeOnlyAlter
                ? "Voucher-type-only: verified bound MASTERID ALTER " +
                  altered.ToString(CultureInfo.InvariantCulture) +
                  ", unchanged/unbound vouchers left untouched " +
                  skipped.Count.ToString(CultureInfo.InvariantCulture) +
                  ". No Voucher CREATE/DELETE or ledger/GST/DRCR recalculation."
                : "Consolidated import used existing AAH source bindings: verified CREATE " +
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

    /// <summary>
    /// One ORIGINAL consolidated identity fingerprint shared by the import
    /// writer and the read-only Tally panel. The UI can detect when a previously
    /// verified voucher now has a changed Ledger/DRCR and requires previous->
    /// desired ALTER, instead of showing the older destination as up-to-date.
    /// No Tally mutation, tax calculation or new accounting model.
    /// </summary>
    /// <summary>
    /// Payment/Receipt/Contra is a mutable Tally treatment, NOT a permanent
    /// bank transaction identity. The branch's original BANK-row number and
    /// branch scope are stable when the bank mapping/treatment changes;
    /// the existing source binding must ALTER that same destination MASTERID.
    /// Normal Sales/Purchase/Journal anchors keep their previous format.
    /// </summary>
    /// <summary>
    /// Resolve the immutable branch SOURCE type, not the mutable linked
    /// destination Tally voucher type. This preserves the old binding from
    /// the original import when switching Sales -> Sales - Karnataka and
    /// back, including the original BANK-row direction-independent anchor.
    /// </summary>
    public static string BuildConsolidatedSourceAnchor(
        string voucherType, string preparedNumber,
        IReadOnlyDictionary<string, string> destinationTypeBaseMap)
    {
        ArgumentNullException.ThrowIfNull(destinationTypeBaseMap);
        var originalType = destinationTypeBaseMap.TryGetValue(
            (voucherType ?? string.Empty).Trim(), out var baseType) &&
            !string.IsNullOrWhiteSpace(baseType)
            ? baseType.Trim() : voucherType;
        return BuildConsolidatedSourceAnchor(originalType, preparedNumber);
    }

    public static string BuildConsolidatedSourceAnchor(string voucherType, string preparedNumber)
    {
        var number = (preparedNumber ?? string.Empty).Trim();
        var type = (voucherType ?? string.Empty).Trim();
        if (number.Length == 0 || type.Length == 0)
            throw new InvalidOperationException("Consolidated source identity is incomplete.");
        var bankOrigin = number.StartsWith("BANK-", StringComparison.OrdinalIgnoreCase) ||
                         number.Contains("-BANK-", StringComparison.OrdinalIgnoreCase);
        return bankOrigin
            ? "CONSOLIDATED|BANK|" + number
            : "CONSOLIDATED|" + type + "|" + number;
    }

    public static (string ContentHash, string SerializedPostings)
        ComputePreparedContentIdentity(XElement voucher)
    {
        ArgumentNullException.ThrowIfNull(voucher);
        if (!DateTime.TryParseExact(Value(voucher, "DATE"), "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InvalidOperationException("Prepared consolidated source date is invalid.");
        var type = Value(voucher, "VOUCHERTYPENAME");
        if (type.Length == 0)
            throw new InvalidOperationException("Prepared consolidated source type is missing.");
        var entries = ReadPostings(voucher);
        if (entries.Count == 0)
            throw new InvalidOperationException("Prepared consolidated source has no signed DR/CR.");
        var exactPostings = string.Join("|", entries
            .OrderBy(e => e.Name, IgnoreCase).ThenBy(e => e.Amount)
            .Select(e => e.Name + ":" +
                e.Amount.ToString("0.00", CultureInfo.InvariantCulture)));
        var reference = Value(voucher, "REFERENCE");
        var hash = TallyVoucherFingerprint.BuildContentHash(
            date, type, reference, Value(voucher, "PARTYLEDGERNAME"),
            entries.Where(e => e.Amount < 0).Sum(e => Math.Abs(e.Amount)),
            Value(voucher, "NARRATION") + "|" + exactPostings);
        return (hash, exactPostings);
    }

    // Only SOURCE prepared XML uses ReadPostings below. Live and synchronized
    // destination XML MUST use the ORIGINAL TallyHttpService native parser,
    // exactly like the normal individual-client Tally sync. This accepts
    // ERP9/TallyPrime ledger-entry wrapper variations, ignores non-posting
    // zero lines and preserves every signed DR/CR amount.
    internal static IReadOnlyList<(string Name, decimal Amount)> ReadNormalTallySignedPostings(
        XElement voucher) =>
        TallyHttpService.ReadVoucherLedgerPostings(voucher)
            .Select(post => (post.LedgerName, post.SignedAmount)).ToArray();

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
    internal static string ExplainPreviousFullVoucherMismatch(
        TallyVoucherIndexRecord live,
        IReadOnlyList<TallyVoucherXmlSnapshot> fullAuthority,
        TallySourceBindingVersionState saved)
    {
        var native = ReadVerifiedLiveConsolidatedVersion(live, fullAuthority);
        var savedAltMatches = long.TryParse(live.AlterId.Trim(),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var liveAlt) &&
            saved.LastVerifiedAlterId > 0 &&
            liveAlt == saved.LastVerifiedAlterId;
        var savedHashMatchesNative = native is not null &&
            IgnoreCase.Equals(saved.VerifiedContentHash, native.Value.Hash);
        var savedVersionHasSourceFingerprint =
            saved.VerifiedVersion.EndsWith(saved.VerifiedContentHash,
                StringComparison.OrdinalIgnoreCase);
        return "native full Voucher DR/CR readback: " +
               (native is null ? "INCOMPLETE/DIFFERENT" : "COMPLETE") +
               ", binding GUID matches: " +
               (saved.Guid.Length == 0 ||
                IgnoreCase.Equals(saved.Guid, live.Guid)) +
               ", last verified ALTERID matches: " + savedAltMatches +
               ", saved previous hash equals native full readback: " +
               savedHashMatchesNative +
               ", saved serialized previous version contains saved fingerprint: " +
               savedVersionHasSourceFingerprint +
               ", previous-version proof: FAILED";
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
            (previous.Guid.Length > 0 &&
             !IgnoreCase.Equals(previous.Guid, live.Guid)) ||
            previous.VerifiedContentHash.Length == 0 ||
            !long.TryParse(live.AlterId, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var currentAlterId) ||
            currentAlterId <= 0 ||
            // Preserve the normal AAH Previous->Desired rule: a recorded
            // ALTERID must be identical to live. Legacy verified bindings
            // without an ALTERID are recoverable ONLY from the exact old
            // signed-content fingerprint below, not from the desired voucher.
            (previous.LastVerifiedAlterId > 0 &&
             currentAlterId != previous.LastVerifiedAlterId) ||
            live.VoucherDate is not DateTime originalDate)
            return false;
        var verified = ReadVerifiedLiveConsolidatedVersion(live, fullAuthority);
        if (verified is null) return false;
        var (verifiedHash, verifiedVersion) = verified.Value;
        // The NORMAL AAH exporter reconciles legacy stored-version
        // representation differences against the unchanged live MASTERID,
        // verified ALTERID and the real previous voucher content. Reuse that
        // authority here: verifiedHash covers the old date, type, reference,
        // party, narration AND every signed DR/CR ledger line. A serialized
        // PreviousVersion difference alone is not an accounting change.
        // A missing legacy ALTERID is never guessed: currentAlterId must be
        // positive and the previously SAVED content hash must match the full
        // live previous version before any bound ALTER may be prepared.
        if (IgnoreCase.Equals(previous.VerifiedContentHash, verifiedHash))
            return string.Equals(previous.VerifiedVersion, verifiedVersion,
                       StringComparison.Ordinal) ||
                   previous.VerifiedVersion.Length == 0 ||
                   previous.VerifiedVersion.StartsWith("CONSOLIDATED|",
                       StringComparison.Ordinal);

        // The original AAH verified *request* fingerprint may differ from a
        // fingerprint reconstructed from Tally's complete persisted readback.
        // This must not prevent a legitimate new ledger allocation or voucher
        // type from using the original bound ALTER route. Match the already
        // saved previous request to its own durable serialized version, and
        // require the PREVIOUS positive verified ALTERID to equal fresh Tally.
        // An external Tally ALTERID change, unknown legacy identity or absent
        // verified history must never become an unconditional rebind or CREATE.
        return previous.LastVerifiedAlterId > 0 &&
               previous.LastVerifiedAlterId == currentAlterId &&
               SavedConsolidatedVersionProvesOwnFingerprint(
                   previous.VerifiedVersion, previous.VerifiedContentHash,
                   originalDate, live.VoucherType, live.Reference, verifiedVersion);
    }

    internal static bool SavedConsolidatedVersionProvesOwnFingerprint(
        string savedVersion, string savedHash, DateTime date,
        string previousType, string previousReference, string readbackVersion)
    {
        if (savedHash.Trim().Length != 64 ||
            !savedHash.All(Uri.IsHexDigit) ||
            !savedVersion.StartsWith("CONSOLIDATED|", StringComparison.Ordinal))
            return false;
        var parts = savedVersion.Split('|');
        if (parts.Length < 3 ||
            !parts[^1].Equals(savedHash, StringComparison.OrdinalIgnoreCase))
            return false;
        // Legacy versions were stamped by earlier verified readbacks before
        // the full signed-posting version serializer was introduced.
        if (parts.Length == 3)
            return parts[1] is "LEGACY" or "OLD-SERIALIZATION";
        return parts.Length >= 6 &&
               parts[1].Equals(date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
               parts[2].Equals(previousType, StringComparison.OrdinalIgnoreCase) &&
               parts[3].Equals(previousReference, StringComparison.OrdinalIgnoreCase) &&
               SavedConsolidatedSignedAmountsMatchReadback(savedVersion, readbackVersion);
    }

    // Source/ledger MASTER renaming can change the display name of an
    // existing voucher's posting WITHOUT changing that voucher's ALTERID.
    // For older request-fingerprint bindings, require the saved PREVIOUS
    // positive/negative posting amounts and occurrence count to match the
    // actual signed Tally readback, even if ledger display names changed.
    // Never use desired postings as evidence of the previous saved state.
    internal static bool SavedConsolidatedSignedAmountsMatchReadback(
        string savedVersion, string readbackVersion)
    {
        static decimal[]? Amounts(string version)
        {
            var parts = version.Split('|');
            if (parts.Length < 6) return null;
            var values = new List<decimal>();
            for (var index = 4; index < parts.Length - 1; index++)
            {
                var posting = parts[index];
                var separator = posting.LastIndexOf(':');
                if (separator < 1 || separator >= posting.Length - 1 ||
                    !decimal.TryParse(posting[(separator + 1)..],
                        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var amount))
                    return null;
                values.Add(amount);
            }
            return values.Count == 0 ? null : values.OrderBy(x => x).ToArray();
        }
        var before = Amounts(savedVersion);
        var live = Amounts(readbackVersion);
        return before is not null && live is not null && before.SequenceEqual(live);
    }

    // This one full live Tally voucher reconstruction supplies BOTH the old
    // PREVIOUS->DESIRED check and the version persisted after a verified
    // CREATE/ALTER readback. It never recalculates the prepared DR/CR.
    internal static (string Hash, string Version)? ReadVerifiedLiveConsolidatedVersion(
        TallyVoucherIndexRecord live,
        IReadOnlyList<TallyVoucherXmlSnapshot> fullAuthority)
    {
        if (live.VoucherDate is not DateTime date) return null;
        var full = fullAuthority.Where(item =>
            IgnoreCase.Equals(item.MasterId, live.MasterId)).ToArray();
        if (full.Length != 1) return null;
        var savedFull = full[0];
        // The original normal Tally Link synchronization supplies BOTH the
        // normalized Voucher index and this full native XML. Index scalars
        // were parsed by TallyHttpService.ParseVoucherElements, which supports
        // ERP9/Prime PARTYNAME, BASICNARRATION and other field aliases.
        // Do not read a SECOND incompatible set of scalar fields here.
        if ((savedFull.AlterId.Trim().Length > 0 &&
             !IgnoreCase.Equals(savedFull.AlterId.Trim(), live.AlterId.Trim())) ||
            (savedFull.Guid.Trim().Length > 0 &&
             live.Guid.Trim().Length > 0 &&
             !IgnoreCase.Equals(savedFull.Guid.Trim(), live.Guid.Trim())))
            return null;
        var voucher = TallyConsolidatedXmlTextSafety.ParseDocument(
            savedFull.OriginalXml, "verified full destination voucher readback")
            .Root?.DescendantsAndSelf().FirstOrDefault(x => Is(x, "VOUCHER"));
        if (voucher is null) return null;
        var xmlMasterId = Value(voucher, "MASTERID");
        var xmlAlterId = Value(voucher, "ALTERID");
        var xmlGuid = Value(voucher, "GUID");
        if ((xmlMasterId.Length > 0 &&
             !IgnoreCase.Equals(xmlMasterId, live.MasterId)) ||
            (xmlAlterId.Length > 0 &&
             !IgnoreCase.Equals(xmlAlterId, live.AlterId)) ||
            (xmlGuid.Length > 0 && live.Guid.Length > 0 &&
             !IgnoreCase.Equals(xmlGuid, live.Guid)))
            return null;
        // Exactly the same native Ledger parser as individual Tally export;
        // the complete signed postings must agree with the synchronized
        // index before this is accepted as the old previous version.
        var postings = ReadNormalTallySignedPostings(voucher);
        if (postings.Count == 0 || live.LedgerPostings.Count != postings.Count ||
            !SamePostings(postings, live.LedgerPostings.Select(p =>
                (p.LedgerName, p.SignedAmount)).ToArray()))
            return null;
        // One normalized source of truth from the ORIGINAL individual Tally
        // Link reader. Raw full XML may expose PARTYNAME rather than
        // PARTYLEDGERNAME or BASICNARRATION rather than NARRATION; direct
        // Value(...) checks falsely rejected unchanged bound Payment vouchers.
        var signed = string.Join("|", postings
            .OrderBy(entry => entry.Name, IgnoreCase).ThenBy(entry => entry.Amount)
            .Select(entry => entry.Name + ":" +
                entry.Amount.ToString("0.00", CultureInfo.InvariantCulture)));
        var hash = TallyVoucherFingerprint.BuildContentHash(
            date.Date, live.VoucherType, live.Reference, live.PartyLedger,
            postings.Where(entry => entry.Amount < 0).Sum(entry => Math.Abs(entry.Amount)),
            live.Narration + "|" + signed);
        return (hash, "CONSOLIDATED|" +
            date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) +
            "|" + live.VoucherType + "|" + live.Reference +
            "|" + signed + "|" + hash);
    }

    private static (string Hash, string Version) RequireVerifiedLiveConsolidatedVersion(
        TallyVoucherIndexRecord live, IReadOnlyList<TallyVoucherXmlSnapshot> full)
    {
        return ReadVerifiedLiveConsolidatedVersion(live, full) ??
            throw new InvalidOperationException(
                "The accepted destination voucher has no uniquely verified full signed DR/CR readback; previous version was not saved.");
    }

    // Exact voucher matching reuses the established ordinary Tally
    // classifier, then independently confirms the full Tally XML posting
    // lines. Branch amounts/taxes are NEVER recalculated here.
    // The original Bank voucher importer compares same-date BANK ledger
    // direction/amount and strict narration/reference before deciding CREATE.
    // Tally may auto-assign a number AND leave Reference blank: matching only
    // planned.VOUCHERNUMBER or non-empty reference would miss that existing
    // voucher and create a duplicate. This is a conservative read-only
    // collision candidate, NOT an authority to ALTER or DELETE. Exact full
    // XML signed DR/CR is still required to adopt an unbound voucher.
    /// <summary>
    /// A narrowly-scoped FIRST-UNBOUND B2C name correction, NOT a generic
    /// reference/amount duplicate matcher. An old Tally name of
    /// "B2C Sales - 24 - Karnataka" can be replaced by the prepared source
    /// "B2C Sales - Karnataka" only after every other posting and the unique
    /// full live Tally object agree. Unrelated voucher numbers and cross-branch
    /// shared B2C references are never evidence of ownership.
    /// </summary>
    internal static bool TryReadFirstUnboundB2cLedgerRename(
        XElement desired, TallyVoucherIndexRecord live,
        IReadOnlyList<TallyVoucherXmlSnapshot> fullAuthority,
        out string previousHash, out string previousVersion)
    {
        previousHash = "";
        previousVersion = "";
        if (desired is null || live is null || fullAuthority is null ||
            !IgnoreCase.Equals(Value(desired, "VOUCHERTYPENAME"), "Sales") ||
            !IgnoreCase.Equals(live.VoucherType, "Sales") ||
            !IgnoreCase.Equals(Field(desired, "ACTION"), "Create"))
            return false;
        var preparedNumber = Value(desired, "VOUCHERNUMBER");
        if (!preparedNumber.StartsWith("B-", StringComparison.OrdinalIgnoreCase) ||
            preparedNumber.IndexOf('-', 2) != 26 ||
            !preparedNumber.Substring(2, 24).All(Uri.IsHexDigit))
            return false;
        var reference = Value(desired, "REFERENCE");
        var narration = Value(desired, "NARRATION");
        if (!reference.StartsWith("B2CS-", StringComparison.OrdinalIgnoreCase) ||
            narration.Length == 0 || live.Reference.Length == 0 ||
            !IgnoreCase.Equals(reference, live.Reference) ||
            !IgnoreCase.Equals(narration, live.Narration) ||
            !DateTime.TryParseExact(Value(desired, "DATE"), "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
            live.VoucherDate?.Date != date.Date ||
            !long.TryParse(live.MasterId, NumberStyles.None,
                CultureInfo.InvariantCulture, out var masterId) || masterId <= 0 ||
            !long.TryParse(live.AlterId, NumberStyles.None,
                CultureInfo.InvariantCulture, out var alterId) || alterId <= 0 ||
            live.Guid.Length == 0)
            return false;
        var full = fullAuthority.Where(x =>
            IgnoreCase.Equals(x.MasterId, live.MasterId)).ToArray();
        if (full.Length != 1 || !IgnoreCase.Equals(full[0].Guid, live.Guid))
            return false;
        var oldVoucher = TallyConsolidatedXmlTextSafety.ParseDocument(
            full[0].OriginalXml, "unbound B2C previous live Tally XML")
            .Root?.DescendantsAndSelf().FirstOrDefault(x => Is(x, "VOUCHER"));
        if (oldVoucher is null ||
            !IgnoreCase.Equals(Value(oldVoucher, "VOUCHERTYPENAME"), live.VoucherType) ||
            !IgnoreCase.Equals(Value(oldVoucher, "REFERENCE"), reference) ||
            !IgnoreCase.Equals(Value(oldVoucher, "NARRATION"), narration) ||
            !DateTime.TryParseExact(Value(oldVoucher, "DATE"), "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var oldDate) ||
            oldDate.Date != date.Date ||
            !IgnoreCase.Equals(Value(oldVoucher, "PARTYLEDGERNAME"), live.PartyLedger))
            return false;
        var oldPostings = ReadNormalTallySignedPostings(oldVoucher);
        var desiredPostings = ReadPostings(desired);
        if (oldPostings.Count < 3 || desiredPostings.Count != oldPostings.Count ||
            !SamePostings(oldPostings, live.LedgerPostings
                .Select(p => (p.LedgerName, p.SignedAmount)).ToArray()))
            return false;
        var remaining = desiredPostings.ToList();
        var unmatchedOld = new List<(string Name, decimal Amount)>();
        foreach (var posting in oldPostings)
        {
            var index = remaining.FindIndex(x =>
                IgnoreCase.Equals(x.Name, posting.Name) && x.Amount == posting.Amount);
            if (index >= 0) remaining.RemoveAt(index);
            else unmatchedOld.Add(posting);
        }
        if (remaining.Count != 1 || unmatchedOld.Count != 1 ||
            remaining[0].Amount >= 0 ||
            unmatchedOld[0].Amount != remaining[0].Amount ||
            !IgnoreCase.Equals(Value(oldVoucher, "PARTYLEDGERNAME"), unmatchedOld[0].Name) ||
            !IgnoreCase.Equals(Value(desired, "PARTYLEDGERNAME"), remaining[0].Name))
            return false;
        const string b2c = "B2C Sales - ";
        var oldName = unmatchedOld[0].Name;
        var newName = remaining[0].Name;
        if (!oldName.StartsWith(b2c, StringComparison.OrdinalIgnoreCase) ||
            !newName.StartsWith(b2c, StringComparison.OrdinalIgnoreCase))
            return false;
        var oldSuffix = oldName[b2c.Length..];
        var newSuffix = newName[b2c.Length..];
        // Only remove the legacy two-digit B2C place code, not arbitrary
        // ledger changes. Preserve the exact branch/state suffix.
        if (oldSuffix.Length <= 6 ||
            !char.IsDigit(oldSuffix[0]) || !char.IsDigit(oldSuffix[1]) ||
            !oldSuffix.AsSpan(2).StartsWith(" - ", StringComparison.Ordinal) ||
            newSuffix.Length == 0 ||
            !IgnoreCase.Equals(oldSuffix[5..], newSuffix))
            return false;
        var signed = string.Join("|", oldPostings
            .OrderBy(x => x.Name, IgnoreCase).ThenBy(x => x.Amount)
            .Select(x => x.Name + ":" +
                x.Amount.ToString("0.00", CultureInfo.InvariantCulture)));
        previousHash = TallyVoucherFingerprint.BuildContentHash(
            oldDate, live.VoucherType, reference,
            Value(oldVoucher, "PARTYLEDGERNAME"),
            oldPostings.Where(x => x.Amount < 0).Sum(x => Math.Abs(x.Amount)),
            narration + "|" + signed);
        previousVersion = "CONSOLIDATED|" +
            oldDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture) +
            "|" + live.VoucherType + "|" + reference +
            "|" + signed + "|" + previousHash;
        return true;
    }

    private static bool PotentialExistingUnboundBankVoucher(
        TallyVoucherIndexRecord live, ExpectedVoucher planned)
    {
        if (planned.Type is not ("Payment" or "Receipt" or "Contra") ||
            !IgnoreCase.Equals(live.VoucherType, planned.Type) ||
            live.VoucherDate?.Date != planned.Date.Date ||
            !IgnoreCase.Equals((live.Reference ?? "").Trim(), planned.Reference.Trim()) ||
            !IgnoreCase.Equals((live.Narration ?? "").Trim(), Value(planned.Xml, "NARRATION")))
            return false;
        var actual = live.LedgerPostings
            .Select(x => (Name: x.LedgerName, Amount: x.SignedAmount)).ToArray();
        return planned.Postings.Any(expected =>
            actual.Any(p => IgnoreCase.Equals(p.Name, expected.Name) &&
                            p.Amount == expected.Amount));
    }

    // Type-only means no change to an original posting, date, reference,
    // narration or party. Use the existing normal Tally strict comparator,
    // then independently read the complete signed native DR/CR from Tally.
    internal static bool MatchesTypeOnlyPreparedVoucher(
        XElement preparedVoucher, TallyVoucherIndexRecord live,
        IReadOnlyList<TallyVoucherXmlSnapshot> fullAuthority)
    {
        if (!DateTime.TryParseExact(Value(preparedVoucher, "DATE"),
                "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None,
                out var date))
            return false;
        IReadOnlyList<(string Name, decimal Amount)> postings;
        try { postings = ReadPostings(preparedVoucher); }
        catch (InvalidOperationException) { return false; }
        if (!TallyAccountingVoucherExportService.VoucherStrictlyMatchesConsolidatedPrepared(
                live, date, live.VoucherType, Value(preparedVoucher, "REFERENCE"),
                Value(preparedVoucher, "NARRATION"), postings))
            return false;
        var originals = fullAuthority.Where(x =>
            IgnoreCase.Equals(x.MasterId, live.MasterId)).ToArray();
        if (originals.Length != 1) return false;
        var original = TallyConsolidatedXmlTextSafety.ParseDocument(
            originals[0].OriginalXml, "native type-only proof")
            .Root?.DescendantsAndSelf().FirstOrDefault(x => Is(x, "VOUCHER"));
        if (original is null ||
            !SamePostings(postings, ReadNormalTallySignedPostings(original)))
            return false;
        var desiredParty = Value(preparedVoucher, "PARTYLEDGERNAME");
        if (desiredParty.Length > 0 &&
            !IgnoreCase.Equals(desiredParty, live.PartyLedger))
            return false;
        foreach (var field in new[] {
                     "PARTYGSTIN", "PLACEOFSUPPLY", "GSTREGISTRATIONTYPE",
                     "STATENAME", "ISGSTOVERRIDDEN", "GSTOVRDNNATURE" })
        {
            var desired = Value(preparedVoucher, field);
            var prior = Value(original, field);
            if (desired.Length > 0 && prior.Length > 0 &&
                !IgnoreCase.Equals(desired, prior))
                return false;
        }
        return true;
    }

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
            SamePostings(expected.Postings, ReadNormalTallySignedPostings(voucher));
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
        TallyCompanyInfo destination, CancellationToken token,
        IReadOnlyCollection<string>? requiredBranchVoucherTypes = null,
        bool allowMissingChildTypesInMasterFirstManualXml = false,
        bool allowExistingNoneNumbering = false)
    {
        // This is the ORIGINAL consolidated Master reader, not a second
        // voucher-type importer. Verify any selected branch voucher types
        // against the same freshly read original Tally Masters BEFORE a
        // manual XML is materialized or an existing voucher can be ALTERed.
        if (requiredBranchVoucherTypes is { Count: > 0 })
            tally.RequireFreshErp9MasterAuthorityForExplicitExport();
        var masterXml = await tally.ExportMastersAsync(destination, token).ConfigureAwait(false);
        if (requiredBranchVoucherTypes is { Count: > 0 })
        {
            var liveTypeNames = TallyConsolidatedXmlTextSafety.ParseDocument(
                    masterXml, "live Voucher Type numbering before consolidated import")
                .Descendants().Where(x => Is(x, "VOUCHERTYPE"))
                .Select(MasterName).ToHashSet(IgnoreCase);
            foreach (var name in requiredBranchVoucherTypes)
            {
                if (allowMissingChildTypesInMasterFirstManualXml &&
                    !liveTypeNames.Contains(name))
                    continue; // Missing manual MASTER is in the identity-free
                              // staged Masters XML, not already in Tally.
                // Existing v632 Voucher Types use None/default numbering.
                // Do not rewrite the user's existing numbering setup.
                if (!allowExistingNoneNumbering)
                    TallyBranchConsolidationImportService.RequireManualVoucherTypeNumbering(
                        masterXml, name);
            }
        }
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
    // Reuse existing live Tally master readback, not a second Bank/Cash
    // importer. This is an additional pre-post safety check, not permission to
    // alter/create a Cash Ledger under any different parent.
    internal static void RequireExistingDefaultCashForBankVouchers(
        IReadOnlyCollection<XElement> vouchers,
        IReadOnlyCollection<XElement> stagedMasters,
        IReadOnlyDictionary<string, XElement> liveMasters)
    {
        var needsDefaultCash = vouchers.Any(v => v.Descendants().Any(x =>
            Is(x, "LEDGERNAME") && x.Value.Trim().Equals(
                "Cash", StringComparison.OrdinalIgnoreCase)));
        if (!needsDefaultCash) return;
        if (stagedMasters.Any(x => Is(x, "LEDGER") &&
            MasterName(x).Equals("Cash", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "Consolidated XML wrongly attempts to CREATE/reparent Tally's " +
                "existing Cash ledger. Regenerate the preview using the " +
                "original default Cash/Cash-in-Hand flow before export.");
        if (!liveMasters.TryGetValue(MasterKey("LEDGER", "Cash"), out var cash) ||
            !Value(cash, "PARENT").Equals("Cash-in-Hand",
                StringComparison.OrdinalIgnoreCase) ||
            !long.TryParse(Value(cash, "MASTERID"),
                NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var masterId) || masterId <= 0)
            throw new InvalidOperationException(
                "Bank Contra requires the original linked Tally Cash ledger " +
                "under Cash-in-Hand with a verified destination MASTERID. " +
                "It is not a Cash Entry module voucher; no Cash ledger " +
                "CREATE/ALTER or voucher POST is permitted.");
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

public sealed record TallyConsolidatedManualImportXmlResult(
    string MastersXml, string VouchersXml,
    int MissingMasters, int ReadyCreates, int ReadyAlters,
    int UnchangedVouchers, string Audit);
