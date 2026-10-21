using System.Security.Cryptography;
using System.Text;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Masters;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Live Tally Bank transaction handoff for Inter-Bank reconciliation.
///
/// Tally remains accounting authority. The service reads exact live voucher ledger
/// postings, classifies Bank Accounts / Bank OD A/c / Secured Loans, converts each
/// financial-ledger posting into AAH Bank-Statement Debit/Credit convention, then
/// persists the converted Date/Bank/Debit/Credit/Narration/Reference identity and gives
/// the same transaction contract to BankTransferReconciliationService.
///
/// The existing Inter-Bank module remains the only matching/accounting decision-maker.
/// When exactly one side is a converted live-Tally row, this execution layer applies that
/// already-decided result to the SAME Tally voucher as Contra (target Bank DR/source Bank
/// CR), live-verifies it, retires the imported Bank Statement duplicate, then runs final
/// Balance Reconciliation as the separate account-level last step.
/// </summary>
public sealed class TallyBankStatementReconciliationService(
    SqliteBankingRepository bankingRepository,
    SqliteTallyRepository tallyRepository,
    TallyHttpService tallyHttpService,
    SqliteMasterRepository masterRepository,
    BankTransferReconciliationService transferReconciliationService,
    bool tallyHostEnabled = true)
{
    private readonly TallyBankTransactionConversionService _conversionService = new();

    public async Task<TallyBankStatementReconciliationResult> ReconcileLinkedClientAsync(
        ClientProfile client,
        CancellationToken cancellationToken = default,
        bool usePreSynchronizedAuthority = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        var result = new TallyBankStatementReconciliationResult();
        if (!tallyHostEnabled)
        {
            result.Status = "Tally Bank transaction conversion is queued for the Main Server/Tally host; this Windows node will not contact Tally directly.";
            return result;
        }

        var link = tallyRepository.GetLinkForClient(client.Id);
        if (link is null || string.IsNullOrWhiteSpace(link.CompanyKey) || string.IsNullOrWhiteSpace(link.CompanyName))
        {
            transferReconciliationService.Reconcile(client.Id, client.FinancialYear);
            result.Status = "No confirmed Tally company link; normal imported-statement Inter-Bank reconciliation ran without Tally conversion.";
            return result;
        }

        var company = BuildCompany(client, link);
        var (fromDate, toDate) = ResolveFinancialYear(client.FinancialYear);
        var state = tallyRepository.GetBankSyncState(client.Id, client.FinancialYear, link.CompanyKey);
        var bankInputToken = BuildBankInputToken(client.Id, client.FinancialYear);
        var hasBankInput = bankingRepository.GetTransactions(client.Id, client.FinancialYear, includeArchived: true)
            .Any(row => row.Debit > 0m || row.Credit > 0m);

        TallyVoucherChangeMarker changeMarker;
        try
        {
            if (usePreSynchronizedAuthority)
            {
                // FIX4: the coordinator has already completed the one full-company
                // snapshot for this connection cycle. Derive the Bank change marker
                // from that synchronized FY voucher index instead of sending separate
                // $$NumVouchers and $$LastAlterID requests.
                var synchronized = tallyRepository.GetVoucherIndexRange(
                    link.CompanyKey, client.FinancialYear, fromDate, toDate);
                var lastAlterId = synchronized
                    .Select(row => long.TryParse(row.AlterId.Trim(), out var value) ? value : 0L)
                    .DefaultIfEmpty(0L)
                    .Max();
                changeMarker = new TallyVoucherChangeMarker
                {
                    Token = string.IsNullOrWhiteSpace(link.LastChangeToken)
                        ? $"SNAPSHOT|{link.CompanyKey}|{synchronized.Count}|{lastAlterId}"
                        : link.LastChangeToken,
                    VoucherCount = synchronized.Count,
                    LastAlterId = lastAlterId,
                    HasReliableAlterId = lastAlterId > 0
                };
            }
            else
            {
                changeMarker = await tallyHttpService.GetVoucherChangeMarkerAsync(company, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Tally is optional for Bank Statement intake. Never hold an import because the
            // linked company is offline. Local imported-to-imported reconciliation may still run,
            // but the saved Bank input token is deliberately NOT advanced so the first later
            // live-Tally connection sees that pending Bank change and processes it once.
            result.LiveReadError = exception.Message;
            if (state is null || !bankInputToken.Equals(state.LastBankInputToken, StringComparison.Ordinal))
                transferReconciliationService.Reconcile(client.Id, client.FinancialYear);
            result.Status = "Tally is not currently reachable; Bank Statement import remains available and no Tally Inter-Bank action was attempted. The pending Bank change will be checked when the linked Tally company becomes available.";
            return result;
        }

        // FIXED41: Bank reconciliation is NOT the owner of the company-wide Tally
        // ALTERID/Master/Voucher checkpoint. A lightweight Bank probe must never advance
        // those fields, otherwise the 5-second authority coordinator can miss a Ledger or
        // Voucher change forever. Update only the independent Bank input fingerprint.
        if (!hasBankInput)
        {
            tallyRepository.UpsertBankInputCheckpoint(
                client.Id, client.FinancialYear, link.CompanyKey, bankInputToken,
                "No Bank Statement input; Bank checkpoint saved without advancing central Tally authority.");
            result.Status = "No Bank Statement input exists for this Client/FY; Tally Bank vouchers were not scanned.";
            return result;
        }

        var needsInitialSeed = state is null || state.LastFullSyncUtc == DateTime.MinValue;
        var tallyChanged = needsInitialSeed || !changeMarker.Token.Equals(state?.LastTallyChangeToken ?? string.Empty, StringComparison.Ordinal);
        var bankChanged = state is null || !bankInputToken.Equals(state.LastBankInputToken, StringComparison.Ordinal);
        if (!tallyChanged && !bankChanged)
        {
            result.UsedCachedVoucherIndex = true;
            result.Status = "No Tally Bank or Bank Statement changes detected; saved Inter-Bank reconciliation state reused without rescanning FY vouchers.";
            return result;
        }

        // Completion 31 recovery runs only when this Client/FY receives its first incremental
        // Bank-sync checkpoint. It must not reopen historical rows on every client open/Refresh.
        var legacyImportsRestored = state is null
            ? bankingRepository.ResetLegacyTallyConvertedArchives(client.Id, client.FinancialYear)
            : 0;
        var preCompletion33ImportsRestored = state is null
            ? bankingRepository.ResetPreCompletion33UnappliedTallyArchives(client.Id, client.FinancialYear)
            : 0;
        var legacyAuditsRemoved = state is null
            ? tallyRepository.DeleteLegacyTallyConvertedBankStatementLinks(link.CompanyKey, client.Id, client.FinancialYear)
            : 0;

        IReadOnlyList<TallyLedgerNameAuthority> liveLedgerAuthorities = Array.Empty<TallyLedgerNameAuthority>();
        var financialLedgers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in bankingRepository.GetAccounts(client.Id, client.FinancialYear))
        {
            var authority = account.AuthorityName.Trim();
            if (authority.Length > 0) financialLedgers.Add(authority);
        }
        // Persisted converted rows are also safe known financial-Ledger identities. They let
        // an incremental changed voucher continue to be classified when an already-known Bank
        // Ledger is involved, while a failed live hierarchy read still prevents advancing the
        // checkpoint so genuinely new financial Ledgers are retried later.
        foreach (var audit in tallyRepository.GetBankStatementLinks(link.CompanyKey, client.Id, client.FinancialYear)
                     .Where(row => row.TransferMatchId.StartsWith("TALLYBTX|", StringComparison.OrdinalIgnoreCase)))
        {
            var ledger = audit.ConvertedBankLedger.Trim();
            if (ledger.Length > 0) financialLedgers.Add(ledger);
        }

        IReadOnlyList<TallyVoucherIndexRecord> changedLive = Array.Empty<TallyVoucherIndexRecord>();
        var tallySyncApplied = !tallyChanged;
        if (tallyChanged)
        {
            try
            {
                IReadOnlyDictionary<string, string> liveGroupParents;
                if (usePreSynchronizedAuthority)
                {
                    // Fix51: the initial baseline plus 5-second Master ALTERID deltas are
                    // the shared current authority. A pinned baseline can become stale after
                    // a later delta, so Inter-Bank always uses the durable synchronized
                    // Master cache and never asks Tally for another broad hierarchy read.
                    var localMasters = masterRepository.Get(client.Id, client.FinancialYear, includeArchived: false)
                        .Where(master => MasterAuthority.IsTallySource(master.Source)).ToArray();
                    liveLedgerAuthorities = localMasters
                        .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
                        .Select(master => new TallyLedgerNameAuthority
                        {
                            ExactName = master.Name, DisplayName = master.Name.Trim(), Parent = master.Parent.Trim(), MasterId = master.TallyMasterId, Guid = master.TallyGuid, AlterId = master.TallyAlterId
                        }).ToArray();
                    liveGroupParents = localMasters
                        .Where(master => master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
                        .GroupBy(master => master.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.First().Parent.Trim(), StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    liveLedgerAuthorities = await tallyHttpService.ExportLedgerNameAuthoritiesAsync(company, cancellationToken).ConfigureAwait(false);
                    liveGroupParents = await tallyHttpService.ExportGroupParentsAsync(company, cancellationToken).ConfigureAwait(false);
                }

                var liveLedgerParents = liveLedgerAuthorities
                    .GroupBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Parent, StringComparer.OrdinalIgnoreCase);
                foreach (var ledger in ResolveFinancialAccountLedgers(liveLedgerParents, liveGroupParents))
                    financialLedgers.Add(ledger);
                BindBankAccountsToLiveAuthorities(client, liveLedgerAuthorities, financialLedgers);
                foreach (var account in bankingRepository.GetAccounts(client.Id, client.FinancialYear))
                {
                    if (!string.IsNullOrWhiteSpace(account.TallyLedgerVerifiedName)) financialLedgers.Add(account.TallyLedgerVerifiedName.Trim());
                    else if (!string.IsNullOrWhiteSpace(account.LedgerName)) financialLedgers.Add(account.LedgerName.Trim());
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result.FinancialLedgerReadError = exception.Message;
            }

            try
            {
                // First use seeds the durable converted-Tally cache once. Afterwards the normal
                // path asks Tally only for vouchers whose ALTERID advanced. A full FY refresh is
                // reserved for first seed, an actual voucher deletion/count rollback, or a Tally
                // build that cannot expose a reliable ALTERID required for safe delta identity.
                var requiresIntegrityReseed = usePreSynchronizedAuthority || needsInitialSeed ||
                    (state is not null && changeMarker.VoucherCount < state.LastVoucherCount) ||
                    !changeMarker.HasReliableAlterId ||
                    (state is not null && state.LastAlterId <= 0);
                if (requiresIntegrityReseed)
                {
                    var previousIndex = state is null
                        ? Array.Empty<TallyVoucherIndexRecord>()
                        : tallyRepository.GetVoucherIndexRange(link.CompanyKey, client.FinancialYear, fromDate, toDate).ToArray();
                    if (usePreSynchronizedAuthority)
                    {
                        changedLive = tallyRepository.GetVoucherIndexRange(
                            link.CompanyKey, client.FinancialYear, fromDate, toDate);
                    }
                    else
                    {
                        var seedAuthority = await tallyHttpService.ExportVoucherAuthorityFreshAsync(
                            company, fromDate, toDate, cancellationToken).ConfigureAwait(false);
                        changedLive = seedAuthority.Index;
                        tallyRepository.ReplaceVoucherXmlAuthority(
                            link.CompanyKey, client.FinancialYear, seedAuthority.Full);
                    }

                    // Completion 36 migration/integrity pass: compare any pre-checkpoint
                    // TALLYBTX audits to the one live seed. A missing MASTERID or changed
                    // ALTERID invalidates only that prior transfer result; unchanged rows
                    // remain durable and are not replayed after this checkpoint is saved.
                    var currentAlterByMaster = changedLive
                        .Where(voucher => !string.IsNullOrWhiteSpace(voucher.MasterId))
                        .GroupBy(voucher => voucher.MasterId.Trim(), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key,
                            group => group.Select(voucher => voucher.AlterId.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase),
                            StringComparer.OrdinalIgnoreCase);
                    var staleAuditMasters = tallyRepository.GetBankStatementLinks(link.CompanyKey, client.Id, client.FinancialYear)
                        .Where(row => row.TransferMatchId.StartsWith("TALLYBTX|", StringComparison.OrdinalIgnoreCase))
                        .Where(row => !string.IsNullOrWhiteSpace(row.TallyMasterId))
                        .Where(row => !currentAlterByMaster.TryGetValue(row.TallyMasterId.Trim(), out var alters) ||
                                      (row.TallyAlterId.Length > 0 && !alters.Contains(row.TallyAlterId.Trim())))
                        .Select(row => row.TallyMasterId.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    bankingRepository.ResetChangedTallyTransferMatches(client.Id, client.FinancialYear, staleAuditMasters);
                    tallyRepository.DeleteConvertedBankStatementLinksForMasterIds(
                        link.CompanyKey, client.Id, client.FinancialYear, staleAuditMasters);

                    if (previousIndex.Length > 0)
                    {
                        var liveMasterIds = changedLive.Select(voucher => voucher.MasterId).Where(value => !string.IsNullOrWhiteSpace(value))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var deletedMasterIds = previousIndex.Select(voucher => voucher.MasterId)
                            .Where(value => !string.IsNullOrWhiteSpace(value) && !liveMasterIds.Contains(value))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        bankingRepository.ResetChangedTallyTransferMatches(client.Id, client.FinancialYear, deletedMasterIds);
                        tallyRepository.DeleteConvertedBankStatementLinksForMasterIds(
                            link.CompanyKey, client.Id, client.FinancialYear, deletedMasterIds);
                    }
                    tallyRepository.ReplaceVoucherIndexRange(link.CompanyKey, client.FinancialYear, fromDate, toDate, changedLive);
                }
                else if (state is not null && changeMarker.LastAlterId > state.LastAlterId)
                {
                    var authorityDelta = await tallyHttpService.ExportAuthorityChangesAfterAlterIdAsync(
                        company, fromDate, toDate, state.LastAlterId, cancellationToken).ConfigureAwait(false);
                    changedLive = authorityDelta.Vouchers;
                    tallyRepository.UpsertVoucherIndexRecords(link.CompanyKey, client.FinancialYear, changedLive);
                    tallyRepository.UpsertVoucherXmlAuthority(link.CompanyKey, client.FinancialYear, authorityDelta.FullVouchers);
                    var changedMasterIds = changedLive.Select(voucher => voucher.MasterId).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
                    bankingRepository.ResetChangedTallyTransferMatches(client.Id, client.FinancialYear, changedMasterIds);
                    tallyRepository.DeleteConvertedBankStatementLinksForMasterIds(
                        link.CompanyKey, client.Id, client.FinancialYear, changedMasterIds);
                }
                // Do not advance the durable Tally marker when the live financial-Ledger
                // hierarchy could not be read. Known account Ledgers may still reconcile safely,
                // but an unseen Bank/OD/Secured-Loan Ledger could otherwise be skipped forever.
                // Keeping the prior marker makes the next live check retry only this unsatisfied
                // change instead of falsely declaring the Tally revision fully synchronized.
                tallySyncApplied = string.IsNullOrWhiteSpace(result.FinancialLedgerReadError);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Once a cache exists, a failed delta read must not silently fall back to a costly
                // full FY rescan on every Refresh. Keep the previous cache and retry only after a
                // later change/check. First seed cannot match safely without live detail.
                result.LiveReadError = exception.Message;
                if (needsInitialSeed)
                {
                    transferReconciliationService.Reconcile(client.Id, client.FinancialYear);
                    result.Status = $"Initial live Tally Bank cache could not be seeded; Bank Statement data remains imported and active. {exception.Message}";
                    return result;
                }
                result.UsedCachedVoucherIndex = true;
                changedLive = Array.Empty<TallyVoucherIndexRecord>();
            }

            result.VouchersCached = changedLive.Count;
            result.FinancialLedgersScanned = financialLedgers.Count;
            var financialLedgerKeys = financialLedgers
                .Select(Normalize)
                .Where(key => key.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var convertedChanges = _conversionService.Convert(changedLive, financialLedgerKeys, liveLedgerAuthorities);
            result.ConvertedBankTransactions = convertedChanges.Count;
            result.FinancialVouchersScanned = convertedChanges
                .Select(row => row.TallyMasterId.Length > 0 ? row.TallyMasterId : row.ConversionId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            result.FinancialVoucherAmountsRead = convertedChanges.Count(row => row.Amount > 0m);
            result.FinancialVoucherNarrationsRead = convertedChanges.Count(row => !string.IsNullOrWhiteSpace(row.Narration));

            // Fix76 Tally-origin treatment adoption. A previously bound Bank Statement
            // row stays linked to the SAME Voucher MASTERID even if the user later changes
            // that live Voucher in Tally (for example Contra -> Payment with Cash). The
            // repository adopts the new live treatment only when the AAH row still equals
            // its last verified treatment; concurrent AAH edits remain review-first.
            foreach (var changedRow in convertedChanges.Where(row => row.TallyMasterId.Trim().Length > 0))
            {
                var liveVoucher = changedLive.FirstOrDefault(voucher =>
                    voucher.MasterId.Trim().Equals(changedRow.TallyMasterId.Trim(), StringComparison.OrdinalIgnoreCase));
                if (liveVoucher is null) continue;
                var counterpart = ResolveChangedVoucherCounterpart(liveVoucher, changedRow, financialLedgerKeys);
                bankingRepository.AdoptBoundTallyVoucherTreatment(
                    client.Id, client.FinancialYear, link.CompanyKey, changedRow, counterpart);
            }

            var existingConvertedAuditRows = tallyRepository.GetBankStatementLinks(link.CompanyKey, client.Id, client.FinancialYear)
                .Where(row => row.TransferMatchId.StartsWith("TALLYBTX|", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var existingConvertedAudits = existingConvertedAuditRows
                .ToDictionary(row => row.TransferMatchId, StringComparer.OrdinalIgnoreCase);
            foreach (var row in convertedChanges)
            {
                var existing = FindExistingConvertedAudit(row, existingConvertedAuditRows);
                if (existing is not null)
                {
                    // Preserve the already-persisted ConversionId while migrating old name-based
                    // rows to the MASTERID-backed posting identity fields.
                    row.ConversionId = existing.TransferMatchId;
                    if (!existing.Status.Equals("Converted - waiting for Inter-Bank match", StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                else if (existingConvertedAudits.TryGetValue(row.ConversionId, out existing) &&
                         !existing.Status.Equals("Converted - waiting for Inter-Bank match", StringComparison.OrdinalIgnoreCase))
                    continue;
                tallyRepository.UpsertBankStatementLink(BuildConvertedScanRow(client, link, row));
            }
        }
        else
        {
            result.UsedCachedVoucherIndex = true;
        }

        // Fix74 input/output link repair: a Bank Statement change can occur while Tally's
        // Voucher marker is unchanged. MASTERID binding must still happen before the existing
        // Inter-Bank engine receives its inputs. Reuse synchronized Master authority first;
        // only a standalone caller with no local authority may perform the compatibility read.
        if (bankChanged && liveLedgerAuthorities.Count == 0)
        {
            try
            {
                liveLedgerAuthorities = masterRepository.Get(client.Id, client.FinancialYear, includeArchived: false)
                    .Where(master => MasterAuthority.IsTallySource(master.Source) &&
                                     master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                     !master.IsArchived && master.TallyMasterId.Trim().Length > 0)
                    .Select(master => new TallyLedgerNameAuthority
                    {
                        ExactName = master.Name, DisplayName = master.Name.Trim(), Parent = master.Parent.Trim(),
                        MasterId = master.TallyMasterId, Guid = master.TallyGuid, AlterId = master.TallyAlterId
                    }).ToArray();
                if (liveLedgerAuthorities.Count == 0 && !usePreSynchronizedAuthority)
                    liveLedgerAuthorities = await tallyHttpService.ExportLedgerNameAuthoritiesAsync(company, cancellationToken).ConfigureAwait(false);
                BindBankAccountsToLiveAuthorities(client, liveLedgerAuthorities, financialLedgers);
                foreach (var account in bankingRepository.GetAccounts(client.Id, client.FinancialYear))
                {
                    if (!string.IsNullOrWhiteSpace(account.TallyLedgerVerifiedName)) financialLedgers.Add(account.TallyLedgerVerifiedName.Trim());
                    else if (!string.IsNullOrWhiteSpace(account.LedgerName)) financialLedgers.Add(account.LedgerName.Trim());
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result.FinancialLedgerReadError = exception.Message;
            }
        }

        var converted = LoadPersistedConvertedTransactions(link, client);
        if (converted.Count == 0 && !tallyChanged)
            result.UsedCachedVoucherIndex = true;

        // THIS is the only matching handoff. The Tally layer does not decide Contra,
        // source/target or matching evidence. It supplies inputs to the existing engine.
        var reconciliation = transferReconciliationService.Reconcile(client.Id, client.FinancialYear, converted);
        var tallyMatches = reconciliation
            .Where(match => match.MatchOrigin.Equals("Tally Converted Bank Transaction", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        result.Matched = tallyMatches.Length;

        if (tallyMatches.Any(match => !BankTransferWorkflowState.IsCompleted(match)) && liveLedgerAuthorities.Count == 0)
        {
            try
            {
                if (usePreSynchronizedAuthority)
                {
                    liveLedgerAuthorities = masterRepository.Get(client.Id, client.FinancialYear, includeArchived: false)
                        .Where(master => MasterAuthority.IsTallySource(master.Source) &&
                                         master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
                        .Select(master => new TallyLedgerNameAuthority
                        {
                            ExactName = master.Name, DisplayName = master.Name.Trim(), Parent = master.Parent.Trim(), MasterId = master.TallyMasterId, Guid = master.TallyGuid, AlterId = master.TallyAlterId
                        }).ToArray();
                    if (liveLedgerAuthorities.Count == 0)
                        throw new InvalidOperationException(
                            "The synchronized Ledger authority is unavailable; refusing an extra Tally read in complete-authority mode.");
                }
                else
                {
                    liveLedgerAuthorities = await tallyHttpService.ExportLedgerNameAuthoritiesAsync(company, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result.FinancialLedgerReadError = exception.Message;
            }
        }

        var allImported = bankingRepository.GetTransactions(client.Id, client.FinancialYear, includeArchived: true)
            .ToDictionary(row => row.Id);
        var accounts = bankingRepository.GetAccounts(client.Id, client.FinancialYear).ToDictionary(row => row.Id);
        var statements = bankingRepository.GetStatements(client.Id, client.FinancialYear);
        var convertedById = converted.ToDictionary(row => row.ConversionId, StringComparer.OrdinalIgnoreCase);
        foreach (var match in tallyMatches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Completed transaction accounting must be idempotent on Refresh. A final
            // account-level Balance Review does not make the Contra/import-retirement action
            // incomplete, so do not execute the same Tally workflow again for those rows.
            if (BankTransferWorkflowState.IsCompleted(match))
                continue;
            if (!convertedById.TryGetValue(match.TallyConversionId, out var tallyRow))
            {
                result.ReviewRequired++;
                continue;
            }
            if (!allImported.TryGetValue(match.SourceTransactionId, out var imported) ||
                !accounts.TryGetValue(imported.BankAccountId, out var importedAccount))
            {
                result.ReviewRequired++;
                continue;
            }

            bankingRepository.UpdateTallyTransferMatchResult(
                client.Id,
                client.FinancialYear,
                match.MatchId,
                "Tally Inter-Bank Match Found",
                $"Inter-Bank evidence is confirmed for {match.MatchId}; waiting for exact live Tally Ledger authority before the same-MASTERID Contra workflow begins.",
                match.TallyVoucherType);

            var apply = await ApplyMatchedContraAsync(
                client,
                company,
                match,
                tallyRow,
                imported,
                importedAccount,
                accounts.Values,
                statements,
                liveLedgerAuthorities,
                cancellationToken).ConfigureAwait(false);

            bankingRepository.UpdateTallyTransferMatchResult(
                client.Id,
                client.FinancialYear,
                match.MatchId,
                apply.MatchStatus,
                apply.Evidence,
                "Contra");

            var audit = BuildMatchedAudit(client, link, match, tallyRow, imported);
            audit.FinalVoucherType = apply.Verified ? "Contra" : string.Empty;
            audit.FinalDebitLedger = apply.Verified ? apply.TargetBankDisplay : string.Empty;
            audit.FinalCreditLedger = apply.Verified ? apply.SourceBankDisplay : string.Empty;
            audit.MatchEvidence = apply.Evidence;
            audit.Status = apply.AuditStatus;
            audit.UpdatedUtc = DateTime.UtcNow;
            if (apply.VerifiedVoucher is not null && !string.IsNullOrWhiteSpace(apply.VerifiedVoucher.AlterId))
                audit.TallyAlterId = apply.VerifiedVoucher.AlterId.Trim();
            tallyRepository.UpsertBankStatementLink(audit);

            // AAH's own Payment/Receipt -> Contra ALTER creates the opposite financial-Bank
            // posting inside the same voucher. Persist that verified post-ALTER posting now, before
            // advancing LastAlterID, so a later statement for EITHER Bank can be reconciled from
            // the saved TALLYBTX cache without forcing a second full/delta read. The already-matched
            // source posting keeps its completed audit; only genuinely new posting identities are
            // inserted as waiting candidates.
            if (apply.VerifiedVoucher is not null)
            {
                var verifiedFinancialKeys = new[] { apply.SourceBankExact, apply.TargetBankExact }
                    .Select(Normalize)
                    .Where(value => value.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var postAlterConversions = _conversionService.Convert(new[] { apply.VerifiedVoucher }, verifiedFinancialKeys, liveLedgerAuthorities);
                var savedAuditRows = tallyRepository.GetBankStatementLinks(link.CompanyKey, client.Id, client.FinancialYear)
                    .Where(row => row.TransferMatchId.StartsWith("TALLYBTX|", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                foreach (var convertedPostAlter in postAlterConversions)
                {
                    var saved = FindExistingConvertedAudit(convertedPostAlter, savedAuditRows);
                    if (saved is not null)
                    {
                        convertedPostAlter.ConversionId = saved.TransferMatchId;
                        if (!saved.Status.Equals("Converted - waiting for Inter-Bank match", StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                    tallyRepository.UpsertBankStatementLink(BuildConvertedScanRow(client, link, convertedPostAlter));
                }
            }

            if (apply.Verified) result.VerificationConfirmed++;
            if (apply.Altered) result.AlteredAndVerified++;
            if (apply.AlreadyCorrect) result.AlreadyCorrect++;
            if (apply.ImportedRetired) result.ImportedTransactionsRetired++;
            if (apply.ReviewRequired) result.ReviewRequired++;
            if (apply.BalanceReviewRequired) result.BalanceReviewRequired++;
            if (apply.AlterError) result.AlterErrors++;
        }

        // FIXED41: do not issue a post-Bank ALTERID probe here. Any Bank-side Tally write
        // must remain visible to the normal 5-second authority heartbeat, which alone
        // consumes and advances the shared company checkpoint.
        var finalBankInputToken = BuildBankInputToken(client.Id, client.FinancialYear);
        // FIXED41: even when Bank reconciliation reads or ALTERs Tally, leave the shared
        // company authority checkpoint untouched. The normal 5-second coordinator must see
        // the resulting ALTERID and merge/verify that change before advancing it. The narrow
        // repository update is atomic, so a concurrent heartbeat cannot be rolled back here.
        tallyRepository.UpsertBankInputCheckpoint(
            client.Id, client.FinancialYear, link.CompanyKey,
            tallySyncApplied ? finalBankInputToken : state?.LastBankInputToken ?? string.Empty,
            result.ReviewRequired > 0 || result.AlterErrors > 0
                ? "Bank reconciliation completed with review items; central Tally authority checkpoint remains owned by the 5-second coordinator."
                : "Bank reconciliation checkpoint saved; central Tally authority checkpoint was not advanced.");

        var currentAudits = tallyRepository.GetBankStatementLinks(link.CompanyKey, client.Id, client.FinancialYear)
            .Where(row => row.TransferMatchId.StartsWith("TALLYBTX|", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        result.UnmatchedConvertedTransactions = currentAudits.Count(row =>
            row.Status.Equals("Converted - waiting for Inter-Bank match", StringComparison.OrdinalIgnoreCase));
        result.Status =
            $"Incremental Tally Bank sync: changed vouchers read {result.VouchersCached:N0}; cached Bank candidates {converted.Count:N0}; financial ledgers {result.FinancialLedgersScanned:N0}; " +
            $"changed Bank postings synced {result.ConvertedBankTransactions:N0}; Inter-Bank matches {result.Matched:N0}; " +
            $"Contra altered+verified {result.AlteredAndVerified:N0}; already-correct Contra {result.AlreadyCorrect:N0}; " +
            $"imported rows retired {result.ImportedTransactionsRetired:N0}; balance review {result.BalanceReviewRequired:N0}; review {result.ReviewRequired:N0}; errors {result.AlterErrors:N0}.";
        if (legacyImportsRestored > 0 || preCompletion33ImportsRestored > 0 || legacyAuditsRemoved > 0)
            result.Status += $" Recovery: legacy imported rows restored {legacyImportsRestored + preCompletion33ImportsRestored:N0}; obsolete audits removed {legacyAuditsRemoved:N0}.";
        if (result.FinancialLedgerReadError.Length > 0)
            result.Status += " Live Tally financial-ledger hierarchy was unavailable; no unsafe Tally ALTER was attempted without exact Ledger authority.";
        return result;
    }

    /// <summary>
    /// Pre-import path. A short live marker probe is the only blocking test before intake.
    /// If Tally is offline/unresponsive, return immediately and let the importer finalize
    /// every hidden staging row as an ordinary active Bank Statement transaction.
    /// </summary>
    public async Task<BankStatementPreImportReconciliationResult> ReconcileBeforeImportAsync(
        ClientProfile client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!tallyHostEnabled)
            return new BankStatementPreImportReconciliationResult { Status = "Tally host is not available on this Windows node; Bank Statement import continues normally." };
        var link = tallyRepository.GetLinkForClient(client.Id);
        if (link is null || string.IsNullOrWhiteSpace(link.CompanyKey) || string.IsNullOrWhiteSpace(link.CompanyName))
            return new BankStatementPreImportReconciliationResult { Status = "No linked Tally company; Bank Statement import continues normally." };

        using var liveProbe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        liveProbe.CancelAfter(TimeSpan.FromMilliseconds(1500));
        try
        {
            var company = BuildCompany(client, link);
            _ = await tallyHttpService.GetVoucherChangeMarkerAsync(company, liveProbe.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new BankStatementPreImportReconciliationResult
            {
                WasAttempted = false,
                TallyConnected = false,
                Status = $"Tally not connected at import time; Bank Statement was not held. {exception.Message}"
            };
        }

        var result = await ReconcileLinkedClientAsync(client, cancellationToken).ConfigureAwait(false);
        // A successful HTTP probe alone is not enough to advance the post-import Bank token.
        // Confirm that the exact hidden-staging revision was durably saved by the incremental
        // reconciliation. If the first live seed/delta failed, normal Bank import still proceeds
        // and the old checkpoint remains so a later Tally connection performs catch-up.
        var savedState = tallyRepository.GetBankSyncState(client.Id, client.FinancialYear, link.CompanyKey);
        var checkpointReady = savedState is not null &&
            savedState.LastBankInputToken.Equals(BuildBankInputToken(client.Id, client.FinancialYear), StringComparison.Ordinal);
        return new BankStatementPreImportReconciliationResult
        {
            WasAttempted = true,
            TallyConnected = true,
            CheckpointReady = checkpointReady,
            MatchedBeforeImport = result.ImportedTransactionsRetired,
            Status = result.ImportedTransactionsRetired > 0
                ? $"Tally Inter-Bank pre-import completed: {result.ImportedTransactionsRetired:N0} staged duplicate row(s) were resolved before active import."
                : checkpointReady
                    ? result.Status
                    : $"Live Tally was detected but the pre-import reconciliation checkpoint was not completed; Bank Statement import was not held and the Tally catch-up remains pending. {result.Status}"
        };
    }

    /// <summary>
    /// Records the post-staging local Bank revision only when Tally is actually reachable.
    /// If import happened while Tally was offline, the old Bank token is deliberately kept
    /// so the first later Tally connection detects and processes those already-imported rows.
    /// </summary>
    public async Task CheckpointAfterBankImportAsync(
        ClientProfile client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var link = tallyRepository.GetLinkForClient(client.Id);
        if (!tallyHostEnabled || link is null) return;
        using var liveProbe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        liveProbe.CancelAfter(TimeSpan.FromMilliseconds(1500));
        try
        {
            _ = await tallyHttpService.GetVoucherChangeMarkerAsync(BuildCompany(client, link), liveProbe.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return;
        }
        // FIXED41: this probe proves only that Tally is reachable for Bank intake. It must
        // not consume the company ALTERID before the central 5-second authority sync sees it.
        tallyRepository.UpsertBankInputCheckpoint(
            client.Id, client.FinancialYear, link.CompanyKey,
            BuildBankInputToken(client.Id, client.FinancialYear),
            "Bank Statement checkpoint saved without advancing central Tally ALTERID authority.");
    }

    private IReadOnlyList<TallyConvertedBankTransaction> LoadPersistedConvertedTransactions(
        TallyClientLink link,
        ClientProfile client) =>
        tallyRepository.GetBankStatementLinks(link.CompanyKey, client.Id, client.FinancialYear)
            .Where(row => row.TransferMatchId.StartsWith("TALLYBTX|", StringComparison.OrdinalIgnoreCase))
            .Where(row => row.TransactionDate != DateTime.MinValue && row.OriginalAmount > 0m && row.ConvertedBankLedger.Length > 0)
            .Select(row => new TallyConvertedBankTransaction
            {
                ConversionId = row.TransferMatchId,
                CompanyKey = link.CompanyKey,
                FinancialYear = client.FinancialYear,
                TallyMasterId = row.TallyMasterId,
                TallyAlterId = row.TallyAlterId,
                VoucherType = row.OriginalVoucherType,
                PostingSequence = row.PostingSequence > 0 ? row.PostingSequence : ParseLegacyPostingSequence(row.TransferMatchId),
                BankLedgerName = row.ConvertedBankLedger,
                BankLedgerMasterId = row.ConvertedBankLedgerMasterId,
                BankLedgerGuid = row.ConvertedBankLedgerGuid,
                BankLedgerAlterId = row.ConvertedBankLedgerAlterId,
                TransactionDate = row.TransactionDate.Date,
                Narration = row.OriginalNarration,
                ReferenceNumber = row.OriginalReference,
                Debit = row.ConvertedDebitAmount,
                Credit = row.ConvertedCreditAmount,
                TallyIsDebit = row.ConvertedCreditAmount > 0m,
                TallySignedAmount = row.ConvertedCreditAmount > 0m ? -row.OriginalAmount : row.OriginalAmount
            })
            .OrderBy(row => row.TransactionDate)
            .ThenBy(row => row.ConversionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private string BuildBankInputToken(long clientId, string financialYear)
    {
        var accountRows = bankingRepository.GetAccounts(clientId, financialYear)
            .OrderBy(account => account.Id)
            .Select(account => string.Join("|",
                "ACCOUNT",
                account.Id,
                account.AuthorityName,
                account.LedgerName,
                account.BankName,
                account.AccountNumber,
                account.AccountType,
                account.TallyLedgerMasterId,
                account.TallyLedgerAlterId,
                account.TallyLedgerVerifiedName));
        var transactionRows = bankingRepository.GetTransactions(clientId, financialYear, includeArchived: true)
            .Where(row => !row.RecordStatus.StartsWith("Duplicate", StringComparison.OrdinalIgnoreCase))
            .Where(row => !row.RecordStatus.Equals("Generated Transfer Charge", StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.Id)
            .Select(row => string.Join("|",
                "TXN",
                row.Id,
                row.BankAccountId,
                row.TransactionDate.ToString("yyyyMMdd"),
                row.Debit.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                row.Credit.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                row.ReferenceNumber,
                row.Narration,
                row.RecordStatus,
                row.MatchId));

        // FIX5: Bank-account authority/ledger/name/account changes are matching inputs too.
        // A rename/relink must invalidate the Inter-Bank checkpoint even if no transaction
        // row changed, otherwise exact live Tally ledger resolution could remain stale.
        var payload = string.Join("\n", accountRows.Concat(transactionRows));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    public IReadOnlyList<TallyBankStatementLinkRecord> GetVerificationRows(ClientProfile client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var link = tallyRepository.GetLinkForClient(client.Id);
        if (link is null) return Array.Empty<TallyBankStatementLinkRecord>();

        // Completion 30 intentionally hides legacy voucher-ALTER verification rows from
        // the old workflow. Durable TALLYBTX audits plus current converted scan rows are
        // the only authority shown in this workspace.
        return tallyRepository.GetBankStatementLinks(link.CompanyKey, client.Id, client.FinancialYear)
            .Where(row => row.TransferMatchId.StartsWith("TALLYBTX|", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(row => row.TransactionDate)
            .ThenBy(row => row.TransferMatchId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<int> PreloadLinkedVoucherCacheAsync(
        ClientProfile client,
        CancellationToken cancellationToken = default,
        bool usePreSynchronizedAuthority = false)
    {
        if (!tallyHostEnabled) return 0;
        var link = tallyRepository.GetLinkForClient(client.Id);
        if (link is null) return 0;
        var hasBankInput = bankingRepository.GetTransactions(client.Id, client.FinancialYear, includeArchived: true)
            .Any(row => row.Debit > 0m || row.Credit > 0m);
        if (!hasBankInput)
        {
            // With FIX4 one-snapshot authority, the FY voucher index is already local.
            // Do not send an additional marker request merely because the link was confirmed.
            if (usePreSynchronizedAuthority)
            {
                var (cachedFrom, cachedTo) = ResolveFinancialYear(client.FinancialYear);
                return tallyRepository.GetVoucherIndexRange(
                    link.CompanyKey, client.FinancialYear, cachedFrom, cachedTo).Count;
            }
            _ = await tallyHttpService.GetVoucherChangeMarkerAsync(BuildCompany(client, link), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        // When statements were imported before Tally was linked, this is the one initial
        // catch-up point: seed/process them now, persist the checkpoint, and make all later
        // client-open/Refresh cycles incremental/no-op until either side actually changes.
        await ReconcileLinkedClientAsync(
            client, cancellationToken, usePreSynchronizedAuthority).ConfigureAwait(false);
        var (fromDate, toDate) = ResolveFinancialYear(client.FinancialYear);
        return tallyRepository.GetVoucherIndexRange(link.CompanyKey, client.FinancialYear, fromDate, toDate).Count;
    }

    private static string ResolveChangedVoucherCounterpart(
        TallyVoucherIndexRecord voucher,
        TallyConvertedBankTransaction bankRow,
        IReadOnlySet<string> financialLedgerKeys)
    {
        static string Key(string? value) => new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        var bankKey = Key(bankRow.BankLedgerName);
        var party = voucher.PartyLedger.Trim();
        if (party.Length > 0 && !Key(party).Equals(bankKey, StringComparison.OrdinalIgnoreCase))
            return party;

        var candidates = voucher.LedgerPostings
            .Where(posting => posting.Amount > 0m && !Key(posting.LedgerName).Equals(bankKey, StringComparison.OrdinalIgnoreCase))
            .GroupBy(posting => Key(posting.LedgerName), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0)
            .Select(group => new
            {
                Name = group.First().LedgerName.Trim(),
                Amount = group.Sum(posting => posting.Amount),
                IsFinancial = financialLedgerKeys.Contains(group.Key)
            })
            // For ordinary Payment/Receipt prefer the non-Bank accounting counterpart;
            // for Contra the only opposite posting is normally another financial Ledger.
            .OrderBy(item => voucher.VoucherType.Equals("Contra", StringComparison.OrdinalIgnoreCase) ? 0 : item.IsFinancial ? 1 : 0)
            .ThenByDescending(item => item.Amount)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return candidates.FirstOrDefault()?.Name ?? string.Empty;
    }

    private async Task<TallyContraApplyResult> ApplyMatchedContraAsync(
        ClientProfile client,
        TallyCompanyInfo company,
        BankTransferMatch match,
        TallyConvertedBankTransaction tallyRow,
        BankTransactionRecord imported,
        BankAccountRecord importedAccount,
        IEnumerable<BankAccountRecord> allAccounts,
        IReadOnlyList<BankStatementRecord> statements,
        IReadOnlyList<TallyLedgerNameAuthority> liveLedgerAuthorities,
        CancellationToken cancellationToken)
    {
        var sourceIsTally = tallyRow.Debit > 0m;
        var sourceDisplay = sourceIsTally ? tallyRow.BankLedgerName : importedAccount.AuthorityName;
        var targetDisplay = sourceIsTally ? importedAccount.AuthorityName : tallyRow.BankLedgerName;

        var sourceLedgerMasterId = sourceIsTally ? tallyRow.BankLedgerMasterId : importedAccount.TallyLedgerMasterId;
        var targetLedgerMasterId = sourceIsTally ? importedAccount.TallyLedgerMasterId : tallyRow.BankLedgerMasterId;
        if (!TryResolveExactLedgerAuthority(sourceDisplay, sourceIsTally ? null : importedAccount, sourceLedgerMasterId, liveLedgerAuthorities, out var sourceExact) ||
            !TryResolveExactLedgerAuthority(targetDisplay, sourceIsTally ? importedAccount : null, targetLedgerMasterId, liveLedgerAuthorities, out var targetExact))
        {
            var evidence = $"Inter-Bank match {match.MatchId} was retained for review because exact live Tally Ledger authority could not be resolved uniquely for source '{CleanDisplay(sourceDisplay)}' and target '{CleanDisplay(targetDisplay)}'. Imported Bank Statement row {imported.Id} remains active.";
            return TallyContraApplyResult.Review(evidence, sourceDisplay, targetDisplay);
        }
        if (Normalize(sourceExact).Equals(Normalize(targetExact), StringComparison.OrdinalIgnoreCase))
            return TallyContraApplyResult.Review("Inter-Bank source and target resolved to the same live Tally Ledger; ALTER was blocked.", sourceDisplay, targetDisplay);

        var masterId = tallyRow.TallyMasterId.Trim();
        if (masterId.Length == 0)
            return TallyContraApplyResult.Review("Converted Tally transaction has no MASTERID; existing voucher cannot be safely ALTERed. Imported row remains active.", sourceDisplay, targetDisplay);

        IReadOnlyList<TallyVoucherIndexRecord> currentDay;
        var contraAfterAlterId = 0L;
        try
        {
            // Fix37: destructive Contra safety uses the synchronized cache plus one
            // tiny ALTERID check. If Tally advanced, fetch only changed vouchers for
            // this day; never issue a complete day/period voucher reread.
            var contraState = tallyRepository.GetBankSyncState(client.Id, client.FinancialYear, company.CompanyKey);
            var contraMarker = await tallyHttpService.GetVoucherChangeMarkerAsync(company, cancellationToken).ConfigureAwait(false);
            contraAfterAlterId = contraState?.LastAlterId ?? 0L;
            if (contraMarker.HasReliableAlterId && contraMarker.LastAlterId > contraAfterAlterId)
            {
                var changed = await tallyHttpService.ExportAuthorityChangesAfterAlterIdAsync(
                    company, tallyRow.TransactionDate.Date, tallyRow.TransactionDate.Date, contraAfterAlterId, cancellationToken).ConfigureAwait(false);
                tallyRepository.UpsertVoucherIndexRecords(company.CompanyKey, client.FinancialYear, changed.Vouchers);
                tallyRepository.UpsertVoucherXmlAuthority(company.CompanyKey, client.FinancialYear, changed.FullVouchers);
                contraAfterAlterId = contraMarker.LastAlterId;
            }
            currentDay = tallyRepository.GetVoucherIndexRange(
                company.CompanyKey, client.FinancialYear, tallyRow.TransactionDate.Date, tallyRow.TransactionDate.Date);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return TallyContraApplyResult.Review($"Live Tally voucher MASTERID {masterId} could not be read for its transaction date: {exception.Message}. Imported Bank Statement row {imported.Id} remains active.", sourceDisplay, targetDisplay);
        }

        var current = currentDay.FirstOrDefault(voucher => voucher.MasterId.Trim().Equals(masterId, StringComparison.OrdinalIgnoreCase));
        if (current is null)
            return TallyContraApplyResult.Review($"Live Tally voucher MASTERID {masterId} was not found on {tallyRow.TransactionDate:dd.MM.yyyy}; ALTER was blocked.", sourceDisplay, targetDisplay);

        var alreadyCorrect = IsVerifiedContra(current, sourceExact, targetExact, match.Amount);
        if (!alreadyCorrect && !LiveVoucherStillMatchesInterbankSeed(current, tallyRow, match.Amount, out var seedFailure))
        {
            return TallyContraApplyResult.Review(
                $"Live Tally voucher MASTERID {masterId} changed after the Inter-Bank match was formed ({seedFailure}); ALTER was blocked and imported Bank Statement row {imported.Id} remains active. Refresh will rebuild the link from current Tally data.",
                sourceDisplay, targetDisplay);
        }

        bankingRepository.UpdateTallyTransferMatchResult(
            client.Id,
            client.FinancialYear,
            match.MatchId,
            "Tally Contra Pending",
            $"Exact live Tally source/target Ledgers and MASTERID {masterId} are resolved; waiting for Contra apply/live verification.",
            current.VoucherType);

        var altered = false;
        if (!alreadyCorrect)
        {
            try
            {
                var cachedFull = tallyRepository.GetVoucherXmlAuthorityByMasterIds(
                    company.CompanyKey, client.FinancialYear, new[] { masterId });
                if (cachedFull.Count != 1)
                    return TallyContraApplyResult.Error(
                        $"Complete FY Tally authority is missing Voucher MASTERID {masterId}; Contra ALTER was blocked without issuing a second pre-write Voucher read. Imported Bank Statement row {imported.Id} remains active.",
                        sourceDisplay, targetDisplay);
                var xml = TallyBankContraAlterXmlBuilder.BuildFromCachedVoucher(
                    company.Name,
                    cachedFull[0],
                    sourceExact,
                    targetExact,
                    match.Amount);
                var response = TallyHttpService.ParseImportResponse(await tallyHttpService.ImportXmlAsync(xml, cancellationToken).ConfigureAwait(false));
                if (response.Errors > 0 || response.Altered <= 0 || !string.IsNullOrWhiteSpace(response.LineError))
                {
                    var evidence = $"Tally Contra ALTER failed for MASTERID {masterId}. Altered={response.Altered}; Errors={response.Errors}; {response.LineError}. Imported Bank Statement row {imported.Id} remains active.";
                    return TallyContraApplyResult.Error(evidence, sourceDisplay, targetDisplay);
                }
                altered = true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return TallyContraApplyResult.Error($"Tally Contra ALTER failed for MASTERID {masterId}: {exception.Message}. Imported Bank Statement row {imported.Id} remains active.", sourceDisplay, targetDisplay);
            }
        }

        IReadOnlyList<TallyVoucherIndexRecord> verificationRead;
        try
        {
            var verificationMarker = await tallyHttpService.GetVoucherChangeMarkerAsync(company, cancellationToken).ConfigureAwait(false);
            if (verificationMarker.HasReliableAlterId && verificationMarker.LastAlterId > contraAfterAlterId)
            {
                var changed = await tallyHttpService.ExportAuthorityChangesAfterAlterIdAsync(
                    company, tallyRow.TransactionDate.Date, tallyRow.TransactionDate.Date, contraAfterAlterId, cancellationToken).ConfigureAwait(false);
                tallyRepository.UpsertVoucherIndexRecords(company.CompanyKey, client.FinancialYear, changed.Vouchers);
                tallyRepository.UpsertVoucherXmlAuthority(company.CompanyKey, client.FinancialYear, changed.FullVouchers);
                contraAfterAlterId = verificationMarker.LastAlterId;
            }
            verificationRead = tallyRepository.GetVoucherIndexRange(
                company.CompanyKey, client.FinancialYear, tallyRow.TransactionDate.Date, tallyRow.TransactionDate.Date);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return TallyContraApplyResult.Error($"Tally Contra {(altered ? "ALTER" : "verification")} could not be read back for MASTERID {masterId}: {exception.Message}. Imported Bank Statement row {imported.Id} remains active.", sourceDisplay, targetDisplay);
        }

        var verifiedVoucher = verificationRead.FirstOrDefault(voucher => voucher.MasterId.Trim().Equals(masterId, StringComparison.OrdinalIgnoreCase));
        if (verifiedVoucher is null || !IsVerifiedContra(verifiedVoucher, sourceExact, targetExact, match.Amount))
        {
            return TallyContraApplyResult.Error($"Tally voucher MASTERID {masterId} did not live-verify as exact Contra after reconciliation: target '{CleanDisplay(targetExact)}' DR {match.Amount:0.00}; source '{CleanDisplay(sourceExact)}' CR {match.Amount:0.00}. Imported Bank Statement row {imported.Id} remains active.", sourceDisplay, targetDisplay);
        }

        bankingRepository.UpdateTallyTransferMatchResult(
            client.Id,
            client.FinancialYear,
            match.MatchId,
            "Tally Contra Verified - Balance Verification Pending",
            $"Tally MASTERID {masterId} live-verified as Contra: target '{CleanDisplay(targetExact)}' DR {match.Amount:0.00}; source '{CleanDisplay(sourceExact)}' CR {match.Amount:0.00}. Waiting for imported-row retirement and FINAL exact balance reconciliation.",
            "Contra");

        var affected = new List<long> { imported.BankAccountId };
        var tallyAccount = allAccounts.FirstOrDefault(account => BankAccountMatchesTallyLedger(account, tallyRow));
        if (tallyAccount is not null &&
            tallyAccount.Id != imported.BankAccountId &&
            statements.Any(statement => statement.BankAccountId == tallyAccount.Id))
            affected.Add(tallyAccount.Id);

        // The imported duplicate is retired only after the same-MASTERID Tally Contra has
        // live-verified. Balance Reconciliation is deliberately the LAST accounting step,
        // exactly like the ordinary Imported <-> Imported route. A remaining account-level
        // opening/closing difference is reported separately and must NOT undo this already-
        // proven transaction pair or resurrect the duplicate Bank Statement row.
        // Once the existing Inter-Bank engine has proven the pair, any prior ordinary
        // Ledger Mapping on the imported duplicate is not an accounting blocker. The
        // Inter-Bank result is authoritative for this transaction: preserve the global
        // mapping rule for other rows, but overwrite this imported row's own mapping with
        // the proven opposite financial Ledger before it leaves the active statement set.
        var interBankCounterpartExact = sourceIsTally ? sourceExact : targetExact;
        var archived = bankingRepository.ArchiveImportedTransactionMatchedToTally(
            client.Id,
            client.FinancialYear,
            imported.Id,
            match.MatchId,
            CleanDisplay(interBankCounterpartExact)) > 0;
        if (!archived && !imported.RecordStatus.Equals("Archived Tally Reconciled", StringComparison.OrdinalIgnoreCase))
        {
            return TallyContraApplyResult.Review(
                $"Tally Contra live-verified, but imported Bank Statement row {imported.Id} could not be retired atomically; row remains for review and final balance reconciliation was not finalized.",
                sourceDisplay, targetDisplay, true, altered, alreadyCorrect) with
            {
                VerifiedVoucher = verifiedVoucher,
                SourceBankExact = sourceExact,
                TargetBankExact = targetExact
            };
        }

        // Finalize the AAH Bank row -> exact Tally Voucher MASTERID binding only after the
        // same voucher has live-verified and the imported duplicate is retired. This makes
        // subsequent Bank edits/reconciliation direct-MASTERID operations, never a new CREATE.
        var importedBankExact = sourceIsTally ? targetExact : sourceExact;
        bankingRepository.UpsertBankTallyExportLink(
            client.Id,
            client.FinancialYear,
            imported.Id,
            imported.TransactionId,
            company.CompanyKey,
            masterId,
            "Contra",
            CleanDisplay(importedBankExact),
            CleanDisplay(interBankCounterpartExact));

        var balanceGate = transferReconciliationService.VerifyFinalBalanceGate(client.Id, client.FinancialYear, affected);
        if (!balanceGate.IsExact)
        {
            var balanceReviewStatus = alreadyCorrect
                ? "Tally Contra Already Correct - Balance Review Required"
                : "Tally Contra Applied - Balance Review Required";
            var evidence = $"Existing Inter-Bank result completed for Tally MASTERID {masterId}: target '{CleanDisplay(targetExact)}' DR {match.Amount:0.00}; source '{CleanDisplay(sourceExact)}' CR {match.Amount:0.00}; Voucher Type Contra live-verified; imported Bank Statement row {imported.Id} retired. FINAL Balance Reconciliation still requires review: {balanceGate.Detail}";
            return new TallyContraApplyResult(
                true, altered, alreadyCorrect, true, false, true, false,
                balanceReviewStatus,
                "Contra verified + imported duplicate retired; final account balance review required",
                evidence, CleanDisplay(sourceExact), CleanDisplay(targetExact))
            {
                VerifiedVoucher = verifiedVoucher,
                SourceBankExact = sourceExact,
                TargetBankExact = targetExact
            };
        }

        var finalStatus = alreadyCorrect
            ? "Tally Contra Already Correct - Balance Verified"
            : "Tally Contra Applied - Balance Verified";
        var finalEvidence = $"Existing Inter-Bank result applied to Tally MASTERID {masterId}: target '{CleanDisplay(targetExact)}' DR {match.Amount:0.00}; source '{CleanDisplay(sourceExact)}' CR {match.Amount:0.00}; Voucher Type Contra live-verified; imported Bank Statement row {imported.Id} retired; FINAL step: {balanceGate.Detail}";
        return new TallyContraApplyResult(
            true, altered, alreadyCorrect,
            archived || imported.RecordStatus.Equals("Archived Tally Reconciled", StringComparison.OrdinalIgnoreCase),
            false, false, false, finalStatus,
            alreadyCorrect
                ? "Existing Contra verified + closing balance matched; imported row retired"
                : "Contra ALTER verified + closing balance matched; imported row retired",
            finalEvidence, CleanDisplay(sourceExact), CleanDisplay(targetExact))
        {
            VerifiedVoucher = verifiedVoucher,
            SourceBankExact = sourceExact,
            TargetBankExact = targetExact
        };
    }

    private static bool IsVerifiedContra(TallyVoucherIndexRecord voucher, string sourceBank, string targetBank, decimal amount)
    {
        if (!voucher.VoucherType.Equals("Contra", StringComparison.OrdinalIgnoreCase)) return false;
        var active = voucher.LedgerPostings.Where(posting => posting.Amount > 0.01m).ToArray();
        if (active.Length != 2) return false;
        var sourceKey = Normalize(sourceBank);
        var targetKey = Normalize(targetBank);
        var sourceCredit = active.Count(posting =>
            Normalize(posting.LedgerName).Equals(sourceKey, StringComparison.OrdinalIgnoreCase) &&
            !posting.IsDebit && Math.Abs(posting.Amount - amount) <= 0.01m) == 1;
        var targetDebit = active.Count(posting =>
            Normalize(posting.LedgerName).Equals(targetKey, StringComparison.OrdinalIgnoreCase) &&
            posting.IsDebit && Math.Abs(posting.Amount - amount) <= 0.01m) == 1;
        return sourceCredit && targetDebit;
    }

    private static bool LiveVoucherStillMatchesInterbankSeed(
        TallyVoucherIndexRecord current,
        TallyConvertedBankTransaction seed,
        decimal expectedAmount,
        out string failure)
    {
        failure = string.Empty;
        if (current.VoucherDate is not DateTime liveDate || liveDate.Date != seed.TransactionDate.Date)
        {
            failure = "voucher date no longer matches the converted Tally seed";
            return false;
        }

        var seedLedgerKey = Normalize(seed.BankLedgerName);
        var matchingPosting = current.LedgerPostings.Count(posting =>
            Normalize(posting.LedgerName).Equals(seedLedgerKey, StringComparison.OrdinalIgnoreCase) &&
            posting.IsDebit == seed.TallyIsDebit &&
            Math.Abs(posting.Amount - expectedAmount) <= 0.01m);
        if (matchingPosting != 1)
        {
            failure = "original Bank ledger side/amount no longer matches";
            return false;
        }

        var liveReference = NormalizeReference(current.Reference);
        var seedReference = NormalizeReference(seed.ReferenceNumber);
        if (liveReference.Length > 0 && seedReference.Length > 0 &&
            !liveReference.Equals(seedReference, StringComparison.OrdinalIgnoreCase))
        {
            failure = "voucher reference changed";
            return false;
        }

        return true;
    }

    private static string NormalizeReference(string? value) =>
        new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    private static bool TryResolveExactLedgerAuthority(
        string requestedName,
        BankAccountRecord? bankAccount,
        string preferredMasterId,
        IReadOnlyList<TallyLedgerNameAuthority> authorities,
        out string exactName)
    {
        exactName = string.Empty;
        if (authorities.Count == 0) return false;

        var masterId = (preferredMasterId ?? string.Empty).Trim();
        if (masterId.Length > 0)
        {
            var byMasterId = authorities
                .Where(item => item.MasterId.Trim().Equals(masterId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.AlterId)
                .ToArray();
            if (byMasterId.Length > 0)
            {
                exactName = byMasterId[0].ExactName;
                return exactName.Length > 0;
            }
            // Stored MASTERID disappeared from the live authority. Do not name-rebind here.
            return false;
        }

        // Tally.ERP 9 can return the same Ledger object more than once in a native
        // collection/report envelope. That is NOT real accounting ambiguity. Collapse
        // byte-identical ExactName rows first, then resolve from the Inter-Bank financial
        // account identity. A different existing Ledger Mapping on the imported Bank
        // Statement row is deliberately irrelevant here: the Inter-Bank match has already
        // proven which two financial accounts participate.
        var requestedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddKey(string? value)
        {
            var key = Normalize(value);
            if (key.Length > 0) requestedKeys.Add(key);
        }

        AddKey(requestedName);
        if (bankAccount is not null)
        {
            AddKey(bankAccount.AuthorityName);
            AddKey(bankAccount.LedgerName);
            AddKey(bankAccount.DisplayName);
        }

        var direct = DistinctExactAuthorities(authorities
            .Where(item => requestedKeys.Contains(Normalize(item.DisplayName))));
        if (TrySelectExactAuthority(requestedName, direct, out exactName)) return true;

        // Fix77: account-number similarity is not authority for a changed Bank Ledger
        // name. The old and new independent Masters normally share the same bank account
        // digits, so using digits here could silently route an Inter-Bank ALTER back to
        // the old MASTERID. Exact current name or MASTERID is required.
        return false;
    }

    private static IReadOnlyList<TallyLedgerNameAuthority> DistinctExactAuthorities(
        IEnumerable<TallyLedgerNameAuthority> authorities) =>
        authorities
            .Where(item => !string.IsNullOrWhiteSpace(item.ExactName))
            .GroupBy(item => item.ExactName, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

    private static bool TrySelectExactAuthority(
        string requestedName,
        IReadOnlyList<TallyLedgerNameAuthority> candidates,
        out string exactName)
    {
        exactName = string.Empty;
        if (candidates.Count == 1)
        {
            exactName = candidates[0].ExactName;
            return true;
        }
        if (candidates.Count == 0) return false;

        // If Tally genuinely has two different Ledgers that normalize to the same visible
        // name, prefer only a byte-exact live name already carried by the request. Otherwise
        // fail closed; Inter-Bank evidence must never cause an arbitrary Master choice.
        var raw = candidates
            .Where(item => item.ExactName.Equals(requestedName ?? string.Empty, StringComparison.Ordinal))
            .ToArray();
        if (raw.Length != 1) return false;
        exactName = raw[0].ExactName;
        return true;
    }

    private static bool BankAccountMatchesTallyLedger(BankAccountRecord account, TallyConvertedBankTransaction tally)
    {
        var accountMasterId = account.TallyLedgerMasterId.Trim();
        var tallyMasterId = tally.BankLedgerMasterId.Trim();
        if (accountMasterId.Length > 0 || tallyMasterId.Length > 0)
            return accountMasterId.Length > 0 && tallyMasterId.Length > 0 &&
                   accountMasterId.Equals(tallyMasterId, StringComparison.OrdinalIgnoreCase);
        return BankAccountMatchesTallyLedger(account, tally.BankLedgerName);
    }

    private static bool BankAccountMatchesTallyLedger(BankAccountRecord account, string tallyLedger)
    {
        var tallyKey = Normalize(tallyLedger);
        if (tallyKey.Length == 0) return false;
        foreach (var candidate in new[] { account.AuthorityName, account.LedgerName, account.DisplayName })
        {
            var key = Normalize(candidate);
            if (key.Length > 0 && key.Equals(tallyKey, StringComparison.OrdinalIgnoreCase)) return true;
        }
        var accountDigits = new string((account.AccountNumber ?? string.Empty).Where(char.IsDigit).ToArray());
        if (accountDigits.Length < 6) return false;
        return new string((tallyLedger ?? string.Empty).Where(char.IsDigit).ToArray()).Contains(accountDigits, StringComparison.Ordinal);
    }

    private static string CleanDisplay(string? value) =>
        string.Join(" ", (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed record TallyContraApplyResult(
        bool Verified,
        bool Altered,
        bool AlreadyCorrect,
        bool ImportedRetired,
        bool ReviewRequired,
        bool BalanceReviewRequired,
        bool AlterError,
        string MatchStatus,
        string AuditStatus,
        string Evidence,
        string SourceBankDisplay,
        string TargetBankDisplay)
    {
        public TallyVoucherIndexRecord? VerifiedVoucher { get; init; }
        public string SourceBankExact { get; init; } = string.Empty;
        public string TargetBankExact { get; init; } = string.Empty;
        public static TallyContraApplyResult Review(
            string evidence,
            string source,
            string target,
            bool verified = false,
            bool altered = false,
            bool alreadyCorrect = false) => new(
                verified, altered, alreadyCorrect, false, true, false, false,
                verified ? "Tally Contra Applied - Review Required" : "Tally Inter-Bank Matched - Review Required",
                verified ? "Contra verified - review required; imported row retained" : "Inter-Bank match review required; imported row retained",
                evidence, CleanDisplay(source), CleanDisplay(target));

        public static TallyContraApplyResult Error(string evidence, string source, string target) => new(
            false, false, false, false, true, false, true,
            "Tally Contra Apply Failed",
            "Contra apply/live verification failed; imported row retained",
            evidence, CleanDisplay(source), CleanDisplay(target));
    }

    private static TallyCompanyInfo BuildCompany(ClientProfile client, TallyClientLink link) => new()
    {
        CompanyKey = link.CompanyKey,
        Name = link.CompanyName,
        CompanyNumber = link.CompanyNumber,
        Gstin = link.Gstin,
        StateName = link.StateName,
        BooksFrom = link.BooksFrom,
        StartingFrom = link.StartingFrom,
        FinancialYear = link.FinancialYear.Length > 0 ? link.FinancialYear : client.FinancialYear
    };

    private static TallyBankStatementLinkRecord BuildMatchedAudit(
        ClientProfile client,
        TallyClientLink link,
        BankTransferMatch match,
        TallyConvertedBankTransaction tally,
        BankTransactionRecord? imported) => new()
    {
        ClientId = client.Id,
        FinancialYear = client.FinancialYear,
        CompanyKey = link.CompanyKey,
        TransferMatchId = tally.ConversionId,
        SourceTransactionId = match.SourceTransactionId,
        TargetTransactionId = 0,
        TransactionDate = tally.TransactionDate.Date,
        TallyMasterId = tally.TallyMasterId,
        TallyAlterId = tally.TallyAlterId,
        PostingSequence = tally.PostingSequence,
        ConvertedBankLedgerMasterId = tally.BankLedgerMasterId,
        ConvertedBankLedgerGuid = tally.BankLedgerGuid,
        ConvertedBankLedgerAlterId = tally.BankLedgerAlterId,
        SourceBankLedgerMasterId = match.SourceBankLedgerMasterId,
        TargetBankLedgerMasterId = match.TargetBankLedgerMasterId,
        OriginalVoucherType = tally.VoucherType,
        OriginalReference = tally.ReferenceNumber,
        OriginalNarration = tally.Narration,
        OriginalDebitLedgers = tally.TallyIsDebit ? tally.BankLedgerName : string.Empty,
        OriginalCreditLedgers = !tally.TallyIsDebit ? tally.BankLedgerName : string.Empty,
        OriginalAmount = tally.Amount,
        FinalVoucherType = string.Empty,
        FinalDebitLedger = string.Empty,
        FinalCreditLedger = string.Empty,
        MatchEvidence = match.Reason + (imported is null
            ? string.Empty
            : $"; imported row {imported.Id} {imported.TransactionDate:yyyy-MM-dd} {imported.Amount:0.00}"),
        Status = "Inter-Bank matched - awaiting Tally Contra apply/live verification/final balance gate",
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

    private static TallyBankStatementLinkRecord BuildConvertedScanRow(
        ClientProfile client,
        TallyClientLink link,
        TallyConvertedBankTransaction tally) => new()
    {
        ClientId = client.Id,
        FinancialYear = client.FinancialYear,
        CompanyKey = link.CompanyKey,
        TransferMatchId = tally.ConversionId,
        SourceTransactionId = 0,
        TargetTransactionId = 0,
        TransactionDate = tally.TransactionDate.Date,
        TallyMasterId = tally.TallyMasterId,
        TallyAlterId = tally.TallyAlterId,
        PostingSequence = tally.PostingSequence,
        ConvertedBankLedgerMasterId = tally.BankLedgerMasterId,
        ConvertedBankLedgerGuid = tally.BankLedgerGuid,
        ConvertedBankLedgerAlterId = tally.BankLedgerAlterId,
        OriginalVoucherType = tally.VoucherType,
        OriginalReference = tally.ReferenceNumber,
        OriginalNarration = tally.Narration,
        OriginalDebitLedgers = tally.TallyIsDebit ? tally.BankLedgerName : string.Empty,
        OriginalCreditLedgers = !tally.TallyIsDebit ? tally.BankLedgerName : string.Empty,
        OriginalAmount = tally.Amount,
        FinalVoucherType = string.Empty,
        FinalDebitLedger = string.Empty,
        FinalCreditLedger = string.Empty,
        MatchEvidence = $"Converted exact fields supplied to Inter-Bank: Date {tally.TransactionDate:yyyy-MM-dd}, Bank {tally.BankLedgerName}, Debit {tally.Debit:0.00}, Credit {tally.Credit:0.00}, Reference {tally.ReferenceNumber}. Tally {tally.TallySide} was reversed once to Bank Statement {tally.Mode} direction; no Inter-Bank match yet.",
        Status = "Converted - waiting for Inter-Bank match",
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = tally.TransactionDate
    };

    private void BindBankAccountsToLiveAuthorities(
        ClientProfile client,
        IReadOnlyList<TallyLedgerNameAuthority> liveLedgerAuthorities,
        IReadOnlySet<string> financialLedgers)
    {
        if (liveLedgerAuthorities.Count == 0) return;
        var financialKeys = financialLedgers.Select(Normalize).Where(key => key.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var localMasters = masterRepository.Get(client.Id, client.FinancialYear, includeArchived: false)
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var accounts = bankingRepository.GetAccounts(client.Id, client.FinancialYear).ToArray();
        var detachedIndependentBankMasters = TallyIndependentBankLedgerPolicy.DetachExplicitIndependentRenames(
            bankingRepository, masterRepository, client.Id, client.FinancialYear, accounts, localMasters);
        if (detachedIndependentBankMasters > 0)
        {
            // Fix78: Inter-Bank must see the same post-detach Master classification as
            // Bank export. The old pre-detach Source label must never participate in binding.
            localMasters = masterRepository.Get(client.Id, client.FinancialYear, includeArchived: false)
                .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        foreach (var account in accounts)
        {
            TallyLedgerNameAuthority? authority = null;
            var boundMasterId = account.TallyLedgerMasterId.Trim();
            var independentCreatePending = TallyIndependentBankLedgerPolicy.HasPendingIndependentMaster(account, localMasters);
            if (boundMasterId.Length > 0)
            {
                var byMaster = liveLedgerAuthorities
                    .Where(item => item.MasterId.Trim().Equals(boundMasterId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item => item.AlterId).ToArray();
                if (byMaster.Length > 0) authority = byMaster[0];
                else
                {
                    // A stored MASTERID is stronger than a mutable name. Never silently rebind
                    // a missing Tally master to another ledger with the same label.
                    continue;
                }
            }
            else
            {
                TallyLedgerNameAuthority[] candidates;
                if (independentCreatePending)
                {
                    // Fix77 R1: an explicitly new Bank Master has no Tally identity yet.
                    // After CREATE it may bind only to the one live Master with the exact
                    // requested current name. Old Bank names/account digits are never identity.
                    var requestedName = account.AuthorityName.Trim();
                    candidates = liveLedgerAuthorities
                        .Where(item => financialKeys.Count == 0 || financialKeys.Contains(Normalize(item.DisplayName)) || financialKeys.Contains(Normalize(item.ExactName)))
                        .Where(item => SameLedgerName(item.DisplayName, requestedName) || SameLedgerName(item.ExactName, requestedName))
                        .GroupBy(item => item.MasterId.Trim(), StringComparer.OrdinalIgnoreCase)
                        .Where(group => group.Key.Length > 0)
                        .Select(group => group.OrderByDescending(item => item.AlterId).First()).ToArray();
                }
                else
                {
                    var aliases = new[] { account.AuthorityName, account.LedgerName, account.DisplayName }
                        .Select(Normalize).Where(value => value.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    candidates = liveLedgerAuthorities
                        .Where(item => financialKeys.Count == 0 || financialKeys.Contains(Normalize(item.DisplayName)) || financialKeys.Contains(Normalize(item.ExactName)))
                        .Where(item => aliases.Contains(Normalize(item.DisplayName)) || aliases.Contains(Normalize(item.ExactName)))
                        .GroupBy(item => item.MasterId.Trim(), StringComparer.OrdinalIgnoreCase)
                        .Where(group => group.Key.Length > 0)
                        .Select(group => group.OrderByDescending(item => item.AlterId).First()).ToArray();
                }
                if (candidates.Length == 0 && !independentCreatePending)
                {
                    // Account-number fallback is first-binding assistance only. When an
                    // explicit independent local Master is waiting for CREATE, the old Bank
                    // Ledger commonly has the same account digits and must never win here.
                    var accountDigits = new string(account.AccountNumber.Where(char.IsDigit).ToArray());
                    if (accountDigits.Length >= 6)
                    {
                        candidates = liveLedgerAuthorities
                            .Where(item => financialKeys.Count == 0 || financialKeys.Contains(Normalize(item.DisplayName)) || financialKeys.Contains(Normalize(item.ExactName)))
                            .Where(item => new string((item.DisplayName + item.ExactName).Where(char.IsDigit).ToArray()).Contains(accountDigits, StringComparison.Ordinal))
                            .GroupBy(item => item.MasterId.Trim(), StringComparer.OrdinalIgnoreCase)
                            .Where(group => group.Key.Length > 0)
                            .Select(group => group.OrderByDescending(item => item.AlterId).First()).ToArray();
                    }
                }
                if (candidates.Length == 1) authority = candidates[0];
            }

            if (authority is null || string.IsNullOrWhiteSpace(authority.MasterId)) continue;
            var currentName = (authority.DisplayName.Length > 0 ? authority.DisplayName : authority.ExactName).Trim();
            var changed = !account.TallyLedgerMasterId.Equals(authority.MasterId.Trim(), StringComparison.OrdinalIgnoreCase) ||
                          !account.TallyLedgerGuid.Equals(authority.Guid.Trim(), StringComparison.OrdinalIgnoreCase) ||
                          account.TallyLedgerAlterId != authority.AlterId ||
                          !account.TallyLedgerVerifiedName.Equals(currentName, StringComparison.Ordinal) ||
                          !account.LedgerName.Equals(currentName, StringComparison.Ordinal);
            if (!changed) continue;
            account.TallyLedgerMasterId = authority.MasterId.Trim();
            account.TallyLedgerGuid = authority.Guid.Trim();
            account.TallyLedgerAlterId = authority.AlterId;
            account.TallyLedgerVerifiedName = currentName;
            account.LedgerName = currentName;
            bankingRepository.SaveAccount(account);
        }
    }

    private static TallyBankStatementLinkRecord? FindExistingConvertedAudit(
        TallyConvertedBankTransaction row,
        IReadOnlyCollection<TallyBankStatementLinkRecord> audits)
    {
        var masterId = row.TallyMasterId.Trim();
        if (masterId.Length == 0) return audits.FirstOrDefault(audit => audit.TransferMatchId.Equals(row.ConversionId, StringComparison.OrdinalIgnoreCase));
        return audits.FirstOrDefault(audit =>
            audit.TallyMasterId.Trim().Equals(masterId, StringComparison.OrdinalIgnoreCase) &&
            (audit.PostingSequence > 0 ? audit.PostingSequence : ParseLegacyPostingSequence(audit.TransferMatchId)) == row.PostingSequence);
    }

    private static bool SameLedgerName(string? first, string? second) =>
        string.Equals((first ?? string.Empty).Trim(), (second ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static int ParseLegacyPostingSequence(string conversionId)
    {
        var parts = (conversionId ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (part.StartsWith("P:", StringComparison.OrdinalIgnoreCase) && int.TryParse(part[2..], out var stable)) return stable;
        }
        return parts.Length >= 3 && int.TryParse(parts[2], out var legacy) ? legacy : 0;
    }

    private static IReadOnlySet<string> ResolveFinancialAccountLedgers(
        IReadOnlyDictionary<string, string> ledgerParents,
        IReadOnlyDictionary<string, string> groupParents)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in ledgerParents)
        {
            if (IsFinancialAccountGroup(pair.Value, groupParents)) result.Add(pair.Key.Trim());
        }
        return result;
    }

    private static bool IsFinancialAccountGroup(string groupName, IReadOnlyDictionary<string, string> groupParents)
    {
        var current = (groupName ?? string.Empty).Trim();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var depth = 0; current.Length > 0 && depth < 64 && visited.Add(current); depth++)
        {
            if (IsFinancialRootGroup(current)) return true;
            if (!groupParents.TryGetValue(current, out var parent)) break;
            current = (parent ?? string.Empty).Trim();
        }
        return false;
    }

    private static bool IsFinancialRootGroup(string groupName)
    {
        var key = Normalize(groupName);
        return key.Equals("BANKACCOUNTS", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("BANKODAC", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("BANKODACCOUNT", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("BANKODACCOUNTS", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("SECUREDLOANS", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value) =>
        new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static (DateTime From, DateTime To) ResolveFinancialYear(string financialYear)
    {
        var digits = new string((financialYear ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length >= 4 && int.TryParse(digits[..4], out var year))
            return (new DateTime(year, 4, 1), new DateTime(year + 1, 3, 31));
        var today = DateTime.Today;
        var start = today.Month >= 4 ? today.Year : today.Year - 1;
        return (new DateTime(start, 4, 1), new DateTime(start + 1, 3, 31));
    }
}

public sealed class TallyBankStatementReconciliationResult
{
    public int VouchersCached { get; set; }
    public int FinancialLedgersScanned { get; set; }
    public int FinancialVouchersScanned { get; set; }
    public int FinancialVoucherAmountsRead { get; set; }
    public int FinancialVoucherNarrationsRead { get; set; }
    public int ConvertedBankTransactions { get; set; }
    public int ImportedTransactionsRetired { get; set; }
    public int UnmatchedConvertedTransactions { get; set; }
    public string VoucherDataWarning { get; set; } = string.Empty;
    public bool UsedCachedVoucherIndex { get; set; }
    public string LiveReadError { get; set; } = string.Empty;
    public string FinancialLedgerReadError { get; set; } = string.Empty;
    public int Matched { get; set; }
    public int SingleStatementLinked { get; set; }
    public int VerificationConfirmed { get; set; }
    public int AlreadyCorrect { get; set; }
    public int AlteredAndVerified { get; set; }
    public int ReviewRequired { get; set; }
    public int BalanceReviewRequired { get; set; }
    public int Ambiguous { get; set; }
    public int AlterErrors { get; set; }
    public string Status { get; set; } = string.Empty;
}
