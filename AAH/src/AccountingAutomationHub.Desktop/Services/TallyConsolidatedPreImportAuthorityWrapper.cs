using System.Globalization;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Tally;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Consolidated-only adapter around the existing normal linked-Tally authority.
/// Never changes the individual-client coordinator, Tally HTTP protocol or
/// ordinary voucher importer. An unreliable $$LastAlterID pulse is NOT a zero
/// voucher company and is never accepted as permission to CREATE.
/// </summary>
public sealed class TallyConsolidatedPreImportAuthorityWrapper(
    TallyLiveConnectionCoordinator normalCoordinator,
    TallyHttpService existingTally,
    SqliteTallyRepository existingRepository)
{
    public async Task VerifyAsync(
        ClientProfile client, TallyClientLink confirmedLink,
        TallyCompanyInfo destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(confirmedLink);
        ArgumentNullException.ThrowIfNull(destination);
        if (client.Id != confirmedLink.ClientId ||
            (confirmedLink.FinancialYear.Length > 0 &&
             !confirmedLink.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase)) ||
            !destination.CompanyKey.Equals(confirmedLink.CompanyKey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Consolidated Client/FY and saved destination CompanyKey differ. No voucher was posted.");

        RequireSameSavedLink(client, confirmedLink);
        if (existingTally.IsImportInProgress || existingTally.IsExportInProgress)
            throw new InvalidOperationException(
                "Tally import/export is in progress; consolidated pre-import was not started.");

        // Reuse the ORIGINAL coordinator's local Client/FY MASTERID, full
        // Voucher XML and ALTERID delta authority; do not request a new
        // full-company snapshot. Normal Refresh may silently defer a pulse,
        // so its return alone is never proof of a live checkpoint.
        // The Live Tally Link owns the synchronized Company/FY authority.
        // Reuse its ordinary delta/checkpoint path rather than imposing a
        // NEW full-year baseline timestamp on every consolidated import.
        await normalCoordinator.RefreshNowAsync(
            forceLinkedSynchronization: true,
            cancellationToken: cancellationToken,
            forceFullBaseline: false);
        RequireSameSavedLink(client, confirmedLink);
        var synchronized = existingRepository.GetBankSyncState(
            client.Id, client.FinancialYear, confirmedLink.CompanyKey);
        if (synchronized is null || synchronized.LastFullSyncUtc == DateTime.MinValue ||
            synchronized.LastFyVoucherCount < 0 || synchronized.LastAlterId <= 0)
            throw new InvalidOperationException(
                "Live Tally Link has not established its original complete FY authority. " +
                "No consolidated voucher can be imported without it.");

        // ERP9's tiny ALTERID pulse can remain unchanged even after a voucher
        // ALTER. The ORIGINAL individual-client native index is the independent
        // proof. If its saved index is old, reconcile from the ORIGINAL native
        // full-Voucher reader under the Live Link's existing operation gate.
        // This repairs already-stale CL019 voucher 35, not only future writes.
        var range = GetFinancialYearRange(client.FinancialYear);
        var savedIndex = existingRepository.GetVoucherIndexRange(
            confirmedLink.CompanyKey, client.FinancialYear, range.From, range.To);
        var nativeIndex = await existingTally.ExportVoucherIndexFreshAsync(
            destination, range.From, range.To, cancellationToken);
        var requiresNativeRefresh = savedIndex.Count != synchronized.LastFyVoucherCount ||
                                    !existingRepository.HasCompleteVoucherXmlAuthority(
                                        confirmedLink.CompanyKey, client.FinancialYear, savedIndex.Count);
        if (!requiresNativeRefresh)
        {
            try { RequireSameVoucherIndex(savedIndex, nativeIndex, synchronized.LastAlterId); }
            catch (InvalidOperationException) { requiresNativeRefresh = true; }
        }
        if (requiresNativeRefresh)
        {
            await normalCoordinator.RunWithLinkedTallyOperationGateAsync(
                token => RefreshVoucherAuthorityFromOriginalLiveLinkAsync(
                    client, confirmedLink, destination, range, token), cancellationToken);
        }
        RequireSameSavedLink(client, confirmedLink);
        // ImportAsync repeats this unchanged native marker and full-DRCR
        // verification before each real voucher CREATE or ALTER.
        await ReadVerifiedMarkerAsync(
            confirmedLink, destination, client.FinancialYear, cancellationToken);

        // No duplicate live-index read here. The ORIGINAL consolidated
        // importer calls ReadVerifiedMarkerAsync immediately before
        // interpreting any saved Voucher MASTERID and again before posting.
        // Thus two fresh proofs surround the original duplicate/ALTER
        // decision instead of doing a redundant third preflight scan.
    }

    public async Task<TallyVoucherChangeMarker> ReadVerifiedMarkerAsync(
        TallyClientLink link, TallyCompanyInfo destination, string financialYear,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(destination);
        RequireSameCompany(link, destination, financialYear);
        // The original client/company may be switched while Tally is reading
        // an index or a previous voucher. Every authorizing read must remain
        // tied to the same saved destination, not just the first preflight.
        var current = normalCoordinator.GetCurrentClientLink();
        if (current is null || current.ClientId != link.ClientId ||
            !current.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase) ||
            (current.FinancialYear.Length > 0 &&
             !current.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "Consolidated Client/FY or destination link switched during live Voucher verification. No voucher was posted.");

        var saved = existingRepository.GetBankSyncState(
            link.ClientId, financialYear, link.CompanyKey);
        if (saved is null || saved.LastFullSyncUtc == DateTime.MinValue ||
            saved.LastAlterId <= 0 || saved.LastFyVoucherCount < 0 ||
            string.IsNullOrWhiteSpace(saved.LastTallyChangeToken) ||
            !saved.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Normal Tally Client/FY voucher authority has no complete saved checkpoint. " +
                "The consolidated wrapper did not create, alter or relink vouchers.");

        var liveMarker = await existingTally.GetVoucherChangeMarkerAsync(
            destination, cancellationToken);
        if (liveMarker.CompanyName.Length > 0 &&
            !liveMarker.CompanyName.Equals(destination.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The live Tally authority response belongs to another company. No voucher was posted.");

        if (liveMarker.HasReliableAlterId && liveMarker.LastAlterId > 0)
        {
            // A genuine live company-wide ALTERID must match precisely.
            // Never prefer a cached number over a reliable changed marker.
            if (string.Equals(liveMarker.Token, saved.LastTallyChangeToken, StringComparison.Ordinal) &&
                liveMarker.VoucherCount == saved.LastVoucherCount)
                return liveMarker;
            throw new InvalidOperationException(
                "Live Tally returned a reliable but changed authority marker " +
                $"(ALTERID {liveMarker.LastAlterId}; synchronized {saved.LastAlterId}). " +
                "The normal Tally authority must synchronize this change before import.");
        }

        // Tally may return 0/0 and no $$LastAlterID through its tiny TDL
        // report even though the normal live Tally cache displays ALTERID
        // 672 and 645 FY vouchers. That cached 672 is NOT independent live
        // proof. Instead use the EXISTING fresh Voucher-index reader, with
        // no full-Voucher XML or Master snapshot, to establish that every
        // destination MASTERID + ALTERID remains exactly as synchronized.
        var range = GetFinancialYearRange(financialYear);
        var cachedIndex = existingRepository.GetVoucherIndexRange(
            link.CompanyKey, financialYear, range.From, range.To);
        if (cachedIndex.Count != saved.LastFyVoucherCount ||
            cachedIndex.Any(x => string.IsNullOrWhiteSpace(x.MasterId) ||
                                 string.IsNullOrWhiteSpace(x.AlterId)) ||
            cachedIndex.Select(x => x.MasterId.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != cachedIndex.Count ||
            !existingRepository.HasCompleteVoucherXmlAuthority(
                link.CompanyKey, financialYear, cachedIndex.Count))
            throw new InvalidOperationException(
                "Existing synchronized FY MASTERID/full XML authority is incomplete. " +
                "No imported fingerprint or cached ALTERID can authorize a voucher CREATE.");

        var freshIndex = await existingTally.ExportVoucherIndexFreshAsync(
            destination, range.From, range.To, cancellationToken);
        try
        {
            RequireSameVoucherIndex(cachedIndex, freshIndex, saved.LastAlterId);
        }
        catch (InvalidOperationException) when (
            FindNativeProjectionRecheckMonths(cachedIndex, freshIndex, saved.LastAlterId).Count > 0)
        {
            // Individual AAH import reads the ORIGINAL native Voucher object
            // with ExportVoucherAuthorityFreshAsync. Consolidated previously
            // compared that baseline with a separate, thinner TDL Index request.
            // The two projections can serialize Reference/PartyLedger/Amount/
            // Narration differently WITHOUT a Tally ALTERID change.
            //
            // Re-read ONLY months with same-MASTERID/same-ALTERID projection
            // differences through the original individual-voucher native
            // reader. Preserve every original index/full XML gate: the new
            // native read must match ALL saved fields before any CREATE/ALTER.
            var byId = freshIndex.ToDictionary(
                row => row.MasterId.Trim(), StringComparer.OrdinalIgnoreCase);
            foreach (var month in FindNativeProjectionRecheckMonths(
                         cachedIndex, freshIndex, saved.LastAlterId))
            {
                var lastDay = month.AddMonths(1).AddDays(-1);
                if (lastDay > range.To) lastDay = range.To;
                var native = await existingTally.ExportVoucherAuthorityFreshAsync(
                    destination, month, lastDay, cancellationToken);
                var expectedIds = cachedIndex
                    .Where(row => row.VoucherDate is DateTime date &&
                                  date.Year == month.Year && date.Month == month.Month)
                    .Select(row => row.MasterId.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var nativeIds = native.Index.Select(row => row.MasterId.Trim()).ToArray();
                if (nativeIds.Length != expectedIds.Count ||
                    !expectedIds.SetEquals(nativeIds))
                    throw new InvalidOperationException(
                        "Original native Tally Voucher month authority differs from " +
                        "the synchronized MASTERIDs. No consolidated CREATE/ALTER was posted.");
                foreach (var row in native.Index)
                    byId[row.MasterId.Trim()] = row;
            }
            RequireSameVoucherIndex(
                cachedIndex, byId.Values.ToArray(), saved.LastAlterId);
        }

        // Proven equivalent only for this specific native Voucher index.
        // The original consolidated importer repeats this proof at its
        // initial read and before posting, and retains all existing
        // previous-version, full signed DR/CR, duplicate and exact
        // live-before-ALTER readbacks. Do NOT advance the saved checkpoint.
        return new TallyVoucherChangeMarker
        {
            Token = saved.LastTallyChangeToken,
            CompanyName = destination.Name,
            VoucherCount = saved.LastVoucherCount,
            LastAlterId = saved.LastAlterId,
            // Native per-Voucher ALTERIDs have been verified on the live
            // index; the global $$LastAlterID itself was NOT available.
            HasReliableAlterId = true,
            LedgerCount = liveMarker.LedgerCount,
            GroupCount = liveMarker.GroupCount,
            VoucherTypeCount = liveMarker.VoucherTypeCount,
            StockItemCount = liveMarker.StockItemCount,
            UnitCount = liveMarker.UnitCount,
            GodownCount = liveMarker.GodownCount
        };
    }

    private async Task RefreshVoucherAuthorityFromOriginalLiveLinkAsync(
        ClientProfile client, TallyClientLink link, TallyCompanyInfo company,
        (DateTime From, DateTime To) range, CancellationToken cancellationToken)
    {
        RequireSameSavedLink(client, link);
        var before = await existingTally.GetVoucherChangeMarkerAsync(
            company, cancellationToken);
        if (before.CompanyName.Length > 0 &&
            !before.CompanyName.Equals(company.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The native Tally refresh did not identify the linked destination company.");
        var current = existingRepository.GetBankSyncState(
            client.Id, client.FinancialYear, link.CompanyKey)
            ?? throw new InvalidOperationException(
                "The Live Tally Link checkpoint disappeared during native voucher refresh.");
        if (current.LastFullSyncUtc == DateTime.MinValue)
            throw new InvalidOperationException(
                "Initial Live Tally Master/FY baseline has not completed.");

        // Same original individual Tally reader, bounded to each calendar
        // month. NEVER write the index before every required native Voucher
        // object and all signed DR/CR XML have been read successfully.
        var rows = new List<TallyVoucherIndexRecord>();
        var xml = new List<TallyVoucherXmlSnapshot>();
        for (var month = new DateTime(range.From.Year, range.From.Month, 1);
             month <= range.To; month = month.AddMonths(1))
        {
            var from = month < range.From ? range.From : month;
            var to = month.AddMonths(1).AddDays(-1);
            if (to > range.To) to = range.To;
            var slice = await existingTally.ExportVoucherAuthorityFreshAsync(
                company, from, to, cancellationToken);
            rows.AddRange(slice.Index);
            xml.AddRange(slice.Full);
        }

        // ERP9's global marker may be unreliable. A second independent thin
        // native index still must prove that this FY's MASTERID/ALTERID
        // population remained stable while the month slices were acquired.
        var recheck = await existingTally.ExportVoucherIndexFreshAsync(
            company, range.From, range.To, cancellationToken);
        var after = await existingTally.GetVoucherChangeMarkerAsync(
            company, cancellationToken);
        if (after.CompanyName.Length > 0 &&
            !after.CompanyName.Equals(company.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The linked Tally destination changed during native voucher readback.");
        if (before.HasReliableAlterId && after.HasReliableAlterId &&
            (before.LastAlterId != after.LastAlterId ||
             !string.Equals(before.Token, after.Token, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                "Live Tally changed while its FY vouchers were being refreshed. " +
                "No stale or mixed-generation authority was saved.");
        if (rows.Count != recheck.Count ||
            rows.GroupBy(v => v.MasterId.Trim(), StringComparer.OrdinalIgnoreCase)
                .Any(g => g.Key.Length == 0 || g.Count() != 1) ||
            recheck.GroupBy(v => v.MasterId.Trim(), StringComparer.OrdinalIgnoreCase)
                .Any(g => g.Key.Length == 0 || g.Count() != 1))
            throw new InvalidOperationException(
                "Native full-FY Voucher index changed while acquiring native XML.");
        var nativeById = rows.ToDictionary(
            v => v.MasterId.Trim(), StringComparer.OrdinalIgnoreCase);
        if (recheck.Any(v =>
                !nativeById.TryGetValue(v.MasterId.Trim(), out var native) ||
                !string.Equals(v.AlterId.Trim(), native.AlterId.Trim(),
                    StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "Fresh native Voucher MASTERID/ALTERID changed during XML read. " +
                "The original Live Tally authority remains unchanged.");
        RequireSameSavedLink(client, link);
        PersistVerifiedOriginalLiveVoucherAuthority(
            existingRepository, current, link, before, rows, xml);
    }

    // Extracted only to enable a real SQLite replay of CL019's stale row.
    // This saves the SAME normal Live Tally Index and full-XML repository
    // tables as the individual importer, never modifies source vouchers,
    // does not POST any Tally XML and does not invent a full Master baseline.
    internal static void PersistVerifiedOriginalLiveVoucherAuthority(
        SqliteTallyRepository repository, TallyBankSyncState state,
        TallyClientLink link, TallyVoucherChangeMarker marker,
        IReadOnlyList<TallyVoucherIndexRecord> rows,
        IReadOnlyList<TallyVoucherXmlSnapshot> full)
    {
        if (state.ClientId != link.ClientId ||
            !state.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase) ||
            !state.FinancialYear.Equals(link.FinancialYear, StringComparison.OrdinalIgnoreCase) ||
            state.LastFullSyncUtc == DateTime.MinValue)
            throw new InvalidOperationException(
                "Native Tally Voucher refresh is not bound to the original Client/FY/CompanyKey.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var latestAlterId = state.LastAlterId;
        foreach (var row in rows)
        {
            if (!row.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase) ||
                !row.FinancialYear.Equals(link.FinancialYear, StringComparison.OrdinalIgnoreCase) ||
                row.MasterId.Trim().Length == 0 || !ids.Add(row.MasterId.Trim()) ||
                !long.TryParse(row.AlterId.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var alterId) || alterId <= 0)
                throw new InvalidOperationException(
                    "Native live Voucher identity/ALTERID is missing, duplicated, or from the wrong FY/book.");
            latestAlterId = Math.Max(latestAlterId, alterId);
        }
        var fullById = full.GroupBy(
                v => v.MasterId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        if (ids.Count != full.Count ||
            rows.Any(row => !fullById.TryGetValue(row.MasterId.Trim(), out var snapshots) ||
                snapshots.Length != 1 ||
                snapshots[0].OriginalXml.Trim().Length == 0 ||
                (row.Guid.Trim().Length > 0 && snapshots[0].Guid.Trim().Length > 0 &&
                 !string.Equals(snapshots[0].Guid.Trim(), row.Guid.Trim(),
                     StringComparison.OrdinalIgnoreCase)) ||
                !string.Equals(snapshots[0].AlterId.Trim(), row.AlterId.Trim(),
                    StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "Live native Voucher index and complete Voucher XML do not agree. " +
                "The saved synchronized authority was not changed.");

        // No Tally POST: only the original shared index/XML cache changes.
        repository.ReplaceVoucherIndex(link.CompanyKey, link.FinancialYear, rows);
        repository.ReplaceVoucherXmlAuthority(link.CompanyKey, link.FinancialYear, full);
        if (!repository.HasCompleteVoucherXmlAuthority(
                link.CompanyKey, link.FinancialYear, rows.Count))
            throw new InvalidOperationException(
                "Updated Live Tally index/full XML failed database readback.");
        var updatedIndex = repository.GetVoucherIndexRange(
            link.CompanyKey, link.FinancialYear,
            new DateTime(int.Parse(link.FinancialYear[..4], CultureInfo.InvariantCulture), 4, 1),
            new DateTime(int.Parse(link.FinancialYear[..4], CultureInfo.InvariantCulture) + 1, 3, 31));
        var originalById = rows.ToDictionary(
            row => row.MasterId.Trim(), StringComparer.OrdinalIgnoreCase);
        if (updatedIndex.Count != rows.Count ||
            updatedIndex.Any(row =>
                !originalById.TryGetValue(row.MasterId.Trim(), out var original) ||
                !string.Equals(row.AlterId, original.AlterId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(row.Guid, original.Guid, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(row.PartyLedger, original.PartyLedger, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(row.ContentHash, original.ContentHash, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "Updated Live Tally MASTERID/ALTERID or ledger content did not persist in SQLite.");
        var verifiedFull = repository.GetVoucherXmlAuthorityByMasterIds(
            link.CompanyKey, link.FinancialYear, ids);
        if (verifiedFull.Count != full.Count ||
            verifiedFull.Any(row =>
                !fullById.TryGetValue(row.MasterId.Trim(), out var snapshots) ||
                snapshots.Length != 1 ||
                !string.Equals(row.AlterId.Trim(), snapshots[0].AlterId.Trim(),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(row.OriginalXml, snapshots[0].OriginalXml, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                "Updated Live Tally full signed Voucher XML did not persist correctly.");
        // Only the proven native Voucher authority advances; the normal
        // Live Tally Link retains exclusive ownership of full Master baseline.
        var saved = repository.GetBankSyncState(
            link.ClientId, link.FinancialYear, link.CompanyKey)
            ?? throw new InvalidOperationException("Original Tally sync checkpoint vanished.");
        saved.LastFyVoucherCount = rows.Count;
        saved.LastAlterId = Math.Max(saved.LastAlterId, latestAlterId);
        if (marker.HasReliableAlterId && marker.LastAlterId > 0)
        {
            saved.LastAlterId = Math.Max(saved.LastAlterId, marker.LastAlterId);
            saved.LastTallyChangeToken = marker.Token;
            saved.LastVoucherCount = marker.VoucherCount;
        }
        saved.LastIncrementalSyncUtc = DateTime.UtcNow;
        saved.LastStatus =
            "Live Tally native FY Voucher authority refreshed using original individual " +
            "reader; " + rows.Count + " complete index/XML object(s), verified through " +
            "ALTERID " + saved.LastAlterId + ".";
        repository.UpsertBankSyncState(saved);
    }

    private static void RequireSameCompany(
        TallyClientLink link, TallyCompanyInfo destination, string financialYear)
    {
        if (link.ClientId <= 0 || link.CompanyKey.Length == 0 ||
            destination.CompanyKey.Length == 0 ||
            !link.CompanyKey.Equals(destination.CompanyKey, StringComparison.OrdinalIgnoreCase) ||
            (link.FinancialYear.Length > 0 &&
             !link.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "The selected consolidated Client/FY or linked Tally CompanyKey changed. No voucher was posted.");
    }

    private void RequireSameSavedLink(ClientProfile client, TallyClientLink link)
    {
        var current = normalCoordinator.GetCurrentClientLink();
        if (current is null || current.ClientId != client.Id ||
            !current.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase) ||
            (current.FinancialYear.Length > 0 &&
             !current.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "The original consolidated Client/FY Tally link changed. No voucher was posted.");
    }

    private static (DateTime From, DateTime To) GetFinancialYearRange(string financialYear)
    {
        if (financialYear.Length < 4 ||
            !int.TryParse(financialYear[..4], NumberStyles.None,
                CultureInfo.InvariantCulture, out var year) ||
            year < 1900 || year > 9998)
            throw new InvalidOperationException("Consolidated financial year is not valid.");
        return (new DateTime(year, 4, 1), new DateTime(year + 1, 3, 31));
    }

    // Only identical live MASTERID + ALTERID + GUID can be checked again
    // through the individual module's native full-Voucher projection. An
    // actual identity/ALTERID change, duplicate, missing ID or unparseable
    // value never gets a projection fallback and remains blocked.
    internal static IReadOnlyList<DateTime> FindNativeProjectionRecheckMonths(
        IReadOnlyList<TallyVoucherIndexRecord> cached,
        IReadOnlyList<TallyVoucherIndexRecord> fresh,
        long synchronizedAlterId)
    {
        if (cached.Count != fresh.Count ||
            cached.Any(row => string.IsNullOrWhiteSpace(row.MasterId) ||
                              !row.VoucherDate.HasValue))
            return Array.Empty<DateTime>();
        var oldById = cached.ToDictionary(
            row => row.MasterId.Trim(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var months = new HashSet<DateTime>();
        foreach (var live in fresh)
        {
            var id = live.MasterId.Trim();
            if (id.Length == 0 || !seen.Add(id) ||
                !oldById.TryGetValue(id, out var old) ||
                !long.TryParse(live.AlterId.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var alter) ||
                alter <= 0 || alter > synchronizedAlterId ||
                !string.Equals(live.AlterId.Trim(), old.AlterId.Trim(),
                    StringComparison.OrdinalIgnoreCase) ||
                (old.Guid.Length > 0 && live.Guid.Length > 0 &&
                 !old.Guid.Equals(live.Guid, StringComparison.OrdinalIgnoreCase)))
                return Array.Empty<DateTime>();

            if ((old.VoucherDate.HasValue && live.VoucherDate.HasValue &&
                 old.VoucherDate.Value.Date != live.VoucherDate.Value.Date) ||
                (old.VoucherType.Length > 0 && live.VoucherType.Length > 0 &&
                 !old.VoucherType.Equals(live.VoucherType, StringComparison.OrdinalIgnoreCase)) ||
                (old.VoucherNumber.Length > 0 && live.VoucherNumber.Length > 0 &&
                 !old.VoucherNumber.Equals(live.VoucherNumber, StringComparison.OrdinalIgnoreCase)) ||
                (old.Reference.Length > 0 && live.Reference.Length > 0 &&
                 !old.Reference.Equals(live.Reference, StringComparison.OrdinalIgnoreCase)) ||
                (old.ContentHash.Length > 0 && live.ContentHash.Length > 0 &&
                 !old.ContentHash.Equals(live.ContentHash, StringComparison.OrdinalIgnoreCase)))
            {
                var date = old.VoucherDate!.Value;
                months.Add(new DateTime(date.Year, date.Month, 1));
            }
        }
        return months.OrderBy(date => date).ToArray();
    }

    private static void RequireSameVoucherIndex(
        IReadOnlyList<TallyVoucherIndexRecord> cached,
        IReadOnlyList<TallyVoucherIndexRecord> fresh,
        long synchronizedAlterId)
    {
        // A zero-row response cannot masquerade as a healthy FY with 645
        // existing vouchers. Extra/deleted MASTERIDs, changed ALTERIDs,
        // differing identifiers and duplicate IDs are all fail-closed.
        if (fresh.Count != cached.Count)
            throw new InvalidOperationException(
                $"Live FY voucher count {fresh.Count} differs from the original synchronized " +
                $"{cached.Count} MASTERIDs. No consolidated voucher will be posted.");

        var cachedById = cached.ToDictionary(
            x => x.MasterId.Trim(), StringComparer.OrdinalIgnoreCase);
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var live in fresh)
        {
            var id = live.MasterId.Trim();
            if (id.Length == 0 || !observed.Add(id) ||
                !cachedById.TryGetValue(id, out var old) ||
                !long.TryParse(live.AlterId.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var liveAlterId) ||
                liveAlterId <= 0 || liveAlterId > synchronizedAlterId ||
                !string.Equals(live.AlterId.Trim(), old.AlterId.Trim(),
                    StringComparison.OrdinalIgnoreCase) ||
                (old.VoucherDate.HasValue && live.VoucherDate.HasValue &&
                 old.VoucherDate.Value.Date != live.VoucherDate.Value.Date) ||
                (old.Guid.Length > 0 && live.Guid.Length > 0 &&
                 !old.Guid.Equals(live.Guid, StringComparison.OrdinalIgnoreCase)) ||
                (old.VoucherType.Length > 0 && live.VoucherType.Length > 0 &&
                 !old.VoucherType.Equals(live.VoucherType, StringComparison.OrdinalIgnoreCase)) ||
                (old.VoucherNumber.Length > 0 && live.VoucherNumber.Length > 0 &&
                 !old.VoucherNumber.Equals(live.VoucherNumber, StringComparison.OrdinalIgnoreCase)) ||
                (old.Reference.Length > 0 && live.Reference.Length > 0 &&
                 !old.Reference.Equals(live.Reference, StringComparison.OrdinalIgnoreCase)) ||
                (old.ContentHash.Length > 0 && live.ContentHash.Length > 0 &&
                 !old.ContentHash.Equals(live.ContentHash, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "Live destination Voucher MASTERID/ALTERID, identity or content changed " +
                    $"from the saved normal Tally authority (MASTERID {id}). " +
                    "No consolidated CREATE/ALTER can be authorized by cached information.");
        }
    }
}
