using System.Diagnostics;
using System.Globalization;
using System.Text;
using AccountingAutomationHub.Application.Clients;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Infrastructure.Persistence;

namespace AccountingAutomationHub.Infrastructure.Tally;

public sealed class TallyBankVoucherImportService
{
    private const string UnmappedBankSuspenseLedger = "Suspense";
    private const int ConflictDetailLimit = 100;

    private readonly SqliteBankingRepository _bankingRepository;
    private readonly SqliteMasterRepository _masterRepository;
    private readonly SqliteTallyRepository _tallyRepository;
    private readonly TallyHttpService _tallyHttpService;
    private readonly TallyLiveMasterSynchronizationService _liveMasterSyncService;
    private readonly IClientPreferenceRepository _clientPreferences;

    public TallyBankVoucherImportService(
        SqliteBankingRepository bankingRepository,
        SqliteMasterRepository masterRepository,
        SqliteTallyRepository tallyRepository,
        TallyHttpService tallyHttpService,
        TallyLiveMasterSynchronizationService liveMasterSyncService,
        IClientPreferenceRepository clientPreferences)
    {
        _bankingRepository = bankingRepository;
        _masterRepository = masterRepository;
        _tallyRepository = tallyRepository;
        _tallyHttpService = tallyHttpService;
        _liveMasterSyncService = liveMasterSyncService;
        _clientPreferences = clientPreferences;
    }

    public async Task<TallyBankImportResult> ImportAsync(
        ClientProfile client,
        TallyCompanyInfo company,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default,
        bool usePreSynchronizedVoucherAuthority = false,
        long synchronizedAuthorityAlterId = 0,
        IReadOnlyCollection<string>? explicitRestoreSourceAnchors = null,
        bool allowUnmappedSuspense = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(company);

        fromDate = fromDate.Date;
        toDate = toDate.Date;
        if (fromDate > toDate)
        {
            throw new InvalidOperationException(
                "The Bank Statement Tally From Date cannot be after the To Date.");
        }

        var result = new TallyBankImportResult
        {
            FromDate = fromDate,
            ToDate = toDate
        };

        // An explicit deleted-voucher restore temporarily scans this FY for its
        // stable source anchors. It must NOT overwrite the user's saved regular
        // Bank Statement export period with that full-FY recovery range.
        if (explicitRestoreSourceAnchors is null || explicitRestoreSourceAnchors.Count == 0)
            _tallyRepository.SaveBankImportPeriod(client.Id, fromDate, toDate);

        // Fix24 first-import-wins gate. Remove any later local physical duplicates before
        // they can participate in Tally classification, and durably queue their Tally
        // MASTERID cleanup. Ledger differences never make a later Bank row unique.
        try
        {
            var localDuplicateCleanup = _bankingRepository.CleanupExactTransactionDuplicates(
                client.Id, client.FinancialYear);
            result.LocalDuplicateTransactionsRemoved = localDuplicateCleanup.TransactionsRemoved;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result.TallyErrors = 1;
            result.LineError = "Fix24 first-import-wins Bank duplicate cleanup failed before Tally export: " + exception.Message;
            result.Status = result.LineError;
            return result;
        }

        if (!await ProcessPendingFirstImportWinsDeletesAsync(
                client, company, result, cancellationToken,
                usePreSynchronizedVoucherAuthority, synchronizedAuthorityAlterId))
            return result;

        // Fix84: recover the two/current externally-adopted Bank Charges sources that
        // Fix80 left archived after preserving their Payment treatment. Reactivation is
        // strictly limited to a proven Bank MASTERID movement: the previous linked Bank
        // Ledger resolves to one Tally MASTERID and the Bank Account now owns another.
        // The normal saved-previous-version gate below must still pass before ALTER.
        _bankingRepository.ReactivateArchivedBankChargesForBankMasterMigration(
            client.Id, client.FinancialYear, company.CompanyKey);

        var restoreSourceAnchors = (explicitRestoreSourceAnchors ?? Array.Empty<string>())
            .Select(value => (value ?? string.Empty).Trim())
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var transactions = _bankingRepository
            .GetTransactions(client.Id, client.FinancialYear, includeArchived: false)
            .Where(transaction =>
                transaction.TransactionDate.Date >= fromDate &&
                transaction.TransactionDate.Date <= toDate &&
                (restoreSourceAnchors.Count == 0 || restoreSourceAnchors.Contains(
                    TallyVoucherFingerprint.BuildSourceKey(client.Id, client.FinancialYear, transaction.TransactionId, transaction.Id))))
            .ToArray();

        result.SelectedTransactions = transactions.Length;
        if (transactions.Length == 0)
        {
            result.Status = BuildSummary(result, imported: false) +
                " No active Bank Statement transactions exist in the selected date range.";
            return result;
        }

        // Fix68 explicit-export authority is prepared once by the coordinator from one
        // complete fresh active-FY baseline. Every selected Bank range reuses that exact
        // live authority locally instead of asking Tally for the same twelve months again.
        // Standalone callers still build the same complete FY authority directly.
        var (authorityFrom, authorityTo) = GetFinancialYearBounds(client.FinancialYear, fromDate, toDate);
        IReadOnlyList<TallyVoucherIndexRecord> liveBefore;
        if (usePreSynchronizedVoucherAuthority)
        {
            if (!_tallyRepository.HasCompleteVoucherXmlAuthority(company.CompanyKey, client.FinancialYear))
                throw new InvalidOperationException(
                    "The prepared fresh Tally FY authority is incomplete. Bank export was blocked before any Voucher write.");
            liveBefore = _tallyRepository.GetVoucherIndexRange(
                company.CompanyKey, client.FinancialYear, authorityFrom, authorityTo);
        }
        else
        {
            var liveBeforeList = new List<TallyVoucherIndexRecord>();
            var authorityMonth = new DateTime(authorityFrom.Year, authorityFrom.Month, 1);
            var authorityLastMonth = new DateTime(authorityTo.Year, authorityTo.Month, 1);
            while (authorityMonth <= authorityLastMonth)
            {
                var sliceFrom = authorityMonth < authorityFrom ? authorityFrom : authorityMonth;
                var sliceEnd = authorityMonth.AddMonths(1).AddDays(-1);
                var sliceTo = sliceEnd > authorityTo ? authorityTo : sliceEnd;
                var sliceAuthority = await _tallyHttpService.ExportVoucherAuthorityFreshAsync(
                    company, sliceFrom, sliceTo, cancellationToken);
                _tallyRepository.ReplaceVoucherIndexRange(
                    company.CompanyKey, client.FinancialYear, sliceFrom, sliceTo, sliceAuthority.Index);
                _tallyRepository.UpsertVoucherXmlAuthority(
                    company.CompanyKey, client.FinancialYear, sliceAuthority.Full);
                liveBeforeList.AddRange(sliceAuthority.Index);
                authorityMonth = authorityMonth.AddMonths(1);
            }
            liveBefore = liveBeforeList;
        }

        // Fix85: classify missing previously-bound Voucher MASTERIDs from the complete
        // fresh active-FY authority before Bank source classification. Missing identity
        // is never permission to CREATE: isolated losses must resolve to exact replacement
        // or DeletedInTally; widespread loss remains exact-rebind recovery.
        _tallyRepository.ReconcileMissingVoucherBindingAuthority(
            client.Id, client.FinancialYear, company.CompanyKey,
            liveBefore.Select(item => item.MasterId));

        async Task<IReadOnlyList<TallyVoucherIndexRecord>> RefreshVoucherAuthorityAsync(DateTime rangeFrom, DateTime rangeTo)
        {
            rangeFrom = rangeFrom.Date; rangeTo = rangeTo.Date;

            // Fix68 destructive-boundary authority: DELETE/ALTER must never depend on
            // an ALTERID pulse arriving quickly enough. Re-read exactly the affected
            // date range and receive each Voucher MASTERID/GUID/ALTERID + full body in
            // the same response. This is intentionally narrower than the one full-FY
            // explicit-export baseline and is used only at a destructive mutation gate.
            var directAuthority = await _tallyHttpService.ExportVoucherAuthorityFreshAsync(
                company, rangeFrom, rangeTo, cancellationToken);
            _tallyRepository.ReplaceVoucherIndexRange(
                company.CompanyKey, client.FinancialYear, rangeFrom, rangeTo, directAuthority.Index);
            _tallyRepository.UpsertVoucherXmlAuthority(
                company.CompanyKey, client.FinancialYear, directAuthority.Full);
            return directAuthority.Index;
        }

        // FIX8: local Imported fingerprints are not duplicate authority by themselves,
        // but they are durable source ownership evidence. Use them only together with
        // the freshly synchronized live Voucher index so an AAH-imported row that was
        // later re-mapped/altered in Tally is recognized as an existing source instead
        // of becoming a generic conflict.
        var importedBankSourceKeys = _tallyRepository.GetImportedSourceKeys(
            client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher");

        var accounts = _bankingRepository
            .GetAccounts(client.Id, client.FinancialYear)
            .ToDictionary(account => account.Id);
        var allTransactionsById = _bankingRepository
            .GetTransactions(client.Id, client.FinancialYear, includeArchived: true)
            .ToDictionary(transaction => transaction.Id);
        var transferByTransactionId = new Dictionary<long, BankTransferMatch>();
        foreach (var match in _bankingRepository.GetTransferMatches(client.Id, client.FinancialYear)
                     .Where(match => match.Status.Equals("Matched", StringComparison.OrdinalIgnoreCase)))
        {
            transferByTransactionId[match.SourceTransactionId] = match;
            transferByTransactionId[match.TargetTransactionId] = match;
        }

        // CURRENT Excel workbook Payment Returns parity. BuildRejectReversalRowSet
        // identifies same-date REJECT/REJECTED debit + credit rows with identical
        // narration and amount. These are one failed bank movement plus its immediate
        // reversal, not two accounting vouchers and not an inter-bank transfer.
        // Keep them out of normal Ledger Mapping/Tally voucher treatment entirely.
        var paymentReturnPairTransactionIds = ExcelLedgerMappingParser
            .BuildRejectReversalTransactionIds(allTransactionsById.Values
                .Where(transaction => !transaction.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase)))
            .ToHashSet();

        var defaultEmptyLedger = ReadDefaultEmptyLedger(client);

        var masterRecords = _masterRepository
            .Get(client.Id, client.FinancialYear, includeArchived: false)
            .Where(master => master.Name.Trim().Length > 0)
            .ToArray();

        // Fix77 migration + runtime rule: when a bound Bank Account now points at a
        // separately-created unbound local Ledger name, detach the Bank Account from the
        // old Tally MASTERID before any same-MASTERID ALTER planning. The old live Master
        // remains active; the new local Master must go through CREATE and receive a new ID.
        var detachedIndependentBankMasters = TallyIndependentBankLedgerPolicy.DetachExplicitIndependentRenames(
            _bankingRepository, _masterRepository, client.Id, client.FinancialYear,
            accounts.Values.ToArray(), masterRecords);
        if (detachedIndependentBankMasters > 0)
        {
            // Fix78: the detach policy can reclassify a historical unbound row from the
            // stale Source='Live Tally' label to local Bank authority. Reload immediately;
            // using the pre-detach in-memory snapshot would falsely publish that unbound
            // row as live Tally-by-name, skip CREATE, then fail the MASTERID barrier.
            masterRecords = _masterRepository
                .Get(client.Id, client.FinancialYear, includeArchived: false)
                .Where(master => master.Name.Trim().Length > 0)
                .ToArray();
        }

        // Preserve the Ledger Group selected in Ledger Mapping even when the active
        // Master row is temporarily missing. Tally.ERP 9 defines Suspense A/c as a
        // reserved Group, not as a voucher Ledger, so unmapped Bank rows use a
        // dedicated ledger under that Group.
        var ledgerGroupHints = _bankingRepository
            .GetLedgerMappings(client.Id, client.FinancialYear)
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.LedgerName) &&
                              !string.IsNullOrWhiteSpace(mapping.MasterGroup))
            .GroupBy(mapping => BuildTallyMasterNameKey(mapping.LedgerName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(mapping => mapping.UpdatedUtc)
                              .Select(mapping => mapping.MasterGroup.Trim())
                              .First(groupName => groupName.Length > 0),
                StringComparer.OrdinalIgnoreCase);

        // Fix37 normal coordinator path reuses synchronized Master authority. The
        // direct Ledger read remains only for standalone/fail-closed callers.
        IReadOnlyDictionary<string,string> liveLedgerParents;
        if (_tallyHttpService.IsLocalErp9Runtime)
        {
            // Fix111 ERP9 explicit-export Master barrier. A prepared/pinned cache may be
            // valid for screen paint, but it must never authorize a Voucher write when a
            // referenced Ledger was deleted or never created in the currently linked ERP9
            // company. Force one serialized live Master refresh now; wait for ERP9 to return
            // success/failure, synchronize MASTERIDs, then classify missing Masters.
            _tallyHttpService.RequireFreshErp9MasterAuthorityForExplicitExport();
            var freshMasterXml = await _tallyHttpService.ExportMastersAsync(company, cancellationToken).ConfigureAwait(false);
            var freshLedgerParents = await _tallyHttpService.ExportLedgerParentsAsync(company, cancellationToken).ConfigureAwait(false);
            var freshGroupNames = await _tallyHttpService.ExportGroupNamesAsync(company, cancellationToken).ConfigureAwait(false);
            _liveMasterSyncService.Synchronize(
                freshMasterXml, client.Id, client.FinancialYear, company.Name,
                freshLedgerParents, freshGroupNames, ledgerGroupOnlyAuthority: true);
            masterRecords = _masterRepository
                .Get(client.Id, client.FinancialYear, includeArchived: false)
                .Where(master => master.Name.Trim().Length > 0)
                .ToArray();
            liveLedgerParents = freshLedgerParents;
        }
        else if (usePreSynchronizedVoucherAuthority)
        {
            // Fix51: baseline + Master ALTERID deltas are the current shared authority.
            // Never let the pinned first baseline override later synchronized Master changes.
            liveLedgerParents = masterRecords
                .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                 MasterAuthority.IsTallySource(master.Source) && !master.IsArchived &&
                                 master.TallyMasterId.Trim().Length > 0)
                .GroupBy(master => master.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Select(master => master.Parent.Trim()).FirstOrDefault(parent => parent.Length > 0) ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        }
        else
            liveLedgerParents = await _tallyHttpService.ExportLedgerParentsAsync(company, cancellationToken);
        var liveLedgerNameIndex = BuildTallyMasterNameIndex(liveLedgerParents);

        // Fix76/Fix77: apply pending same-name Bank Ledger Parent changes to the SAME Tally
        // MASTERID before any Bank transaction identity or duplicate work. The previous
        // live ALTERID must still match AAH's saved binding; otherwise the change is
        // review-first and no Master/Voucher mutation is attempted.
        var bankAccountsForMasterAlter = accounts.Values.ToArray();
        if (TallyBoundLedgerAlterPlanner.HasPendingEdits(bankAccountsForMasterAlter, masterRecords))
        {
            try
            {
                var liveAuthorities = await _tallyHttpService.ExportLedgerNameAuthoritiesAsync(company, cancellationToken).ConfigureAwait(false);
                var boundPlans = TallyBoundLedgerAlterPlanner.Build(bankAccountsForMasterAlter, masterRecords, liveAuthorities);
                if (boundPlans.Count > 0)
                {
                    var alterXml = TallyBoundLedgerAlterPlanner.BuildXml(company.Name, boundPlans);
                    var alterText = await _tallyHttpService.ImportXmlAsync(alterXml, cancellationToken).ConfigureAwait(false);
                    var alterResponse = TallyHttpService.ParseImportResponse(alterText);
                    result.MasterResponsePath = SaveResponseLog(
                        client, company, fromDate, toDate, alterXml, alterText, "bank-bound-ledger-alter");
                    if (alterResponse.Errors > 0 || !string.IsNullOrWhiteSpace(alterResponse.LineError) || alterResponse.Altered < boundPlans.Count)
                    {
                        result.TallyErrors = Math.Max(1, alterResponse.Errors);
                        result.LineError = alterResponse.LineError;
                        result.UnverifiedAfterImport = transactions.Length;
                        result.Status = $"Bound Bank Ledger MASTERID ALTER failed before transaction classification. Expected {boundPlans.Count:N0} ALTER(s), Tally reported {alterResponse.Altered:N0}. {alterResponse.LineError}";
                        return result;
                    }

                    foreach (var plan in boundPlans)
                    {
                        if (!plan.CurrentName.Equals(plan.DesiredName, StringComparison.OrdinalIgnoreCase))
                            _masterRepository.ApplyVerifiedLedgerRenameReferences(client.Id, client.FinancialYear, plan.CurrentName, plan.DesiredName);
                    }

                    var refreshedMasterXml = await _tallyHttpService.ExportMastersAsync(company, cancellationToken).ConfigureAwait(false);
                    var refreshedLedgerParents = await _tallyHttpService.ExportLedgerParentsAsync(company, cancellationToken).ConfigureAwait(false);
                    var refreshedGroupNames = await _tallyHttpService.ExportGroupNamesAsync(company, cancellationToken).ConfigureAwait(false);
                    _liveMasterSyncService.Synchronize(
                        refreshedMasterXml, client.Id, client.FinancialYear, company.Name,
                        refreshedLedgerParents, refreshedGroupNames, ledgerGroupOnlyAuthority: true);
                    masterRecords = _masterRepository.Get(client.Id, client.FinancialYear, includeArchived: false)
                        .Where(master => master.Name.Trim().Length > 0).ToArray();
                    liveLedgerParents = refreshedLedgerParents;
                    liveLedgerNameIndex = BuildTallyMasterNameIndex(liveLedgerParents);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result.TallyErrors = 1;
                result.UnverifiedAfterImport = transactions.Length;
                result.LineError = "Bound Bank Ledger MASTERID ALTER/rebind failed: " + exception.Message;
                result.Status = "MASTERID barrier stopped Bank transaction classification. " + result.LineError;
                return result;
            }
        }

        // Fresh direct Tally collection wins over an old local Source='Live Tally' label.
        // If a Ledger was deleted in Tally after the last background sync, retire only
        // that stale Tally authority now, before deciding whether a Bank Account or an
        // explicit Ledger Mapping may safely recreate it. This prevents a stale Tally
        // row from blocking the real app-side authority and avoids creating a second
        // active Master row for the same normalized Ledger.
        var boundBankMasterIds = accounts.Values.Where(account => account.TallyLedgerMasterId.Trim().Length > 0)
            .Select(account => account.TallyLedgerMasterId.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staleTallyLedgerIds = masterRecords
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                             MasterAuthority.IsTallySource(master.Source) &&
                             master.TallyMasterId.Trim().Length > 0 &&
                             // Fix76: never archive a stable Bank MASTERID merely because a
                             // pending local rename means its desired display name is not yet
                             // present in the live name index.
                             !boundBankMasterIds.Contains(master.TallyMasterId.Trim()) &&
                             !TryResolveExistingTallyMasterName(master.Name, liveLedgerNameIndex, out _))
            .Select(master => master.PersistentId)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        foreach (var staleId in staleTallyLedgerIds)
            _masterRepository.TryArchive(staleId, archived: true);
        if (staleTallyLedgerIds.Length > 0)
            masterRecords = _masterRepository
                .Get(client.Id, client.FinancialYear, includeArchived: false)
                .Where(master => master.Name.Trim().Length > 0)
                .ToArray();

        // Batch canonical names are also keyed by Tally's master-name identity so two
        // new spellings in the same export cannot produce duplicate CREATE masters.
        var batchLedgerCanonicalNames = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var reusedExistingTallyLedgerNames = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        var ready = new List<PreparedBankVoucher>();
        var batchFingerprints = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        // FIX9 cardinality authority. Previously Imported sources claim their live
        // MASTERID first. A later identical live MASTERID can then be proven to be an
        // extra/manual duplicate only when it is newer than the retained claimed row.
        var reservedLiveMasterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimedImportedLiveIdentityMasters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var duplicateDeletePlans = new List<PendingBankVoucherDelete>();

        // Process previously Imported sources first on each date so identical Bank
        // occurrences are assigned one-to-one before any new source is classified.
        foreach (var transaction in transactions
                     .OrderBy(item => item.TransactionDate.Date)
                     .ThenByDescending(item => importedBankSourceKeys.Contains(
                         TallyVoucherFingerprint.BuildSourceKey(
                             client.Id, client.FinancialYear, item.TransactionId, item.Id)))
                     .ThenBy(item => item.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryNormalizeMode(transaction.Mode, out var voucherType))
            {
                result.UnsupportedTransactions++;
                continue;
            }

            if (!accounts.TryGetValue(transaction.BankAccountId, out var account))
            {
                result.MissingLedgerTransactions++;
                continue;
            }

            var requestedBankLedger = account.AuthorityName.Trim();

            // Excel Payment Returns parity comes BEFORE any inter-bank treatment.
            // A paired REJECT debit/credit is a net-zero failed movement and must not
            // create two Bank vouchers or enter normal Ledger Mapping.
            if (paymentReturnPairTransactionIds.Contains(transaction.Id))
            {
                result.PaymentReturnPairTransactionsSkipped++;
                continue;
            }

            // FIXED31: manual export still requires Final Ledger. Automatic Tally Sync may
            // explicitly opt into Import Suspense for an Auto Import Bank row. In that case
            // keep the AAH transaction unmapped and use the dedicated Suspense Ledger only
            // as this Voucher version's counterpart. The durable source binding then forces a
            // later Final Ledger assignment through previous-version verification + ALTER of
            // the same Voucher MASTERID; it can never become a second CREATE merely because
            // the counterpart changed.
            var finalLedger = (transaction.LedgerName ?? string.Empty).Trim();
            if (finalLedger.Length == 0 && !allowUnmappedSuspense)
            {
                result.MissingLedgerTransactions++;
                continue;
            }

            var requestedCounterpartLedger = finalLedger.Length == 0
                ? UnmappedBankSuspenseLedger
                : NormalizeCounterpartLedger(finalLedger, defaultEmptyLedger);
            var isPaymentReturn = IsPaymentReturnTransaction(transaction);
            if (isPaymentReturn)
            {
                if (TryResolvePaymentReturnTreatment(
                        transaction, allTransactionsById,
                        requestedBankLedger, out var paymentReturnCounterpartLedger,
                        out var paymentReturnDetail))
                {
                    // A return reverses the original Bank movement. The current Bank
                    // Statement debit/credit still determines Payment vs Receipt; only
                    // the opposite Ledger is inherited from the original transaction.
                    voucherType = transaction.Credit > 0m ? "Receipt" : "Payment";
                    requestedCounterpartLedger = paymentReturnCounterpartLedger;
                    if (!transaction.Mode.Equals(voucherType, StringComparison.OrdinalIgnoreCase) ||
                        !TallyMasterNamesEquivalent(transaction.LedgerName, paymentReturnCounterpartLedger))
                    {
                        transaction.Mode = voucherType;
                        transaction.LedgerName = paymentReturnCounterpartLedger;
                        transaction.MappingStatus = "Mapped";
                        _bankingRepository.SaveTransaction(transaction);
                        result.TreatmentCorrectedTransactions++;
                    }
                    if (paymentReturnDetail.Length > 0 &&
                        !result.TreatmentDetails.Contains(paymentReturnDetail, StringComparer.OrdinalIgnoreCase))
                        result.TreatmentDetails.Add(paymentReturnDetail);
                }
                else if (requestedCounterpartLedger.Length == 0 ||
                         TallyMasterNamesEquivalent(requestedCounterpartLedger, requestedBankLedger))
                {
                    result.TreatmentRequiredTransactions++;
                    result.TreatmentDetails.Add(
                        $"{transaction.TransactionDate:dd-MMM-yyyy} | PAYMENT RETURN | {transaction.Narration} | " +
                        "Original payment counterpart could not be identified safely. The Bank Ledger will not be used as its own counterpart.");
                    continue;
                }
            }
            else if (TryResolveInterbankTreatment(
                         transaction, accounts, allTransactionsById, transferByTransactionId,
                         out var transferCounterpartLedger))
            {
                voucherType = "Contra";
                requestedCounterpartLedger = transferCounterpartLedger;
                if (!transaction.Mode.Equals("Contra", StringComparison.OrdinalIgnoreCase) ||
                    !TallyMasterNamesEquivalent(transaction.LedgerName, transferCounterpartLedger))
                {
                    transaction.Mode = "Contra";
                    transaction.LedgerName = transferCounterpartLedger;
                    transaction.MappingStatus = "Mapped";
                    _bankingRepository.SaveTransaction(transaction);
                    result.TreatmentCorrectedTransactions++;
                }
            }

            var bankLedger = ResolveTallyLedgerName(
                requestedBankLedger, liveLedgerNameIndex, batchLedgerCanonicalNames);
            var counterpartLedger = ResolveTallyLedgerName(
                requestedCounterpartLedger, liveLedgerNameIndex, batchLedgerCanonicalNames);
            RememberEquivalentTallyReuse(
                reusedExistingTallyLedgerNames, requestedBankLedger, bankLedger);
            RememberEquivalentTallyReuse(
                reusedExistingTallyLedgerNames, requestedCounterpartLedger, counterpartLedger);

            // A Bank voucher can never debit and credit the same Ledger. Payment
            // Returns are resolved first, then genuine inter-bank transfers. If neither
            // system treatment can resolve a safe opposite Ledger, skip only this row and
            // surface Treatment Required instead of invalidating the whole month.
            if (TallyMasterNamesEquivalent(bankLedger, counterpartLedger))
            {
                result.TreatmentRequiredTransactions++;
                result.TreatmentDetails.Add(
                    $"{transaction.TransactionDate:dd-MMM-yyyy} | {transaction.Narration} | " +
                    $"Bank and counterpart both resolve to '{bankLedger}'. Review Payment Return / Ledger Mapping treatment.");
                continue;
            }
            if (bankLedger.Length == 0)
            {
                result.MissingLedgerTransactions++;
                continue;
            }

            var amount = transaction.Amount;
            if (amount <= 0)
            {
                result.UnsupportedTransactions++;
                continue;
            }

            var sourceKey = TallyVoucherFingerprint.BuildSourceKey(
                client.Id,
                client.FinancialYear,
                transaction.TransactionId,
                transaction.Id);

            // Fix74 strict Bank identity: include date/source/Voucher Type/BANK ledger/
            // amount/reference/narration. Only the selected top/counterpart Ledger is
            // excluded from first-pass duplicate identity; it is compared as the desired
            // treatment after the physical Bank transaction has been identified.
            var contentHash = TallyVoucherFingerprint.BuildBankSourceIdentityHash(
                transaction.TransactionDate,
                sourceKey,
                voucherType,
                bankLedger,
                amount,
                transaction.ReferenceNumber,
                transaction.Narration);

            var sourceWasPreviouslyImported = importedBankSourceKeys.Contains(sourceKey);
            var sourceReference = transaction.ReferenceNumber.Trim();

            // Fix74 MASTER-FIRST: this pass prepares only the desired Bank voucher intent.
            // No duplicate/ALTER/CREATE classification is allowed until all referenced
            // Ledger masters have been live-verified, rebound by Tally MASTERID, and the
            // exact current Tally Ledger names are available. This prevents a changed or
            // newly-created Ledger from making an existing Voucher look like a CREATE.
            ready.Add(new PreparedBankVoucher(
                transaction,
                voucherType,
                bankLedger,
                counterpartLedger,
                sourceKey,
                contentHash,
                transaction.ReferenceNumber.Trim(),
                string.Empty,
                string.Empty));
        }

        result.ReadyTransactions = ready.Count;
        result.ReadyAlterTransactions = ready.Count(item => item.ExistingTallyMasterId.Trim().Length > 0);
        result.ReadyCreateTransactions = result.ReadyTransactions - result.ReadyAlterTransactions;

        if (ready.Count == 0)
        {
            result.Status = BuildSummary(result, imported: false);
            return result;
        }

        var bankAccounts = accounts.Values.ToArray();
        var bankAccountIndex = bankAccounts
            .Where(account => account.AuthorityName.Trim().Length > 0)
            .GroupBy(account => BuildTallyMasterNameKey(account.AuthorityName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var masterLedgerCandidates = masterRecords
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
            .GroupBy(master => BuildTallyMasterNameKey(master.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var masterLedgerIndex = masterLedgerCandidates
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value
                    .OrderByDescending(master => master.Source.Contains("Tally", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(master => master.PersistentId)
                    .First(),
                StringComparer.OrdinalIgnoreCase);

        // Fix74 MASTER-FIRST: Live Tally/master authority is finalized BEFORE any Bank
        // transaction duplicate/ALTER/CREATE decision. The first pass above contains only
        // logical voucher intents. We create/rebind genuinely missing Masters, capture their
        // Tally MASTERIDs, and only then classify transactions against live Voucher authority.

        var ambiguousLocalLedgerDuplicates = ready
            .SelectMany(voucher => new[] { voucher.BankLedger, voucher.CounterpartLedger })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !TryResolveExistingTallyMasterName(name, liveLedgerNameIndex, out _))
            .Select(name => new { Name = name, Key = BuildTallyMasterNameKey(name) })
            .Where(item => item.Key.Length > 0 &&
                           masterLedgerCandidates.TryGetValue(item.Key, out var candidates) &&
                           candidates.Length > 1)
            .Select(item => item.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ambiguousLocalLedgerDuplicates.Length > 0)
        {
            result.TallyErrors = 1;
            result.LineError =
                "Duplicate local Ledger Master review is required before Tally CREATE: " +
                string.Join(", ", ambiguousLocalLedgerDuplicates);
            result.UnverifiedAfterImport = ready.Count;
            result.Status =
                "Ledger preflight stopped before any master or voucher was sent. " +
                "Resolve the active same-type duplicate Ledger Master(s) in Master Centre using Edit / Update Manual, Archive or Delete as applicable. " +
                "Tally/imported Masters remain protected. " + result.LineError;
            return result;
        }

        var requiredLedgers = BuildRequiredLedgerSpecs(
            ready, bankAccountIndex, masterLedgerIndex, ledgerGroupHints);

        // A Bank Account record and a user-confirmed Ledger Mapping + Group are both
        // app-side Master authority. Persist those authorities locally before CREATE
        // so they participate in Master Centre, Cash/Journal selectors and duplicate
        // review even when an older stale Tally row was just archived by live sync.
        MaterializeBankAuthorityMasters(client, requiredLedgers, masterLedgerCandidates);

        var missingLedgers = requiredLedgers
            .Where(item => !TryResolveExistingTallyMasterName(
                item.Name, liveLedgerNameIndex, out _))
            .ToArray();

        // Master generation is local/manual-create only. A Ledger whose app Master
        // is already Tally-authoritative is never sent back to Tally as CREATE.
        // Likewise, a transaction-only inferred name must first exist as a local
        // Master before the Hub is allowed to create it in Tally.
        var nonCreatableMissingLedgers = missingLedgers
            .Where(item => !item.CanCreateFromLocalMaster)
            .ToArray();
        if (nonCreatableMissingLedgers.Length > 0)
        {
            result.TallyErrors = 1;
            result.LineError =
                "Required Ledger(s) are not live in Tally and are not eligible for local/manual CREATE: " +
                string.Join(", ", nonCreatableMissingLedgers.Select(item =>
                    item.LocalSource.Length > 0
                        ? $"{item.Name} [{item.LocalSource}]"
                        : $"{item.Name} [no local Master]"));
            result.UnverifiedAfterImport = ready.Count;
            var emptyLedgerNeedsManualMaster = nonCreatableMissingLedgers.Any(item =>
                TallyMasterIdentity.Equivalent(item.Name, defaultEmptyLedger));
            var emptyLedgerGuidance = emptyLedgerNeedsManualMaster
                ? " Open Banking > Bank Statement > Empty Ledger Export and select an existing Master or create a Manual Master first."
                : string.Empty;
            result.Status =
                "Ledger preflight stopped the Bank voucher export before any master or voucher was sent. " +
                "Tally-source Masters are never recreated; only local/manual Masters can be created. " +
                result.LineError + emptyLedgerGuidance;
            return result;
        }

        var createdManualGroupNames = new List<string>();

        if (missingLedgers.Length > 0)
        {
            // A Ledger master can only be created under an existing Tally Group.
            // Verify every PARENT against the live ERP 9 Group collection first so
            // the Hub never sends a master that Tally cannot classify.
            IReadOnlySet<string> liveGroups;
            if (usePreSynchronizedVoucherAuthority)
            {
                liveGroups = masterRecords.Where(master => master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) &&
                                                           MasterAuthority.IsTallySource(master.Source) && !master.IsArchived)
                    .Select(master => master.Name.Trim()).Where(name => name.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            else
                liveGroups = await _tallyHttpService.ExportGroupNamesAsync(company, cancellationToken);
            var liveGroupNameIndex = BuildTallyMasterNameIndex(liveGroups);
            var missingParentGroups = missingLedgers
                .Select(item => item.Parent.Trim())
                .Where(parent => parent.Length > 0 &&
                                 !TryResolveExistingTallyMasterName(
                                     parent, liveGroupNameIndex, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (missingParentGroups.Length > 0)
            {
                var groupPlan = BuildMissingManualGroupPlan(
                    missingParentGroups, masterRecords, liveGroupNameIndex);
                if (groupPlan.UnresolvedGroups.Count > 0)
                {
                    result.TallyErrors = 1;
                    result.LineError =
                        "Required Tally Ledger Group(s) do not exist and no eligible local/non-Tally Group chain is available: " +
                        string.Join(", ", groupPlan.UnresolvedGroups);
                    result.UnverifiedAfterImport = ready.Count;
                    result.Status =
                        "Ledger preflight stopped the Bank voucher export before any master or voucher was sent. " +
                        "Only local/non-Tally Groups may be CREATEd automatically; Tally/imported/system Groups are never recreated. " +
                        result.LineError;
                    return result;
                }

                if (groupPlan.GroupsToCreate.Count > 0)
                {
                    var groupXml = TallyErp9ManualGroupImport.Build(company.Name, groupPlan.GroupsToCreate);
                    TallyErp9ManualGroupImport.Validate(groupXml, company.Name, groupPlan.GroupsToCreate);
                    var groupResponseText = await _tallyHttpService.ImportXmlAsync(groupXml, cancellationToken);
                    var groupResponse = TallyHttpService.ParseImportResponse(groupResponseText);
                    result.GroupResponsePath = SaveResponseLog(
                        client, company, fromDate, toDate, groupXml, groupResponseText, "bank-ledger-groups");

                    if (groupResponse.Errors > 0 || groupResponse.Exceptions > 0 ||
                        !string.IsNullOrWhiteSpace(groupResponse.LineError))
                    {
                        result.TallyErrors = Math.Max(1, groupResponse.Errors + groupResponse.Exceptions);
                        result.LineError = groupResponse.LineError.Length > 0
                            ? groupResponse.LineError
                            : $"Tally rejected the required local Group stage. Created={groupResponse.Created}; " +
                              $"Ignored={groupResponse.Ignored}; Exceptions={groupResponse.Exceptions}; Errors={groupResponse.Errors}.";
                        result.UnverifiedAfterImport = ready.Count;
                        result.Status =
                            "Local Group preflight stopped the Bank voucher export before any Ledger/voucher was sent. " +
                            result.LineError;
                        return result;
                    }

                    // Fix111 ERP9 proof-after-write barrier: never infer that a Group now
                    // exists merely because the import response did not report an error.
                    // Wait for that response, then reread the linked ERP9 Group collection
                    // and prove the parent exists before any dependent Ledger CREATE.
                    if (_tallyHttpService.IsLocalErp9Runtime || !usePreSynchronizedVoucherAuthority)
                    {
                        liveGroups = await _tallyHttpService.ExportGroupNamesAsync(company, cancellationToken);
                    }
                    else
                    {
                        liveGroups = liveGroups.Concat(groupPlan.GroupsToCreate.Select(group => group.Name))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    }
                    liveGroupNameIndex = BuildTallyMasterNameIndex(liveGroups);
                    var stillMissingGroups = missingParentGroups
                        .Where(parent => !TryResolveExistingTallyMasterName(parent, liveGroupNameIndex, out _))
                        .ToArray();
                    if (stillMissingGroups.Length > 0)
                    {
                        result.TallyErrors = 1;
                        result.LineError =
                            "Tally returned success for the Group request, but the required Group(s) were not present on the fresh ERP9 reread: " +
                            string.Join(", ", stillMissingGroups);
                        result.UnverifiedAfterImport = ready.Count;
                        result.Status =
                            "Local Group live-verification stopped the Bank voucher export before any Ledger/voucher was sent. " +
                            result.LineError;
                        return result;
                    }

                    createdManualGroupNames.AddRange(groupPlan.GroupsToCreate.Select(group => group.Name.Trim()));
                }
            }

            missingLedgers = missingLedgers
                .Select(item => item with
                {
                    Parent = ResolveExistingTallyLedgerName(item.Parent, liveGroupNameIndex)
                })
                .ToArray();

            var masterXml = BuildErp9LedgerCreateXml(company.Name, missingLedgers);
            var expectedMasterParents = missingLedgers
                .GroupBy(item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().Parent.Trim(),
                    StringComparer.OrdinalIgnoreCase);
            TallyBankXmlContractValidator.ValidateLedgerMasterImport(
                masterXml, company.Name, expectedMasterParents);

            var masterResponseText = await _tallyHttpService.ImportXmlAsync(masterXml, cancellationToken);
            var masterResponse = TallyHttpService.ParseImportResponse(masterResponseText);
            result.MasterResponsePath = SaveResponseLog(
                client, company, fromDate, toDate, masterXml, masterResponseText, "bank-ledger-masters");

            if (masterResponse.Errors > 0 || masterResponse.Exceptions > 0 ||
                !string.IsNullOrWhiteSpace(masterResponse.LineError))
            {
                result.TallyErrors = Math.Max(1, masterResponse.Errors + masterResponse.Exceptions);
                result.LineError = masterResponse.LineError.Length > 0
                    ? masterResponse.LineError
                    : $"Tally rejected the required Ledger Master stage. Created={masterResponse.Created}; " +
                      $"Ignored={masterResponse.Ignored}; Exceptions={masterResponse.Exceptions}; Errors={masterResponse.Errors}.";
                result.UnverifiedAfterImport = ready.Count;
                result.Status =
                    "Ledger preflight stopped the Bank voucher export before any voucher was sent. " +
                    result.LineError;
                return result;
            }

            // Fix111 ERP9 proof-after-write barrier: a zero-error import response is not
            // enough authority for a dependent Voucher. After ERP9 returns, reread the
            // live Ledger collection and require every requested Ledger + parent to exist.
            if (_tallyHttpService.IsLocalErp9Runtime || !usePreSynchronizedVoucherAuthority)
            {
                liveLedgerParents = await _tallyHttpService.ExportLedgerParentsAsync(company, cancellationToken);
            }
            else
            {
                var mergedLedgers = new Dictionary<string,string>(liveLedgerParents, StringComparer.OrdinalIgnoreCase);
                foreach (var ledger in missingLedgers) mergedLedgers[ledger.Name] = ledger.Parent;
                liveLedgerParents = mergedLedgers;
            }
            liveLedgerNameIndex = BuildTallyMasterNameIndex(liveLedgerParents);
            var stillMissing = missingLedgers
                .Where(item => !TryResolveExistingTallyMasterName(
                    item.Name, liveLedgerNameIndex, out _))
                .Select(item => item.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (stillMissing.Length > 0)
            {
                result.TallyErrors = 1;
                result.LineError =
                    "Tally returned success for the Ledger request, but the required Ledger(s) were not present on the fresh ERP9 reread: " +
                    string.Join(", ", stillMissing);
                result.UnverifiedAfterImport = ready.Count;
                result.Status =
                    "Ledger live-verification stopped the Bank voucher export before any voucher was sent. " +
                    result.LineError;
                return result;
            }

            var wrongCreatedParents = missingLedgers
                .Select(item => new
                {
                    Item = item,
                    Resolved = TryResolveExistingTallyMasterName(
                        item.Name, liveLedgerNameIndex, out var exactName)
                        ? exactName
                        : string.Empty
                })
                .Where(entry => entry.Resolved.Length > 0 &&
                                liveLedgerParents.TryGetValue(entry.Resolved, out var liveParent) &&
                                !liveParent.Equals(entry.Item.Parent, StringComparison.OrdinalIgnoreCase))
                .Select(entry =>
                    $"{entry.Item.Name}: expected '{entry.Item.Parent}', " +
                    $"Tally reports '{liveLedgerParents[entry.Resolved]}' on '{entry.Resolved}'")
                .ToArray();
            if (wrongCreatedParents.Length > 0)
            {
                result.TallyErrors = 1;
                result.LineError =
                    "Tally created/returned Ledger parent mismatch: " +
                    string.Join(" | ", wrongCreatedParents);
                result.UnverifiedAfterImport = ready.Count;
                result.Status =
                    "Ledger preflight stopped the Bank voucher export before any voucher was sent. " +
                    result.LineError;
                return result;
            }
        }

        // Fix74 MASTERID barrier. If this pass mutated any Group/Ledger Masters, obtain
        // the Tally-assigned MASTERID/GUID/ALTERID immediately and synchronize that exact
        // authority back into AAH before a single transaction is classified. With no Master
        // mutation, the coordinator's pre-synchronized Master authority is reused locally.
        // Standalone/manual callers always refresh exact Master objects here as well; a
        // names-only Ledger/Group read is never sufficient transaction identity authority.
        if (!usePreSynchronizedVoucherAuthority || missingLedgers.Length > 0 || createdManualGroupNames.Count > 0)
        {
            try
            {
                var refreshedMasterXml = await _tallyHttpService.ExportMastersAsync(company, cancellationToken);
                var refreshedLedgerParents = await _tallyHttpService.ExportLedgerParentsAsync(company, cancellationToken);
                var refreshedGroupNames = await _tallyHttpService.ExportGroupNamesAsync(company, cancellationToken);
                _liveMasterSyncService.Synchronize(
                    refreshedMasterXml, client.Id, client.FinancialYear, company.Name,
                    refreshedLedgerParents, refreshedGroupNames, ledgerGroupOnlyAuthority: true);
                liveLedgerParents = refreshedLedgerParents;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result.TallyErrors = 1;
                result.UnverifiedAfterImport = ready.Count;
                result.LineError = "Master CREATE/ALTER was accepted, but Tally MASTERID rebind failed: " + exception.Message;
                result.Status = "MASTERID barrier stopped Bank transaction classification. " + result.LineError;
                return result;
            }
        }

        masterRecords = _masterRepository
            .Get(client.Id, client.FinancialYear, includeArchived: false)
            .Where(master => master.Name.Trim().Length > 0)
            .ToArray();

        var requiredMasterBindings = ready
            .SelectMany(item => new[] { item.BankLedger, item.CounterpartLedger })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var masterBindingErrors = new List<string>();
        foreach (var requiredName in requiredMasterBindings)
        {
            var candidates = masterRecords
                .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                 MasterAuthority.IsTallySource(master.Source) && !master.IsArchived &&
                                 TallyMasterIdentity.Equivalent(master.Name, requiredName) &&
                                 master.TallyMasterId.Trim().Length > 0)
                .GroupBy(master => master.TallyMasterId.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(master => master.TallyAlterId).First())
                .ToArray();
            if (candidates.Length != 1)
            {
                masterBindingErrors.Add(candidates.Length == 0
                    ? $"{requiredName}: no Tally Ledger MASTERID"
                    : $"{requiredName}: ambiguous Tally Ledger MASTERIDs [{string.Join(", ", candidates.Select(item => item.TallyMasterId))}]");
            }
        }
        if (masterBindingErrors.Count > 0)
        {
            result.TallyErrors = 1;
            result.UnverifiedAfterImport = ready.Count;
            result.LineError = "Master comparison is incomplete: " + string.Join(" | ", masterBindingErrors);
            result.Status = "MASTERID barrier stopped Bank duplicate/ALTER/CREATE classification. " + result.LineError;
            return result;
        }

        // Fix111 R1 MASTERID-FIRST reference authority. Name was used only to discover
        // a unique live binding above. From this point onward, every prepared Bank
        // Voucher carries the two Ledger MASTERIDs explicitly. Mutable/cleaned names
        // are no longer allowed to choose which live Tally Master will be addressed.
        string ResolveRequiredLedgerMasterId(string requiredName)
        {
            var ids = masterRecords
                .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                 MasterAuthority.IsTallySource(master.Source) && !master.IsArchived &&
                                 TallyMasterIdentity.Equivalent(master.Name, requiredName) &&
                                 master.TallyMasterId.Trim().Length > 0)
                .Select(master => master.TallyMasterId.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return ids.Length == 1 ? ids[0] : string.Empty;
        }

        bool IsFreshLiveLedgerMasterId(string? masterId)
        {
            masterId = (masterId ?? string.Empty).Trim();
            return masterId.Length > 0 && masterRecords.Any(master =>
                master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                MasterAuthority.IsTallySource(master.Source) && !master.IsArchived &&
                master.TallyMasterId.Trim().Equals(masterId, StringComparison.OrdinalIgnoreCase));
        }

        bool CanUseExistingAccountMasterId(BankAccountRecord account)
        {
            if (!IsFreshLiveLedgerMasterId(account.TallyLedgerMasterId)) return false;
            var verifiedName = account.TallyLedgerVerifiedName.Trim();
            // A deliberate user rename represents an independent new Bank Ledger request
            // (Fix77). In that one case the old ID must not override the new requested name.
            return verifiedName.Length == 0 || SameLedgerName(account.AuthorityName, verifiedName);
        }

        string ResolveBankLedgerMasterId(PreparedBankVoucher voucher)
        {
            // First authority is the BankAccount's already-bound Tally MASTERID, but only
            // when that ID is present in this fresh Master snapshot and the account has
            // not explicitly been renamed into a different independent Ledger request.
            // A stale/deleted ID never survives into Voucher XML; only then do we use
            // unique-name recovery.
            if (accounts.TryGetValue(voucher.Transaction.BankAccountId, out var account) &&
                CanUseExistingAccountMasterId(account))
                return account.TallyLedgerMasterId.Trim();
            return ResolveRequiredLedgerMasterId(voucher.BankLedger);
        }

        string ResolveCounterpartLedgerMasterId(string counterpartLedger)
        {
            // Contra/inter-bank counterpart accounts carry their own permanent MASTERID.
            // Prefer that ID before mapped-name recovery so both financial sides remain
            // stable even when their Tally display names contain hidden legacy characters.
            var boundAccountIds = accounts.Values
                .Where(account => CanUseExistingAccountMasterId(account) &&
                                  TallyMasterIdentity.Equivalent(account.AuthorityName, counterpartLedger))
                .Select(account => account.TallyLedgerMasterId.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (boundAccountIds.Length == 1) return boundAccountIds[0];
            return ResolveRequiredLedgerMasterId(counterpartLedger);
        }

        ready = ready
            .Select(voucher => voucher with
            {
                BankLedgerMasterId = ResolveBankLedgerMasterId(voucher),
                CounterpartLedgerMasterId = ResolveCounterpartLedgerMasterId(voucher.CounterpartLedger)
            })
            .ToList();

        var missingPreparedMasterIds = ready
            .SelectMany(voucher => new[]
            {
                (Name: voucher.BankLedger, MasterId: voucher.BankLedgerMasterId),
                (Name: voucher.CounterpartLedger, MasterId: voucher.CounterpartLedgerMasterId)
            })
            .Where(item => item.MasterId.Trim().Length == 0)
            .Select(item => item.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingPreparedMasterIds.Length > 0)
        {
            result.TallyErrors = 1;
            result.UnverifiedAfterImport = ready.Count;
            result.LineError = "MASTERID-first binding was not established for: " +
                               string.Join(", ", missingPreparedMasterIds);
            result.Status = "MASTERID barrier stopped Bank Voucher serialization. " + result.LineError;
            return result;
        }

        // Persist BankAccountId -> Tally Ledger MASTERID before Inter-Bank or Voucher logic
        // consumes the account. Once bound, MASTERID wins over every mutable Ledger name.
        foreach (var account in accounts.Values)
        {
            var liveBySavedId = account.TallyLedgerMasterId.Trim().Length == 0
                ? null
                : masterRecords.FirstOrDefault(master =>
                    master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                    MasterAuthority.IsTallySource(master.Source) && !master.IsArchived &&
                    master.TallyMasterId.Trim().Equals(account.TallyLedgerMasterId.Trim(), StringComparison.OrdinalIgnoreCase));

            // Fix77: if the Bank Account now names a different Ledger, the saved MASTERID
            // still belongs exclusively to the old Ledger. Detach it and resolve the new
            // name independently after Master CREATE; never overwrite the new name from ID.
            if (liveBySavedId is not null &&
                !SameLedgerName(liveBySavedId.Name, account.AuthorityName))
            {
                account.TallyLedgerMasterId = string.Empty;
                account.TallyLedgerGuid = string.Empty;
                account.TallyLedgerAlterId = 0;
                account.TallyLedgerVerifiedName = string.Empty;
                _bankingRepository.SaveAccount(account);
                liveBySavedId = null;
            }

            var resolved = liveBySavedId;
            if (resolved is null)
            {
                var nameMatches = masterRecords
                    .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                     MasterAuthority.IsTallySource(master.Source) && !master.IsArchived &&
                                     master.TallyMasterId.Trim().Length > 0 &&
                                     SameLedgerName(master.Name, account.AuthorityName))
                    .GroupBy(master => master.TallyMasterId.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(master => master.TallyAlterId).First())
                    .ToArray();
                if (nameMatches.Length == 1)
                {
                    // Fix111: ERP9 can lose/delete a previously bound Ledger outside AAH.
                    // After the fresh Master barrier and any required Master CREATE, the
                    // same Bank Account name may therefore legitimately own a NEW Tally
                    // MASTERID. Retire only the stale saved identity and rebind by the exact
                    // one-live-Master name; never carry the deleted MASTERID into a Voucher.
                    if (account.TallyLedgerMasterId.Trim().Length > 0 &&
                        !account.TallyLedgerMasterId.Trim().Equals(nameMatches[0].TallyMasterId.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        account.TallyLedgerMasterId = string.Empty;
                        account.TallyLedgerGuid = string.Empty;
                        account.TallyLedgerAlterId = 0;
                        account.TallyLedgerVerifiedName = string.Empty;
                        _bankingRepository.SaveAccount(account);
                    }
                    resolved = nameMatches[0];
                }
                else if (nameMatches.Length > 1)
                {
                    result.TallyErrors = 1;
                    result.LineError = $"Bank account '{account.AuthorityName}' matches multiple Tally Ledger MASTERIDs; automatic binding was blocked.";
                    result.Status = "MASTERID barrier stopped Bank transaction classification. " + result.LineError;
                    return result;
                }
                else if (account.TallyLedgerMasterId.Trim().Length > 0)
                {
                    result.TallyErrors = 1;
                    result.LineError = $"Bank account '{account.AuthorityName}' is bound to Tally Ledger MASTERID {account.TallyLedgerMasterId}, but that MASTERID is absent from the fresh ERP9 Master authority and no exact replacement Ledger exists.";
                    result.Status = "MASTERID barrier stopped Bank transaction classification. " + result.LineError;
                    return result;
                }
            }

            if (resolved is not null &&
                (!account.TallyLedgerMasterId.Equals(resolved.TallyMasterId, StringComparison.OrdinalIgnoreCase) ||
                 !account.TallyLedgerGuid.Equals(resolved.TallyGuid, StringComparison.OrdinalIgnoreCase) ||
                 account.TallyLedgerAlterId != resolved.TallyAlterId ||
                 !account.LedgerName.Equals(resolved.Name, StringComparison.Ordinal)))
            {
                account.TallyLedgerMasterId = resolved.TallyMasterId.Trim();
                account.TallyLedgerGuid = resolved.TallyGuid.Trim();
                account.TallyLedgerAlterId = resolved.TallyAlterId;
                account.TallyLedgerVerifiedName = resolved.Name;
                account.LedgerName = resolved.Name;
                _bankingRepository.SaveAccount(account);
            }
        }

        // Re-resolve every referenced Ledger from the live Tally collection after
        // master creation. Existing equivalent names are reused exactly as Tally stores
        // them; the Hub never creates/changes a duplicate master just to match spacing
        // or punctuation in a mapped Ledger name.
        liveLedgerNameIndex = BuildTallyMasterNameIndex(liveLedgerParents);
        ready = ready
            .Select(voucher => voucher with
            {
                BankLedger = ResolveExistingTallyLedgerName(voucher.BankLedger, liveLedgerNameIndex),
                CounterpartLedger = ResolveExistingTallyLedgerName(voucher.CounterpartLedger, liveLedgerNameIndex)
            })
            .ToList();

        var referencedLedgerNames = ready
            .SelectMany(item => new[] { item.BankLedger, item.CounterpartLedger })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var unresolvedLiveLedgers = referencedLedgerNames
            .Where(name => !liveLedgerParents.ContainsKey(name))
            .ToArray();
        if (unresolvedLiveLedgers.Length > 0)
        {
            result.TallyErrors = 1;
            result.LineError = "Live Tally ledger verification failed for: " +
                               string.Join(", ", unresolvedLiveLedgers);
            result.UnverifiedAfterImport = ready.Count;
            result.Status =
                "Ledger preflight stopped the Bank voucher export before any voucher was sent. " +
                result.LineError;
            return result;
        }

        if (liveLedgerParents.TryGetValue(UnmappedBankSuspenseLedger, out var suspenseParent) &&
            !suspenseParent.Equals("Suspense A/c", StringComparison.OrdinalIgnoreCase))
        {
            result.TallyErrors = 1;
            result.LineError =
                $"Ledger '{UnmappedBankSuspenseLedger}' exists in Tally under '{suspenseParent}', " +
                "but Bank unmapped rows require it under the Suspense A/c group.";
            result.UnverifiedAfterImport = ready.Count;
            result.Status =
                "Ledger preflight stopped the Bank voucher export before any voucher was sent. " +
                result.LineError;
            return result;
        }

        // Normalized/display master identity is only the matching authority. Voucher XML
        // must use the exact raw Ledger name returned by the current live Tally collection.
        // This is essential for legacy ERP 9 Ledgers containing trailing spaces/CR/LF.
        IReadOnlyList<TallyLedgerNameAuthority> liveLedgerAuthorities;
        if (usePreSynchronizedVoucherAuthority)
        {
            // Fix111 R1: pinned exact spelling is selected by MASTERID first. A cleaned
            // display-name match is no longer allowed to select an equivalent but
            // different live Master. Newly-created Masters that were not present in the
            // pinned baseline fall back to the just-synchronized MASTERID + current name.
            // Fix111 R2: ERP 9 exact Ledger execution authority lives in the pinned
            // ERP9 core-Master cache, not necessarily in the generic full snapshot.
            // Read that exact cache first so MASTERID -> raw Ledger spelling survives
            // through XML serialization (including legacy CR/LF/trailing spaces).
            _tallyHttpService.TryGetPinnedLedgerNameAuthorities(company, out var pinnedExactLedgers);
            liveLedgerAuthorities = liveLedgerParents.Select(pair =>
            {
                var ids = masterRecords
                    .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                     MasterAuthority.IsTallySource(master.Source) && !master.IsArchived &&
                                     TallyMasterIdentity.Equivalent(master.Name, pair.Key) &&
                                     master.TallyMasterId.Trim().Length > 0)
                    .Select(master => master.TallyMasterId.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var masterId = ids.Length == 1 ? ids[0] : string.Empty;
                var pinned = masterId.Length == 0
                    ? null
                    : pinnedExactLedgers.FirstOrDefault(item =>
                        item.MasterId.Trim().Equals(masterId, StringComparison.OrdinalIgnoreCase));
                return pinned ?? new TallyLedgerNameAuthority
                {
                    ExactName = pair.Key, DisplayName = pair.Key, Parent = pair.Value, MasterId = masterId
                };
            }).ToArray();
        }
        else
            liveLedgerAuthorities = await _tallyHttpService.ExportLedgerNameAuthoritiesAsync(company, cancellationToken);

        var exactLedgerAuthorityByMasterId = BuildExactTallyLedgerAuthorityByMasterId(liveLedgerAuthorities);
        var unresolvedExactAuthorities = ready
            .SelectMany(voucher => new[]
            {
                (Name: voucher.BankLedger, MasterId: voucher.BankLedgerMasterId),
                (Name: voucher.CounterpartLedger, MasterId: voucher.CounterpartLedgerMasterId)
            })
            .Where(item => !TryResolveExactTallyLedgerAuthorityByMasterId(
                item.MasterId, exactLedgerAuthorityByMasterId, out _))
            .Select(item => $"{item.Name} [MASTERID {item.MasterId}]")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unresolvedExactAuthorities.Length > 0)
        {
            result.TallyErrors = 1;
            result.LineError = "Exact live Tally Ledger authority could not be captured by MASTERID for: " +
                               string.Join(", ", unresolvedExactAuthorities);
            result.UnverifiedAfterImport = ready.Count;
            result.Status =
                "MASTERID-first Ledger preflight stopped the Bank voucher export before any voucher was sent. " +
                result.LineError;
            return result;
        }

        ready = ready
            .Select(voucher => voucher with
            {
                BankLedger = ResolveExactTallyLedgerAuthorityByMasterId(
                    voucher.BankLedgerMasterId, voucher.BankLedger, exactLedgerAuthorityByMasterId),
                CounterpartLedger = ResolveExactTallyLedgerAuthorityByMasterId(
                    voucher.CounterpartLedgerMasterId, voucher.CounterpartLedger, exactLedgerAuthorityByMasterId)
            })
            .ToList();

        var bankMasterSyncNote =
            $"Ledger masters live-verified MASTERID-first and exact Tally XML authority captured {referencedLedgerNames.Length:N0}/{referencedLedgerNames.Length:N0}.";
        if (createdManualGroupNames.Count > 0)
            bankMasterSyncNote += " Local Group(s) created and live-verified before dependent Ledgers: " +
                                  string.Join(", ", createdManualGroupNames.Distinct(StringComparer.OrdinalIgnoreCase)) + ".";
        if (reusedExistingTallyLedgerNames.Count > 0)
        {
            bankMasterSyncNote += " Existing Tally master(s) reused without CREATE: " +
                string.Join(", ", reusedExistingTallyLedgerNames
                    .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => $"{item.Key} -> {item.Value}")) + ".";
        }

        // Fix74 authoritative transaction classification happens only AFTER the Master
        // barrier above. Keep a versioned previous -> desired lifecycle for every Bank
        // source, and never allow an accepted/pending source to fall back into CREATE.
        var preparedIntents = ready.ToArray();
        ready = new List<PreparedBankVoucher>();
        batchFingerprints.Clear();
        reservedLiveMasterIds.Clear();
        claimedImportedLiveIdentityMasters.Clear();
        duplicateDeletePlans.Clear();

        var tallyLedgerMasterByKey = masterRecords
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                             MasterAuthority.IsTallySource(master.Source) && !master.IsArchived &&
                             master.TallyMasterId.Trim().Length > 0)
            .GroupBy(master => TallyMasterIdentity.Key(master.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .GroupBy(master => master.TallyMasterId.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(masterIdGroup => masterIdGroup.OrderByDescending(master => master.TallyAlterId).First())
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        string ResolveLedgerMasterId(string ledgerName)
        {
            var key = TallyMasterIdentity.Key(ledgerName);
            if (key.Length == 0 || !tallyLedgerMasterByKey.TryGetValue(key, out var candidates) || candidates.Length != 1)
                return string.Empty;
            return candidates[0].TallyMasterId.Trim();
        }

        string BuildDesiredBankVersion(PreparedBankVoucher item)
        {
            // Fix111 R1: carry the already-proven MASTERIDs into Voucher versioning.
            // Name re-resolution is legacy fallback only and must never outrank a bound ID.
            var bankMid = item.BankLedgerMasterId.Trim().Length > 0
                ? item.BankLedgerMasterId.Trim()
                : ResolveLedgerMasterId(item.BankLedger);
            var counterpartMid = item.CounterpartLedgerMasterId.Trim().Length > 0
                ? item.CounterpartLedgerMasterId.Trim()
                : ResolveLedgerMasterId(item.CounterpartLedger);
            string debitMid;
            string creditMid;
            if (item.VoucherType.Equals("Payment", StringComparison.OrdinalIgnoreCase))
            {
                debitMid = counterpartMid; creditMid = bankMid;
            }
            else if (item.VoucherType.Equals("Receipt", StringComparison.OrdinalIgnoreCase))
            {
                debitMid = bankMid; creditMid = counterpartMid;
            }
            else if (item.VoucherType.Equals("Contra", StringComparison.OrdinalIgnoreCase) && item.Transaction.Credit > 0)
            {
                debitMid = bankMid; creditMid = counterpartMid;
            }
            else
            {
                debitMid = counterpartMid; creditMid = bankMid;
            }

            return string.Join("|", new[]
            {
                $"DATE={item.Transaction.TransactionDate:yyyyMMdd}",
                $"TYPE={NormalizeLiveMatchText(item.VoucherType).ToUpperInvariant()}",
                $"BANKMID={bankMid}",
                $"COUNTERMID={counterpartMid}",
                $"AMOUNT={item.Transaction.Amount.ToString("0.00", CultureInfo.InvariantCulture)}",
                $"REF={NormalizeLiveMatchText(item.ReferenceNumber).ToUpperInvariant()}",
                $"NARR={NormalizeLiveMatchText(item.Transaction.Narration).ToUpperInvariant()}",
                $"DRMID={debitMid}",
                $"CRMID={creditMid}"
            });
        }

        static string ReadVersionField(string? version, string field)
        {
            var prefix = field.Trim().ToUpperInvariant() + "=";
            foreach (var part in (version ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return part[prefix.Length..].Trim();
            }
            return string.Empty;
        }

        string ResolveLedgerNameByMasterId(string masterId)
        {
            var id = (masterId ?? string.Empty).Trim();
            if (id.Length == 0) return string.Empty;
            return masterRecords
                .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                 master.TallyMasterId.Trim().Equals(id, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(master => master.TallyAlterId)
                .Select(master => master.Name.Trim())
                .FirstOrDefault() ?? string.Empty;
        }

        static bool CanAdoptExternalBankTreatment(string previousVersion, string liveVersion)
        {
            // Voucher MASTERID ownership is checked by the caller. For an external Tally
            // treatment edit we keep the immutable physical evidence strict and allow only
            // TYPE / counterpart / DR-CR treatment to change. BANKMID here is the saved
            // previous Bank MASTERID, so a separate new Bank Master does not steal identity.
            foreach (var field in new[] { "DATE", "BANKMID", "AMOUNT", "REF", "NARR" })
            {
                if (!ReadVersionField(previousVersion, field).Equals(
                        ReadVersionField(liveVersion, field), StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return ReadVersionField(previousVersion, "BANKMID").Length > 0;
        }

        static bool IsOnlyBankMasterMigration(string previousVersion, string desiredVersion)
        {
            // Fix86 reconciliation classification. These rows are not generic external-delete
            // conflicts: their source date/type/amount/reference/narration/counterpart are
            // unchanged and the only desired movement is the Bank Ledger MASTERID itself.
            // Keep them visible as Bank-master migration reconciliation until an explicit
            // reconciliation action restores/rebinds the missing physical Tally Voucher.
            var oldBankMid = ReadVersionField(previousVersion, "BANKMID");
            var newBankMid = ReadVersionField(desiredVersion, "BANKMID");
            if (oldBankMid.Length == 0 || newBankMid.Length == 0 ||
                oldBankMid.Equals(newBankMid, StringComparison.OrdinalIgnoreCase)) return false;

            foreach (var field in new[] { "DATE", "TYPE", "COUNTERMID", "AMOUNT", "REF", "NARR" })
            {
                if (!ReadVersionField(previousVersion, field).Equals(
                        ReadVersionField(desiredVersion, field), StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            static string NormalizeSide(string value, string oldMid, string newMid) =>
                string.Join(",", (value ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(mid => mid.Equals(oldMid, StringComparison.OrdinalIgnoreCase) ? newMid : mid)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(mid => mid, StringComparer.OrdinalIgnoreCase));

            var previousDebit = NormalizeSide(ReadVersionField(previousVersion, "DRMID"), oldBankMid, newBankMid);
            var previousCredit = NormalizeSide(ReadVersionField(previousVersion, "CRMID"), oldBankMid, newBankMid);
            var desiredDebit = NormalizeSide(ReadVersionField(desiredVersion, "DRMID"), string.Empty, string.Empty);
            var desiredCredit = NormalizeSide(ReadVersionField(desiredVersion, "CRMID"), string.Empty, string.Empty);
            return previousDebit.Equals(desiredDebit, StringComparison.OrdinalIgnoreCase) &&
                   previousCredit.Equals(desiredCredit, StringComparison.OrdinalIgnoreCase);
        }

        string ResolveLiveCounterpart(TallyVoucherIndexRecord live, string bankMasterId)
        {
            var bankId = (bankMasterId ?? string.Empty).Trim();
            var names = SplitLiveLedgerSet(live.DebitLedgers)
                .Concat(SplitLiveLedgerSet(live.CreditLedgers))
                .Where(name => name.Trim().Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(name =>
                {
                    var mid = ResolveLedgerMasterId(name);
                    return bankId.Length == 0 || !mid.Equals(bankId, StringComparison.OrdinalIgnoreCase);
                })
                .ToArray();
            if (names.Length == 1) return names[0].Trim();
            var party = live.PartyLedger.Trim();
            if (party.Length > 0 && names.Contains(party, StringComparer.OrdinalIgnoreCase)) return party;
            return names.FirstOrDefault() ?? string.Empty;
        }

        string BuildLiveBankVersion(TallyVoucherIndexRecord live, string expectedBankMasterId)
        {
            var debitIds = SplitLiveLedgerSet(live.DebitLedgers)
                .Select(name => ResolveLedgerMasterId(name))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var creditIds = SplitLiveLedgerSet(live.CreditLedgers)
                .Select(name => ResolveLedgerMasterId(name))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Fix77: live previous-state verification must use the Bank MASTERID that
            // belonged to that saved previous version. When the Bank Account moves to a
            // newly-created independent Ledger, the existing Voucher still contains the
            // OLD Bank MASTERID until ALTER succeeds. Never substitute the desired/new
            // Bank MASTERID while proving the previous version.
            var requestedBankMid = (expectedBankMasterId ?? string.Empty).Trim();
            var allLiveIds = debitIds.Concat(creditIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var bankMid = requestedBankMid.Length > 0 &&
                          allLiveIds.Contains(requestedBankMid, StringComparer.OrdinalIgnoreCase)
                ? requestedBankMid
                : string.Empty;
            var liveCounterpartIds = bankMid.Length == 0
                ? allLiveIds
                : allLiveIds
                    .Where(masterId => !masterId.Equals(bankMid, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            var counterpartMid = liveCounterpartIds.Length == 1 ? liveCounterpartIds[0] : string.Empty;
            return string.Join("|", new[]
            {
                $"DATE={live.VoucherDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty}",
                $"TYPE={NormalizeLiveMatchText(live.VoucherType).ToUpperInvariant()}",
                $"BANKMID={bankMid}",
                $"COUNTERMID={counterpartMid}",
                $"AMOUNT={Math.Abs(live.Amount).ToString("0.00", CultureInfo.InvariantCulture)}",
                $"REF={NormalizeLiveMatchText(live.Reference).ToUpperInvariant()}",
                $"NARR={NormalizeLiveMatchText(live.Narration).ToUpperInvariant()}",
                $"DRMID={string.Join(",", debitIds)}",
                $"CRMID={string.Join(",", creditIds)}"
            });
        }

        long LiveAlterId(TallyVoucherIndexRecord? live) =>
            live is not null && long.TryParse(live.AlterId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var alterId)
                ? alterId
                : 0L;

        void ConfirmBankVersion(PreparedBankVoucher item, TallyVoucherIndexRecord live, string response)
        {
            _tallyRepository.ConfirmSourceBindingVersion(
                client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher",
                item.SourceKey, item.SourceKey, live.MasterId.Trim(), live.Guid.Trim(),
                item.ContentHash, BuildDesiredBankVersion(item), LiveAlterId(live), response);
            _bankingRepository.UpsertBankTallyExportLink(
                client.Id, client.FinancialYear, item.Transaction.Id, item.SourceKey, company.CompanyKey,
                live.MasterId.Trim(), item.VoucherType, item.BankLedger, item.CounterpartLedger);
        }

        foreach (var intent in preparedIntents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transaction = intent.Transaction;
            var voucherType = intent.VoucherType;
            var bankLedger = intent.BankLedger;
            var counterpartLedger = intent.CounterpartLedger;
            var sourceKey = intent.SourceKey;
            var contentHash = intent.ContentHash;
            var sourceReference = intent.ReferenceNumber;
            var desiredVersion = BuildDesiredBankVersion(intent);
            var sourceWasPreviouslyImported = importedBankSourceKeys.Contains(sourceKey);
            var versionState = _tallyRepository.GetSourceBindingVersionState(
                client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher", sourceKey);
            var explicitRestoreRequested = versionState?.State.Equals("RestoreRequested", StringComparison.OrdinalIgnoreCase) == true;
            var missingBoundNeedsResolution = false;
            var missingBoundMasterId = string.Empty;
            var missingBindingState = versionState?.State ?? string.Empty;
            // Fix111 R2: CreatePending is a local pre-send staging state, not proof
            // that Tally accepted a Voucher. After this run's fresh complete live preflight,
            // a CreatePending source with no strict live match may be staged/sent again.
            // Accepted/unknown-outcome states remain fail-closed until MASTERID recovery.
            var holdCreateUntilMasterIdRecovered = versionState is not null &&
                versionState.MasterId.Trim().Length == 0 &&
                versionState.State is "AcceptedPending" or "AcceptedAwaitingMasterId" or
                    "AcceptedAwaitingVerification" or "NeedsReview" or "ModifyPending" or "StaleMissingLive" or "RebindRequired";

            var durableBinding = _tallyRepository.GetSourceBinding(
                client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher", sourceKey);
            if (durableBinding is not null)
            {
                var (boundMasterId, _, lastHash, state) = durableBinding.Value;
                if (boundMasterId.Length > 0)
                {
                    var liveBound = liveBefore.FirstOrDefault(item =>
                        item.MasterId.Trim().Equals(boundMasterId, StringComparison.OrdinalIgnoreCase));
                    if (liveBound is not null)
                    {
                        // Fix74 MASTERID-first modification contract:
                        //   1. MASTERID locates the exact Tally object.
                        //   2. If live already equals desired, confirm/skip.
                        //   3. Otherwise live MUST equal AAH's saved previous verified version.
                        //   4. Only then stage previous -> desired as ALTER.
                        // Never compare the live object to the *current* Bank text first;
                        // reference/narration/type/date/amount/top-Ledger are all allowed to
                        // be part of the desired new version once identity is MASTERID-bound.
                        var desiredBankMid = ReadVersionField(desiredVersion, "BANKMID");
                        var liveDesiredVersion = BuildLiveBankVersion(liveBound, desiredBankMid);
                        if (liveDesiredVersion.Equals(desiredVersion, StringComparison.Ordinal))
                        {
                            ConfirmBankVersion(intent, liveBound,
                                "Fresh live MASTERID already equals the complete current AAH Bank version after Master-first comparison.");
                            result.ExactDuplicatesSkipped++;
                            continue;
                        }

                        var previousVersion = versionState?.VerifiedVersion ?? string.Empty;
                        if (previousVersion.Length > 0)
                        {
                            var previousBankMid = ReadVersionField(previousVersion, "BANKMID");
                            var livePreviousVersion = BuildLiveBankVersion(liveBound, previousBankMid);
                            if (!livePreviousVersion.Equals(previousVersion, StringComparison.Ordinal))
                            {
                                // Fix80: when the exact bound Voucher MASTERID changed in Tally
                                // but still proves the same physical Bank transaction and saved
                                // previous Bank MASTERID, adopt that external treatment into AAH
                                // instead of pushing the stale AAH Contra back. This handles the
                                // strict Contra->Payment/Receipt case even when the Bank Account
                                // has meanwhile moved to a new independent Ledger MASTERID.
                                if (CanAdoptExternalBankTreatment(previousVersion, livePreviousVersion))
                                {
                                    var previousBankName = ResolveLedgerNameByMasterId(previousBankMid);
                                    var liveCounterpart = ResolveLiveCounterpart(liveBound, previousBankMid);
                                    _bankingRepository.ResetChangedTallyTransferMatches(
                                        client.Id, client.FinancialYear, [boundMasterId]);
                                    var adopted = _bankingRepository.AdoptBoundTallyVoucherTreatment(
                                        client.Id, client.FinancialYear, company.CompanyKey,
                                        new TallyConvertedBankTransaction
                                        {
                                            TallyMasterId = boundMasterId,
                                            TallyGuid = liveBound.Guid.Trim(),
                                            TallyAlterId = liveBound.AlterId.Trim(),
                                            VoucherType = liveBound.VoucherType.Trim(),
                                            VoucherNumber = liveBound.VoucherNumber.Trim(),
                                            BankLedgerName = previousBankName,
                                            BankLedgerMasterId = previousBankMid,
                                            TransactionDate = liveBound.VoucherDate ?? transaction.TransactionDate,
                                            Narration = liveBound.Narration.Trim(),
                                            ReferenceNumber = liveBound.Reference.Trim()
                                        },
                                        liveCounterpart);
                                    if (adopted > 0)
                                    {
                                        _tallyRepository.ConfirmSourceBindingVersion(
                                            client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher",
                                            sourceKey, sourceKey, boundMasterId, liveBound.Guid.Trim(), lastHash,
                                            livePreviousVersion, LiveAlterId(liveBound),
                                            "Fix80 adopted verified external Tally Bank treatment on the same Voucher MASTERID; no write-back performed.");
                                        result.TreatmentCorrectedTransactions += adopted;
                                        result.ExactDuplicatesSkipped++;
                                        continue;
                                    }
                                }

                                result.ConflictsBlocked++;
                                AddConflictDetail(
                                    result, transaction, voucherType, bankLedger, counterpartLedger, sourceKey,
                                    new LiveBankVoucherMatch(
                                        LiveBankVoucherMatchKind.Conflict, liveBound,
                                        $"MASTERID {boundMasterId} is bound to this AAH Bank source, but live Tally does not equal AAH's saved previous verified version. External/unverified change is protected; CREATE and ALTER are both blocked."));
                                continue;
                            }
                        }
                        else
                        {
                            // One-time legacy migration. The durable source->MASTERID link is
                            // ownership evidence, but before adopting the current live object as
                            // PreviousVersion also prove the old stored treatment when available.
                            // This migration branch disappears naturally after the first verified
                            // version is saved; every later edit is exact previous -> desired.
                            var previous = _bankingRepository.GetBankTallyExportTreatment(
                                client.Id, client.FinancialYear, transaction.Id, company.CompanyKey);
                            var previousKnown = previous is not null &&
                                                previous.Value.TallyMasterId.Equals(boundMasterId, StringComparison.OrdinalIgnoreCase) &&
                                                previous.Value.VoucherType.Length > 0 && previous.Value.BankLedger.Length > 0 &&
                                                previous.Value.CounterpartLedger.Length > 0;
                            if (previousKnown && !LiveBankTreatmentMatches(
                                    liveBound, transaction, previous!.Value.VoucherType,
                                    previous.Value.BankLedger, previous.Value.CounterpartLedger))
                            {
                                result.ConflictsBlocked++;
                                AddConflictDetail(
                                    result, transaction, voucherType, bankLedger, counterpartLedger, sourceKey,
                                    new LiveBankVoucherMatch(
                                        LiveBankVoucherMatchKind.Conflict, liveBound,
                                        $"Legacy bound MASTERID {boundMasterId} does not match AAH's previously stored Bank treatment; previous version cannot be adopted automatically."));
                                continue;
                            }

                            var legacyBankMid = previousKnown
                                ? ResolveLedgerMasterId(previous!.Value.BankLedger)
                                : ReadVersionField(desiredVersion, "BANKMID");
                            var legacyLiveVersion = BuildLiveBankVersion(liveBound, legacyBankMid);
                            _tallyRepository.ConfirmSourceBindingVersion(
                                client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher",
                                sourceKey, sourceKey, boundMasterId, liveBound.Guid, lastHash,
                                legacyLiveVersion, LiveAlterId(liveBound),
                                "Legacy durable Bank MASTERID adopted as the previous verified version before first Fix74 ALTER.");
                        }

                        _tallyRepository.StageSourceBindingChange(
                            client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher",
                            sourceKey, sourceKey, boundMasterId, liveBound.Guid, contentHash,
                            desiredVersion, "ALTER", "ModifyPending",
                            "Bound MASTERID matched AAH's previous verified Bank version; desired new version staged for ALTER.");
                        batchFingerprints[sourceKey] = contentHash;
                        ready.Add(intent with { ExistingTallyMasterId = boundMasterId });
                        continue;
                    }

                    // Fix85: do not clear a missing bound MASTERID and do not convert
                    // it into CREATE permission. Keep the old identity until the strict
                    // Bank classifier below either recovers one exact replacement or
                    // concludes that the Voucher was deleted directly in Tally.
                    missingBoundMasterId = boundMasterId;
                    missingBindingState = versionState?.State ?? state;
                    if (missingBindingState.Equals("DeletedInTally", StringComparison.OrdinalIgnoreCase))
                    {
                        // Search current authority for a later exact manual replacement
                        // before keeping this source deleted. No CREATE permission is added.
                        missingBoundNeedsResolution = true;
                    }
                    // Fix86: even an earlier ExternalDeleteConflict must first get one
                    // strict current-authority replacement search. If no exact live Voucher
                    // exists, a pure old-Bank-MASTERID -> current-Bank-MASTERID movement is
                    // surfaced separately as reconciliation-required rather than mixed with
                    // genuine concurrent source edits. No CREATE permission is granted here.
                    missingBoundNeedsResolution = true;
                    holdCreateUntilMasterIdRecovered = false;
                }
                _ = state;
            }

            var liveMatch = ClassifyLiveBankVoucher(
                liveBefore, transaction, voucherType, counterpartLedger, bankLedger, sourceReference,
                sourceWasPreviouslyImported, reservedLiveMasterIds, claimedImportedLiveIdentityMasters);
            if (liveMatch.Kind != LiveBankVoucherMatchKind.None)
            {
                var liveMasterId = liveMatch.Record?.MasterId.Trim() ?? string.Empty;
                if (explicitRestoreRequested)
                {
                    if (liveMatch.Record is not null && liveMasterId.Length > 0)
                    {
                        var desiredBankMid = ReadVersionField(desiredVersion, "BANKMID");
                        var liveRestoreVersion = BuildLiveBankVersion(liveMatch.Record, desiredBankMid);
                        if (liveRestoreVersion.Equals(desiredVersion, StringComparison.Ordinal))
                        {
                            ConfirmBankVersion(intent, liveMatch.Record,
                                "R37 Restore-to-Tally found the exact live Bank Voucher and rebound it without resending.");
                            result.ExactDuplicatesSkipped++;
                        }
                        else
                        {
                            result.ConflictsBlocked++;
                            AddConflictDetail(result, transaction, voucherType, bankLedger, counterpartLedger, sourceKey,
                                new LiveBankVoucherMatch(LiveBankVoucherMatchKind.Conflict, liveMatch.Record,
                                    "Restore-to-Tally found a live Bank Voucher for the same source but its content differs. The live Voucher was not altered and no duplicate was created."));
                        }
                    }
                    else
                    {
                        result.ConflictsBlocked++;
                        AddConflictDetail(result, transaction, voucherType, bankLedger, counterpartLedger, sourceKey,
                            new LiveBankVoucherMatch(LiveBankVoucherMatchKind.Conflict, liveMatch.Record,
                                "Restore-to-Tally found an ambiguous live Bank Voucher candidate. No CREATE/ALTER was allowed."));
                    }
                    continue;
                }
                var liveIdentityKey = liveMatch.Record is null
                    ? string.Empty
                    : BuildLiveOccurrenceIdentityKey(liveMatch.Record, bankLedger);

                if (sourceWasPreviouslyImported && liveMasterId.Length > 0 &&
                    liveMatch.Kind is LiveBankVoucherMatchKind.Exact or LiveBankVoucherMatchKind.ExistingSource or LiveBankVoucherMatchKind.TreatmentUpdate)
                {
                    reservedLiveMasterIds.Add(liveMasterId);
                    if (liveIdentityKey.Length > 0 && !claimedImportedLiveIdentityMasters.ContainsKey(liveIdentityKey))
                        claimedImportedLiveIdentityMasters[liveIdentityKey] = liveMasterId;
                }

                if (liveMatch.Kind is LiveBankVoucherMatchKind.Exact or LiveBankVoucherMatchKind.ExistingSource)
                {
                    if (liveMatch.Record is not null && liveMasterId.Length > 0)
                        ConfirmBankVersion(intent, liveMatch.Record,
                            liveMatch.Kind == LiveBankVoucherMatchKind.Exact
                                ? "Fresh strict Bank duplicate check bound exact live MASTERID."
                                : "Fresh strict Bank duplicate check rebound existing AAH source to live MASTERID.");
                    if (liveMatch.Kind == LiveBankVoucherMatchKind.Exact) result.ExactDuplicatesSkipped++;
                    else result.ExistingSourceDuplicatesSkipped++;
                    continue;
                }

                if (liveMatch.Kind == LiveBankVoucherMatchKind.TreatmentUpdate &&
                    liveMatch.Record is not null && liveMasterId.Length > 0)
                {
                    var priorVersion = versionState?.VerifiedVersion ?? string.Empty;
                    var priorBankMid = priorVersion.Length > 0
                        ? ReadVersionField(priorVersion, "BANKMID")
                        : ResolveLedgerMasterId(bankLedger);
                    var liveVersion = BuildLiveBankVersion(liveMatch.Record, priorBankMid);
                    if (priorVersion.Length > 0 && !liveVersion.Equals(priorVersion, StringComparison.Ordinal))
                    {
                        result.ConflictsBlocked++;
                        AddConflictDetail(result, transaction, voucherType, bankLedger, counterpartLedger, sourceKey,
                            new LiveBankVoucherMatch(LiveBankVoucherMatchKind.Conflict, liveMatch.Record,
                                $"Live MASTERID {liveMasterId} matches the Bank source but not AAH's saved previous verified version; external Tally change is protected."));
                        continue;
                    }
                    if (priorVersion.Length == 0)
                    {
                        var previous = _bankingRepository.GetBankTallyExportTreatment(
                            client.Id, client.FinancialYear, transaction.Id, company.CompanyKey);
                        var previousKnown = previous is not null && previous.Value.VoucherType.Length > 0 &&
                                            previous.Value.BankLedger.Length > 0 && previous.Value.CounterpartLedger.Length > 0;
                        if (previousKnown && !LiveBankTreatmentMatches(
                                liveMatch.Record, transaction, previous!.Value.VoucherType,
                                previous.Value.BankLedger, previous.Value.CounterpartLedger))
                        {
                            result.ConflictsBlocked++;
                            AddConflictDetail(result, transaction, voucherType, bankLedger, counterpartLedger, sourceKey,
                                new LiveBankVoucherMatch(LiveBankVoucherMatchKind.Conflict, liveMatch.Record,
                                    $"Live MASTERID {liveMasterId} matches physical Bank identity but not AAH's previous verified treatment."));
                            continue;
                        }
                        _tallyRepository.ConfirmSourceBindingVersion(
                            client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher",
                            sourceKey, sourceKey, liveMasterId, liveMatch.Record.Guid,
                            liveMatch.Record.ContentHash, liveVersion, LiveAlterId(liveMatch.Record),
                            "Existing Bank Voucher captured as previous verified version before treatment ALTER.");
                    }
                    _tallyRepository.StageSourceBindingChange(
                        client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher",
                        sourceKey, sourceKey, liveMasterId, liveMatch.Record.Guid, contentHash,
                        desiredVersion, "ALTER", "ModifyPending",
                        "Strict Bank identity matched; previous version confirmed and desired treatment staged.");
                    batchFingerprints[sourceKey] = contentHash;
                    ready.Add(intent with { ExistingTallyMasterId = liveMasterId });
                    continue;
                }

                if (!missingBoundNeedsResolution && !holdCreateUntilMasterIdRecovered &&
                    liveMatch.Kind == LiveBankVoucherMatchKind.ReplaceManualDuplicate &&
                    liveMatch.Record is not null && liveMasterId.Length > 0 && liveIdentityKey.Length > 0 &&
                    claimedImportedLiveIdentityMasters.TryGetValue(liveIdentityKey, out var retainedMasterId) &&
                    IsStrictlyNewerMasterId(liveMasterId, retainedMasterId))
                {
                    duplicateDeletePlans.Add(new PendingBankVoucherDelete(
                        transaction.TransactionDate, liveMatch.Record.VoucherType, liveMasterId,
                        retainedMasterId, liveIdentityKey, bankLedger, sourceKey));
                    reservedLiveMasterIds.Add(liveMasterId);
                    _tallyRepository.StageSourceBindingChange(
                        client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher",
                        sourceKey, sourceKey, string.Empty, string.Empty, contentHash,
                        desiredVersion, "CREATE", "CreatePending",
                        "Later manual duplicate scheduled for verified DELETE; replacement source staged without reusing its MASTERID.");
                    batchFingerprints[sourceKey] = contentHash;
                    ready.Add(intent with { VerificationExcludedTallyMasterId = retainedMasterId });
                    continue;
                }

                result.ConflictsBlocked++;
                AddConflictDetail(result, transaction, voucherType, bankLedger, counterpartLedger, sourceKey, liveMatch);
                continue;
            }

            // Fix85: the strict Bank classifier above had first chance to recover an
            // exact current replacement MASTERID. If the old bound MASTERID is still
            // absent and nothing exact exists, adopt an isolated loss as DeletedInTally.
            // Widespread identity loss remains RebindRequired. Neither may CREATE.
            if (missingBoundNeedsResolution)
            {
                if (missingBindingState.Equals("MissingLiveCandidate", StringComparison.OrdinalIgnoreCase) ||
                    missingBindingState.Equals("Verified", StringComparison.OrdinalIgnoreCase) ||
                    missingBindingState.Equals("DeletedInTally", StringComparison.OrdinalIgnoreCase) ||
                    missingBindingState.Equals("ExternalDeleteConflict", StringComparison.OrdinalIgnoreCase) ||
                    missingBindingState.Equals("BankMigrationReconciliation", StringComparison.OrdinalIgnoreCase))
                {
                    var deletionVersionState = _tallyRepository.GetSourceBindingVersionState(
                        client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher", sourceKey);
                    var previousVerified = deletionVersionState?.VerifiedVersion ?? string.Empty;
                    if (previousVerified.Length > 0 && IsOnlyBankMasterMigration(previousVerified, desiredVersion))
                    {
                        _tallyRepository.UpsertSourceBinding(
                            client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher",
                            sourceKey, sourceKey, missingBoundMasterId, deletionVersionState?.Guid ?? string.Empty,
                            contentHash, "BankMigrationReconciliation",
                            $"Fix86 reconciliation: bound Voucher MASTERID {missingBoundMasterId} is absent and no exact replacement exists; the only AAH-side change is Bank Ledger MASTERID {ReadVersionField(previousVerified, "BANKMID")} -> {ReadVersionField(desiredVersion, "BANKMID")}. Keep this source separate from external-delete conflicts. CREATE/ALTER remain blocked until explicit reconciliation/restore.");
                        result.TreatmentRequiredTransactions++;
                        result.TreatmentDetails.Add(
                            $"{transaction.TransactionDate:dd-MMM-yyyy} | {transaction.Amount:N2} | {sourceKey} | Bank MASTERID migration reconciliation required: missing Voucher MASTERID {missingBoundMasterId}; Bank {ReadVersionField(previousVerified, "BANKMID")} -> {ReadVersionField(desiredVersion, "BANKMID")}; source fields/treatment otherwise unchanged.");
                        continue;
                    }
                    if (previousVerified.Length == 0 ||
                        !previousVerified.Equals(desiredVersion, StringComparison.Ordinal))
                    {
                        result.ConflictsBlocked++;
                        AddConflictDetail(result, transaction, voucherType, bankLedger, counterpartLedger, sourceKey,
                            new LiveBankVoucherMatch(
                                LiveBankVoucherMatchKind.Conflict, null,
                                $"Bound MASTERID {missingBoundMasterId} is missing, but current AAH Bank treatment no longer equals the saved previous verified version. Tally deletion and local modification conflict; CREATE/ALTER are blocked."));
                        continue;
                    }
                    if (missingBindingState.Equals("DeletedInTally", StringComparison.OrdinalIgnoreCase))
                    {
                        result.DeletedInTallySkipped++;
                        continue;
                    }

                    var deleted = _tallyRepository.MarkSourceBindingDeletedInTally(
                        client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher",
                        sourceKey, missingBoundMasterId,
                        $"Fix85 adopted deletion in Tally: bound Bank Voucher MASTERID {missingBoundMasterId} is absent from complete fresh FY authority and no exact replacement Voucher was found. Bank Statement source remains in AAH; automatic CREATE is forbidden.");
                    if (deleted)
                    {
                        result.DeletedInTallySkipped++;
                        continue;
                    }
                    result.ConflictsBlocked++;
                    AddConflictDetail(result, transaction, voucherType, bankLedger, counterpartLedger, sourceKey,
                        new LiveBankVoucherMatch(
                            LiveBankVoucherMatchKind.Conflict, null,
                            $"Bound MASTERID {missingBoundMasterId} disappeared, but AAH has a concurrent/pending change. External deletion could not be adopted; CREATE/ALTER are blocked."));
                    continue;
                }

                result.GenerationRebindBlocked++;
                result.ConflictsBlocked++;
                AddConflictDetail(result, transaction, voucherType, bankLedger, counterpartLedger, sourceKey,
                    new LiveBankVoucherMatch(
                        LiveBankVoucherMatchKind.Conflict, null,
                        $"Bound MASTERID {missingBoundMasterId} is absent during company-generation/rebind recovery. Only one exact live replacement MASTERID may be rebound; automatic CREATE is forbidden."));
                continue;
            }

            if (holdCreateUntilMasterIdRecovered)
            {
                if (missingBindingState.Equals("RebindRequired", StringComparison.OrdinalIgnoreCase) ||
                    missingBindingState.Equals("StaleMissingLive", StringComparison.OrdinalIgnoreCase))
                    result.GenerationRebindBlocked++;
                result.ConflictsBlocked++;
                AddConflictDetail(result, transaction, voucherType, bankLedger, counterpartLedger, sourceKey,
                    new LiveBankVoucherMatch(
                        LiveBankVoucherMatchKind.Conflict, null,
                        $"Source is in '{versionState?.State}' and has no recovered MASTERID yet. CREATE is blocked until exact live identity is recovered or an explicit Restore-to-Tally action is requested."));
                continue;
            }

            if (explicitRestoreRequested)
            {
                // CL017: the historical verified version proves which deleted source
                // the user approved. CURRENT AAH ledger/treatment is the CREATE payload.
                // Fresh live identity, Master dependencies and strict duplicate checks
                // have already run; never reuse the deleted Voucher MASTERID.
                var previousVerified = versionState?.VerifiedVersion ?? string.Empty;
                if (previousVerified.Length == 0 ||
                    !string.Equals(versionState?.State, "RestoreRequested", StringComparison.OrdinalIgnoreCase) ||
                    versionState?.PendingVersion.Length > 0)
                {
                    result.ConflictsBlocked++;
                    AddConflictDetail(result, transaction, voucherType, bankLedger, counterpartLedger, sourceKey,
                        new LiveBankVoucherMatch(LiveBankVoucherMatchKind.Conflict, null,
                            "Restore-to-Tally lacks a previous verified version or has unverified pending work."));
                    continue;
                }
                _tallyRepository.StageSourceBindingChange(
                    client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher",
                    sourceKey, sourceKey, string.Empty, string.Empty, contentHash,
                    desiredVersion, "CREATE", "CreatePending",
                    "CL017 user-approved restore: current Bank source treatment follows the ordinary CREATE route after fresh live duplicate checks; old Voucher MASTERID and version remain in history.");
                batchFingerprints[sourceKey] = contentHash;
                ready.Add(intent);
                continue;
            }

            // Durable Auto/Bank import history proves this logical source was already
            // sent before. Fix85 removes the former missing-MASTERID CREATE exception.
            if (sourceWasPreviouslyImported)
            {
                result.ConflictsBlocked++;
                _tallyRepository.UpsertSourceBinding(
                    client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher",
                    sourceKey, sourceKey, string.Empty, string.Empty, contentHash,
                    "AcceptedAwaitingMasterId",
                    "Known previously imported Bank source has no durable live Voucher MASTERID. Automatic CREATE is forbidden until exact identity is recovered or explicitly restored.");
                AddConflictDetail(result, transaction, voucherType, bankLedger, counterpartLedger, sourceKey,
                    new LiveBankVoucherMatch(
                        LiveBankVoucherMatchKind.Conflict, null,
                        "Previously imported Bank source has no durable live MASTERID and no strict live match. CREATE is blocked to prevent recreation of a Tally-deleted voucher."));
                continue;
            }

            if (batchFingerprints.TryGetValue(sourceKey, out var batchHash))
            {
                if (batchHash.Equals(contentHash, StringComparison.OrdinalIgnoreCase))
                    result.RepeatedSourceCandidatesSkipped++;
                else
                {
                    result.ConflictsBlocked++;
                    AddBatchConflictDetail(result, transaction, sourceKey);
                }
                continue;
            }

            _tallyRepository.StageSourceBindingChange(
                client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher",
                sourceKey, sourceKey, string.Empty, string.Empty, contentHash,
                desiredVersion, "CREATE", "CreatePending",
                "Masters are MASTERID-bound and strict live Bank duplicate authority found no existing Voucher; CREATE staged.");
            batchFingerprints[sourceKey] = contentHash;
            ready.Add(intent);
        }

        result.ReadyTransactions = ready.Count;
        result.ReadyAlterTransactions = ready.Count(item => item.ExistingTallyMasterId.Trim().Length > 0);
        result.ReadyCreateTransactions = result.ReadyTransactions - result.ReadyAlterTransactions;
        if (ready.Count == 0)
        {
            result.Status = BuildSummary(result, imported: false) + " Master-first comparison completed; no Bank Voucher write is required.";
            return result;
        }

        // FIX9 duplicate replacement is a separate fail-closed mutation stage. Delete
        // only an exact later duplicate MASTERID that has an older identical retained
        // MASTERID already claimed by a previously Imported source. Re-read Tally and
        // prove both sides of that condition before any replacement CREATE/ALTER runs.
        if (duplicateDeletePlans.Count > 0)
        {
            var deletePlans = duplicateDeletePlans
                .GroupBy(item => item.MasterId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();

            // Re-read immediately before DELETE so the MASTERID/cardinality proof is
            // authoritative at the mutation boundary, not only at the start of import.
            try
            {
                liveBefore = await RefreshVoucherAuthorityAsync(fromDate, toDate);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result.TallyErrors = 1;
                result.UnverifiedAfterImport = ready.Count;
                result.LineError =
                    "Fresh live Tally preflight failed before duplicate DELETE: " + exception.Message;
                result.Status = BuildSummary(result, imported: false) + " " + result.LineError;
                return result;
            }

            var invalidDeletePlan = deletePlans.FirstOrDefault(plan =>
            {
                var target = liveBefore.FirstOrDefault(item =>
                    item.MasterId.Trim().Equals(plan.MasterId, StringComparison.OrdinalIgnoreCase));
                var retained = liveBefore.FirstOrDefault(item =>
                    item.MasterId.Trim().Equals(plan.RetainedMasterId, StringComparison.OrdinalIgnoreCase));
                return target is null || retained is null ||
                       !BuildLiveOccurrenceIdentityKey(target, plan.BankLedger).Equals(plan.LiveIdentityKey, StringComparison.OrdinalIgnoreCase) ||
                       !BuildLiveOccurrenceIdentityKey(retained, plan.BankLedger).Equals(plan.LiveIdentityKey, StringComparison.OrdinalIgnoreCase) ||
                       !IsStrictlyNewerMasterId(plan.MasterId, plan.RetainedMasterId);
            });
            if (invalidDeletePlan is not null)
            {
                result.TallyErrors = 1;
                result.UnverifiedAfterImport = ready.Count;
                result.LineError =
                    $"Bank duplicate replacement safety gate failed for MASTERID {invalidDeletePlan.MasterId}; " +
                    "the later duplicate and retained AAH occurrence could not both be proven from the fresh live snapshot.";
                result.Status = BuildSummary(result, imported: false) + " " + result.LineError;
                return result;
            }

            var deleteExpectations = deletePlans
                .Select(plan => new TallyBankVoucherDeleteExpectation(
                    plan.Date, plan.VoucherType, plan.MasterId,
                    plan.RetainedMasterId, plan.LiveIdentityKey))
                .ToArray();
            var deleteXml = TallyBankVoucherDeleteXmlBuilder.Build(company.Name, deleteExpectations);
            TallyBankVoucherDeleteXmlValidator.Validate(deleteXml, company.Name, deleteExpectations);
            var deleteResponseText = await _tallyHttpService.ImportXmlAsync(deleteXml, cancellationToken);
            var deleteResponse = TallyHttpService.ParseImportResponse(deleteResponseText);
            result.TallyDeleted += deleteResponse.Deleted;
            result.DuplicateDeleteResponsePath = SaveResponseLog(
                client, company, fromDate, toDate, deleteXml, deleteResponseText,
                "bank-duplicate-delete");

            if (deleteResponse.Errors > 0 || !string.IsNullOrWhiteSpace(deleteResponse.LineError))
            {
                result.TallyErrors = Math.Max(1, deleteResponse.Errors);
                result.UnverifiedAfterImport = ready.Count;
                result.LineError = deleteResponse.LineError.Length > 0
                    ? deleteResponse.LineError
                    : "Tally rejected the exact duplicate Bank voucher DELETE request.";
                result.Status = BuildSummary(result, imported: false) + " " + result.LineError;
                return result;
            }

            // Fix37: only after Tally has accepted the DELETE do we remove the known
            // MASTERIDs from synchronized local authority. A following tiny marker/delta
            // then merges any other voucher changes without rereading the full period.
            _tallyRepository.DeleteVoucherIndexByMasterIds(
                company.CompanyKey, client.FinancialYear, deletePlans.Select(plan => plan.MasterId));

            try
            {
                liveBefore = await RefreshVoucherAuthorityAsync(fromDate, toDate);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result.TallyErrors = 1;
                result.UnverifiedAfterImport = ready.Count;
                result.LineError =
                    "Tally duplicate DELETE response was received, but the required live verification failed: " +
                    exception.Message;
                result.Status = BuildSummary(result, imported: false) + " " + result.LineError;
                return result;
            }

            var deleteVerificationFailures = deletePlans
                .Where(plan =>
                {
                    var targetStillLive = liveBefore.Any(item =>
                        item.MasterId.Trim().Equals(plan.MasterId, StringComparison.OrdinalIgnoreCase));
                    var retained = liveBefore.FirstOrDefault(item =>
                        item.MasterId.Trim().Equals(plan.RetainedMasterId, StringComparison.OrdinalIgnoreCase));
                    return targetStillLive || retained is null ||
                           !BuildLiveOccurrenceIdentityKey(retained, plan.BankLedger).Equals(
                               plan.LiveIdentityKey, StringComparison.OrdinalIgnoreCase);
                })
                .Select(plan => plan.MasterId)
                .ToArray();
            if (deleteVerificationFailures.Length > 0)
            {
                result.TallyErrors = 1;
                result.UnverifiedAfterImport = ready.Count;
                result.LineError =
                    "Exact duplicate Bank voucher DELETE did not live-verify for MASTERID(s): " +
                    string.Join(", ", deleteVerificationFailures) +
                    ". Replacement CREATE was blocked.";
                result.Status = BuildSummary(result, imported: false) + " " + result.LineError;
                return result;
            }
        }

        // FIX9 final same-MASTERID authority for Ledger/voucher treatment updates.
        // Re-read immediately before ALTER when the duplicate-delete stage did not
        // already perform a fresh read, then prove every target MASTERID is still the
        // same Bank transaction before applying the newest AAH treatment.
        var alterReady = ready
            .Where(item => item.ExistingTallyMasterId.Trim().Length > 0)
            .ToArray();
        if (alterReady.Length > 0)
        {
            if (duplicateDeletePlans.Count == 0)
            {
                try
                {
liveBefore = await RefreshVoucherAuthorityAsync(fromDate, toDate);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    result.TallyErrors = 1;
                    result.UnverifiedAfterImport = ready.Count;
                    result.LineError =
                        "Fresh live Tally preflight failed before Bank Ledger/treatment ALTER: " + exception.Message;
                    result.Status = BuildSummary(result, imported: false) + " " + result.LineError;
                    return result;
                }
            }

            foreach (var item in alterReady)
            {
                var target = liveBefore.FirstOrDefault(liveItem =>
                    liveItem.MasterId.Trim().Equals(
                        item.ExistingTallyMasterId.Trim(), StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    result.TallyErrors = 1;
                    result.UnverifiedAfterImport = ready.Count;
                    result.LineError =
                        $"Bank Ledger/treatment ALTER was blocked because live MASTERID {item.ExistingTallyMasterId} no longer exists.";
                    result.Status = BuildSummary(result, imported: false) + " " + result.LineError;
                    return result;
                }

                // Fix83: this is the final concurrency guard for an already-bound Voucher.
                // When the Bank Account moved from old Ledger MASTERID 1972 to the new
                // independent MASTERID 2085, the live Voucher MUST still contain 1972
                // immediately before ALTER. Re-classifying it against the desired/new
                // Bank Ledger wrongly reports an identity change. Prove the durable
                // PreviousVersion instead; only legacy rows without one use the older
                // first-pass classifier fallback.
                var finalVersionState = _tallyRepository.GetSourceBindingVersionState(
                    client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher", item.SourceKey);
                var previousVersion = finalVersionState?.VerifiedVersion?.Trim() ?? string.Empty;
                if (previousVersion.Length > 0)
                {
                    var previousBankMid = ReadVersionField(previousVersion, "BANKMID");
                    var livePreviousVersion = BuildLiveBankVersion(target, previousBankMid);
                    if (!livePreviousVersion.Equals(previousVersion, StringComparison.Ordinal))
                    {
                        result.TallyErrors = 1;
                        result.UnverifiedAfterImport = ready.Count;
                        result.LineError =
                            $"Bank Ledger/treatment ALTER was blocked because MASTERID {item.ExistingTallyMasterId} no longer matches AAH's saved previous verified version.";
                        result.Status = BuildSummary(result, imported: false) + " " + result.LineError;
                        return result;
                    }
                    continue;
                }

                var finalMatch = ClassifyLiveBankVoucher(
                    new[] { target },
                    item.Transaction,
                    item.VoucherType,
                    item.CounterpartLedger,
                    item.BankLedger,
                    item.ReferenceNumber,
                    sourceWasPreviouslyImported: true);
                if (finalMatch.Kind is not (LiveBankVoucherMatchKind.TreatmentUpdate or
                    LiveBankVoucherMatchKind.Exact or LiveBankVoucherMatchKind.ExistingSource))
                {
                    result.TallyErrors = 1;
                    result.UnverifiedAfterImport = ready.Count;
                    result.LineError =
                        $"Bank Ledger/treatment ALTER was blocked because legacy MASTERID {item.ExistingTallyMasterId} changed identity after preflight.";
                    result.Status = BuildSummary(result, imported: false) + " " + result.LineError;
                    return result;
                }
            }
        }

        const int minAdaptiveBatch = 10;
        const int maxAdaptiveBatch = 400;
        var adaptiveBatch = Math.Min(80, ready.Count);
        var attemptedReady = new List<PreparedBankVoucher>();
        var totalCreated = 0;
        var totalAltered = 0;
        var totalIgnored = 0;
        var totalErrors = 0;
        var lineErrors = new List<string>();
        var responseLogs = new List<string>();
        var bankOffset = 0;
        var transportUnknown = false;
        string lastXml = string.Empty;
        string lastResponseText = string.Empty;
        while (bankOffset < ready.Count)
        {
            var take = Math.Min(adaptiveBatch, ready.Count - bankOffset);
            var bankBatch = ready.Skip(bankOffset).Take(take).ToArray();
            attemptedReady.AddRange(bankBatch);
            var voucherExpectations = bankBatch
                .Select(voucher => new TallyBankCreateAlterExpectation(
                    voucher.Transaction.TransactionDate, voucher.VoucherType, voucher.ReferenceNumber,
                    voucher.Transaction.Narration, BuildErp9Entries(voucher).ToArray(), voucher.ExistingTallyMasterId))
                .ToArray();
            var xml = TallyBankCreateAlterXmlBuilder.Build(company.Name, voucherExpectations);
            TallyBankCreateAlterXmlValidator.Validate(xml, company.Name, voucherExpectations);
            lastXml = xml;
            var watch = Stopwatch.StartNew();
            string responseText;
            try
            {
                responseText = await _tallyHttpService.ImportXmlAsync(xml, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                transportUnknown = true;
                foreach (var item in bankBatch)
                {
                    var pendingState = item.ExistingTallyMasterId.Trim().Length > 0
                        ? "AcceptedAwaitingVerification"
                        : "AcceptedAwaitingMasterId";
                    _tallyRepository.UpsertSourceBinding(
                        client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher",
                        item.SourceKey, item.SourceKey, item.ExistingTallyMasterId, string.Empty, item.ContentHash,
                        pendingState, "Bank transport outcome unknown: " + exception.Message);
                }
                result.TallyErrors = Math.Max(result.TallyErrors, 1);
                result.LineError = "Bank batch transport outcome unknown; held without automatic resend: " + exception.Message;
                break;
            }
            watch.Stop();
            lastResponseText = responseText;
            var response = TallyHttpService.ParseImportResponse(responseText);
            totalCreated += response.Created;
            totalAltered += response.Altered;
            totalIgnored += response.Ignored;
            totalErrors += response.Errors;
            if (!string.IsNullOrWhiteSpace(response.LineError)) lineErrors.Add(response.LineError);
            var batchSummary = $"BankBatch={bankOffset}-{bankOffset + take - 1}; Created={response.Created}; Altered={response.Altered}; Ignored={response.Ignored}; Errors={response.Errors}; LineError={response.LineError}; ElapsedMs={watch.ElapsedMilliseconds}";
            responseLogs.Add(batchSummary);
            var clean = response.Errors == 0 && string.IsNullOrWhiteSpace(response.LineError);
            var mutated = response.Created > 0 || response.Altered > 0;
            foreach (var item in bankBatch)
            {
                var pendingState = item.ExistingTallyMasterId.Trim().Length > 0
                    ? "AcceptedAwaitingVerification"
                    : "AcceptedAwaitingMasterId";
                _tallyRepository.UpsertSourceBinding(
                    client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher",
                    item.SourceKey, item.SourceKey, item.ExistingTallyMasterId, string.Empty, item.ContentHash,
                    clean || mutated ? pendingState : "Rejected", batchSummary);
            }
            if (!clean)
            {
                if (mutated)
                    result.LineError = "Bank batch partially mutated Tally and is held for verification; it will not be automatically resent.";
                break;
            }
            bankOffset += take;
            var ms = watch.ElapsedMilliseconds;
            adaptiveBatch = ms <= 2000 ? Math.Min(maxAdaptiveBatch, Math.Max(adaptiveBatch + 20, (int)Math.Ceiling(adaptiveBatch * 1.35)))
                : ms <= 6000 ? Math.Min(maxAdaptiveBatch, adaptiveBatch + 10)
                : ms <= 12000 ? Math.Max(minAdaptiveBatch, (int)Math.Floor(adaptiveBatch * 0.75))
                : Math.Max(minAdaptiveBatch, adaptiveBatch / 2);
        }

        result.TallyCreated = totalCreated;
        result.TallyAltered = totalAltered;
        result.TallyIgnored = totalIgnored;
        result.TallyErrors = Math.Max(result.TallyErrors, totalErrors);
        if (lineErrors.Count > 0)
            result.LineError = string.Join(" | ", lineErrors.Distinct());
        result.ResponsePath = SaveResponseLog(
            client, company, fromDate, toDate, lastXml,
            lastResponseText + Environment.NewLine + string.Join(Environment.NewLine, responseLogs), "bank-vouchers");
        var responseSummary =
            $"Created={totalCreated}; Altered={totalAltered}; Ignored={totalIgnored}; Errors={totalErrors}; " +
            $"LineError={result.LineError}; AdaptiveBatches={responseLogs.Count}; TransportUnknown={transportUnknown}";

        // IMPORTRESULT alone does not prove which vouchers reached the books. Re-read
        // one fresh live Voucher authority response and mark only vouchers that can be
        // found again with generated MASTERID/GUID. A post-import read failure is NOT
        // the same as an import failure: Tally
        // may already have accepted the voucher. Preserve the import response and mark
        // the batch LiveVerificationPending instead of throwing away that evidence and
        // encouraging an unsafe retry that could duplicate a voucher.
        PreparedBankVoucher[] verified;
        PreparedBankVoucher[] unverified;
        var verifiedLiveBySourceKey = new Dictionary<string, TallyVoucherIndexRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Fix68 post-write identity capture: do not depend on a possibly lagging
            // global ALTERID pulse to learn the MASTERIDs of Vouchers Tally has just
            // accepted. Read the exact written date span once, receiving MASTERID/GUID/
            // ALTERID and the full Voucher body together, then bind every accepted AAH
            // source to the live Voucher identity before this export returns.
            // CL017: bounded calendar-month readback. An explicit whole-FY restore
            // must not generate one huge Tally Voucher XML request after a successful
            // CREATE; only months containing accepted write attempts are fetched.
            var boundedLiveAfter = new List<TallyVoucherIndexRecord>();
            foreach (var group in attemptedReady.GroupBy(item =>
                         (item.Transaction.TransactionDate.Year, item.Transaction.TransactionDate.Month))
                         .OrderBy(group => group.Key.Year).ThenBy(group => group.Key.Month))
            {
                var verifyFrom = group.Min(item => item.Transaction.TransactionDate.Date);
                var verifyTo = group.Max(item => item.Transaction.TransactionDate.Date);
                var postAuthority = await _tallyHttpService.ExportVoucherAuthorityFreshAsync(
                    company, verifyFrom, verifyTo, cancellationToken);
                _tallyRepository.ReplaceVoucherIndexRange(
                    company.CompanyKey, client.FinancialYear, verifyFrom, verifyTo, postAuthority.Index);
                _tallyRepository.UpsertVoucherXmlAuthority(
                    company.CompanyKey, client.FinancialYear, postAuthority.Full);
                boundedLiveAfter.AddRange(postAuthority.Index);
            }
            var liveAfter = boundedLiveAfter;
            var verifiedList = new List<PreparedBankVoucher>();
            var verificationReservedMasterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in attemptedReady)
            {
                IEnumerable<TallyVoucherIndexRecord> verificationLive = liveAfter;
                if (item.VerificationExcludedTallyMasterId.Trim().Length > 0)
                {
                    verificationLive = verificationLive.Where(liveItem =>
                        !liveItem.MasterId.Trim().Equals(
                            item.VerificationExcludedTallyMasterId.Trim(),
                            StringComparison.OrdinalIgnoreCase));
                }
                if (item.ExistingTallyMasterId.Trim().Length > 0)
                {
                    verificationLive = verificationLive.Where(liveItem =>
                        liveItem.MasterId.Trim().Equals(
                            item.ExistingTallyMasterId.Trim(),
                            StringComparison.OrdinalIgnoreCase));
                }

                // Post-import cardinality verification reserves each matched MASTERID
                // once. Passing Imported=true here only enables deterministic one-to-one
                // occurrence allocation; an item is accepted only when the final match
                // itself is Exact under the new AAH accounting shape.
                var verificationMatch = ClassifyLiveBankVoucher(
                    verificationLive,
                    item.Transaction,
                    item.VoucherType,
                    item.CounterpartLedger,
                    item.BankLedger,
                    item.ReferenceNumber,
                    sourceWasPreviouslyImported: true,
                    reservedLiveMasterIds: verificationReservedMasterIds);
                if (verificationMatch.Kind != LiveBankVoucherMatchKind.Exact) continue;

                verifiedList.Add(item);
                var verifiedRecord = verificationMatch.Record;
                var verifiedMasterId = verifiedRecord?.MasterId.Trim() ?? string.Empty;
                if (verifiedMasterId.Length > 0 && verifiedRecord is not null)
                {
                    verificationReservedMasterIds.Add(verifiedMasterId);
                    verifiedLiveBySourceKey[item.SourceKey] = verifiedRecord;
                    _bankingRepository.UpsertBankTallyExportLink(
                        client.Id, client.FinancialYear, item.Transaction.Id, item.SourceKey, company.CompanyKey,
                        verifiedMasterId, item.VoucherType, item.BankLedger, item.CounterpartLedger);
                }
            }
            verified = verifiedList.ToArray();
            var verifiedKeys = verified
                .Select(item => item.SourceKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            unverified = attemptedReady
                .Where(item => !verifiedKeys.Contains(item.SourceKey))
                .ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            verified = [];
            unverified = attemptedReady.ToArray();
            result.LiveVerificationError = exception.Message;
        }

        var cleanImportResult = !transportUnknown && totalErrors == 0 && lineErrors.Count == 0;
        // Fix68 retains the Fix37 write-success rule: a clean IMPORTRESULT means Tally
        // accepted the Bank write. Post-write live identity verification is separate and
        // must not invite an automatic resend.
        result.ImportedTransactions = cleanImportResult ? attemptedReady.Count : verified.Length;
        result.UnverifiedAfterImport = unverified.Length;

        // ERP 9 strict Bank export deliberately stops at accounting masters +
        // vouchers. Optional Bank-detail alteration tags vary by Tally release and
        // are not needed to post these accounting vouchers. The Hub keeps Bank
        // metadata locally; this strict path sends no optional Bank-master fields.

        if (verified.Length > 0)
        {
            _tallyRepository.RecordImportFingerprints(
                client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher",
                verified.Select(item => (item.SourceKey, item.ContentHash)),
                "Imported", responseSummary + "; LiveVerified=Yes");
            foreach (var item in verified)
            {
                verifiedLiveBySourceKey.TryGetValue(item.SourceKey, out var liveRecord);
                var masterId = liveRecord?.MasterId.Trim() ?? string.Empty;
                if (masterId.Length == 0)
                    masterId = _bankingRepository.GetBankTallyExportMasterId(
                        client.Id, client.FinancialYear, item.Transaction.Id, company.CompanyKey);
                if (masterId.Length == 0) masterId = item.ExistingTallyMasterId.Trim();
                if (liveRecord is not null && masterId.Length > 0)
                {
                    _tallyRepository.ConfirmSourceBindingVersion(
                        client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher",
                        item.SourceKey, item.SourceKey, masterId, liveRecord.Guid.Trim(),
                        item.ContentHash, BuildDesiredBankVersion(item), LiveAlterId(liveRecord),
                        responseSummary + "; LiveVerified=Yes; PreviousVersionPromoted=Yes");
                }
            }
        }
        if (unverified.Length > 0)
        {
            var unverifiedStatus = cleanImportResult ? "Imported" : "Failed";
            _tallyRepository.RecordImportFingerprints(
                client.Id, client.FinancialYear, company.CompanyKey, "Bank Voucher",
                unverified.Select(item => (item.SourceKey, item.ContentHash)),
                unverifiedStatus, responseSummary + "; PostWriteVerified=No; RequireFreshAuthorityBeforeResend=Yes");
        }

        result.Status = BuildSummary(result, imported: result.ImportedTransactions > 0);
        if (result.LiveVerificationError.Length > 0)
        {
            result.Status +=
                " Tally accepted the import response, but the ERP 9 live read could not verify it yet. " +
                "Do not blindly retry this period; refresh/live-verify first. Verification: " +
                result.LiveVerificationError;
        }
        if (bankMasterSyncNote.Length > 0) result.Status += " " + bankMasterSyncNote;
        return result;
    }

    private async Task<bool> ProcessPendingFirstImportWinsDeletesAsync(
        ClientProfile client,
        TallyCompanyInfo company,
        TallyBankImportResult result,
        CancellationToken cancellationToken,
        bool usePreSynchronizedVoucherAuthority,
        long synchronizedAuthorityAlterId)
    {
        var pending = _bankingRepository
            .GetPendingTallyDuplicateDeletes(client.Id, client.FinancialYear)
            .ToArray();
        result.QueuedTallyDuplicateDeletesPending = pending.Length;
        if (pending.Length == 0) return true;

        var accounts = _bankingRepository.GetAccounts(client.Id, client.FinancialYear)
            .ToDictionary(account => account.Id);
        var minDate = pending.Min(item => item.TransactionDate).Date;
        var maxDate = pending.Max(item => item.TransactionDate).Date;

        IReadOnlyList<TallyVoucherIndexRecord> live;
        try
        {
            // Fix68 pending DELETE authority is always a fresh exact-range read. A
            // historic AAH duplicate-cleanup queue is never allowed to delete against
            // a cache or a potentially lagging ALTERID delta.
            var liveAuthority = await _tallyHttpService.ExportVoucherAuthorityFreshAsync(
                company, minDate, maxDate, cancellationToken);
            live = liveAuthority.Index;
            _tallyRepository.ReplaceVoucherIndexRange(
                company.CompanyKey, client.FinancialYear, minDate, maxDate, liveAuthority.Index);
            _tallyRepository.UpsertVoucherXmlAuthority(
                company.CompanyKey, client.FinancialYear, liveAuthority.Full);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result.TallyErrors = 1;
            result.LineError =
                "Fresh live Tally read failed before Fix24 pending duplicate cleanup: " + exception.Message;
            result.Status = BuildSummary(result, imported: false) + " " + result.LineError;
            return false;
        }

        var expectations = new List<TallyBankVoucherDeleteExpectation>();
        var targetMasterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in pending.GroupBy(item => item.PhysicalIdentityKey, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var queueRows = group.OrderBy(item => item.RemovedTransactionId).ToArray();
            var sample = queueRows[0];
            if (!accounts.TryGetValue(sample.BankAccountId, out var account))
            {
                foreach (var row in queueRows)
                    _bankingRepository.UpdatePendingTallyDuplicateDelete(
                        row.Id, string.Empty, string.Empty, "Pending",
                        "Fix24 cleanup could not resolve the retained Bank Account. No Tally DELETE was attempted.");
                continue;
            }

            var bankLedger = account.AuthorityName.Trim();
            if (bankLedger.Length == 0)
            {
                foreach (var row in queueRows)
                    _bankingRepository.UpdatePendingTallyDuplicateDelete(
                        row.Id, string.Empty, string.Empty, "Pending",
                        "Fix24 cleanup could not resolve the Bank Ledger authority. No Tally DELETE was attempted.");
                continue;
            }

            var candidates = FindPendingDuplicateLiveCandidates(live, sample, account)
                .OrderBy(item => ParseMasterIdForOrdering(item.MasterId))
                .ThenBy(item => item.MasterId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var linkedRetainedMasterId = _bankingRepository.GetBankTallyExportMasterId(
                client.Id, client.FinancialYear, sample.RetainedTransactionId, company.CompanyKey);
            var retainedMasterId = queueRows.Select(item => item.RetainedTallyMasterId.Trim())
                .FirstOrDefault(value => value.Length > 0 && candidates.Any(candidate =>
                    candidate.MasterId.Trim().Equals(value, StringComparison.OrdinalIgnoreCase)))
                ?? string.Empty;
            if (retainedMasterId.Length == 0 && linkedRetainedMasterId.Length > 0 &&
                candidates.Any(candidate => candidate.MasterId.Trim().Equals(linkedRetainedMasterId, StringComparison.OrdinalIgnoreCase)))
                retainedMasterId = linkedRetainedMasterId;

            var knownRemovedMasterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in queueRows)
            {
                foreach (var value in (row.KnownTallyMasterId ?? string.Empty)
                             .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    knownRemovedMasterIds.Add(value);
                var linked = _bankingRepository.GetBankTallyExportMasterId(
                    client.Id, client.FinancialYear, row.RemovedTransactionId, company.CompanyKey);
                if (linked.Length > 0) knownRemovedMasterIds.Add(linked);
            }

            if (candidates.Length == 0)
            {
                foreach (var row in queueRows)
                {
                    _bankingRepository.UpdatePendingTallyDuplicateDelete(
                        row.Id, row.KnownTallyMasterId, retainedMasterId, "Resolved",
                        "Fix24 first-import-wins: no matching live Tally duplicate remains; any previously queued later MASTERID is already absent.");
                    result.QueuedTallyDuplicateDeletesResolved++;
                }
                continue;
            }

            // If the retained source has no stored MASTERID yet, the oldest matching live
            // occurrence is the retained first import. Ledger differences are deliberately
            // ignored. If only one occurrence remains and it is not positively linked to a
            // removed local row, it is treated as the retained/remaining occurrence.
            if (retainedMasterId.Length == 0 && candidates.Length > 1)
                retainedMasterId = candidates[0].MasterId.Trim();

            var deleteCandidates = new List<TallyVoucherIndexRecord>();
            if (retainedMasterId.Length > 0)
            {
                deleteCandidates.AddRange(candidates.Where(candidate =>
                    !candidate.MasterId.Trim().Equals(retainedMasterId, StringComparison.OrdinalIgnoreCase)));
            }
            else if (candidates.Length == 1 &&
                     knownRemovedMasterIds.Contains(candidates[0].MasterId.Trim()))
            {
                // Exact source->MASTERID history proves the only live row belongs to a
                // removed later local duplicate. Delete it now; the retained first local
                // transaction is exported normally later in this same Bank export.
                deleteCandidates.Add(candidates[0]);
            }
            else if (candidates.Length == 1)
            {
                foreach (var row in queueRows)
                {
                    _bankingRepository.UpdatePendingTallyDuplicateDelete(
                        row.Id, row.KnownTallyMasterId, candidates[0].MasterId.Trim(), "Resolved",
                        "Fix24 first-import-wins: only one matching live Tally occurrence remains; no later duplicate MASTERID remains to delete.");
                    result.QueuedTallyDuplicateDeletesResolved++;
                }
                continue;
            }

            if (deleteCandidates.Count == 0)
            {
                foreach (var row in queueRows)
                {
                    _bankingRepository.UpdatePendingTallyDuplicateDelete(
                        row.Id, row.KnownTallyMasterId, retainedMasterId, "Resolved",
                        "Fix24 first-import-wins: no later matching live MASTERID remains.");
                    result.QueuedTallyDuplicateDeletesResolved++;
                }
                continue;
            }

            var deleteIdsText = string.Join(",", deleteCandidates.Select(item => item.MasterId.Trim()));
            foreach (var row in queueRows)
            {
                _bankingRepository.UpdatePendingTallyDuplicateDelete(
                    row.Id, deleteIdsText, retainedMasterId, "Pending",
                    $"Fix24 first-import-wins captured later Tally MASTERID(s) {deleteIdsText}. Retained MASTERID {(retainedMasterId.Length > 0 ? retainedMasterId : "will be recreated from first local source")}.");
            }

            foreach (var candidate in deleteCandidates)
            {
                var masterId = candidate.MasterId.Trim();
                if (masterId.Length == 0 || !targetMasterIds.Add(masterId)) continue;
                expectations.Add(new TallyBankVoucherDeleteExpectation(
                    candidate.VoucherDate?.Date ?? sample.TransactionDate.Date,
                    candidate.VoucherType,
                    masterId,
                    retainedMasterId,
                    BuildLiveOccurrenceIdentityKey(candidate),
                    AllowMissingRetainedMaster: retainedMasterId.Length == 0));
            }
        }

        if (expectations.Count == 0)
        {
            result.QueuedTallyDuplicateDeletesPending = _bankingRepository
                .GetPendingTallyDuplicateDeletes(client.Id, client.FinancialYear).Count;
            return true;
        }

        try
        {
            var deleteXml = TallyBankVoucherDeleteXmlBuilder.Build(company.Name, expectations);
            TallyBankVoucherDeleteXmlValidator.Validate(deleteXml, company.Name, expectations);
            var deleteResponseText = await _tallyHttpService.ImportXmlAsync(deleteXml, cancellationToken);
            var deleteResponse = TallyHttpService.ParseImportResponse(deleteResponseText);
            result.TallyDeleted += deleteResponse.Deleted;
            result.DuplicateDeleteResponsePath = SaveResponseLog(
                client, company, minDate, maxDate, deleteXml, deleteResponseText,
                "bank-first-import-wins-delete");

            if (deleteResponse.Errors > 0 || !string.IsNullOrWhiteSpace(deleteResponse.LineError))
            {
                result.TallyErrors = Math.Max(1, deleteResponse.Errors);
                result.LineError = deleteResponse.LineError.Length > 0
                    ? deleteResponse.LineError
                    : "Tally rejected the Fix24 later-duplicate MASTERID DELETE request.";
                result.Status = BuildSummary(result, imported: false) + " " + result.LineError;
                return false;
            }

            _tallyRepository.DeleteVoucherIndexByMasterIds(
                company.CompanyKey, client.FinancialYear, expectations.Select(item => item.MasterId));
            _tallyRepository.DeleteVoucherXmlAuthorityByMasterIds(
                company.CompanyKey, client.FinancialYear, expectations.Select(item => item.MasterId));
            // Fix68 verify the DELETE against one fresh same-response authority read.
            // Never infer successful deletion from a marker/delta or a locally removed row.
            var postDeleteAuthority = await _tallyHttpService.ExportVoucherAuthorityFreshAsync(
                company, minDate, maxDate, cancellationToken);
            live = postDeleteAuthority.Index;
            _tallyRepository.ReplaceVoucherIndexRange(
                company.CompanyKey, client.FinancialYear, minDate, maxDate, postDeleteAuthority.Index);
            _tallyRepository.UpsertVoucherXmlAuthority(
                company.CompanyKey, client.FinancialYear, postDeleteAuthority.Full);

            foreach (var row in _bankingRepository.GetPendingTallyDuplicateDeletes(client.Id, client.FinancialYear).ToArray())
            {
                if (!accounts.TryGetValue(row.BankAccountId, out var account)) continue;
                var remaining = FindPendingDuplicateLiveCandidates(live, row, account)
                    .OrderBy(item => ParseMasterIdForOrdering(item.MasterId))
                    .ThenBy(item => item.MasterId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var retainedId = row.RetainedTallyMasterId.Trim();
                if (retainedId.Length == 0 && remaining.Length > 0)
                {
                    var linkedRetained = _bankingRepository.GetBankTallyExportMasterId(
                        client.Id, client.FinancialYear, row.RetainedTransactionId, company.CompanyKey);
                    if (linkedRetained.Length > 0 && remaining.Any(item => item.MasterId.Trim().Equals(linkedRetained, StringComparison.OrdinalIgnoreCase)))
                        retainedId = linkedRetained;
                    else if (remaining.Length == 1)
                        retainedId = remaining[0].MasterId.Trim();
                }

                var hasLaterDuplicate = remaining.Any(item =>
                    retainedId.Length > 0 &&
                    !item.MasterId.Trim().Equals(retainedId, StringComparison.OrdinalIgnoreCase));
                var knownRemovedStillLive = (row.KnownTallyMasterId ?? string.Empty)
                    .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(masterId => remaining.Any(item => item.MasterId.Trim().Equals(masterId, StringComparison.OrdinalIgnoreCase)));

                if (!hasLaterDuplicate && !knownRemovedStillLive)
                {
                    _bankingRepository.UpdatePendingTallyDuplicateDelete(
                        row.Id, row.KnownTallyMasterId ?? string.Empty, retainedId, "Resolved",
                        "Fix24 first-import-wins live verification passed: later duplicate MASTERID(s) are absent; the retained first occurrence was not deleted.");
                    result.QueuedTallyDuplicateDeletesResolved++;
                }
                else
                {
                    _bankingRepository.UpdatePendingTallyDuplicateDelete(
                        row.Id, row.KnownTallyMasterId ?? string.Empty, retainedId, "Pending",
                        "Fix24 cleanup partially completed; one or more later duplicate MASTERIDs are still live and will be retried on the next Bank export.");
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result.TallyErrors = 1;
            result.LineError = "Fix24 duplicate MASTERID cleanup/live verification failed: " + exception.Message;
            result.Status = BuildSummary(result, imported: false) + " " + result.LineError;
            return false;
        }

        result.QueuedTallyDuplicateDeletesPending = _bankingRepository
            .GetPendingTallyDuplicateDeletes(client.Id, client.FinancialYear).Count;
        return true;
    }

    private static IEnumerable<TallyVoucherIndexRecord> FindPendingDuplicateLiveCandidates(
        IEnumerable<TallyVoucherIndexRecord> live,
        BankPendingTallyDuplicateDeleteRecord pending,
        BankAccountRecord account)
    {
        var reference = NormalizeLiveMatchText(pending.ReferenceNumber);
        var narration = NormalizeLiveMatchText(pending.Narration);
        var amount = pending.Amount;
        var bankLedger = account.AuthorityName.Trim();
        var strongAccountNumber = BankAccountIdentity.StrongAccountNumber(account.AccountNumber);

        bool MatchesPhysicalBankLedger(string ledgerName)
        {
            var name = ledgerName.Trim();
            if (name.Length == 0) return false;
            if (bankLedger.Length > 0 && TallyMasterNamesEquivalent(name, bankLedger)) return true;
            if (strongAccountNumber.Length == 0) return false;
            var compact = new string(name.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
            return compact.Contains(strongAccountNumber, StringComparison.OrdinalIgnoreCase);
        }

        return live.Where(item =>
        {
            if (item.VoucherDate?.Date != pending.TransactionDate.Date) return false;
            if (Math.Abs(Math.Abs(item.Amount) - amount) > 0.01m) return false;
            if (!TryNormalizeMode(pending.Mode, out var expectedVoucherType) ||
                !item.VoucherType.Equals(expectedVoucherType, StringComparison.OrdinalIgnoreCase))
                return false;

            var debitLedgers = SplitLiveLedgerSet(item.DebitLedgers);
            var creditLedgers = SplitLiveLedgerSet(item.CreditLedgers);
            var bankOnDebit = debitLedgers.Any(MatchesPhysicalBankLedger);
            var bankOnCredit = creditLedgers.Any(MatchesPhysicalBankLedger);
            if (!bankOnDebit && !bankOnCredit)
            {
                // Older/live Tally indexes can occasionally expose only Party Ledger. Accept
                // that fallback only when it still proves the same physical Bank account.
                if (!MatchesPhysicalBankLedger(item.PartyLedger)) return false;
            }
            else
            {
                // Bank-statement Debit is a credit to the Bank Ledger in Tally; statement
                // Credit is a debit to the Bank Ledger. This prevents same-reference/opposite-
                // direction vouchers from being treated as the same physical movement.
                if (pending.Debit > 0m && !bankOnCredit) return false;
                if (pending.Credit > 0m && !bankOnDebit) return false;
            }

            // Fix74 duplicate DELETE candidates use the same strict Bank identity as
            // CREATE/ALTER classification. Both reference and narration must match
            // exactly after whitespace normalization; the top/counterpart Ledger remains
            // the only deliberately excluded field.
            return NormalizeLiveMatchText(item.Reference).Equals(reference, StringComparison.OrdinalIgnoreCase) &&
                   NormalizeLiveMatchText(item.Narration).Equals(narration, StringComparison.OrdinalIgnoreCase);
        });
    }

    private sealed record LocalGroupCreatePlan(
        IReadOnlyList<MasterRecord> GroupsToCreate,
        IReadOnlyList<string> UnresolvedGroups);

    private static LocalGroupCreatePlan BuildMissingManualGroupPlan(
        IEnumerable<string> requiredGroupNames,
        IReadOnlyCollection<MasterRecord> masterRecords,
        IReadOnlyDictionary<string, List<string>> liveGroupNameIndex)
    {
        var localGroups = masterRecords
            .Where(master => master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) &&
                             MasterAuthority.IsLocalEditableSource(master.Source) &&
                             !master.IsArchived &&
                             master.Name.Trim().Length > 0)
            .GroupBy(master => BuildTallyMasterNameKey(master.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var ordered = new List<MasterRecord>();
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddGroup(string requestedName)
        {
            requestedName = (requestedName ?? string.Empty).Trim();
            if (requestedName.Length == 0 || requestedName.Equals("Primary", StringComparison.OrdinalIgnoreCase)) return;
            if (TryResolveExistingTallyMasterName(requestedName, liveGroupNameIndex, out _)) return;

            var key = BuildTallyMasterNameKey(requestedName);
            if (key.Length == 0 || planned.Contains(key) || unresolved.Contains(requestedName)) return;
            if (!visiting.Add(key))
            {
                unresolved.Add(requestedName + " [circular local Group hierarchy]");
                return;
            }

            if (!localGroups.TryGetValue(key, out var candidates) || candidates.Length == 0)
            {
                unresolved.Add(requestedName);
                visiting.Remove(key);
                return;
            }

            if (candidates.Length != 1)
            {
                unresolved.Add(requestedName + " [ambiguous active local Group duplicate]");
                visiting.Remove(key);
                return;
            }
            var group = candidates[0];

            var parent = string.IsNullOrWhiteSpace(group.Parent) ? "Primary" : group.Parent.Trim();
            if (parent.Equals("Primary", StringComparison.OrdinalIgnoreCase))
            {
                unresolved.Add(requestedName +
                    " [Local Group is directly under Primary; automatic Tally CREATE is blocked because the local Master does not store Tally's required Group Nature. Edit the local Group and select an existing accounting parent Group before export.]");
                visiting.Remove(key);
                return;
            }

            if (!TryResolveExistingTallyMasterName(parent, liveGroupNameIndex, out _))
            {
                AddGroup(parent);
                if (!TryResolveExistingTallyMasterName(parent, liveGroupNameIndex, out _) &&
                    !planned.Contains(BuildTallyMasterNameKey(parent)))
                {
                    unresolved.Add(requestedName + $" [parent '{parent}' unavailable]");
                    visiting.Remove(key);
                    return;
                }
            }

            if (planned.Add(key))
                ordered.Add(new MasterRecord
                {
                    PersistentId = group.PersistentId,
                    ClientId = group.ClientId,
                    FinancialYear = group.FinancialYear,
                    Name = group.Name.Trim(),
                    Parent = parent.Equals("Primary", StringComparison.OrdinalIgnoreCase)
                        ? "Primary"
                        : ResolveExistingOrPlannedGroupName(parent, liveGroupNameIndex, ordered),
                    Type = "Group",
                    Company = group.Company,
                    Source = group.Source,
                    IsUserEdited = group.IsUserEdited
                });
            visiting.Remove(key);
        }

        foreach (var required in requiredGroupNames.Distinct(StringComparer.OrdinalIgnoreCase))
            AddGroup(required);

        return new LocalGroupCreatePlan(ordered, unresolved.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static string ResolveExistingOrPlannedGroupName(
        string requestedName,
        IReadOnlyDictionary<string, List<string>> liveGroupNameIndex,
        IReadOnlyCollection<MasterRecord> planned)
    {
        if (TryResolveExistingTallyMasterName(requestedName, liveGroupNameIndex, out var exact)) return exact;
        var key = BuildTallyMasterNameKey(requestedName);
        return planned.FirstOrDefault(group => BuildTallyMasterNameKey(group.Name).Equals(key, StringComparison.OrdinalIgnoreCase))?.Name.Trim()
               ?? requestedName.Trim();
    }

    private static Dictionary<string, List<string>> BuildTallyMasterNameIndex(
        IReadOnlyDictionary<string, string> liveLedgerParents) =>
        BuildTallyMasterNameIndex(liveLedgerParents.Keys);

    private static Dictionary<string, List<string>> BuildTallyMasterNameIndex(
        IEnumerable<string> liveNames)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in liveNames)
        {
            var key = BuildTallyMasterNameKey(name);
            if (key.Length == 0) continue;
            if (!index.TryGetValue(key, out var names))
            {
                names = new List<string>();
                index[key] = names;
            }
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        return index;
    }

    private static bool SameLedgerName(string? first, string? second) =>
        string.Equals((first ?? string.Empty).Trim(), (second ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static string BuildTallyMasterNameKey(string? value)
    {
        // Tally.ERP 9 documents that punctuation/non-relevant characters are ignored
        // for master-name uniqueness (for example CST, C.S.T. and C. S. T.).
        // Keep only letters/digits so the Hub performs the duplicate check BEFORE
        // sending Action=Create. This is intentionally separate from narration logic.
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Trim().Normalize(NormalizationForm.FormKC);
        return new string(normalized
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    private static Dictionary<string, TallyLedgerNameAuthority> BuildExactTallyLedgerAuthorityByMasterId(
        IEnumerable<TallyLedgerNameAuthority> authorities)
    {
        var index = new Dictionary<string, TallyLedgerNameAuthority>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in authorities
                     .Where(authority => authority.MasterId.Trim().Length > 0)
                     .GroupBy(authority => authority.MasterId.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var candidates = group
                .OrderByDescending(authority => authority.AlterId)
                .ThenByDescending(authority => authority.ExactName.Length)
                .ToArray();
            if (candidates.Length == 0) continue;
            var exactNames = candidates.Select(authority => authority.ExactName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (exactNames.Length > 1)
                throw new InvalidOperationException(
                    $"Tally MASTERID {group.Key} returned multiple exact Ledger names in the same live authority read. Voucher export was blocked.");
            index[group.Key] = candidates[0];
        }
        return index;
    }

    private static bool TryResolveExactTallyLedgerAuthorityByMasterId(
        string masterId,
        IReadOnlyDictionary<string, TallyLedgerNameAuthority> index,
        out string exactName)
    {
        exactName = string.Empty;
        masterId = (masterId ?? string.Empty).Trim();
        if (masterId.Length == 0 || !index.TryGetValue(masterId, out var authority))
            return false;
        if (string.IsNullOrWhiteSpace(authority.ExactName))
            return false;
        exactName = authority.ExactName;
        return true;
    }

    private static string ResolveExactTallyLedgerAuthorityByMasterId(
        string masterId,
        string requestedName,
        IReadOnlyDictionary<string, TallyLedgerNameAuthority> index) =>
        TryResolveExactTallyLedgerAuthorityByMasterId(masterId, index, out var exactName)
            ? exactName
            : requestedName;

    private static Dictionary<string, List<TallyLedgerNameAuthority>> BuildExactTallyLedgerAuthorityIndex(
        IEnumerable<TallyLedgerNameAuthority> authorities)
    {
        var index = new Dictionary<string, List<TallyLedgerNameAuthority>>(StringComparer.OrdinalIgnoreCase);
        foreach (var authority in authorities)
        {
            var key = BuildTallyMasterNameKey(authority.DisplayName);
            if (key.Length == 0) continue;
            if (!index.TryGetValue(key, out var values))
            {
                values = new List<TallyLedgerNameAuthority>();
                index[key] = values;
            }
            if (!values.Any(value => value.ExactName.Equals(authority.ExactName, StringComparison.Ordinal)))
                values.Add(authority);
        }
        return index;
    }

    private static bool TryResolveExactTallyLedgerAuthority(
        string requestedName,
        IReadOnlyDictionary<string, List<TallyLedgerNameAuthority>> index,
        out string exactName)
    {
        exactName = string.Empty;
        var key = BuildTallyMasterNameKey(requestedName);
        if (key.Length == 0 || !index.TryGetValue(key, out var matches) || matches.Count == 0)
            return false;

        var exactDisplayMatches = matches
            .Where(item => item.DisplayName.Equals(requestedName.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exactDisplayMatches.Length == 1)
        {
            exactName = exactDisplayMatches[0].ExactName;
            return true;
        }
        if (matches.Count == 1)
        {
            exactName = matches[0].ExactName;
            return true;
        }
        return false;
    }

    private static string ResolveExactTallyLedgerAuthority(
        string requestedName,
        IReadOnlyDictionary<string, List<TallyLedgerNameAuthority>> index) =>
        TryResolveExactTallyLedgerAuthority(requestedName, index, out var exactName)
            ? exactName
            : requestedName;

    private static bool TallyMasterNamesEquivalent(string? first, string? second)
    {
        var firstKey = BuildTallyMasterNameKey(first);
        return firstKey.Length > 0 &&
               firstKey.Equals(BuildTallyMasterNameKey(second), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveExistingTallyMasterName(
        string requestedName,
        IReadOnlyDictionary<string, List<string>> liveLedgerNameIndex,
        out string exactTallyName)
    {
        exactTallyName = string.Empty;
        var key = BuildTallyMasterNameKey(requestedName);
        if (key.Length == 0 || !liveLedgerNameIndex.TryGetValue(key, out var matches) || matches.Count == 0)
            return false;

        var exact = matches.FirstOrDefault(name =>
            name.Equals(requestedName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(exact))
        {
            exactTallyName = exact;
            return true;
        }

        if (matches.Count == 1)
        {
            exactTallyName = matches[0];
            return true;
        }

        throw new InvalidOperationException(
            $"Tally master preflight found multiple live Masters equivalent to '{requestedName}': " +
            string.Join(", ", matches) +
            ". No master was created or altered because the existing Tally identity is ambiguous.");
    }

    private static string ResolveTallyLedgerName(
        string requestedName,
        IReadOnlyDictionary<string, List<string>> liveLedgerNameIndex,
        IDictionary<string, string> batchCanonicalNames)
    {
        requestedName = (requestedName ?? string.Empty).Trim();
        if (requestedName.Length == 0) return string.Empty;

        if (TryResolveExistingTallyMasterName(
                requestedName, liveLedgerNameIndex, out var existingName))
            return existingName;

        var key = BuildTallyMasterNameKey(requestedName);
        if (key.Length == 0) return requestedName;
        if (batchCanonicalNames.TryGetValue(key, out var canonical)) return canonical;
        batchCanonicalNames[key] = requestedName;
        return requestedName;
    }

    private static string ResolveExistingTallyLedgerName(
        string requestedName,
        IReadOnlyDictionary<string, List<string>> liveLedgerNameIndex)
    {
        return TryResolveExistingTallyMasterName(
            requestedName, liveLedgerNameIndex, out var exactName)
            ? exactName
            : requestedName.Trim();
    }

    private static void RememberEquivalentTallyReuse(
        IDictionary<string, string> reused,
        string requestedName,
        string resolvedName)
    {
        requestedName = (requestedName ?? string.Empty).Trim();
        resolvedName = (resolvedName ?? string.Empty).Trim();
        if (requestedName.Length == 0 || resolvedName.Length == 0 ||
            requestedName.Equals(resolvedName, StringComparison.OrdinalIgnoreCase))
            return;

        reused[requestedName] = resolvedName;
    }

    private static RequiredLedger[] BuildRequiredLedgerSpecs(
        IReadOnlyCollection<PreparedBankVoucher> vouchers,
        IReadOnlyDictionary<string, BankAccountRecord> bankAccounts,
        IReadOnlyDictionary<string, AccountingAutomationHub.Domain.Masters.MasterRecord> masterLedgers,
        IReadOnlyDictionary<string, string> ledgerGroupHints)
    {
        var names = vouchers
            .SelectMany(voucher => new[] { voucher.BankLedger, voucher.CounterpartLedger })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var specs = new List<RequiredLedger>(names.Length);
        foreach (var name in names)
        {
            if (name.Equals("Suspense A/c", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Suspense A/C", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Tally XML contract violation: Suspense A/c is a Group and cannot be used as a voucher Ledger.");
            }

            var masterKey = BuildTallyMasterNameKey(name);
            masterLedgers.TryGetValue(masterKey, out var stored);
            var localSource = stored?.Source?.Trim() ?? string.Empty;
            var canCreateFromLocalMaster = stored is not null &&
                !stored.IsArchived &&
                stored.TallyMasterId.Trim().Length == 0 &&
                stored.IsUserEdited;

            if (bankAccounts.TryGetValue(masterKey, out var bankAccount))
            {
                // Fix77: an explicit local/user Master row is the Parent authority for an
                // independent Bank Ledger CREATE. Do not recalculate its group from Bank
                // Account metadata (OD limit/account type), because the user may have
                // deliberately moved the new Master from Bank OD A/c to Bank Accounts.
                // Bank-account-derived parent remains only the fallback when no explicit
                // local Master parent exists.
                var explicitLocalParent = stored is not null &&
                                          !stored.IsArchived &&
                                          stored.TallyMasterId.Trim().Length == 0 &&
                                          stored.IsUserEdited &&
                                          stored.Parent.Trim().Length > 0
                    ? stored.Parent.Trim()
                    : string.Empty;
                var bankParent = explicitLocalParent.Length > 0
                    ? explicitLocalParent
                    : ResolveBankParent(bankAccount);
                specs.Add(new RequiredLedger(
                    name, bankParent, true,
                    canCreateFromLocalMaster ? localSource : "Bank Account", true));
                continue;
            }

            var parent = stored?.Parent?.Trim() ?? string.Empty;
            var hasExplicitMappingGroup = ledgerGroupHints.TryGetValue(masterKey, out var mappedGroup) &&
                                          !string.IsNullOrWhiteSpace(mappedGroup);
            if ((parent.Length == 0 || MasterAuthority.IsTallySource(stored?.Source)) && hasExplicitMappingGroup)
                parent = mappedGroup!.Trim();

            if (name.Equals(UnmappedBankSuspenseLedger, StringComparison.OrdinalIgnoreCase))
            {
                parent = "Suspense A/c";
            }
            else if (name.Equals("Cash", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("Cash A/c", StringComparison.OrdinalIgnoreCase))
            {
                parent = "Cash-in-hand";
            }
            else if (parent.Length == 0)
            {
                var use = vouchers.First(voucher =>
                    voucher.BankLedger.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    voucher.CounterpartLedger.Equals(name, StringComparison.OrdinalIgnoreCase));
                parent = ResolveCounterpartFallbackGroup(use.VoucherType, name);
            }

            if (parent.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Tally XML contract violation: no Ledger Group could be resolved for '{name}'.");
            }

            var mappedAuthority = hasExplicitMappingGroup;
            var deterministicLocalAuthority =
                name.Equals(UnmappedBankSuspenseLedger, StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Cash", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Cash A/c", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Bank Charges", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Bank Interest", StringComparison.OrdinalIgnoreCase);
            var canCreate = canCreateFromLocalMaster || mappedAuthority || deterministicLocalAuthority;
            var effectiveSource = canCreateFromLocalMaster
                ? localSource
                : mappedAuthority
                    ? "Ledger Mapping"
                    : deterministicLocalAuthority
                        ? "Banking"
                        : localSource;

            specs.Add(new RequiredLedger(
                name, parent, false, effectiveSource, canCreate));
        }

        return specs.ToArray();
    }

    private void MaterializeBankAuthorityMasters(
        ClientProfile client,
        IReadOnlyCollection<RequiredLedger> requiredLedgers,
        IReadOnlyDictionary<string, MasterRecord[]> existingCandidates)
    {
        var local = new List<MasterRecord>();
        foreach (var ledger in requiredLedgers.Where(item => item.CanCreateFromLocalMaster))
        {
            var key = BuildTallyMasterNameKey(ledger.Name);
            var activeLocalExists = existingCandidates.TryGetValue(key, out var candidates) &&
                                    candidates.Any(candidate =>
                                        !candidate.IsArchived &&
                                        candidate.TallyMasterId.Trim().Length == 0 &&
                                        candidate.IsUserEdited);
            if (activeLocalExists) continue;

            local.Add(new MasterRecord
            {
                ClientId = client.Id,
                FinancialYear = client.FinancialYear,
                Company = string.IsNullOrWhiteSpace(client.LegalName) ? client.ClientName : client.LegalName,
                Name = ledger.Name.Trim(),
                Parent = ledger.Parent.Trim(),
                Type = "Ledger",
                Source = ledger.LocalSource.Length > 0 ? ledger.LocalSource : "Banking",
                IsUserEdited = true
            });
        }

        if (local.Count > 0)
            _masterRepository.Upsert(client.Id, client.FinancialYear, local, markUserEdited: true);
    }

    private static string BuildErp9LedgerCreateXml(
        string companyName,
        IReadOnlyCollection<RequiredLedger> ledgers)
    {
        var builder = new StringBuilder();
        TallyExcelParityXmlWriter.BeginAllMastersImport(builder, companyName);
        foreach (var ledger in ledgers
                     .Where(item => !string.IsNullOrWhiteSpace(item.Name) &&
                                    !string.IsNullOrWhiteSpace(item.Parent))
                     .GroupBy(item => BuildTallyMasterNameKey(item.Name), StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            TallyExcelParityXmlWriter.AppendErp9MinimalLedgerMaster(
                builder, ledger.Name.Trim(), ledger.Parent.Trim(), "Create");
        }
        TallyExcelParityXmlWriter.EndAllMastersImport(builder);
        return builder.ToString();
    }

    private static string BuildErp9VoucherImportXml(
        string companyName,
        IReadOnlyCollection<TallyErp9BankVoucherExpectation> vouchers)
    {
        // Exact live Ledger authority has already been captured during preflight.
        // Use the Bank-only writer that preserves trailing spaces/CR/LF instead of
        // the general workbook writer, whose normal string contract intentionally trims.
        return TallyErp9ExactBankVoucherXmlBuilder.Build(companyName, vouchers);
    }

    private static IReadOnlyCollection<TallyErp9BankLedgerLine> BuildErp9Entries(
        PreparedBankVoucher voucher)
    {
        var transaction = voucher.Transaction;
        var amount = transaction.Amount;

        return voucher.VoucherType.ToUpperInvariant() switch
        {
            "PAYMENT" =>
            [
                new TallyErp9BankLedgerLine(voucher.CounterpartLedger, amount, true),
                new TallyErp9BankLedgerLine(voucher.BankLedger, amount, false)
            ],
            "RECEIPT" =>
            [
                new TallyErp9BankLedgerLine(voucher.CounterpartLedger, amount, false),
                new TallyErp9BankLedgerLine(voucher.BankLedger, amount, true)
            ],
            "CONTRA" when transaction.Credit > 0 =>
            [
                new TallyErp9BankLedgerLine(voucher.BankLedger, amount, true),
                new TallyErp9BankLedgerLine(voucher.CounterpartLedger, amount, false)
            ],
            "CONTRA" =>
            [
                new TallyErp9BankLedgerLine(voucher.CounterpartLedger, amount, true),
                new TallyErp9BankLedgerLine(voucher.BankLedger, amount, false)
            ],
            _ => []
        };
    }

    private static string ResolveBankParent(BankAccountRecord account)
    {
        var accountType = ResolveEffectiveBankAccountType(account);
        return accountType.Equals("Over Draft", StringComparison.OrdinalIgnoreCase) ||
               accountType.Equals("Cash Credit", StringComparison.OrdinalIgnoreCase)
            ? "Bank OD A/c"
            : "Bank Accounts";
    }

    private static string ResolveEffectiveBankAccountType(BankAccountRecord account)
    {
        // Existing Hub data can contain a stale/default AccountType even when the
        // authoritative Bank Ledger name already carries the user's SB/CA/OD/CC
        // classification. Prefer the explicit ledger-name token when available.
        var authority = (" " + (account.AuthorityName ?? string.Empty).Trim().ToUpperInvariant() + " ");
        if (authority.Contains(" OD -", StringComparison.Ordinal) ||
            authority.Contains(" OD ", StringComparison.Ordinal) ||
            authority.Contains(" OVERDRAFT ", StringComparison.Ordinal))
            return "Over Draft";
        if (authority.Contains(" CC -", StringComparison.Ordinal) ||
            authority.Contains(" OCC -", StringComparison.Ordinal) ||
            authority.Contains(" CASH CREDIT ", StringComparison.Ordinal))
            return "Cash Credit";
        if (authority.Contains(" SB -", StringComparison.Ordinal) ||
            authority.Contains(" SA -", StringComparison.Ordinal) ||
            authority.Contains(" SAVINGS ", StringComparison.Ordinal) ||
            authority.Contains(" SAVING ", StringComparison.Ordinal))
            return "Saving Account";
        if (authority.Contains(" CA -", StringComparison.Ordinal) ||
            authority.Contains(" CURRENT ", StringComparison.Ordinal))
            return "Current Account";

        var normalized = (account.AccountType ?? string.Empty).Trim().ToUpperInvariant();
        return normalized switch
        {
            "OD" or "OVERDRAFT" or "OVER DRAFT" or "OVERDRAFT ACCOUNT" or "OVER DRAFT ACCOUNT" => "Over Draft",
            "CC" or "OCC" or "CASH CREDIT" or "CASH CREDIT ACCOUNT" => "Cash Credit",
            "SB" or "SA" or "SAVING" or "SAVINGS" or "SAVING ACCOUNT" or "SAVINGS ACCOUNT" => "Saving Account",
            "CA" or "CURRENT" or "CURRENT ACCOUNT" => "Current Account",
            _ => account.AccountType?.Trim() ?? string.Empty
        };
    }

    private static bool IsPaymentReturnTransaction(BankTransactionRecord transaction)
    {
        var summary = ExcelLedgerMappingParser.BuildNormalizedSummary(
            transaction.Narration ?? string.Empty,
            transaction.Mode ?? string.Empty).Trim();
        if (summary.Equals("BANK CHARGES", StringComparison.OrdinalIgnoreCase))
            return false;
        if (summary.Equals("UPI RETURN", StringComparison.OrdinalIgnoreCase) ||
            summary.Equals("CHEQUE RETURN", StringComparison.OrdinalIgnoreCase))
            return true;

        var text = string.Join(' ', (transaction.Narration ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToUpperInvariant();
        if (text.Length == 0) return false;
        return ContainsWholeWord(text, "REJECT") ||
               ContainsWholeWord(text, "REJECTED") ||
               text.Contains("RETURNED CHEQUE", StringComparison.Ordinal) ||
               text.Contains("CHEQUE RETURN", StringComparison.Ordinal) ||
               text.Contains("CHQ RETURN", StringComparison.Ordinal) ||
               text.Contains("UPI RETURN", StringComparison.Ordinal) ||
               text.Contains("UPI REVERSAL", StringComparison.Ordinal) ||
               text.Contains("UPI REVERSE", StringComparison.Ordinal) ||
               text.Contains("REV-UPI", StringComparison.Ordinal) ||
               text.Contains("REV UPI", StringComparison.Ordinal);
    }

    private static bool TryResolvePaymentReturnTreatment(
        BankTransactionRecord transaction,
        IReadOnlyDictionary<long, BankTransactionRecord> allTransactions,
        string currentBankLedger,
        out string counterpartLedger,
        out string detail)
    {
        counterpartLedger = string.Empty;
        detail = string.Empty;

        bool IsUsableOriginal(BankTransactionRecord candidate)
        {
            if (candidate.Id == transaction.Id ||
                candidate.BankAccountId != transaction.BankAccountId ||
                Math.Abs(candidate.Amount - transaction.Amount) > 0.01m ||
                candidate.TransactionDate.Date > transaction.TransactionDate.Date)
                return false;

            // The original and return must move the Bank in opposite directions.
            var oppositeDirection = transaction.Credit > 0m
                ? candidate.Debit > 0m
                : transaction.Debit > 0m && candidate.Credit > 0m;
            if (!oppositeDirection || IsPaymentReturnTransaction(candidate)) return false;

            var ledger = candidate.LedgerName?.Trim() ?? string.Empty;
            return ledger.Length > 0 && !TallyMasterNamesEquivalent(ledger, currentBankLedger);
        }

        BankTransactionRecord? original = null;

        // A Payment Returns workflow may already carry an explicit same-bank match.
        // Honour that authority first. Inter-bank matches are handled later and are
        // deliberately excluded here by requiring the same BankAccountId.
        if (transaction.MatchedTransactionId is long matchedId && matchedId > 0 &&
            allTransactions.TryGetValue(matchedId, out var matched) &&
            IsUsableOriginal(matched))
        {
            original = matched;
        }

        var candidates = allTransactions.Values
            .Where(IsUsableOriginal)
            .Where(candidate =>
                (transaction.TransactionDate.Date - candidate.TransactionDate.Date).TotalDays <= 90)
            .ToArray();

        if (original is null)
        {
            var reference = NormalizeLiveMatchText(transaction.ReferenceNumber);
            if (reference.Length > 0)
            {
                var referenceMatches = candidates.Where(candidate =>
                        NormalizeLiveMatchText(candidate.ReferenceNumber)
                            .Equals(reference, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(candidate => candidate.TransactionDate)
                    .ToArray();
                if (referenceMatches.Length == 1) original = referenceMatches[0];
            }
        }

        // Some banks omit a structured Reference on the return row. Amount + same
        // Bank + opposite direction is accepted only when one unique recent original
        // exists, so a repeated round amount can never be guessed silently.
        if (original is null)
        {
            var recent = candidates
                .Where(candidate =>
                    (transaction.TransactionDate.Date - candidate.TransactionDate.Date).TotalDays <= 14)
                .OrderByDescending(candidate => candidate.TransactionDate)
                .ToArray();
            if (recent.Length == 1) original = recent[0];
        }

        if (original is null) return false;
        counterpartLedger = original.LedgerName.Trim();
        if (counterpartLedger.Length == 0 ||
            TallyMasterNamesEquivalent(counterpartLedger, currentBankLedger))
        {
            counterpartLedger = string.Empty;
            return false;
        }

        detail =
            $"{transaction.TransactionDate:dd-MMM-yyyy} | PAYMENT RETURN | reversed original " +
            $"{original.TransactionDate:dd-MMM-yyyy} {original.TransactionId} against Ledger '{counterpartLedger}'.";
        return true;
    }

    private static bool ContainsWholeWord(string text, string token)
    {
        var padded = " " + text.Replace('/', ' ').Replace('-', ' ').Replace(':', ' ') + " ";
        return padded.Contains(" " + token + " ", StringComparison.Ordinal);
    }

    private static bool TryResolveInterbankTreatment(
        BankTransactionRecord transaction,
        IReadOnlyDictionary<long, BankAccountRecord> accounts,
        IReadOnlyDictionary<long, BankTransactionRecord> allTransactions,
        IReadOnlyDictionary<long, BankTransferMatch> transferByTransactionId,
        out string counterpartBankLedger)
    {
        counterpartBankLedger = string.Empty;
        long otherTransactionId = 0;
        if (transferByTransactionId.TryGetValue(transaction.Id, out var match))
        {
            otherTransactionId = match.SourceTransactionId == transaction.Id
                ? match.TargetTransactionId
                : match.SourceTransactionId;
        }
        else if (transaction.MatchedTransactionId is long matchedId && matchedId > 0)
        {
            otherTransactionId = matchedId;
        }

        if (otherTransactionId <= 0 ||
            !allTransactions.TryGetValue(otherTransactionId, out var otherTransaction) ||
            otherTransaction.BankAccountId == transaction.BankAccountId ||
            !accounts.TryGetValue(otherTransaction.BankAccountId, out var otherAccount))
            return false;

        counterpartBankLedger = otherAccount.AuthorityName.Trim();
        return counterpartBankLedger.Length > 0;
    }

    private static LiveBankVoucherMatch ClassifyLiveBankVoucher(
        IEnumerable<TallyVoucherIndexRecord> live,
        BankTransactionRecord transaction,
        string voucherType,
        string counterpartLedger,
        string bankLedger,
        string reference,
        bool sourceWasPreviouslyImported = false,
        IReadOnlySet<string>? reservedLiveMasterIds = null,
        IReadOnlyDictionary<string, string>? claimedImportedLiveIdentityMasters = null)
    {
        var amount = transaction.Amount;
        var sameDateAmount = live.Where(item =>
                item.VoucherDate?.Date == transaction.TransactionDate.Date &&
                Math.Abs(Math.Abs(item.Amount) - amount) <= 0.01m &&
                (reservedLiveMasterIds is null || item.MasterId.Trim().Length == 0 ||
                 !reservedLiveMasterIds.Contains(item.MasterId.Trim())))
            .ToArray();
        if (sameDateAmount.Length == 0) return LiveBankVoucherMatch.None;

        var normalizedReference = NormalizeLiveMatchText(reference);
        var sourceNarration = NormalizeLiveMatchText(transaction.Narration);

        // Fix74 strict Bank first-pass identity. Every stable Bank-source field must
        // match exactly: date, amount, Voucher Type, Bank Ledger/direction, reference
        // and narration. ONLY the selected top/counterpart Ledger is intentionally
        // excluded from this first-pass identity so a mapping/treatment change can be
        // recognized as an ALTER of the same physical Bank transaction.
        var sameBank = sameDateAmount
            .Where(item => LiveBankLedgerIdentityMatches(item, transaction, bankLedger))
            .ToArray();
        if (sameBank.Length == 0)
        {
            // A same-date/amount/text row with no usable Ledger evidence is not safe
            // CREATE authority. Block rather than guessing across Bank accounts.
            var unverifiable = sameDateAmount.FirstOrDefault(item =>
                StrictBankTextMatches(normalizedReference, sourceNarration, item) &&
                !LiveVoucherHasLedgerEvidence(item));
            return unverifiable is null
                ? LiveBankVoucherMatch.None
                : new LiveBankVoucherMatch(
                    LiveBankVoucherMatchKind.Conflict,
                    unverifiable,
                    "Strict Bank date/amount/text identity exists but Tally exposed no reliable Bank-Ledger evidence; CREATE is blocked.");
        }

        var strictPhysical = sameBank
            .Where(item => item.VoucherType.Equals(voucherType, StringComparison.OrdinalIgnoreCase) &&
                           StrictBankTextMatches(normalizedReference, sourceNarration, item))
            .ToArray();

        // Same Bank/date/amount/text but a different Voucher Type is not a duplicate.
        // For a previously imported source it may be the immediately previous version,
        // so surface it as a treatment-update candidate and let the caller verify the
        // saved previous version before ALTER. For an unowned source, fail closed.
        if (strictPhysical.Length == 0)
        {
            var differentType = sameBank
                .Where(item => !item.VoucherType.Equals(voucherType, StringComparison.OrdinalIgnoreCase) &&
                               StrictBankTextMatches(normalizedReference, sourceNarration, item))
                .ToArray();
            if (differentType.Length == 1 && differentType[0].MasterId.Trim().Length > 0)
            {
                return sourceWasPreviouslyImported
                    ? new LiveBankVoucherMatch(
                        LiveBankVoucherMatchKind.TreatmentUpdate,
                        differentType[0],
                        "Previously imported Bank source matches every first-pass identity field except Voucher Type; previous-version verification is required before ALTER.")
                    : new LiveBankVoucherMatch(
                        LiveBankVoucherMatchKind.Conflict,
                        differentType[0],
                        "Same Bank/date/amount/reference/narration exists under a different Voucher Type; it is not an exact duplicate and CREATE is blocked until ownership is resolved.");
            }
            if (differentType.Length > 1)
                return new LiveBankVoucherMatch(
                    LiveBankVoucherMatchKind.Conflict,
                    differentType[0],
                    "Multiple live vouchers match Bank/date/amount/reference/narration but use different Voucher Types; identity is ambiguous and CREATE is blocked.");

            // No fuzzy narration/reference/accounting-shape fallback is allowed. A row
            // with different text is not an exact duplicate. If the source is already
            // MASTERID-bound, the caller handles changes through previous-version ->
            // desired-version verification before this classifier is consulted.
            return LiveBankVoucherMatch.None;
        }

        var exactCurrentTreatment = strictPhysical
            .Where(item => LiveBankLedgerSidesCompatible(
                item, transaction, voucherType, counterpartLedger, bankLedger))
            .ToArray();

        if (exactCurrentTreatment.Length == 1)
        {
            var candidate = exactCurrentTreatment[0];
            var liveIdentityKey = BuildLiveOccurrenceIdentityKey(candidate, bankLedger);

            // First-import-wins duplicate replacement remains, but only for rows whose
            // complete strict first-pass Bank identity is identical. The mutable top
            // Ledger is not part of the occurrence key by design.
            if (!sourceWasPreviouslyImported &&
                liveIdentityKey.Length > 0 &&
                candidate.MasterId.Trim().Length > 0 &&
                claimedImportedLiveIdentityMasters is not null &&
                claimedImportedLiveIdentityMasters.TryGetValue(liveIdentityKey, out var retainedMasterId) &&
                IsStrictlyNewerMasterId(candidate.MasterId, retainedMasterId))
            {
                return new LiveBankVoucherMatch(
                    LiveBankVoucherMatchKind.ReplaceManualDuplicate,
                    candidate,
                    $"Later strict-identical live MASTERID {candidate.MasterId.Trim()} duplicates retained AAH MASTERID {retainedMasterId}; delete only the later duplicate after fresh verification.");
            }

            return new LiveBankVoucherMatch(
                LiveBankVoucherMatchKind.Exact,
                candidate,
                "Fresh live Tally row matches the complete strict Bank identity and current top-Ledger treatment.");
        }

        if (exactCurrentTreatment.Length > 1)
        {
            if (sourceWasPreviouslyImported)
            {
                var candidate = exactCurrentTreatment
                    .OrderBy(item => ParseMasterIdForOrdering(item.MasterId))
                    .ThenBy(item => item.MasterId, StringComparer.OrdinalIgnoreCase)
                    .First();
                return new LiveBankVoucherMatch(
                    LiveBankVoucherMatchKind.Exact,
                    candidate,
                    "Previously imported source claimed the oldest MASTERID among multiple strict-identical live occurrences; later occurrences remain duplicate candidates.");
            }

            return new LiveBankVoucherMatch(
                LiveBankVoucherMatchKind.Conflict,
                exactCurrentTreatment[0],
                "More than one unclaimed live voucher matches the complete strict Bank identity and current treatment.");
        }

        // At this point every stable first-pass identity field matches and only the
        // selected top/counterpart Ledger treatment differs. This is exactly the one
        // Bank-only exception requested. Never call it an exact duplicate. A previously
        // imported source may ALTER the unique MASTERID only after the caller confirms
        // that its live state equals AAH's saved previous verified version.
        if (strictPhysical.Length == 1)
        {
            var candidate = strictPhysical[0];
            if (candidate.MasterId.Trim().Length == 0)
                return new LiveBankVoucherMatch(
                    LiveBankVoucherMatchKind.Conflict,
                    candidate,
                    "Strict Bank identity matched but Tally did not provide MASTERID; top-Ledger modification cannot be performed safely.");

            return sourceWasPreviouslyImported
                ? new LiveBankVoucherMatch(
                    LiveBankVoucherMatchKind.TreatmentUpdate,
                    candidate,
                    "Complete strict Bank identity matched the same physical transaction; only the top/counterpart Ledger treatment differs. Verify previous version, then ALTER this MASTERID.")
                : new LiveBankVoucherMatch(
                    LiveBankVoucherMatchKind.Conflict,
                    candidate,
                    "Complete strict Bank identity already exists with a different top/counterpart Ledger, but this source has no proven prior ownership; CREATE is blocked.");
        }

        return new LiveBankVoucherMatch(
            LiveBankVoucherMatchKind.Conflict,
            strictPhysical[0],
            "Multiple live vouchers match the complete strict Bank first-pass identity; top-Ledger treatment cannot be resolved safely.");
    }

    private static string BuildLiveOccurrenceIdentityKey(
        TallyVoucherIndexRecord live,
        string bankLedger = "")
    {
        // Fix72: physical Bank identity includes the Bank-account Ledger but excludes
        // the selected/top/counterpart Ledger. Do not use the full Ledger sets or
        // ContentHash: the counterpart is intentionally mutable treatment.
        var expectedBank = NormalizeLiveMatchText(bankLedger).ToUpperInvariant();
        var liveBankMarker = expectedBank.Length == 0
            ? string.Empty
            : LiveVoucherContainsLedger(live, bankLedger)
                ? expectedBank
                : "<BANK-MISMATCH>";
        return string.Join("|",
            live.VoucherDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty,
            NormalizeLiveMatchText(live.VoucherType).ToUpperInvariant(),
            Math.Abs(live.Amount).ToString("0.00", CultureInfo.InvariantCulture),
            liveBankMarker,
            NormalizeLiveMatchText(live.Reference).ToUpperInvariant(),
            NormalizeLiveMatchText(live.Narration).ToUpperInvariant());
    }

    private static long ParseMasterIdForOrdering(string? masterId) =>
        long.TryParse((masterId ?? string.Empty).Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value)
            ? value
            : long.MaxValue;

    private static bool IsStrictlyNewerMasterId(string? candidateMasterId, string? retainedMasterId)
    {
        if (!long.TryParse((candidateMasterId ?? string.Empty).Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var candidate) ||
            !long.TryParse((retainedMasterId ?? string.Empty).Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var retained))
            return false;
        return candidate > retained;
    }

    private static bool StrictBankTextMatches(
        string normalizedReference,
        string normalizedSourceNarration,
        TallyVoucherIndexRecord live)
    {
        var liveReference = NormalizeLiveMatchText(live.Reference);
        var liveNarration = NormalizeLiveMatchText(live.Narration);
        return liveReference.Equals(normalizedReference, StringComparison.OrdinalIgnoreCase) &&
               liveNarration.Equals(normalizedSourceNarration, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddConflictDetail(
        TallyBankImportResult result,
        BankTransactionRecord transaction,
        string voucherType,
        string bankLedger,
        string counterpartLedger,
        string sourceKey,
        LiveBankVoucherMatch match)
    {
        if (result.ConflictDetails.Count >= ConflictDetailLimit)
        {
            if (!result.ConflictDetails.Contains(
                    $"Additional conflict rows omitted after first {ConflictDetailLimit:N0} details.",
                    StringComparer.OrdinalIgnoreCase))
            {
                result.ConflictDetails.Add(
                    $"Additional conflict rows omitted after first {ConflictDetailLimit:N0} details.");
            }
            return;
        }

        var live = match.Record;
        var liveIdentity = live is null
            ? "live row unavailable"
            : $"MASTERID {ShortConflictText(live.MasterId, 32)} | {ShortConflictText(live.VoucherType, 32)} | " +
              $"DR [{ShortConflictText(live.DebitLedgers, 96)}] | CR [{ShortConflictText(live.CreditLedgers, 96)}] | " +
              $"Ref [{ShortConflictText(live.Reference, 64)}] | Narr [{ShortConflictText(live.Narration, 96)}]";

        result.ConflictDetails.Add(
            $"{transaction.TransactionDate:dd-MMM-yyyy} | {transaction.Amount.ToString("0.00", CultureInfo.InvariantCulture)} | " +
            $"Source {ShortConflictText(sourceKey, 72)} | Expected {voucherType}: " +
            $"{ShortConflictText(bankLedger, 72)} <-> {ShortConflictText(counterpartLedger, 72)} | " +
            $"{match.Reason} | Live {liveIdentity}");
    }

    private static void AddBatchConflictDetail(
        TallyBankImportResult result,
        BankTransactionRecord transaction,
        string sourceKey)
    {
        if (result.ConflictDetails.Count >= ConflictDetailLimit)
        {
            if (!result.ConflictDetails.Contains(
                    $"Additional conflict rows omitted after first {ConflictDetailLimit:N0} details.",
                    StringComparer.OrdinalIgnoreCase))
            {
                result.ConflictDetails.Add(
                    $"Additional conflict rows omitted after first {ConflictDetailLimit:N0} details.");
            }
            return;
        }

        result.ConflictDetails.Add(
            $"{transaction.TransactionDate:dd-MMM-yyyy} | {transaction.Amount.ToString("0.00", CultureInfo.InvariantCulture)} | " +
            $"Source {ShortConflictText(sourceKey, 72)} appears more than once in this export with different content.");
    }

    private static string ShortConflictText(string? value, int maxLength)
    {
        var normalized = NormalizeLiveMatchText(value);
        if (normalized.Length <= maxLength) return normalized;
        return normalized[..Math.Max(0, maxLength - 3)] + "...";
    }

    private static bool LiveVoucherContainsLedger(TallyVoucherIndexRecord item, string ledger)
    {
        if (ledger.Trim().Length == 0) return false;
        var debitLedgers = SplitLiveLedgerSet(item.DebitLedgers);
        var creditLedgers = SplitLiveLedgerSet(item.CreditLedgers);
        if (debitLedgers.Count > 0 || creditLedgers.Count > 0)
            return debitLedgers.Concat(creditLedgers).Any(name => TallyMasterNamesEquivalent(name, ledger));

        return item.PartyLedger.Length > 0 && TallyMasterNamesEquivalent(item.PartyLedger, ledger);
    }

    private static bool LiveBankLedgerIdentityMatches(
        TallyVoucherIndexRecord item,
        BankTransactionRecord transaction,
        string bankLedger)
    {
        if (bankLedger.Trim().Length == 0) return false;
        var debitLedgers = SplitLiveLedgerSet(item.DebitLedgers);
        var creditLedgers = SplitLiveLedgerSet(item.CreditLedgers);
        if (debitLedgers.Count > 0 || creditLedgers.Count > 0)
        {
            // Bank-statement Debit means the Bank Ledger is credited in Tally;
            // statement Credit means the Bank Ledger is debited. This direction is
            // part of the physical bank movement and is independent of the mutable
            // selected/top Ledger treatment.
            if (transaction.Debit > 0m)
                return creditLedgers.Any(name => TallyMasterNamesEquivalent(name, bankLedger));
            if (transaction.Credit > 0m)
                return debitLedgers.Any(name => TallyMasterNamesEquivalent(name, bankLedger));
            return debitLedgers.Concat(creditLedgers)
                .Any(name => TallyMasterNamesEquivalent(name, bankLedger));
        }

        return item.PartyLedger.Trim().Length > 0 &&
               TallyMasterNamesEquivalent(item.PartyLedger, bankLedger);
    }

    private static bool LiveVoucherHasLedgerEvidence(TallyVoucherIndexRecord item) =>
        SplitLiveLedgerSet(item.DebitLedgers).Count > 0 ||
        SplitLiveLedgerSet(item.CreditLedgers).Count > 0 ||
        item.PartyLedger.Trim().Length > 0;

    private static bool LiveBankTreatmentMatches(
        TallyVoucherIndexRecord item,
        BankTransactionRecord transaction,
        string voucherType,
        string bankLedger,
        string counterpartLedger) =>
        item.VoucherType.Equals(voucherType, StringComparison.OrdinalIgnoreCase) &&
        LiveBankLedgerSidesCompatible(item, transaction, voucherType, counterpartLedger, bankLedger);

    private static bool LiveBankLedgerSidesCompatible(
        TallyVoucherIndexRecord item,
        BankTransactionRecord transaction,
        string voucherType,
        string counterpartLedger,
        string bankLedger)
    {
        var debitLedgers = SplitLiveLedgerSet(item.DebitLedgers);
        var creditLedgers = SplitLiveLedgerSet(item.CreditLedgers);
        if (debitLedgers.Count > 0 || creditLedgers.Count > 0)
        {
            string expectedDebit;
            string expectedCredit;
            if (voucherType.Equals("Payment", StringComparison.OrdinalIgnoreCase))
            {
                expectedDebit = counterpartLedger; expectedCredit = bankLedger;
            }
            else if (voucherType.Equals("Receipt", StringComparison.OrdinalIgnoreCase))
            {
                expectedDebit = bankLedger; expectedCredit = counterpartLedger;
            }
            else if (voucherType.Equals("Contra", StringComparison.OrdinalIgnoreCase) && transaction.Credit > 0)
            {
                expectedDebit = bankLedger; expectedCredit = counterpartLedger;
            }
            else
            {
                expectedDebit = counterpartLedger; expectedCredit = bankLedger;
            }

            return debitLedgers.Any(name => TallyMasterNamesEquivalent(name, expectedDebit)) &&
                   creditLedgers.Any(name => TallyMasterNamesEquivalent(name, expectedCredit));
        }

        // Legacy response fallback when Ledger Entries were not returned. PartyLedger
        // may itself be blank for Bank vouchers, so do not reject an otherwise valid
        // candidate solely for an absent PartyLedger.
        return item.PartyLedger.Length == 0 ||
               TallyMasterNamesEquivalent(item.PartyLedger, counterpartLedger) ||
               TallyMasterNamesEquivalent(item.PartyLedger, bankLedger);
    }

    private static IReadOnlyList<string> SplitLiveLedgerSet(string? value) =>
        (value ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string NormalizeLiveMatchText(string? value) =>
        string.Join(' ', (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool TryNormalizeMode(
        string mode,
        out string voucherType)
    {
        var normalized = (mode ?? string.Empty).Trim().ToUpperInvariant();
        voucherType = normalized switch
        {
            "PAYMENT" => "Payment",
            "RECEIPT" => "Receipt",
            "CONTRA" => "Contra",
            _ => string.Empty
        };
        return voucherType.Length > 0;
    }

    private static (DateTime From, DateTime To) GetFinancialYearBounds(string financialYear, DateTime fallbackFrom, DateTime fallbackTo)
    {
        var parts = (financialYear ?? string.Empty).Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 1 && int.TryParse(parts[0], out var startYear) && startYear >= 1900 && startYear <= 9998)
            return (new DateTime(startYear, 4, 1), new DateTime(startYear + 1, 3, 31));
        return (fallbackFrom.Date, fallbackTo.Date);
    }

    private static string BuildSummary(
        TallyBankImportResult result,
        bool imported)
    {
        var acceptedByTally = Math.Max(0, result.TallyCreated + result.TallyAltered);
        var action = imported && result.UnverifiedAfterImport > 0
            ? $"Tally accepted {result.ImportedTransactions:N0} Bank Statement voucher(s); {result.UnverifiedAfterImport:N0} await post-write delta verification."
            : imported
                ? $"Imported and live-verified {result.ImportedTransactions:N0} Bank Statement voucher(s)."
                : acceptedByTally > 0 && result.LiveVerificationError.Length > 0
                    ? $"Tally accepted {acceptedByTally:N0} Bank Statement voucher(s); live verification is pending."
                    : "No Bank Statement voucher was imported.";

        return $"{action} Selected {result.SelectedTransactions:N0}; " +
               $"ready {result.ReadyTransactions:N0}; live Tally exact duplicates {result.ExactDuplicatesSkipped:N0}; " +
               $"previously imported/altered existing {result.ExistingSourceDuplicatesSkipped:N0}; " +
               $"repeated source candidates {result.RepeatedSourceCandidatesSkipped:N0}; " +
               $"deleted in Tally {result.DeletedInTallySkipped:N0}; rebind-blocked {result.GenerationRebindBlocked:N0}; " +
               $"conflicts {result.ConflictsBlocked:N0}; missing ledgers {result.MissingLedgerTransactions:N0}; " +
               $"payment-return pair rows skipped {result.PaymentReturnPairTransactionsSkipped:N0}; " +
               $"treatment corrected {result.TreatmentCorrectedTransactions:N0}; treatment required {result.TreatmentRequiredTransactions:N0}; " +
               $"unsupported {result.UnsupportedTransactions:N0}; Tally created {result.TallyCreated:N0}, " +
               $"altered {result.TallyAltered:N0}, deleted exact duplicates {result.TallyDeleted:N0}, " +
               $"local later duplicates removed {result.LocalDuplicateTransactionsRemoved:N0}, " +
               $"queued Tally duplicate cleanup resolved {result.QueuedTallyDuplicateDeletesResolved:N0}, pending {result.QueuedTallyDuplicateDeletesPending:N0}; " +
               $"ignored {result.TallyIgnored:N0}, errors {result.TallyErrors:N0}; " +
               $"live-unverified {result.UnverifiedAfterImport:N0}.";
    }

    private static string SaveResponseLog(
        ClientProfile client,
        TallyCompanyInfo company,
        DateTime fromDate,
        DateTime toDate,
        string request,
        string response,
        string stage = "import")
    {
        var folder = Path.Combine(
            ApplicationDataPaths.RootFolder,
            "Logs",
            "Tally",
            DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);

        var safeClient = string.Concat(
            client.ClientCode.Where(character =>
                !Path.GetInvalidFileNameChars().Contains(character)));
        if (safeClient.Length == 0)
        {
            safeClient = client.Id.ToString(CultureInfo.InvariantCulture);
        }

        var safeStage = string.Concat((stage ?? "import").Where(character =>
            !Path.GetInvalidFileNameChars().Contains(character))).Replace(' ', '-');
        var stem =
            $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{safeClient}-{fromDate:yyyyMMdd}-{toDate:yyyyMMdd}-{safeStage}";
        var requestPath = Path.Combine(folder, stem + "-request.xml");
        var responsePath = Path.Combine(folder, stem + "-response.xml");
        File.WriteAllText(requestPath, request, Encoding.UTF8);
        File.WriteAllText(responsePath, response, Encoding.UTF8);
        // Fix37: latest two completed request/response pairs per Bank procedure stage.
        var responses = Directory.GetFiles(folder, $"*-{safeClient}-*-{safeStage}-response.xml")
            .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        foreach (var oldResponse in responses.Skip(2))
        {
            try
            {
                var oldRequest = oldResponse[..^"-response.xml".Length] + "-request.xml";
                if (File.Exists(oldRequest)) File.Delete(oldRequest);
                File.Delete(oldResponse);
            }
            catch { }
        }
        return responsePath;
    }


    private static string NormalizeCounterpartLedger(string? ledgerName, string defaultEmptyLedger)
    {
        var normalized = ledgerName?.Trim() ?? string.Empty;
        if (normalized.Length == 0 ||
            normalized.Equals("Suspense A/c", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Suspense A/C", StringComparison.OrdinalIgnoreCase))
        {
            var fallback = (defaultEmptyLedger ?? string.Empty).Trim();
            return fallback.Length > 0 ? fallback : UnmappedBankSuspenseLedger;
        }
        return normalized;
    }

    private string ReadDefaultEmptyLedger(ClientProfile client)
    {
        try
        {
            var saved = _clientPreferences.Get(
                client.Id,
                BankingPreferenceKeys.DefaultEmptyLedger(client.FinancialYear));
            var normalized = saved?.Trim() ?? string.Empty;
            if (normalized.Equals(BankingPreferenceKeys.LegacyDefaultEmptyLedgerName, StringComparison.OrdinalIgnoreCase))
                return BankingPreferenceKeys.DefaultEmptyLedgerName;
            if (normalized.Length > 0) return normalized;
        }
        catch
        {
            // Preference failures must not block Bank export; the dedicated Suspense
            // Ledger under the Suspense A/c Group remains the deterministic fallback.
        }
        return BankingPreferenceKeys.DefaultEmptyLedgerName;
    }

    private static string ResolveCounterpartFallbackGroup(string voucherType, string ledgerName)
    {
        if (ledgerName.Equals(UnmappedBankSuspenseLedger, StringComparison.OrdinalIgnoreCase))
            return "Suspense A/c";
        if (ledgerName.Equals("Bank Charges", StringComparison.OrdinalIgnoreCase))
            return "Indirect Expenses";
        if (ledgerName.Equals("Bank Interest", StringComparison.OrdinalIgnoreCase))
            return "Indirect Incomes";
        if (voucherType.Equals("Receipt", StringComparison.OrdinalIgnoreCase))
            return "Sundry Debtors";
        if (voucherType.Equals("Payment", StringComparison.OrdinalIgnoreCase))
            return "Sundry Creditors";
        if (voucherType.Equals("Contra", StringComparison.OrdinalIgnoreCase))
            return "Bank Accounts";
        return "Sundry Debtors";
    }

    private enum LiveBankVoucherMatchKind
    {
        None,
        Exact,
        ExistingSource,
        TreatmentUpdate,
        ReplaceManualDuplicate,
        Conflict
    }

    private sealed record LiveBankVoucherMatch(
        LiveBankVoucherMatchKind Kind,
        TallyVoucherIndexRecord? Record,
        string Reason)
    {
        public static LiveBankVoucherMatch None { get; } =
            new(LiveBankVoucherMatchKind.None, null, string.Empty);
    }

    private sealed record RequiredLedger(
        string Name,
        string Parent,
        bool IsBank,
        string LocalSource,
        bool CanCreateFromLocalMaster);

    private sealed record PreparedBankVoucher(
        BankTransactionRecord Transaction,
        string VoucherType,
        string BankLedger,
        string CounterpartLedger,
        string SourceKey,
        string ContentHash,
        string ReferenceNumber,
        string ExistingTallyMasterId,
        string VerificationExcludedTallyMasterId,
        string BankLedgerMasterId = "",
        string CounterpartLedgerMasterId = "");

    private sealed record PendingBankVoucherDelete(
        DateTime Date,
        string VoucherType,
        string MasterId,
        string RetainedMasterId,
        string LiveIdentityKey,
        string BankLedger,
        string ReplacementSourceKey);

}
