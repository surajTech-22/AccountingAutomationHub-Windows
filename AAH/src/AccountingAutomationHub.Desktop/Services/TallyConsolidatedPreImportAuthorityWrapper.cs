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
        await normalCoordinator.RefreshNowAsync(
            forceLinkedSynchronization: true,
            cancellationToken: cancellationToken,
            forceFullBaseline: false);
        RequireSameSavedLink(client, confirmedLink);

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
        RequireSameVoucherIndex(cachedIndex, freshIndex, saved.LastAlterId);

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
