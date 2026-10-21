using AccountingAutomationHub.Domain.Audit;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Audit;
using AccountingAutomationHub.Infrastructure.Tally;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Explicit Audit -> Tally bridge. Audit keeps its own client/link authority, while the
/// transport pattern mirrors Accounting: first full FY authority, then lightweight
/// change detection. Fix123 keeps the normal company/ALTERID/Master/Voucher path enabled
/// for ERP 9 as well; low-priority timer work yields when another authority operation is
/// active rather than removing ERP 9 functionality. Accounting client authority is never
/// mutated by this service.
/// </summary>
public sealed class AuditTallyLinkService
{
    private readonly SqliteTallyRepository _repository = new();
    private readonly SqliteAuditRepository _auditRepository = new();
    private volatile bool _erp9RuntimeDetected;

    public bool IsErp9RuntimeDetected => _erp9RuntimeDetected;

    public AuditTallyLinkService()
    {
        _repository.Initialize();
        _auditRepository.Initialize();
    }

    public async Task<IReadOnlyList<TallyCompanyInfo>> DiscoverOpenCompaniesAsync(CancellationToken cancellationToken = default)
    {
        using var http = new TallyHttpService(timeout: TimeSpan.FromSeconds(8));
        var companies = await http.DiscoverCompaniesAsync(cancellationToken, includeLightweightCounts: false).ConfigureAwait(false);
        if (http.IsLocalErp9Runtime) _erp9RuntimeDetected = true;
        return companies;
    }

    public async Task<bool> ProbeTransportAsync(CancellationToken cancellationToken = default)
    {
        using var http = new TallyHttpService(timeout: TimeSpan.FromSeconds(2));
        return await http.ProbeEndpointAsync(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RefreshVoucherAuthorityAsync(
        long auditClientId,
        TallyCompanyInfo company,
        string financialYear,
        CancellationToken cancellationToken = default)
    {
        ValidateLinkInputs(auditClientId, company, financialYear);
        using var http = new TallyHttpService(timeout: TimeSpan.FromSeconds(20));
        return await RefreshVoucherAuthorityCoreAsync(
            auditClientId,
            company,
            financialYear,
            cancellationToken,
            http,
            persistPulseCheckpoint: true).ConfigureAwait(false);
    }

    public async Task<AuditTallyPulseResult> PulseAuthorityAsync(
        long auditClientId,
        TallyCompanyInfo company,
        string financialYear,
        CancellationToken cancellationToken = default)
    {
        ValidateLinkInputs(auditClientId, company, financialYear);
        using var http = new TallyHttpService(timeout: TimeSpan.FromSeconds(12));
        var marker = await http.GetVoucherChangeMarkerAsync(company, cancellationToken).ConfigureAwait(false);
        var state = _auditRepository.GetTallySyncState(auditClientId, financialYear, company.CompanyKey);
        var now = DateTime.UtcNow;

        // Upgrade path / first live cycle: build a complete durable FY baseline once. Every
        // later normal cycle is a tiny marker check and, only when ALTERID advances, a delta.
        if (state is null || state.LastAlterId <= 0 || string.IsNullOrWhiteSpace(state.LastChangeToken))
        {
            var count = await RefreshVoucherAuthorityCoreAsync(
                auditClientId,
                company,
                financialYear,
                cancellationToken,
                http,
                persistPulseCheckpoint: false).ConfigureAwait(false);
            var checkpoint = await http.GetVoucherChangeMarkerAsync(company, cancellationToken).ConfigureAwait(false);
            SavePulseState(auditClientId, financialYear, company.CompanyKey, checkpoint, now, fullSync: true, deltaSync: false,
                $"Initial Audit FY authority baseline · {count:N0} voucher(s)");
            return new AuditTallyPulseResult(true, true, false, checkpoint.LastAlterId, count,
                $"Live · baseline synchronized · ALTERID {checkpoint.LastAlterId:N0}");
        }

        var tokenSame = marker.Token.Equals(state.LastChangeToken, StringComparison.Ordinal);
        var countsSame = marker.VoucherCount == state.LastVoucherCount &&
                         marker.LedgerCount == state.LastLedgerCount &&
                         marker.GroupCount == state.LastGroupCount;
        if (tokenSame && countsSame)
        {
            state.LastPulseUtc = now;
            state.LastStatus = $"Current at ALTERID {state.LastAlterId:N0}";
            _auditRepository.UpsertTallySyncState(state);
            return new AuditTallyPulseResult(false, false, false, state.LastAlterId, 0,
                $"Live · current · ALTERID {state.LastAlterId:N0}");
        }

        // A count reduction can represent a Tally-side delete that an ALTERID-only delta
        // cannot safely reconstruct. An unreliable or rewound ALTERID also falls back to a
        // complete FY baseline rather than guessing.
        var mustFullRefresh = !marker.HasReliableAlterId ||
                              marker.LastAlterId < state.LastAlterId ||
                              marker.VoucherCount < state.LastVoucherCount ||
                              marker.LedgerCount < state.LastLedgerCount ||
                              marker.GroupCount < state.LastGroupCount;
        if (mustFullRefresh)
        {
            var count = await RefreshVoucherAuthorityCoreAsync(
                auditClientId,
                company,
                financialYear,
                cancellationToken,
                http,
                persistPulseCheckpoint: false).ConfigureAwait(false);
            var checkpoint = await http.GetVoucherChangeMarkerAsync(company, cancellationToken).ConfigureAwait(false);
            SavePulseState(auditClientId, financialYear, company.CompanyKey, checkpoint, now, fullSync: true, deltaSync: false,
                $"Audit authority re-baselined · {count:N0} voucher(s)");
            return new AuditTallyPulseResult(true, true, false, checkpoint.LastAlterId, count,
                $"Live · baseline repaired · ALTERID {checkpoint.LastAlterId:N0}");
        }

        if (marker.LastAlterId <= state.LastAlterId)
        {
            // Counts/token changed without a newer reliable ALTERID. Fail closed to a baseline.
            var count = await RefreshVoucherAuthorityCoreAsync(
                auditClientId,
                company,
                financialYear,
                cancellationToken,
                http,
                persistPulseCheckpoint: false).ConfigureAwait(false);
            var checkpoint = await http.GetVoucherChangeMarkerAsync(company, cancellationToken).ConfigureAwait(false);
            SavePulseState(auditClientId, financialYear, company.CompanyKey, checkpoint, now, fullSync: true, deltaSync: false,
                $"Audit authority re-baselined after non-monotonic marker · {count:N0} voucher(s)");
            return new AuditTallyPulseResult(true, true, false, checkpoint.LastAlterId, count,
                $"Live · baseline repaired · ALTERID {checkpoint.LastAlterId:N0}");
        }

        var (from, to) = AdvancedClosingEntryPlanner.FinancialYearRange(financialYear);
        var delta = await http.ExportAuthorityChangesAfterAlterIdAsync(
            company,
            from,
            to,
            state.LastAlterId,
            cancellationToken).ConfigureAwait(false);
        if (delta.Vouchers.Count > 0)
        {
            _repository.UpsertVoucherIndexRecords(company.CompanyKey, financialYear, delta.Vouchers);
            _repository.UpsertVoucherXmlAuthority(company.CompanyKey, financialYear, delta.FullVouchers);
        }

        // Ledger names/groups/balances are lightweight compared with a full FY voucher read,
        // and closing balances can change after a voucher ALTER. Refresh Master/Group authority
        // whenever global ALTERID advances so Audit account tiles stay live too.
        await RefreshMasterAuthorityAsync(auditClientId, company, financialYear, cancellationToken, http).ConfigureAwait(false);

        SavePulseState(auditClientId, financialYear, company.CompanyKey, marker, now, fullSync: false, deltaSync: true,
            $"ALTERID delta · {delta.Vouchers.Count:N0} changed FY voucher(s)");
        return new AuditTallyPulseResult(true, false, true, marker.LastAlterId, delta.Vouchers.Count,
            $"Live · {delta.Vouchers.Count:N0} change(s) · ALTERID {marker.LastAlterId:N0}");
    }

    public async Task<TallyImportResponse> AlterExactVoucherLedgerAsync(
        AuditClientProfile auditClient,
        string voucherMasterId,
        string sourceLedgerName,
        string targetLedgerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditClient);
        var companyKey = auditClient.LinkedTallyCompanyKey.Trim();
        var masterId = (voucherMasterId ?? string.Empty).Trim();
        var source = (sourceLedgerName ?? string.Empty).Trim();
        var target = (targetLedgerName ?? string.Empty).Trim();
        if (companyKey.Length == 0) throw new InvalidOperationException("This Audit client is not linked to a Tally company.");
        if (masterId.Length == 0) throw new InvalidOperationException("Exact Tally Voucher MASTERID is required.");
        if (source.Length == 0 || target.Length == 0 || source.Equals(target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A different FROM and INTO Ledger is required for Tally alteration.");

        var snapshots = _repository.GetVoucherXmlAuthorityByMasterIds(companyKey, auditClient.FinancialYear, new[] { masterId });
        var snapshot = snapshots.FirstOrDefault(item => item.MasterId.Trim().Equals(masterId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Cached full Voucher XML for MASTERID {masterId} is not available. Refresh linked Tally authority first.");

        using var http = new TallyHttpService(timeout: TimeSpan.FromSeconds(20));
        var companies = await http.DiscoverCompaniesAsync(cancellationToken, includeLightweightCounts: false).ConfigureAwait(false);
        var company = companies.FirstOrDefault(item => item.CompanyKey.Equals(companyKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The explicitly linked Tally company is not currently open.");
        var response = await http.AlterVoucherLedgerReferencesAsync(company, new[] { snapshot }, source, target, cancellationToken).ConfigureAwait(false);
        var verified = await http.ExportVoucherXmlSnapshotsByKnownMasterIdsForVerificationAsync(
            company, new[] { masterId }, AdvancedClosingEntryPlanner.FinancialYearRange(auditClient.FinancialYear).From,
            AdvancedClosingEntryPlanner.FinancialYearRange(auditClient.FinancialYear).To, cancellationToken).ConfigureAwait(false);
        _repository.UpsertVoucherXmlAuthority(companyKey, auditClient.FinancialYear, verified);
        return response;
    }

    public async Task<int> RefreshMasterAuthorityAsync(
        long auditClientId,
        TallyCompanyInfo company,
        string financialYear,
        CancellationToken cancellationToken = default)
    {
        using var http = new TallyHttpService(timeout: TimeSpan.FromSeconds(12));
        return await RefreshMasterAuthorityAsync(auditClientId, company, financialYear, cancellationToken, http).ConfigureAwait(false);
    }

    private async Task<int> RefreshVoucherAuthorityCoreAsync(
        long auditClientId,
        TallyCompanyInfo company,
        string financialYear,
        CancellationToken cancellationToken,
        TallyHttpService http,
        bool persistPulseCheckpoint)
    {
        var (from, to) = AdvancedClosingEntryPlanner.FinancialYearRange(financialYear);
        var total = 0;
        var month = new DateTime(from.Year, from.Month, 1);
        var lastMonth = new DateTime(to.Year, to.Month, 1);
        while (month <= lastMonth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sliceFrom = month < from ? from : month;
            var monthEnd = month.AddMonths(1).AddDays(-1);
            var sliceTo = monthEnd > to ? to : monthEnd;
            var authority = await http.ExportVoucherAuthorityFreshAsync(
                company,
                sliceFrom,
                sliceTo,
                cancellationToken).ConfigureAwait(false);
            _repository.ReplaceVoucherIndexRange(
                company.CompanyKey,
                financialYear,
                sliceFrom,
                sliceTo,
                authority.Index);
            _repository.UpsertVoucherXmlAuthority(
                company.CompanyKey,
                financialYear,
                authority.Full);
            total += authority.Index.Count;
            month = month.AddMonths(1);
        }

        await RefreshMasterAuthorityAsync(auditClientId, company, financialYear, cancellationToken, http).ConfigureAwait(false);
        if (persistPulseCheckpoint)
        {
            var marker = await http.GetVoucherChangeMarkerAsync(company, cancellationToken).ConfigureAwait(false);
            SavePulseState(auditClientId, financialYear, company.CompanyKey, marker, DateTime.UtcNow, fullSync: true, deltaSync: false,
                $"Full Audit FY authority · {total:N0} voucher(s)");
        }
        return total;
    }

    private async Task<int> RefreshMasterAuthorityAsync(
        long auditClientId,
        TallyCompanyInfo company,
        string financialYear,
        CancellationToken cancellationToken,
        TallyHttpService http)
    {
        if (auditClientId <= 0) throw new InvalidOperationException("Select an Audit Client before reading Tally Masters.");
        ArgumentNullException.ThrowIfNull(company);
        if (string.IsNullOrWhiteSpace(company.CompanyKey)) throw new InvalidOperationException("The selected Tally company does not have a stable CompanyKey.");

        var (from, to) = AdvancedClosingEntryPlanner.FinancialYearRange(financialYear);
        var ledgerAuthority = await http.ExportLedgerNameAuthoritiesForPeriodAsync(company, from, to, cancellationToken).ConfigureAwait(false);
        var groupParents = await http.ExportGroupParentsAsync(company, cancellationToken).ConfigureAwait(false);
        _auditRepository.ReplaceTallyMasterAuthority(
            auditClientId,
            financialYear,
            company.CompanyKey,
            ledgerAuthority.Select(item => new AuditTallyLedgerAuthority
            {
                AuditClientId = auditClientId,
                FinancialYear = financialYear,
                CompanyKey = company.CompanyKey,
                ExactName = item.ExactName,
                DisplayName = item.DisplayName,
                Parent = item.Parent,
                Gstin = item.Gstin,
                OpeningBalance = item.OpeningBalance,
                ClosingBalance = item.ClosingBalance,
                MasterId = item.MasterId,
                Guid = item.Guid,
                AlterId = item.AlterId,
                UpdatedUtc = DateTime.UtcNow
            }),
            groupParents);
        return ledgerAuthority.Count;
    }

    private void SavePulseState(
        long auditClientId,
        string financialYear,
        string companyKey,
        TallyVoucherChangeMarker marker,
        DateTime nowUtc,
        bool fullSync,
        bool deltaSync,
        string status)
    {
        var previous = _auditRepository.GetTallySyncState(auditClientId, financialYear, companyKey);
        _auditRepository.UpsertTallySyncState(new AuditTallySyncState
        {
            AuditClientId = auditClientId,
            FinancialYear = financialYear,
            CompanyKey = companyKey,
            LastChangeToken = marker.Token,
            LastVoucherCount = marker.VoucherCount,
            LastLedgerCount = marker.LedgerCount,
            LastGroupCount = marker.GroupCount,
            LastAlterId = marker.LastAlterId,
            LastFullSyncUtc = fullSync ? nowUtc : previous?.LastFullSyncUtc ?? DateTime.MinValue,
            LastDeltaSyncUtc = deltaSync ? nowUtc : previous?.LastDeltaSyncUtc ?? DateTime.MinValue,
            LastPulseUtc = nowUtc,
            LastStatus = status
        });
    }

    private static void ValidateLinkInputs(long auditClientId, TallyCompanyInfo company, string financialYear)
    {
        ArgumentNullException.ThrowIfNull(company);
        if (auditClientId <= 0)
            throw new InvalidOperationException("Select an Audit Client before linking Tally.");
        if (string.IsNullOrWhiteSpace(company.CompanyKey))
            throw new InvalidOperationException("The selected Tally company does not have a stable CompanyKey.");
        if (string.IsNullOrWhiteSpace(financialYear))
            throw new InvalidOperationException("Select an Audit Client/FY before linking Tally.");
    }
}

public sealed record AuditTallyPulseResult(
    bool AuthorityChanged,
    bool FullRefresh,
    bool DeltaRefresh,
    long AlterId,
    int ChangedVoucherCount,
    string Status);
