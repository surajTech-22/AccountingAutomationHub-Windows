using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Tally;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Consolidated-only pre-import bridge around the EXISTING normal linked-Tally
/// coordinator. The individual-client refresh, Tally link, 5-second heartbeat,
/// and ordinary voucher export are not modified. This wrapper issues no
/// voucher CREATE, ALTER or DELETE and never builds a second FY snapshot.
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
                "The consolidated Client/FY and confirmed destination CompanyKey differ. No voucher was posted.");

        void RequireSameSavedLink()
        {
            var current = normalCoordinator.GetCurrentClientLink();
            if (current is null || current.ClientId != client.Id ||
                !current.CompanyKey.Equals(confirmedLink.CompanyKey, StringComparison.OrdinalIgnoreCase) ||
                (current.FinancialYear.Length > 0 &&
                 !current.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "The saved consolidated Client/FY Tally link changed during pre-import. No voucher was posted.");
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireSameSavedLink();

            if (existingTally.IsImportInProgress || existingTally.IsExportInProgress)
                throw new InvalidOperationException(
                    "Another Tally import or export is active; consolidated pre-import was not started.");

            // Original synchronized Client/FY/CompanyKey baseline/delta route.
            // This call can defer a refresh without throwing (startup not
            // ready, Tally busy, or normal background sync failure). Do NOT
            // interpret its return alone as a completed checkpoint.
            await normalCoordinator.RefreshNowAsync(
                forceLinkedSynchronization: true, cancellationToken: cancellationToken,
                forceFullBaseline: false);

            RequireSameSavedLink();
            var live = await existingTally.GetVoucherChangeMarkerAsync(
                destination, cancellationToken);
            var saved = existingRepository.GetBankSyncState(
                client.Id, client.FinancialYear, confirmedLink.CompanyKey);

            // Match the UNCHANGED consolidated importer's strict live-marker
            // gate. ImportAsync itself still verifies complete MASTERID index,
            // full signed DR/CR XML, previous version, ALTERID and duplicates.
            var markerMatches = saved is not null &&
                saved.LastFullSyncUtc != DateTime.MinValue &&
                saved.LastAlterId > 0 &&
                saved.LastFyVoucherCount >= 0 &&
                live.HasReliableAlterId && live.LastAlterId > 0 &&
                string.Equals(live.Token, saved.LastTallyChangeToken,
                    StringComparison.Ordinal) &&
                live.VoucherCount == saved.LastVoucherCount &&
                (live.CompanyName.Length == 0 ||
                 live.CompanyName.Equals(destination.Name,
                     StringComparison.OrdinalIgnoreCase));

            if (markerMatches) return;

            if (attempt == 1)
                throw new InvalidOperationException(
                    "Consolidated import stopped BEFORE Tally voucher posting: " +
                    "the existing NORMAL linked Tally synchronization still has " +
                    "no verified live Client/FY checkpoint. " +
                    $"Live ALTERID {live.LastAlterId}, saved ALTERID {saved?.LastAlterId ?? 0}; " +
                    $"live vouchers {live.VoucherCount}, saved vouchers {saved?.LastVoucherCount ?? -1}; " +
                    $"full FY baseline present {saved is not null && saved.LastFullSyncUtc != DateTime.MinValue}; " +
                    $"reliable ALTERID {live.HasReliableAlterId}; " +
                    $"change marker matches {string.Equals(live.Token, saved?.LastTallyChangeToken, StringComparison.Ordinal)}; " +
                    $"company matches {live.CompanyName.Length == 0 || live.CompanyName.Equals(destination.Name, StringComparison.OrdinalIgnoreCase)}. " +
                    "Source bindings and saved Tally links were preserved. " +
                    "No alternate snapshot, relink, or voucher CREATE retry was attempted.");

            // One bounded retry of the SAME existing normal coordinator only;
            // never a new consolidated snapshot/delta processor. The source
            // database and voucher importer remain unchanged.
        }
    }
}
