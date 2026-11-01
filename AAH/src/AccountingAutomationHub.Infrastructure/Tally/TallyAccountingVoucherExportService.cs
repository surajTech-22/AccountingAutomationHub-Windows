using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using System.Xml.Linq;
using AccountingAutomationHub.Application.Banking;
using AccountingAutomationHub.Application.Journals;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Application.Purchases;
using AccountingAutomationHub.Application.Sales;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Journals;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Domain.Sales;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Journals;
using AccountingAutomationHub.Infrastructure.Governance;
using AccountingAutomationHub.Infrastructure.Persistence;

namespace AccountingAutomationHub.Infrastructure.Tally;

public sealed class TallyAccountingVoucherExportService
{
    private readonly IBankingRepository _bankingRepository;
    private readonly IPurchaseRecordRepository _purchaseRepository;
    private readonly ISalesRecordRepository _salesRepository;
    private readonly IJournalRepository _journalRepository;
    private readonly IMasterRepository _masterRepository;
    private readonly IStockMasterRepository _stockMasterRepository;
    private readonly SqliteTallyRepository _tallyRepository;
    private readonly TallyHttpService _httpService;
    private readonly TallyLiveMasterSynchronizationService _liveMasterSyncService;

    public TallyAccountingVoucherExportService(
        IBankingRepository bankingRepository,
        IPurchaseRecordRepository purchaseRepository,
        ISalesRecordRepository salesRepository,
        IJournalRepository journalRepository,
        IMasterRepository masterRepository,
        IStockMasterRepository stockMasterRepository,
        SqliteTallyRepository tallyRepository,
        TallyHttpService httpService,
        TallyLiveMasterSynchronizationService liveMasterSyncService)
    {
        _bankingRepository = bankingRepository;
        _purchaseRepository = purchaseRepository;
        _salesRepository = salesRepository;
        _journalRepository = journalRepository;
        _masterRepository = masterRepository;
        _stockMasterRepository = stockMasterRepository;
        _tallyRepository = tallyRepository;
        _httpService = httpService;
        _liveMasterSyncService = liveMasterSyncService;
    }

    public async Task<TallyVoucherExportResult> ExportAsync(
        ClientProfile client,
        TallyCompanyInfo company,
        IReadOnlyList<TallyVoucherExportRange> purchaseRanges,
        IReadOnlyList<TallyVoucherExportRange> salesRanges,
        IReadOnlyList<TallyVoucherExportRange> cashEntryRanges,
        IReadOnlyList<TallyVoucherExportRange> journalRanges,
        CancellationToken cancellationToken = default,
        bool usePreSynchronizedVoucherAuthority = false,
        long synchronizedAuthorityAlterId = 0,
        Func<IReadOnlyList<string>, Task<bool>>? confirmMissingRecovery = null,
        IReadOnlyCollection<string>? explicitRestoreSourceAnchors = null,
        bool includeRecurringCash = true)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(company);
        purchaseRanges = purchaseRanges.Select(range => range.Normalize()).ToArray();
        salesRanges = salesRanges.Select(range => range.Normalize()).ToArray();
        cashEntryRanges = cashEntryRanges.Select(range => range.Normalize()).ToArray();
        journalRanges = journalRanges.Select(range => range.Normalize()).ToArray();
        var allRanges = purchaseRanges.Concat(salesRanges).Concat(cashEntryRanges).Concat(journalRanges).ToArray();
        if (allRanges.Length == 0)
            return new TallyVoucherExportResult { Status = "No accounting export period was selected." };
        var restoreSourceAnchors = (explicitRestoreSourceAnchors ?? Array.Empty<string>())
            .Select(value => (value ?? string.Empty).Trim())
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new TallyVoucherExportResult
        {
            FromDate = allRanges.Min(range => range.FromDate),
            ToDate = allRanges.Max(range => range.ToDate)
        };

        // Fix68 explicit-export authority is prepared once by the coordinator from one
        // complete fresh active-FY Company/Master/Voucher baseline. Purchase/Sales/Cash/
        // Journal duplicate decisions reuse that same authority locally instead of
        // downloading the same twelve Voucher months again. Standalone callers retain
        // the direct month-bounded recovery path.
        var (authorityFrom, authorityTo) = GetFinancialYearBounds(client.FinancialYear, result.FromDate, result.ToDate);
        IReadOnlyList<TallyVoucherIndexRecord> liveBefore;
        if (usePreSynchronizedVoucherAuthority)
        {
            if (!_tallyRepository.HasCompleteVoucherXmlAuthority(company.CompanyKey, client.FinancialYear))
                throw new InvalidOperationException(
                    "The prepared fresh Tally FY authority is incomplete. Accounting export was blocked before any Voucher write.");
            liveBefore = _tallyRepository.GetVoucherIndexRange(
                company.CompanyKey, client.FinancialYear, authorityFrom, authorityTo);
        }
        else
        {
            var liveBeforeList = new List<TallyVoucherIndexRecord>();
            var month = new DateTime(authorityFrom.Year, authorityFrom.Month, 1);
            var lastMonth = new DateTime(authorityTo.Year, authorityTo.Month, 1);
            while (month <= lastMonth)
            {
                var monthFrom = month < authorityFrom ? authorityFrom : month;
                var monthEnd = month.AddMonths(1).AddDays(-1);
                var monthTo = monthEnd > authorityTo ? authorityTo : monthEnd;
                var sliceAuthority = await _httpService.ExportVoucherAuthorityFreshAsync(
                    company, monthFrom, monthTo, cancellationToken);
                _tallyRepository.ReplaceVoucherIndexRange(
                    company.CompanyKey, client.FinancialYear, monthFrom, monthTo, sliceAuthority.Index);
                _tallyRepository.UpsertVoucherXmlAuthority(
                    company.CompanyKey, client.FinancialYear, sliceAuthority.Full);
                liveBeforeList.AddRange(sliceAuthority.Index);
                month = month.AddMonths(1);
            }
            liveBefore = liveBeforeList;
        }

        // Fix85: one complete fresh FY authority classifies missing previously-bound
        // Voucher MASTERIDs before any source can reach CREATE. Individual missing
        // identities become source-level deletion candidates; widespread identity loss
        // enters exact-rebind recovery. Neither path grants automatic CREATE.
        _tallyRepository.ReconcileMissingVoucherBindingAuthority(
            client.Id, client.FinancialYear, company.CompanyKey,
            liveBefore.Select(item => item.MasterId));

        var allMasters = _masterRepository.Get(client.Id, client.FinancialYear, false).ToArray();
        var masterLedgers = allMasters
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                             master.Name.Trim().Length > 0)
            .ToArray();
        var ledgerParents = masterLedgers
            .GroupBy(master => master.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(master => master.Parent.Trim())
                    .FirstOrDefault(parent => parent.Length > 0) ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
        // Fix74: module preparation produces logical requests only. No Voucher duplicate/
        // CREATE/ALTER decision is allowed until the Master layer has been completed and
        // every ledger used by the requests is bound back to a Tally MASTERID.
        var rawPrepared = new List<PreparedVoucher>();
        var settings = _journalRepository.GetSettings(client.Id, client.FinancialYear);

        if (purchaseRanges.Count > 0)
            AddPurchaseVouchers(client, company, purchaseRanges, settings, rawPrepared, result, skipTallyDuplicateChecks: true, explicitRestoreSourceAnchors: restoreSourceAnchors);
        if (salesRanges.Count > 0)
            AddSalesVouchers(client, company, salesRanges, rawPrepared, result, skipTallyDuplicateChecks: true, explicitRestoreSourceAnchors: restoreSourceAnchors);
        if (cashEntryRanges.Count > 0)
            AddCashEntryVouchers(client, company, cashEntryRanges, ledgerParents, rawPrepared, result, includeRecurringCash: includeRecurringCash, skipTallyDuplicateChecks: true, explicitRestoreSourceAnchors: restoreSourceAnchors);
        if (journalRanges.Count > 0)
        {
            AddManualJournals(client, company, journalRanges, rawPrepared, result, skipTallyDuplicateChecks: true, explicitRestoreSourceAnchors: restoreSourceAnchors);
            AddAutomaticGstJournals(client, company, journalRanges, rawPrepared, result, skipTallyDuplicateChecks: true, explicitRestoreSourceAnchors: restoreSourceAnchors);
        }

        if (rawPrepared.Count == 0)
        {
            result.Status = BuildSummary(result, false);
            return result;
        }
        var prepared = new List<PreparedVoucher>();

        var stockItems = _stockMasterRepository.GetStockItems(client.Id, client.FinancialYear);
        // R17: frozen voucher entries must use the exact name of their verified
        // durable Master before both master dependency lookup and voucher XML.
        var stockExportIdentity = new StockExportIdentityResolver(stockItems,
            _stockMasterRepository.GetItemAliases(client.Id, client.FinancialYear));
        for (var index = 0; index < rawPrepared.Count; index++)
        {
            var voucher = rawPrepared[index];
            if (voucher.InventoryEntries is not { Count: > 0 } inventoryEntries) continue;
            rawPrepared[index] = voucher with
            {
                InventoryEntries = inventoryEntries.Select(entry =>
                {
                    var master = stockExportIdentity.Resolve(entry.StockItemPersistentId, entry.ItemName, entry.TallyStockMasterId);
                    return entry with { ItemName = master.Name.Trim() };
                }).ToArray()
            };
        }
        var stockItemUnitProfiles = _stockMasterRepository.GetStockItemUnitProfiles(client.Id, client.FinancialYear);
        var stockUnits = _stockMasterRepository.GetUnits(client.Id, client.FinancialYear);
        var stockGroups = _stockMasterRepository.GetSimpleMasters(client.Id, client.FinancialYear, "Stock Group");
        var stockCategories = _stockMasterRepository.GetSimpleMasters(client.Id, client.FinancialYear, "Stock Category");
        var godowns = _stockMasterRepository.GetSimpleMasters(client.Id, client.FinancialYear, "Godown");
        var costCentres = _stockMasterRepository.GetSimpleMasters(client.Id, client.FinancialYear, "Cost Centre");
        var bankAccounts = _bankingRepository.GetAccounts(client.Id, client.FinancialYear);

        // Fix77: a changed Bank Ledger name may intentionally be a new independent
        // Master. If an unbound local Master with that desired name exists, detach the
        // Bank Account from its old Tally MASTERID before Master-first processing. The old
        // Master remains active and the new Master must receive a fresh CREATE MASTERID.
        var detachedIndependentBankMasters = TallyIndependentBankLedgerPolicy.DetachExplicitIndependentRenames(
            _bankingRepository, _masterRepository, client.Id, client.FinancialYear,
            bankAccounts, allMasters);
        if (detachedIndependentBankMasters > 0)
        {
            // Fix78: all modules in this export cycle must observe the same post-detach Master state. Do not let Accounting keep a stale pre-detach Source label.
            allMasters = _masterRepository.Get(client.Id, client.FinancialYear, false).ToArray();
        }

        // Fix37: the normal coordinator path reuses the already synchronized exact
        // Ledger/Group authority. Never download the complete Ledger/Group lists again
        // immediately before voucher XML construction. Standalone callers remain
        // fail-closed and can still perform the direct reads below.
        IReadOnlyDictionary<string,string> liveLedgerParents;
        IReadOnlySet<string> liveGroupNames;
        if (_httpService.IsLocalErp9Runtime)
        {
            // Fix111 ERP9 explicit-export Master barrier. Prepared Master authority is
            // excellent for fast UI paint, but an ERP9 Voucher write must be preceded by
            // one fresh serialized live Master read so a deleted/not-yet-created Ledger
            // can never be treated as present merely because it remains in the local cache.
            _httpService.RequireFreshErp9MasterAuthorityForExplicitExport();
            var freshMasterXml = await _httpService.ExportMastersAsync(company, cancellationToken).ConfigureAwait(false);
            var freshLedgerParents = await _httpService.ExportLedgerParentsAsync(company, cancellationToken).ConfigureAwait(false);
            var freshGroupNames = await _httpService.ExportGroupNamesAsync(company, cancellationToken).ConfigureAwait(false);
            _liveMasterSyncService.Synchronize(
                freshMasterXml, client.Id, client.FinancialYear, company.Name,
                freshLedgerParents, freshGroupNames, ledgerGroupOnlyAuthority: true);
            allMasters = _masterRepository.Get(client.Id, client.FinancialYear, false).ToArray();
            liveLedgerParents = freshLedgerParents;
            liveGroupNames = freshGroupNames;
        }
        else if (usePreSynchronizedVoucherAuthority)
        {
            // Fix51: use the durable current Master authority maintained by the initial
            // baseline + 5-second Master ALTERID deltas. The pinned first baseline can
            // be older than a later delta and must not override current Ledger/Group state.
            liveLedgerParents = allMasters
                .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                 MasterAuthority.IsTallySource(master.Source) && !master.IsArchived &&
                                 master.TallyMasterId.Trim().Length > 0)
                .GroupBy(master => master.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Select(master => master.Parent.Trim()).FirstOrDefault(parent => parent.Length > 0) ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            liveGroupNames = allMasters
                .Where(master => master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) &&
                                 MasterAuthority.IsTallySource(master.Source) && !master.IsArchived)
                .Select(master => master.Name.Trim()).Where(name => name.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            liveLedgerParents = await _httpService.ExportLedgerParentsAsync(company, cancellationToken);
            liveGroupNames = await _httpService.ExportGroupNamesAsync(company, cancellationToken);
        }
        // Fix76/Fix77 same-MASTERID Parent modification. A user Parent edit on a
        // Tally-bound Bank Ledger is a pending next version of that MASTERID, not a new
        // name-based Master. Resolve the exact live Ledger by MASTERID, confirm its
        // previous ALTERID, ALTER it first, then rebind the accepted Master authority
        // before any Voucher duplicate/ALTER/CREATE classification.
        if (TallyBoundLedgerAlterPlanner.HasPendingEdits(bankAccounts, allMasters))
        {
            try
            {
                var liveAuthorities = await _httpService.ExportLedgerNameAuthoritiesAsync(company, cancellationToken).ConfigureAwait(false);
                var boundPlans = TallyBoundLedgerAlterPlanner.Build(bankAccounts, allMasters, liveAuthorities);
                if (boundPlans.Count > 0)
                {
                    var alterXml = TallyBoundLedgerAlterPlanner.BuildXml(company.Name, boundPlans);
                    var alterResponseText = await _httpService.ImportXmlAsync(alterXml, cancellationToken).ConfigureAwait(false);
                    var alterResponse = TallyHttpService.ParseImportResponse(alterResponseText);
                    if (alterResponse.Errors > 0 || !string.IsNullOrWhiteSpace(alterResponse.LineError) || alterResponse.Altered < boundPlans.Count)
                    {
                        result.TallyErrors = Math.Max(1, alterResponse.Errors);
                        result.LineError = alterResponse.LineError;
                        result.ResponsePath = SaveResponseLog(client, result.FromDate, result.ToDate, alterXml, alterResponseText);
                        result.Status = $"Bound Ledger MASTERID ALTER failed before Voucher classification. Expected {boundPlans.Count:N0} ALTER(s), Tally reported {alterResponse.Altered:N0}. {alterResponse.LineError}";
                        return result;
                    }

                    foreach (var plan in boundPlans)
                    {
                        if (!plan.CurrentName.Equals(plan.DesiredName, StringComparison.OrdinalIgnoreCase))
                            _masterRepository.ApplyVerifiedLedgerRenameReferences(client.Id, client.FinancialYear, plan.CurrentName, plan.DesiredName);
                    }

                    var refreshedMasterXml = await _httpService.ExportMastersAsync(company, cancellationToken).ConfigureAwait(false);
                    var refreshedLedgerParents = await _httpService.ExportLedgerParentsAsync(company, cancellationToken).ConfigureAwait(false);
                    var refreshedGroupNames = await _httpService.ExportGroupNamesAsync(company, cancellationToken).ConfigureAwait(false);
                    _liveMasterSyncService.Synchronize(
                        refreshedMasterXml, client.Id, client.FinancialYear, company.Name,
                        refreshedLedgerParents, refreshedGroupNames, ledgerGroupOnlyAuthority: true);
                    allMasters = _masterRepository.Get(client.Id, client.FinancialYear, false).ToArray();
                    liveLedgerParents = refreshedLedgerParents;
                    liveGroupNames = refreshedGroupNames;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result.LiveReadErrors++;
                result.LiveReadErrorDetails.Add("Bound Ledger MASTERID ALTER/rebind failed: " + exception.Message);
                result.Status = "Master-first barrier stopped accounting Voucher processing because a pending same-MASTERID Ledger Parent/group change could not be verified and applied. " + exception.Message;
                return result;
            }
        }

        // Fix83/Fix111: the remaining GST asset Master mutations are sent through Tally ERP 9's
        // documented Import Data / All Masters master contract before the generic Master stage.
        // This stage is deliberately minimal: only the GST Receivable subgroup/ledgers
        // and the deterministic Not-shown-in-2B parent ALTERs are included. Each accepted
        // mutation is then live-verified before Voucher classification can continue.
        var gstAssetMasterXml = BuildFix83GstAssetMasterXml(
            company.Name, rawPrepared, allMasters, settings, liveLedgerParents, liveGroupNames);
        if (HasImportPayload(gstAssetMasterXml))
        {
            var gstAssetMasterResponseText = await _httpService.ImportXmlAsync(gstAssetMasterXml, cancellationToken).ConfigureAwait(false);
            var gstAssetMasterResponse = TallyHttpService.ParseImportResponse(gstAssetMasterResponseText);
            result.MasterResponsePath = SaveResponseLog(
                client, result.FromDate, result.ToDate, gstAssetMasterXml, gstAssetMasterResponseText, "gst-asset-masters");
            if (gstAssetMasterResponse.Errors > 0 || gstAssetMasterResponse.Exceptions > 0 ||
                !string.IsNullOrWhiteSpace(gstAssetMasterResponse.LineError))
            {
                result.TallyErrors = Math.Max(1, gstAssetMasterResponse.Errors + gstAssetMasterResponse.Exceptions);
                result.LineError = gstAssetMasterResponse.LineError;
                result.Status =
                    "Fix83 GST asset Master stage failed before Voucher classification. " +
                    $"Created={gstAssetMasterResponse.Created}; Altered={gstAssetMasterResponse.Altered}; " +
                    $"Ignored={gstAssetMasterResponse.Ignored}; Exceptions={gstAssetMasterResponse.Exceptions}; " +
                    $"Errors={gstAssetMasterResponse.Errors}. {gstAssetMasterResponse.LineError}";
                return result;
            }

            try
            {
                var refreshedMasterXml = await _httpService.ExportMastersAsync(company, cancellationToken).ConfigureAwait(false);
                var refreshedLedgerParents = await _httpService.ExportLedgerParentsAsync(company, cancellationToken).ConfigureAwait(false);
                var refreshedGroupNames = await _httpService.ExportGroupNamesAsync(company, cancellationToken).ConfigureAwait(false);
                VerifyFix83GstAssetMasters(rawPrepared, allMasters, settings, refreshedLedgerParents, refreshedGroupNames);
                _liveMasterSyncService.Synchronize(
                    refreshedMasterXml, client.Id, client.FinancialYear, company.Name,
                    refreshedLedgerParents, refreshedGroupNames, ledgerGroupOnlyAuthority: true);
                allMasters = _masterRepository.Get(client.Id, client.FinancialYear, false).ToArray();
                liveLedgerParents = refreshedLedgerParents;
                liveGroupNames = refreshedGroupNames;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result.LiveReadErrors++;
                result.LiveReadErrorDetails.Add("Fix83 GST asset Master live verification failed: " + exception.Message);
                result.Status =
                    "Fix83 GST asset Masters were sent, but exact live Group/Ledger parent verification failed. " +
                    "Voucher processing was blocked. " + exception.Message;
                return result;
            }
        }

        var liveLedgerKeys = liveLedgerParents.Keys.Select(TallyMasterKey).Where(key => key.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var liveGroupKeys = liveGroupNames.Select(TallyMasterKey).Where(key => key.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Fix61 two-stage adaptive import. Resolve/create required Masters once, then
        // send only Voucher XML in adaptive batches. A slow Tally reduces later batches;
        // quick clean responses grow them. No uncertain batch is automatically resent.
        var masterXml = BuildMastersOnlyXml(
            company.Name, result.FromDate, rawPrepared, allMasters, bankAccounts,
            stockItems, stockItemUnitProfiles, stockUnits, stockGroups, stockCategories, godowns, costCentres,
            liveLedgerKeys, liveGroupKeys);
        var masterResponseText = string.Empty;
        // Fix62 voucher verification repair: when all required Masters already exist live,
        // the Master envelope contains no actual Master payload. Do not send that empty
        // request at all. Tally correctly reports a zero-work response, but older parser
        // fallback logic could misread the structural <RESPONSE> contents as LINEERROR.
        if (HasImportPayload(masterXml))
        {
            masterResponseText = await _httpService.ImportXmlAsync(masterXml, cancellationToken);
            var masterResponse = TallyHttpService.ParseImportResponse(masterResponseText);
            if (masterResponse.Errors > 0 || masterResponse.Exceptions > 0 || !string.IsNullOrWhiteSpace(masterResponse.LineError))
            {
                result.TallyErrors = Math.Max(1, masterResponse.Errors + masterResponse.Exceptions);
                result.LineError = masterResponse.LineError;
                result.MasterResponsePath = SaveResponseLog(client, result.FromDate, result.ToDate, masterXml, masterResponseText, "masters");
                // Fix131: Tally may accept the first Masters in a batch and reject a later
                // Stock Item. Before returning the failure, reread live Master authority and
                // adopt every MASTERID that Tally already accepted. The voucher stage remains
                // blocked, but the next retry will not blindly CREATE those accepted Masters.
                var partialReadback = await RebindAcceptedMastersAfterFailedMasterBatchAsync(
                    client, company, result, cancellationToken).ConfigureAwait(false);
                result.Status = "Required Tally Master stage failed. Voucher stage was not sent. " +
                                $"Exceptions={masterResponse.Exceptions}; Errors={masterResponse.Errors}; LineError={masterResponse.LineError}. {partialReadback}";
                return result;
            }
        }

        // Fix74 MASTER-FIRST barrier. A successful Master write is not enough: obtain the
        // Tally-assigned MASTERIDs, synchronize the same Master authority into AAH, and prove
        // every Ledger used by these logical voucher requests is bound before transaction
        // duplicate/ALTER/CREATE classification starts.
        if (!usePreSynchronizedVoucherAuthority || HasImportPayload(masterXml))
        {
            try
            {
                // Standalone/manual export must establish exact Master identity even when
                // no CREATE payload was required. A names-only Ledger/Group read cannot
                // authorize transaction duplicate/ALTER/CREATE decisions. The normal
                // coordinator path may reuse its already-synchronized MASTERID authority
                // unless this stage actually changed a Master.
                var refreshedMasterXml = await _httpService.ExportMastersAsync(company, cancellationToken).ConfigureAwait(false);
                var refreshedLedgerParents = await _httpService.ExportLedgerParentsAsync(company, cancellationToken).ConfigureAwait(false);
                var refreshedGroupNames = await _httpService.ExportGroupNamesAsync(company, cancellationToken).ConfigureAwait(false);
                _liveMasterSyncService.Synchronize(
                    refreshedMasterXml,
                    client.Id,
                    client.FinancialYear,
                    company.Name,
                    refreshedLedgerParents,
                    refreshedGroupNames,
                    ledgerGroupOnlyAuthority: true);
                allMasters = _masterRepository.Get(client.Id, client.FinancialYear, false).ToArray();
                // Fix117: the same MASTER-first barrier also captures Tally Stock Item identity.
                stockItems = _stockMasterRepository.GetStockItems(client.Id,client.FinancialYear).ToArray();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result.LiveReadErrors++;
                result.LiveReadErrorDetails.Add("Required Master MASTERID rebind failed: " + exception.Message);
                result.Status = "Required Tally Masters were accepted, but their MASTERIDs could not be rebound into AAH. Voucher duplicate/ALTER/CREATE stage was blocked.";
                return result;
            }
        }

        // Fix117: once the fresh Stock Master barrier has assigned/recovered Tally identity,
        // persist that identity back onto every durable Purchase/Sales inventory row. The
        // AAH Stock Item ID remains the primary local relationship; Tally MASTERID is the
        // current external identity snapshot used to detect stale/rebound Stock Items.
        RebindDurableInventoryStockIdentity(client, stockItems);

        var referencedStockEntries = rawPrepared.SelectMany(voucher => voucher.InventoryEntries ?? []).ToArray();
        if (referencedStockEntries.Length > 0)
        {
            var stockById = stockItems.Where(item=>item.PersistentId>0).ToDictionary(item=>item.PersistentId);
            var stockBindingErrors = referencedStockEntries.Select(entry =>
            {
                if(entry.StockItemPersistentId<=0) return $"{entry.ItemName}: no AAH Stock Item ID";
                stockById.TryGetValue(entry.StockItemPersistentId,out var item);
                if(item is null) return $"{entry.ItemName}: AAH Stock Item ID {entry.StockItemPersistentId} no longer exists";
                if(!item.Name.Equals(entry.ItemName.Trim(),StringComparison.OrdinalIgnoreCase)) return $"{entry.ItemName}: Stock Item ID {entry.StockItemPersistentId} now belongs to '{item.Name}'";
                if(item.TallyMasterId.Trim().Length==0) return $"{entry.ItemName}: no Tally Stock MASTERID after Master stage";
                return string.Empty;
            }).Where(value=>value.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if(stockBindingErrors.Length>0)
            {
                result.MissingLedgers += stockBindingErrors.Length;
                result.Status="Stock Master comparison is not clear. Voucher stage was blocked until every inventory line resolves to an AAH Stock Item with a fresh Tally MASTERID: "+string.Join(" | ",stockBindingErrors.Take(12));
                return result;
            }
        }

        // CL017 narrow master-first correction: normalized display names are NOT Tally
        // Ledger identities. In legacy companies, two distinct live MASTERIDs may have
        // names differing only by trailing CR/LF characters. Keep the existing exact
        // MASTERID/GUID binding; allow a normalized-name fallback only for one live ID.
        var boundLedgerMasters = allMasters
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                              MasterAuthority.IsTallySource(master.Source) && !master.IsArchived &&
                              master.TallyMasterId.Trim().Length > 0)
            .ToArray();
        var ledgerMasterCandidatesByKey = boundLedgerMasters
            .GroupBy(master => TallyMasterKey(master.Name), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0)
            .ToDictionary(
                group => group.Key,
                group => group.Select(master => master.TallyMasterId.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        // Do NOT Trim: a trailing CR/LF is part of the Tally exact Ledger name.
        var referencedLedgerNames = rawPrepared
            .SelectMany(item => item.Entries.Select(entry => entry.LedgerName).Append(item.PartyLedger))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var collidedReferencedKeys = referencedLedgerNames.Select(TallyMasterKey)
            .Where(key => ledgerMasterCandidatesByKey.TryGetValue(key, out var ids) && ids.Length > 1)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Explicit names are separately namespaced from unique normalized aliases.
        // Never put a colliding normalized name into the identity map.
        var ledgerMasterIdByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ledgerMasterCandidatesByKey.Where(item => item.Value.Length == 1))
            ledgerMasterIdByKey["NORMALIZED:" + item.Key] = item.Value[0];
        if (collidedReferencedKeys.Count > 0)
        {
            try
            {
                // Reuse the existing source-preserving Tally authority reader. It keeps
                // ExactName untouched even when DisplayName removes trailing CR/LF.
                // This is a read-only identity check, never a Tally CREATE/ALTER.
                var liveExactLedgers = await _httpService.ExportLedgerNameAuthoritiesAsync(
                    company, cancellationToken).ConfigureAwait(false);
                var boundMasterIds = boundLedgerMasters.Select(master => master.TallyMasterId.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var exactCandidates = liveExactLedgers
                    .Where(ledger => ledger.MasterId.Trim().Length > 0 &&
                                     boundMasterIds.Contains(ledger.MasterId.Trim()) &&
                                     collidedReferencedKeys.Contains(TallyMasterKey(ledger.ExactName)))
                    .GroupBy(ledger => ledger.ExactName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key,
                        group => group.Select(ledger => ledger.MasterId.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                        StringComparer.OrdinalIgnoreCase);
                foreach (var name in referencedLedgerNames)
                {
                    if (!collidedReferencedKeys.Contains(TallyMasterKey(name))) continue;
                    if (exactCandidates.TryGetValue(name, out var exactIds) && exactIds.Length == 1)
                        ledgerMasterIdByKey["EXACT:" + name] = exactIds[0];
                    // If no exact name or two matching MASTERIDs exist, deliberately
                    // leave unbound and block the voucher stage below.
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result.LiveReadErrors++;
                result.LiveReadErrorDetails.Add("Exact Ledger MASTERID authority unavailable: " + exception.Message);
                result.Status = "Exact Tally Ledger names could not be verified. Voucher stage was blocked; no voucher sent.";
                return result;
            }
        }
        var masterBindingErrors = referencedLedgerNames
            .Where(name => ResolveLedgerMasterId(name, ledgerMasterIdByKey).Length == 0)
            .Select(name =>
            {
                var key = TallyMasterKey(name);
                if (key.Length == 0 || !ledgerMasterCandidatesByKey.TryGetValue(key, out var ids) || ids.Length == 0)
                    return $"{name}: no Tally Ledger MASTERID";
                return $"{name}: exact Ledger identity unresolved among Tally MASTERIDs [{string.Join(", ", ids)}]";
            })
            .ToArray();
        if (masterBindingErrors.Length > 0)
        {
            result.MissingLedgers += masterBindingErrors.Length;
            result.Status = "Master comparison is not clear. Voucher stage was blocked until every referenced Ledger has exactly one Tally MASTERID: " +
                            string.Join(" | ", masterBindingErrors.Take(12)) + (masterBindingErrors.Length > 12 ? " ..." : string.Empty);
            return result;
        }

        // Only now classify transactions from the fresh authority. Every strict duplicate
        // and previous->desired comparison below resolves Ledger sides to MASTERID, so a
        // rename cannot make an existing Voucher look new and same-name distinct Masters
        // cannot be silently conflated.
        var missingRecovery = new List<PreparedVoucher>();
        foreach (var request in rawPrepared)
            AddPrepared(client, company, prepared, result, request, skipTallyDuplicateChecks: false,
                ledgerMasterIdByKey: ledgerMasterIdByKey, missingRecovery: missingRecovery);

        if (missingRecovery.Count > 0)
        {
            var snapshots = missingRecovery.ToDictionary(item => item.SourceKey, item => RecoveryStateSnapshot(
                _tallyRepository.GetSourceBindingVersionState(client.Id, client.FinancialYear,
                    company.CompanyKey, item.SourceType, item.SourceAnchor)));
            var descriptions = missingRecovery.Select(item => $"{item.VoucherType} {item.ReferenceNumber} / {item.Date:dd-MMM-yyyy}").ToArray();
            var approved = confirmMissingRecovery is not null && await confirmMissingRecovery(descriptions);
            cancellationToken.ThrowIfCancellationRequested();
            if (approved)
            {
                // Confirmation is not live absence evidence. Read the full FY again after it.
                var recoveryAuthority = await _httpService.ExportVoucherAuthorityFreshAsync(
                    company, authorityFrom, authorityTo, cancellationToken);
                liveBefore = recoveryAuthority.Index;
                _tallyRepository.ReplaceVoucherIndexRange(company.CompanyKey, client.FinancialYear,
                    authorityFrom, authorityTo, recoveryAuthority.Index);
                _tallyRepository.UpsertVoucherXmlAuthority(company.CompanyKey, client.FinancialYear, recoveryAuthority.Full);
            }
            foreach (var item in missingRecovery)
            {
                if (!approved)
                {
                    BlockConflict(result, item, "Missing voucher recovery was not confirmed; prior uncertain write remains held.");
                    continue;
                }
                var current = _tallyRepository.GetSourceBindingVersionState(client.Id, client.FinancialYear,
                    company.CompanyKey, item.SourceType, item.SourceAnchor);
                if (!snapshots[item.SourceKey].Equals(RecoveryStateSnapshot(current), StringComparison.Ordinal))
                {
                    BlockConflict(result, item, "Recovery state changed while confirmation was open; review again before restoring.");
                    continue;
                }
                // A changed date must not hide an existing source reference elsewhere in the FY.
                var reference = NormalizeComparisonText(item.ReferenceNumber);
                if (liveBefore.Any(live => live.VoucherType.Equals(item.VoucherType, StringComparison.OrdinalIgnoreCase) &&
                    NormalizeComparisonText(live.Reference).Equals(reference, StringComparison.OrdinalIgnoreCase)))
                {
                    AddPrepared(client, company, prepared, result, item, ledgerMasterIdByKey: ledgerMasterIdByKey);
                    continue;
                }
                AddPrepared(client, company, prepared, result, item, ledgerMasterIdByKey: ledgerMasterIdByKey,
                    explicitMissingRecovery: true);
            }
        }

        result.Ready = prepared.Count;
        result.ReadyAlters = prepared.Count(item => item.ExistingTallyMasterId.Trim().Length > 0);
        result.ReadyCreates = result.Ready - result.ReadyAlters;
        if (prepared.Count == 0)
        {
            result.Status = BuildSummary(result, false);
            return result;
        }

        const int minBatchSize = 10;
        const int maxBatchSize = 400;
        var batchSize = Math.Min(80, prepared.Count);
        var recoveryBatches = new Stack<PreparedVoucher[]>();
        var knownLiveIds = liveBefore.Select(item => item.MasterId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var acceptedPrepared = new HashSet<PreparedVoucher>();
        var rejectedPrepared = new HashSet<PreparedVoucher>();
        var offset = 0;
        var responseLogs = new List<string>();
        var voucherRequestLogs = new List<string>();
        var attemptedPrepared = new List<PreparedVoucher>();
        var transportUnknown = false;
        var totalCreated = 0;
        var totalAltered = 0;
        var totalIgnored = 0;
        var totalErrors = 0;
        var lineErrors = new List<string>();
        while (offset < prepared.Count || recoveryBatches.Count > 0)
        {
            var recovering = recoveryBatches.Count > 0;
            var batch = recovering ? recoveryBatches.Pop() : prepared.Skip(offset).Take(batchSize).ToArray();
            var take = batch.Length;
            if (!recovering) offset += take;
            foreach (var item in batch)
                if (!attemptedPrepared.Contains(item)) attemptedPrepared.Add(item);
            var voucherXml = BuildVoucherOnlyXml(company.Name, batch);
            var batchLabel = $"Request {voucherRequestLogs.Count + 1} ({take} vouchers; recovery={recovering})";
            voucherRequestLogs.Add($"<!-- AAH accounting {batchLabel}; sent exactly as below -->{Environment.NewLine}{voucherXml}");
            var watch = Stopwatch.StartNew();
            string responseText;
            try
            {
                responseText = await _httpService.ImportXmlAsync(voucherXml, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                foreach (var item in batch)
                {
                    var anchor = item.SourceAnchor.Trim().Length > 0 ? item.SourceAnchor.Trim() : item.SourceKey.Trim();
                    _tallyRepository.UpsertSourceBinding(
                        client.Id, client.FinancialYear, company.CompanyKey, item.SourceType, anchor, item.SourceKey,
                        item.ExistingTallyMasterId, string.Empty, item.ContentHash,
                        item.ExistingTallyMasterId.Trim().Length > 0 ? "AcceptedAwaitingVerification" : "AcceptedAwaitingMasterId",
                        "Transport outcome unknown; previous verified version retained: " + exception.Message);
                }
                transportUnknown = true;
                result.LiveReadErrors++;
                result.LiveReadErrorDetails.Add($"Voucher batch at offset {offset} has unknown Tally transport outcome and was held: {exception.Message}");
                break;
            }
            watch.Stop();
            var batchResponse = TallyHttpService.ParseImportResponse(responseText);
            totalCreated += batchResponse.Created;
            totalAltered += batchResponse.Altered;
            totalIgnored += batchResponse.Ignored;
            totalErrors += batchResponse.Errors + batchResponse.Exceptions;
            if (!string.IsNullOrWhiteSpace(batchResponse.LineError)) lineErrors.Add(batchResponse.LineError);
            responseLogs.Add($"{batchLabel}: Created={batchResponse.Created}; Altered={batchResponse.Altered}; Ignored={batchResponse.Ignored}; Errors={batchResponse.Errors}; Exceptions={batchResponse.Exceptions}; LineError={batchResponse.LineError}; ElapsedMs={watch.ElapsedMilliseconds}");

            var clean = batchResponse.Errors == 0 && batchResponse.Exceptions == 0 &&
                        string.IsNullOrWhiteSpace(batchResponse.LineError) &&
                        batchResponse.Created + batchResponse.Altered == take;
            if (clean) acceptedPrepared.UnionWith(batch);
            foreach (var item in batch)
            {
                var anchor = item.SourceAnchor.Trim().Length > 0 ? item.SourceAnchor.Trim() : item.SourceKey.Trim();
                _tallyRepository.UpsertSourceBinding(
                    client.Id, client.FinancialYear, company.CompanyKey, item.SourceType, anchor, item.SourceKey,
                    item.ExistingTallyMasterId, string.Empty, item.ContentHash,
                    item.ExistingTallyMasterId.Trim().Length > 0 ? "AcceptedAwaitingVerification" : "AcceptedAwaitingMasterId",
                    responseLogs[^1]);
            }

            if (!clean)
            {
                // Counts do not identify which vouchers succeeded. Read fresh authority before
                // cutting a failed batch; never replay a voucher that may already exist in Tally.
                try
                {
                    var recoveryAuthority = await _httpService.ExportVoucherAuthorityFreshAsync(
                        company, result.FromDate, result.ToDate, cancellationToken);
                    _tallyRepository.ReplaceVoucherIndexRange(
                        company.CompanyKey, client.FinancialYear, result.FromDate, result.ToDate, recoveryAuthority.Index);
                    _tallyRepository.UpsertVoucherXmlAuthority(
                        company.CompanyKey, client.FinancialYear, recoveryAuthority.Full);
                    var retry = new List<PreparedVoucher>();
                    var observedCreateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in batch)
                    {
                        var reference = NormalizeComparisonText(item.ReferenceNumber.Length > 0 ? item.ReferenceNumber : item.SourceKey);
                        var candidates = recoveryAuthority.Index.Where(live =>
                            item.ExistingTallyMasterId.Trim().Length > 0
                                ? live.MasterId.Equals(item.ExistingTallyMasterId, StringComparison.OrdinalIgnoreCase)
                                : live.VoucherDate?.Date == item.Date.Date &&
                                  live.VoucherType.Equals(item.VoucherType, StringComparison.OrdinalIgnoreCase) &&
                                  (NormalizeComparisonText(live.Reference).Equals(reference, StringComparison.OrdinalIgnoreCase) ||
                                   NormalizeComparisonText(live.VoucherNumber).Equals(NormalizeComparisonText(item.VoucherNumber), StringComparison.OrdinalIgnoreCase)))
                            .ToArray();
                        if (item.ExistingTallyMasterId.Trim().Length == 0)
                            foreach (var candidate in candidates)
                                if (!knownLiveIds.Contains(candidate.MasterId)) observedCreateIds.Add(candidate.MasterId);
                        if (candidates.Length == 1 && VoucherStrictlyMatchesPrepared(candidates[0], item, ledgerMasterIdByKey))
                            acceptedPrepared.Add(item);
                        else if (candidates.Length == 0 && item.ExistingTallyMasterId.Trim().Length == 0)
                            retry.Add(item);
                        else
                            lineErrors.Add($"{item.VoucherType} {item.VoucherNumber} ({item.Date:yyyy-MM-dd}): held for review; live identity is present or ALTER outcome is unresolved. Not resent.");
                    }
                    knownLiveIds.UnionWith(recoveryAuthority.Index.Select(item => item.MasterId));
                    // If reported creates cannot be located, absence is not trustworthy enough
                    // to authorize another CREATE. Hold this batch and continue untouched batches.
                    if (observedCreateIds.Count < batchResponse.Created)
                    {
                        result.LiveReadErrors++;
                        result.LiveReadErrorDetails.Add($"{batchLabel}: not all reported creates were located; unresolved entries held without resend.");
                    }
                    else if (take == 1)
                    {
                        foreach (var item in retry)
                        {
                            rejectedPrepared.Add(item);
                            lineErrors.Add($"{item.VoucherType} {item.VoucherNumber} ({item.Date:yyyy-MM-dd}): skipped after rejection: {batchResponse.LineError}");
                        }
                    }
                    else
                    {
                        // Split only the verified-absent remainder. Successful prefixes are removed;
                        // subdivision isolates a bad entry while the suffix still travels in batches.
                        var halves = SplitFailedVoucherBatch(retry.ToArray());
                        for (var i = halves.Length - 1; i >= 0; i--) recoveryBatches.Push(halves[i]);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    result.LiveReadErrors++;
                    result.LiveReadErrorDetails.Add($"{batchLabel}: recovery read failed; affected entries held without resend: {exception.Message}");
                }
                continue;
            }

            if (!recovering)
            {
                var ms = watch.ElapsedMilliseconds;
                batchSize = ms <= 2000 ? Math.Min(maxBatchSize, Math.Max(batchSize + 20, (int)Math.Ceiling(batchSize * 1.35)))
                    : ms <= 6000 ? Math.Min(maxBatchSize, batchSize + 10)
                    : ms <= 12000 ? Math.Max(minBatchSize, (int)Math.Floor(batchSize * 0.75))
                    : Math.Max(minBatchSize, batchSize / 2);
            }
        }

        result.TallyCreated = totalCreated;
        result.TallyAltered = totalAltered;
        result.TallyIgnored = totalIgnored;
        result.TallyErrors = totalErrors;
        result.LineError = string.Join(" | ", lineErrors.Distinct());
        var responseSummary = $"Created={totalCreated}; Altered={totalAltered}; Ignored={totalIgnored}; Errors={totalErrors}; LineError={result.LineError}; AdaptiveBatches={responseLogs.Count}";
        var protocolRequest = voucherRequestLogs.Count > 0
            ? string.Join(Environment.NewLine + Environment.NewLine, voucherRequestLogs)
            : masterXml;
        var protocolResponse = string.Join(Environment.NewLine, new[]
        {
            masterResponseText,
            string.Join(Environment.NewLine, responseLogs)
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        result.ResponsePath = SaveResponseLog(client, result.FromDate, result.ToDate, protocolRequest, protocolResponse);

        // Fix61 accepted-write barrier: persist the fact that Tally accepted/may have
        // mutated the envelope before any post-write read. The next export is never
        // allowed to turn these sources back into CREATE merely because authority is
        // temporarily behind Tally. Existing ALTER MASTERIDs are retained immediately.
        var cleanWriteResponse = !transportUnknown && result.TallyErrors == 0 && string.IsNullOrWhiteSpace(result.LineError);
        if (cleanWriteResponse || result.TallyCreated > 0 || result.TallyAltered > 0 || transportUnknown)
        {
            foreach (var item in attemptedPrepared.Where(item => !rejectedPrepared.Contains(item)))
            {
                var anchor = item.SourceAnchor.Trim().Length > 0 ? item.SourceAnchor.Trim() : item.SourceKey.Trim();
                _tallyRepository.UpsertSourceBinding(
                    client.Id, client.FinancialYear, company.CompanyKey, item.SourceType, anchor, item.SourceKey,
                    item.ExistingTallyMasterId, string.Empty, item.ContentHash,
                    item.ExistingTallyMasterId.Trim().Length > 0 ? "AcceptedAwaitingVerification" : "AcceptedAwaitingMasterId",
                    responseSummary);
            }
        }

        // Fix68 retains the Fix37 write-success rule: a clean IMPORTRESULT is immediate
        // acceptance evidence. Identity verification is a separate fresh live Voucher
        // authority read; failure of that read must never cause the same envelope to be
        // automatically re-sent.
        IReadOnlyList<TallyVoucherIndexRecord> liveAfter;
        var postWriteVerificationAvailable = true;
        try
        {
            // Fix68 post-write identity capture. Tally import responses expose only the
            // last created Voucher ID for a multi-Voucher batch, so the exact per-source
            // MASTERIDs must come from the live Voucher objects Tally generated. Read the
            // written accounting span once and take MASTERID/GUID/ALTERID + full body from
            // that same response. Do not rely on a global ALTERID pulse being visible yet.
            // CL017: verify each written calendar month separately. A selected
            // restore may cover the full FY, but no post-import full-FY Voucher XML
            // request may be sent to Tally (native access-violation pressure).
            var boundedLiveAfter = new List<TallyVoucherIndexRecord>();
            foreach (var group in attemptedPrepared.GroupBy(item => (item.Date.Year, item.Date.Month))
                         .OrderBy(group => group.Key.Year).ThenBy(group => group.Key.Month))
            {
                var verifyFrom = group.Min(item => item.Date.Date);
                var verifyTo = group.Max(item => item.Date.Date);
                var postAuthority = await _httpService.ExportVoucherAuthorityFreshAsync(
                    company, verifyFrom, verifyTo, cancellationToken);
                _tallyRepository.ReplaceVoucherIndexRange(
                    company.CompanyKey, client.FinancialYear, verifyFrom, verifyTo, postAuthority.Index);
                _tallyRepository.UpsertVoucherXmlAuthority(
                    company.CompanyKey, client.FinancialYear, postAuthority.Full);
                boundedLiveAfter.AddRange(postAuthority.Index);
            }
            liveAfter = boundedLiveAfter;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            postWriteVerificationAvailable = false;
            liveAfter = _tallyRepository.GetVoucherIndexRange(
                company.CompanyKey, client.FinancialYear, result.FromDate, result.ToDate);
            result.LiveReadErrors++;
            result.LiveReadErrorDetails.Add("Post-write live Voucher identity verification pending: " + exception.Message);
        }

        TallyVoucherIndexRecord? FindLive(PreparedVoucher item)
        {
            if (item.ExistingTallyMasterId.Trim().Length > 0)
            {
                var bound = liveAfter.FirstOrDefault(voucher =>
                    voucher.MasterId.Equals(item.ExistingTallyMasterId, StringComparison.OrdinalIgnoreCase));
                return bound is not null && VoucherStrictlyMatchesPrepared(bound, item, ledgerMasterIdByKey) ? bound : null;
            }

            var reference = item.ReferenceNumber.Length > 0 ? item.ReferenceNumber : item.SourceKey;
            var normalizedReference = NormalizeComparisonText(reference);
            var candidates = liveAfter.Where(voucher =>
                NormalizeComparisonText(voucher.Reference).Equals(normalizedReference, StringComparison.OrdinalIgnoreCase) &&
                voucher.VoucherType.Equals(item.VoucherType, StringComparison.OrdinalIgnoreCase) &&
                voucher.VoucherDate?.Date == item.Date.Date).ToArray();
            if (candidates.Length == 0) return null;
            var exact = candidates.Where(voucher => VoucherStrictlyMatchesPrepared(voucher, item, ledgerMasterIdByKey)).ToArray();
            return exact.Length == 1 ? exact[0] : null;
        }

        var verifiedPairs = postWriteVerificationAvailable
            ? attemptedPrepared.Select(item => (Item: item, Live: FindLive(item))).Where(pair => pair.Live is not null).ToArray()
            : Array.Empty<(PreparedVoucher Item, TallyVoucherIndexRecord? Live)>();
        var verified = verifiedPairs.Select(pair => pair.Item).ToArray();
        var verifiedSet = verified.ToHashSet();
        var unverified = attemptedPrepared.Where(item => !verifiedSet.Contains(item)).ToArray();
        result.Imported = acceptedPrepared.Union(verifiedSet).Count();
        result.UnverifiedAfterImport = unverified.Count(item => !rejectedPrepared.Contains(item));

        foreach (var group in verified.GroupBy(item => item.SourceType, StringComparer.OrdinalIgnoreCase))
        {
            _tallyRepository.RecordImportFingerprints(
                client.Id, client.FinancialYear, company.CompanyKey, group.Key,
                group.Select(item => (item.SourceKey, item.ContentHash)),
                "Imported", responseSummary + "; LiveVerified=Yes");
        }
        foreach (var pair in verifiedPairs)
        {
            var live = pair.Live!;
            var anchor = pair.Item.SourceAnchor.Trim().Length > 0 ? pair.Item.SourceAnchor.Trim() : pair.Item.SourceKey.Trim();
            _tallyRepository.ConfirmSourceBindingVersion(
                client.Id, client.FinancialYear, company.CompanyKey, pair.Item.SourceType, anchor, pair.Item.SourceKey,
                live.MasterId, live.Guid, pair.Item.ContentHash, BuildLiveVoucherVersion(live, ledgerMasterIdByKey),
                ParseAlterId(live.AlterId), responseSummary + "; LiveVerified=Yes; Previous->Desired version committed with Tally Voucher Number preserved.");
        }
        foreach (var group in unverified.GroupBy(item => (item.SourceType, Accepted: acceptedPrepared.Contains(item))))
        {
            var unverifiedStatus = group.Key.Accepted ? "Imported" : "Failed";
            _tallyRepository.RecordImportFingerprints(
                client.Id, client.FinancialYear, company.CompanyKey, group.Key.SourceType,
                group.Select(item => (item.SourceKey, item.ContentHash)),
                unverifiedStatus, responseSummary + "; PostWriteVerified=No; RequireFreshAuthorityBeforeResend=Yes");
        }
        foreach (var item in unverified)
        {
            var anchor = item.SourceAnchor.Trim().Length > 0 ? item.SourceAnchor.Trim() : item.SourceKey.Trim();
            _tallyRepository.UpsertSourceBinding(
                client.Id, client.FinancialYear, company.CompanyKey, item.SourceType, anchor, item.SourceKey,
                item.ExistingTallyMasterId, string.Empty, item.ContentHash,
                acceptedPrepared.Contains(item)
                    ? (item.ExistingTallyMasterId.Trim().Length > 0 ? "AcceptedAwaitingVerification" : "AcceptedAwaitingMasterId")
                    : (rejectedPrepared.Contains(item) ? "Rejected" : "NeedsReview"),
                responseSummary + "; PostWriteVerified=No; RequireFreshAuthorityBeforeResend=Yes");
        }
        result.Status = BuildSummary(result, result.Imported > 0) + $" Rejected and skipped {rejectedPrepared.Count:N0}; batch recovery enabled.";
        return result;
    }

    /// <summary>
    /// Builds a current full-accounting XML snapshot from local repositories only.
    /// No Tally HTTP call is made and no import fingerprint is changed. This is the
    /// live preview maintained after Purchase/Sales/Journal/GST refreshes; the
    /// explicit Tally Export action still performs live duplicate checks and sends.
    /// </summary>
    public TallyLiveAccountingXmlSnapshot GenerateLiveXml(
        ClientProfile client,
        TallyCompanyInfo company,
        DateTime fromDate,
        DateTime toDate)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(company);
        if (toDate.Date < fromDate.Date) (fromDate, toDate) = (toDate, fromDate);

        var range = new TallyVoucherExportRange
        {
            FromDate = fromDate.Date,
            ToDate = toDate.Date
        };
        var ranges = new[] { range };
        var result = new TallyVoucherExportResult
        {
            FromDate = range.FromDate,
            ToDate = range.ToDate
        };

        var allMasters = _masterRepository
            .Get(client.Id, client.FinancialYear, false)
            .ToArray();
        var ledgerParents = allMasters
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                             master.Name.Trim().Length > 0)
            .GroupBy(master => master.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(master => master.Parent.Trim())
                    .FirstOrDefault(parent => parent.Length > 0) ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
        var prepared = new List<PreparedVoucher>();
        var settings = _journalRepository.GetSettings(client.Id, client.FinancialYear);

        AddPurchaseVouchers(client, company, ranges, settings, prepared, result);
        AddSalesVouchers(client, company, ranges, prepared, result);
        AddCashEntryVouchers(client, company, ranges, ledgerParents, prepared, result);
        AddManualJournals(client, company, ranges, prepared, result);
        AddAutomaticGstJournals(client, company, ranges, prepared, result);
        result.Ready = prepared.Count;

        var stockItems = _stockMasterRepository.GetStockItems(client.Id, client.FinancialYear);
        var stockItemUnitProfiles = _stockMasterRepository.GetStockItemUnitProfiles(client.Id, client.FinancialYear);
        var stockUnits = _stockMasterRepository.GetUnits(client.Id, client.FinancialYear);
        var stockGroups = _stockMasterRepository.GetSimpleMasters(client.Id, client.FinancialYear, "Stock Group");
        var stockCategories = _stockMasterRepository.GetSimpleMasters(client.Id, client.FinancialYear, "Stock Category");
        var godowns = _stockMasterRepository.GetSimpleMasters(client.Id, client.FinancialYear, "Godown");
        var costCentres = _stockMasterRepository.GetSimpleMasters(client.Id, client.FinancialYear, "Cost Centre");
        var bankAccounts = _bankingRepository.GetAccounts(client.Id, client.FinancialYear);
        var xml = BuildImportXml(
            company.Name,
            range.FromDate,
            prepared,
            allMasters,
            bankAccounts,
            stockItems,
            stockItemUnitProfiles,
            stockUnits,
            stockGroups,
            stockCategories,
            godowns,
            costCentres);

        return new TallyLiveAccountingXmlSnapshot
        {
            Xml = xml,
            SourceAnchors = prepared.Select(voucher =>
                voucher.SourceAnchor.Trim().Length > 0
                    ? voucher.SourceAnchor.Trim()
                    : voucher.SourceKey.Trim()).ToArray(),
            SourceEntityTypes = prepared.Select(voucher => voucher.SourceType).ToArray(),
            FromDate = range.FromDate,
            ToDate = range.ToDate,
            Selected = result.Selected,
            Ready = result.Ready,
            MissingLedgers = result.MissingLedgers,
            Unsupported = result.Unsupported,
            RepeatedSourceCandidatesSkipped = result.RepeatedSourceCandidatesSkipped,
            ExactDuplicatesSkipped = result.ExactDuplicatesSkipped,
            ConflictsBlocked = result.ConflictsBlocked,
            AuthorityAlterId = _tallyRepository.GetBankSyncState(client.Id, client.FinancialYear, company.CompanyKey)?.LastAlterId ?? 0L,
            NewMastersToCreate = CountCreateMasters(xml)
        };
    }

    private static int CountCreateMasters(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return 0;
        return xml.Split('\n')
            .Count(line => line.Contains("ACTION=\"Create\"", StringComparison.OrdinalIgnoreCase) &&
                           !line.Contains("<VOUCHER", StringComparison.OrdinalIgnoreCase));
    }

    private void AddPurchaseVouchers(
        ClientProfile client,
        TallyCompanyInfo company,
        IReadOnlyList<TallyVoucherExportRange> ranges,
        JournalGenerationSettings settings,
        ICollection<PreparedVoucher> target,
        TallyVoucherExportResult result,
        bool skipTallyDuplicateChecks = false,
        IReadOnlySet<string>? explicitRestoreSourceAnchors = null)
    {
        IReadOnlyList<PurchaseImportRecord> records;
        try { records = _purchaseRepository.GetRecords(client.Id, client.FinancialYear); }
        catch { records = []; }

        // Excel parity: Purchase and Debit Note use GST Date as voucher date and
        // Invoice/Note Date as reference date. Rows without GST Date are retained
        // in the app but are not silently assigned another voucher date.
        var groups = records
            .Where(row => row.GstDate is DateTime date && ranges.Any(range => range.Contains(date)))
            .GroupBy(row =>
            {
                var gstin = NormalizeGstin(row.SupplierGstin);
                var canonicalParty = ResolveLedgerCanonicalName(client, row.SupplierName);
                return new
                {
                    VoucherType = NormalizePurchaseDestination(row.Destination, row.DocumentType),
                    DocumentKey = NormalizeDocumentNumberKey(row.DocumentNumber),
                    VoucherDate = row.GstDate!.Value.Date,
                    ReferenceDate = row.DocumentDate?.Date,
                    PartyIdentity = BuildDocumentPartyIdentity(gstin, canonicalParty),
                    Gstin = gstin,
                    IsRcm = row.Destination.Equals("RCM", StringComparison.OrdinalIgnoreCase) ||
                            row.ReverseCharge.Equals("Y", StringComparison.OrdinalIgnoreCase) ||
                            row.ReverseCharge.Equals("Yes", StringComparison.OrdinalIgnoreCase),
                    IsDebitNoteReverse = row.Destination.Equals(
                        PurchaseTreatmentPolicy.DebitNoteReverseDestination,
                        StringComparison.OrdinalIgnoreCase)
                };
            });

        foreach (var group in groups)
        {
            result.Selected++;
            var rows = group.ToArray();
            var documentNumber = ResolveDocumentNumber(rows.Select(row => row.DocumentNumber));
            var party = ResolveDocumentPartyName(
                client,
                rows.Select(row => row.SupplierName),
                group.Key.Gstin);
            if (party.Length == 0 || documentNumber.Length == 0)
            { result.MissingLedgers++; continue; }
            if (group.Key.ReferenceDate is null)
            { result.Unsupported++; continue; }
            if (!HasRequiredPurchaseLedgers(rows, group.Key.IsRcm))
            { result.MissingLedgers++; continue; }
            var inventoryEntries = BuildPurchaseInventoryEntries(rows, group.Key.VoucherType, group.Key.IsRcm);
            var inventoryInvoice = inventoryEntries.Count > 0;
            var entries = BuildPurchaseEntries(
                rows, group.Key.VoucherType, party, group.Key.IsRcm, settings, inventoryInvoice, inventoryEntries);
            var purchaseInventoryIsDebit =
                group.Key.VoucherType.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ||
                rows.Any(PurchaseTreatmentPolicy.IsDebitNoteReverse);
            if (entries.Count == 0 || entries.Any(entry => entry.LedgerName.Length == 0) ||
                !IsBalanced(entries, inventoryEntries, purchaseInventoryIsDebit))
            { result.Unsupported++; continue; }

            var sourceKey = BuildDocumentSourceKey("PURCHASE", client, group.Key.VoucherType,
                documentNumber, group.Key.Gstin, group.Key.VoucherDate);
            var narration = group.Key.IsRcm
                ? $"Purchase under Reverse Charge - {documentNumber}"
                : group.Key.IsDebitNoteReverse
                    ? $"Supplier Debit Note - Tally Credit Note - Purchase/Input GST increase - {documentNumber}"
                    : $"{group.Key.VoucherType} {documentNumber}";
            AddPrepared(client, company, target, result, new PreparedVoucher(
                "Purchase Voucher", sourceKey, string.Empty, group.Key.VoucherDate,
                group.Key.ReferenceDate, group.Key.VoucherType, documentNumber,
                documentNumber, party, group.Key.Gstin,
                rows.Select(row => row.PlaceOfSupply.Trim()).FirstOrDefault(value => value.Length > 0) ?? string.Empty,
                narration, entries, InventoryEntries: inventoryEntries, IsReverseCharge: group.Key.IsRcm,
                SourceAnchor: BuildStableRecordAnchor("PURCHASE", client, rows.Select(row => row.PersistentId))), skipTallyDuplicateChecks,
                explicitRestoreSourceAnchors: explicitRestoreSourceAnchors);
        }
    }

    private void AddSalesVouchers(
        ClientProfile client,
        TallyCompanyInfo company,
        IReadOnlyList<TallyVoucherExportRange> ranges,
        ICollection<PreparedVoucher> target,
        TallyVoucherExportResult result,
        bool skipTallyDuplicateChecks = false,
        IReadOnlySet<string>? explicitRestoreSourceAnchors = null)
    {
        IReadOnlyList<SalesImportRecord> records;
        try { records = _salesRepository.GetRecords(client.Id, client.FinancialYear); }
        catch { records = []; }
        var groups = records
            .Where(row => (row.GstDate ?? row.DocumentDate) is DateTime date && ranges.Any(range => range.Contains(date)))
            .GroupBy(row =>
            {
                var gstin = NormalizeGstin(row.CustomerGstin);
                var canonicalParty = ResolveLedgerCanonicalName(client, row.CustomerName);
                return new
                {
                    VoucherType = NormalizeSalesDestination(row.Destination, row.DocumentType),
                    DocumentKey = NormalizeDocumentNumberKey(row.DocumentNumber),
                    VoucherDate = (row.GstDate ?? row.DocumentDate)!.Value.Date,
                    ReferenceDate = row.DocumentDate?.Date,
                    PartyIdentity = BuildDocumentPartyIdentity(gstin, canonicalParty),
                    Gstin = gstin
                };
            });

        foreach (var group in groups)
        {
            result.Selected++;
            var rows = group.ToArray();
            var documentNumber = ResolveDocumentNumber(rows.Select(row => row.DocumentNumber));
            var party = ResolveDocumentPartyName(
                client,
                rows.Select(row => row.CustomerName),
                group.Key.Gstin);
            if (party.Length == 0 || documentNumber.Length == 0)
            { result.MissingLedgers++; continue; }
            if (group.Key.ReferenceDate is null)
            { result.Unsupported++; continue; }
            if (!HasRequiredSalesLedgers(rows))
            { result.MissingLedgers++; continue; }

            var inventoryEntries = BuildSalesInventoryEntries(rows, group.Key.VoucherType);
            var inventoryInvoice = inventoryEntries.Count > 0;
            var entries = BuildSalesEntries(rows, group.Key.VoucherType, party, inventoryInvoice, inventoryEntries);
            if (entries.Count == 0 || entries.Any(entry => entry.LedgerName.Length == 0) ||
                !IsBalanced(entries, inventoryEntries, !group.Key.VoucherType.Equals("Sales", StringComparison.OrdinalIgnoreCase)))
            { result.Unsupported++; continue; }

            var sourceKey = BuildDocumentSourceKey("SALES", client, group.Key.VoucherType,
                documentNumber, group.Key.Gstin, group.Key.VoucherDate);
            var business = rows.FirstOrDefault(row => HasSalesBusinessData(row)) ?? rows[0];
            var narration = rows.Select(row => row.Narration.Trim()).FirstOrDefault(value => value.Length > 0)
                            ?? $"{group.Key.VoucherType} {documentNumber}";
            AddPrepared(client, company, target, result, new PreparedVoucher(
                "Sales Voucher", sourceKey, string.Empty, group.Key.VoucherDate,
                group.Key.ReferenceDate, group.Key.VoucherType, documentNumber,
                documentNumber, party, group.Key.Gstin,
                rows.Select(row => row.PlaceOfSupply.Trim()).FirstOrDefault(value => value.Length > 0) ?? string.Empty,
                narration, entries, inventoryEntries, business,
                SourceAnchor: BuildStableRecordAnchor("SALES", client, rows.Select(row => row.PersistentId))), skipTallyDuplicateChecks,
                explicitRestoreSourceAnchors: explicitRestoreSourceAnchors);
        }
    }

    private void AddCashEntryVouchers(
        ClientProfile client,
        TallyCompanyInfo company,
        IReadOnlyList<TallyVoucherExportRange> ranges,
        IReadOnlyDictionary<string, string> ledgerParents,
        ICollection<PreparedVoucher> target,
        TallyVoucherExportResult result,
        bool includeRecurringCash = true,
        bool skipTallyDuplicateChecks = false,
        IReadOnlySet<string>? explicitRestoreSourceAnchors = null)
    {
        IReadOnlyList<CashEntryRecord> records;
        try { records = _bankingRepository.GetCashEntries(client.Id, client.FinancialYear); }
        catch { records = []; }

        // R23: one logical Payment/Receipt/Contra voucher per date and direction.
        // The legacy single-row route remains unchanged for all other Cash entries.
        if (includeRecurringCash)
            AddRecurringCashVouchers(client, company, ranges, ledgerParents, records, target, result, skipTallyDuplicateChecks, explicitRestoreSourceAnchors);

        foreach (var row in records.Where(row =>
                     !row.SourceJobId.StartsWith("R23-CASH:", StringComparison.Ordinal) &&
                     ranges.Any(range => range.Contains(row.EntryDate)) &&
                     row.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase)))
        {
            result.Selected++;
            var transactionLedger = (row.MappedMasterLedger.Length > 0
                ? row.MappedMasterLedger
                : row.EntryLedger).Trim();
            var commonLedger = row.CommonLedger.Trim();
            var amount = Round(row.Amount);
            var direction = row.Debit > 0m ? "DR" : row.Credit > 0m ? "CR" : row.SingleDrCr.Trim().ToUpperInvariant();
            if (transactionLedger.Length == 0 || commonLedger.Length == 0)
            { result.MissingLedgers++; continue; }
            if (amount <= 0.005m || direction is not ("DR" or "CR"))
            { result.Unsupported++; continue; }

            ledgerParents.TryGetValue(transactionLedger, out var transactionGroup);
            ledgerParents.TryGetValue(commonLedger, out var commonGroup);
            transactionGroup ??= string.Empty;
            commonGroup ??= string.Empty;
            if (IsCashOrBankGroup(transactionGroup) && IsCashOrBankGroup(commonGroup))
            { result.Unsupported++; continue; }
            var voucherType = ResolveCashVoucherType(commonGroup, direction, row.VoucherType);
            if (voucherType.Length == 0)
            { result.Unsupported++; continue; }

            var party = voucherType.Equals("Journal", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : transactionLedger;
            var entries = direction == "DR"
                ? new[]
                {
                    new VoucherLedgerEntry(transactionLedger, amount, true,
                        party.Length > 0),
                    new VoucherLedgerEntry(commonLedger, amount, false, false, "Cash-in-hand")
                }
                : new[]
                {
                    new VoucherLedgerEntry(transactionLedger, amount, false,
                        party.Length > 0),
                    new VoucherLedgerEntry(commonLedger, amount, true, false, "Cash-in-hand")
                };
            if (!IsBalanced(entries)) { result.Unsupported++; continue; }

            var identity = row.EntryId.Trim().Length > 0
                ? row.EntryId.Trim()
                : row.Id.ToString(CultureInfo.InvariantCulture);
            var sourceKey = $"AAH|CASH|{client.Id}|{client.FinancialYear}|{identity}|{row.EntryDate:yyyyMMdd}";
            AddPrepared(client, company, target, result, new PreparedVoucher(
                "Cash Voucher", sourceKey, string.Empty, row.EntryDate.Date, null,
                voucherType, string.Empty, identity, party, string.Empty, string.Empty,
                row.Narration.Trim(), entries, SourceAnchor: $"AAH|CASH|{client.Id}|{client.FinancialYear}|{identity}"), skipTallyDuplicateChecks,
                explicitRestoreSourceAnchors: explicitRestoreSourceAnchors);
        }
    }

    private void AddRecurringCashVouchers(
        ClientProfile client, TallyCompanyInfo company,
        IReadOnlyList<TallyVoucherExportRange> ranges,
        IReadOnlyDictionary<string, string> ledgerParents,
        IReadOnlyList<CashEntryRecord> records,
        ICollection<PreparedVoucher> target,
        TallyVoucherExportResult result,
        bool skipTallyDuplicateChecks,
        IReadOnlySet<string>? explicitRestoreSourceAnchors = null)
    {
        foreach (var group in records.Where(row => row.SourceJobId.StartsWith("R23-CASH:", StringComparison.Ordinal))
                     .GroupBy(row => row.SourceJobId, StringComparer.Ordinal))
        {
            var rows = group.OrderBy(row => row.EntryId, StringComparer.Ordinal).ToArray();
            if (!rows.Any(row => ranges.Any(range => range.Contains(row.EntryDate)))) continue;
            result.Selected++;
            if (!RecurringDirectExportAuthority.IsCompleteCashGroup(client.Id, client.FinancialYear, group.Key, out var reason))
            {
                result.ConflictsBlocked++;
                result.ConflictDetails.Add($"Recurring Cash {group.Key}: {reason}");
                continue;
            }
            var first = rows[0];
            var date = first.EntryDate.Date;
            var common = first.CommonLedger.Trim();
            var direction = first.Debit > 0m ? "DR" : "CR";
            var voucherType = first.VoucherType.Trim();
            if (rows.Any(row => row.EntryDate.Date != date ||
                    !row.CommonLedger.Trim().Equals(common, StringComparison.OrdinalIgnoreCase) ||
                    !row.VoucherType.Trim().Equals(voucherType, StringComparison.OrdinalIgnoreCase) ||
                    (row.Debit > 0m ? "DR" : "CR") != direction ||
                    !row.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase) ||
                    row.Debit < 0m || row.Credit < 0m ||
                    (row.Debit > 0m && row.Credit > 0m) ||
                    row.Amount <= 0.005m ||
                    !ranges.Any(range => range.Contains(row.EntryDate))))
            {
                result.ConflictsBlocked++;
                result.ConflictDetails.Add($"Recurring Cash {group.Key}: incompatible date, direction, voucher type, amount or status. Group remains HOLD.");
                continue;
            }
            if (!ledgerParents.TryGetValue(common, out var commonGroup) ||
                !ledgerParents.ContainsKey(common) ||
                voucherType is not ("Payment" or "Receipt" or "Contra" or "Journal"))
            {
                result.MissingLedgers++;
                continue;
            }
            var counterpart = new List<VoucherLedgerEntry>();
            var valid = true;
            foreach (var row in rows)
            {
                var ledger = (row.MappedMasterLedger.Length > 0 ? row.MappedMasterLedger : row.EntryLedger).Trim();
                if (!ledgerParents.ContainsKey(ledger) || ledger.Equals(common, StringComparison.OrdinalIgnoreCase))
                { valid = false; break; }
                counterpart.Add(new VoucherLedgerEntry(ledger, Round(row.Amount), direction == "DR", true));
            }
            if (!valid)
            {
                result.MissingLedgers++;
                continue;
            }
            var amount = Round(counterpart.Sum(entry => entry.Amount));
            if (amount <= 0.005m) { result.Unsupported++; continue; }
            var entries = counterpart
                .GroupBy(entry => (entry.LedgerName, entry.IsDebit), new RecurringCashPostingComparer())
                .Select(grouping => new VoucherLedgerEntry(grouping.Key.LedgerName,
                    Round(grouping.Sum(entry => entry.Amount)), grouping.Key.IsDebit, true))
                .ToList();
            entries.Add(new VoucherLedgerEntry(common, amount, direction != "DR", false, commonGroup));
            if (!IsBalanced(entries)) { result.Unsupported++; continue; }
            var party = voucherType.Equals("Journal", StringComparison.OrdinalIgnoreCase) ? string.Empty : counterpart[0].LedgerName;
            var reference = first.ReferenceNumber.Trim();
            if (reference.Length == 0) { result.Unsupported++; continue; }
            var sourceAnchor = $"AAH|CASH|{client.Id}|{client.FinancialYear}|{group.Key}";
            var sourceKey = $"{sourceAnchor}|{date:yyyyMMdd}";
            AddPrepared(client, company, target, result, new PreparedVoucher(
                "Cash Voucher", sourceKey, string.Empty, date, null,
                voucherType, string.Empty, reference, party, string.Empty, string.Empty,
                first.Narration.Trim(), entries, SourceAnchor: sourceAnchor), skipTallyDuplicateChecks,
                explicitRestoreSourceAnchors: explicitRestoreSourceAnchors);
        }
    }

    private sealed class RecurringCashPostingComparer : IEqualityComparer<(string LedgerName, bool IsDebit)>
    {
        public bool Equals((string LedgerName, bool IsDebit) x, (string LedgerName, bool IsDebit) y) =>
            x.IsDebit == y.IsDebit && string.Equals(x.LedgerName, y.LedgerName, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string LedgerName, bool IsDebit) value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.LedgerName), value.IsDebit);
    }

    private void AddManualJournals(
        ClientProfile client,
        TallyCompanyInfo company,
        IReadOnlyList<TallyVoucherExportRange> ranges,
        ICollection<PreparedVoucher> target,
        TallyVoucherExportResult result,
        bool skipTallyDuplicateChecks = false,
        IReadOnlySet<string>? explicitRestoreSourceAnchors = null)
    {
        foreach (var voucher in _journalRepository.GetVouchers(client.Id, client.FinancialYear)
                     .Where(voucher =>
                         !voucher.GenerationMode.Equals("Automatic", StringComparison.OrdinalIgnoreCase) &&
                         IsExportableJournalStatus(voucher.WorkflowStatus) &&
                         ranges.Any(range => range.Contains(voucher.VoucherDate))))
        {
            result.Selected++;
            var entries = voucher.Lines
                .Where(line => line.Amount > 0.005m && line.LedgerName.Trim().Length > 0)
                .OrderBy(line => line.LineNumber)
                .Select(line => new VoucherLedgerEntry(
                    line.LedgerName.Trim(), Round(line.Amount),
                    line.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase),
                    false))
                .ToArray();
            if (entries.Length == 0 || !IsBalanced(entries))
            { result.Unsupported++; continue; }

            var identity = voucher.VoucherNumber.Trim().Length > 0
                ? voucher.VoucherNumber.Trim()
                : voucher.PersistentId.ToString(CultureInfo.InvariantCulture);
            var sourceKey = $"AAH|JOURNAL|{client.Id}|{client.FinancialYear}|{identity}|{voucher.VoucherDate:yyyyMMdd}";
            var voucherNumber = voucher.VoucherNumber.Trim().StartsWith("GST-", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : voucher.VoucherNumber.Trim();
            AddPrepared(client, company, target, result, new PreparedVoucher(
                "Manual Journal", sourceKey, string.Empty, voucher.VoucherDate.Date, null,
                "Journal", voucherNumber, identity, string.Empty,
                string.Empty, string.Empty, voucher.Narration.Trim(), entries,
                SourceAnchor: $"AAH|JOURNAL|{client.Id}|{client.FinancialYear}|PID:{voucher.PersistentId}"), skipTallyDuplicateChecks,
                explicitRestoreSourceAnchors: explicitRestoreSourceAnchors);
        }
    }

    private static bool IsExportableJournalStatus(string status)
    {
        var value = (status ?? string.Empty).Trim();
        return !value.Equals("Archived", StringComparison.OrdinalIgnoreCase) &&
               !value.Equals("Deleted", StringComparison.OrdinalIgnoreCase) &&
               !value.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) &&
               !value.Equals("Void", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveCashVoucherType(
        string commonGroup,
        string direction,
        string storedVoucherType)
    {
        if (IsCashOrBankGroup(commonGroup))
            return direction.Equals("DR", StringComparison.OrdinalIgnoreCase) ? "Payment" : "Receipt";
        if (commonGroup.Trim().Length > 0) return "Journal";

        // Older imported Cash Entry rows may predate master-group capture. Keep
        // their already validated workbook-derived voucher type as a fallback.
        var stored = (storedVoucherType ?? string.Empty).Trim();
        if (stored.Equals("Payment", StringComparison.OrdinalIgnoreCase)) return "Payment";
        if (stored.Equals("Receipt", StringComparison.OrdinalIgnoreCase)) return "Receipt";
        if (stored.Equals("Journal", StringComparison.OrdinalIgnoreCase)) return "Journal";
        return string.Empty;
    }

    private static bool IsCashOrBankGroup(string groupName)
    {
        var key = new string((groupName ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        return key is "CASHINHAND" or "BANKACCOUNTS";
    }

    private void AddAutomaticGstJournals(
        ClientProfile client,
        TallyCompanyInfo company,
        IReadOnlyList<TallyVoucherExportRange> ranges,
        ICollection<PreparedVoucher> target,
        TallyVoucherExportResult result,
        bool skipTallyDuplicateChecks = false,
        IReadOnlySet<string>? explicitRestoreSourceAnchors = null)
    {
        foreach (var voucher in _journalRepository.GetVouchers(client.Id, client.FinancialYear)
                     .Where(voucher => voucher.GenerationMode.Equals("Automatic", StringComparison.OrdinalIgnoreCase) &&
                                       IsExportableJournalStatus(voucher.WorkflowStatus) &&
                                       ranges.Any(range => range.Contains(voucher.VoucherDate))))
        {
            result.Selected++;
            var entries = voucher.Lines
                .Where(line => line.Amount != 0m && line.LedgerName.Trim().Length > 0)
                .Select(line => new VoucherLedgerEntry(line.LedgerName.Trim(), Math.Abs(line.Amount),
                    line.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase), false)).ToArray();
            if (entries.Length == 0 || !IsBalanced(entries)) { result.Unsupported++; continue; }
            // Stable logical identity: GST Journal regeneration replaces database rows,
            // so PersistentId must never be part of the Tally duplicate/reference key.
            var sourceKey = $"AAH|GSTJ|{client.Id}|{client.FinancialYear}|{voucher.PeriodKey}";
            var voucherNumber = voucher.VoucherNumber.Trim().StartsWith("GST-", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : voucher.VoucherNumber.Trim();
            AddPrepared(client, company, target, result, new PreparedVoucher(
                "GST Journal", sourceKey, string.Empty, voucher.VoucherDate.Date, null,
                "Journal", voucherNumber, sourceKey, string.Empty, string.Empty, string.Empty,
                voucher.Narration.Trim(), entries,
                HasRcmAdjustment: voucher.Lines.Any(line =>
                    line.LineRole.Contains("RCM", StringComparison.OrdinalIgnoreCase)),
                SourceAnchor: $"AAH|GSTJ|{client.Id}|{client.FinancialYear}|{voucher.PeriodKey}"),
                skipTallyDuplicateChecks,
                explicitRestoreSourceAnchors: explicitRestoreSourceAnchors);
        }
    }

    private static (DateTime From, DateTime To) GetFinancialYearBounds(string financialYear, DateTime fallbackFrom, DateTime fallbackTo)
    {
        var parts = (financialYear ?? string.Empty).Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 1 && int.TryParse(parts[0], out var startYear) && startYear >= 1900 && startYear <= 9998)
            return (new DateTime(startYear, 4, 1), new DateTime(startYear + 1, 3, 31));
        return (fallbackFrom.Date, fallbackTo.Date);
    }

    private static bool LegacySourceIdentityEquivalent(string left, string right)
    {
        var a = (left ?? string.Empty).Split('|');
        var b = (right ?? string.Empty).Split('|');
        if (a.Length != b.Length || a.Length < 5) return false;
        if (!a[0].Equals("AAH", StringComparison.OrdinalIgnoreCase) ||
            !b[0].Equals("AAH", StringComparison.OrdinalIgnoreCase) ||
            !a[1].Equals(b[1], StringComparison.OrdinalIgnoreCase) ||
            !a[2].Equals(b[2], StringComparison.OrdinalIgnoreCase) ||
            !a[3].Equals(b[3], StringComparison.OrdinalIgnoreCase)) return false;
        var module = a[1].ToUpperInvariant();
        if (module is "PURCHASE" or "SALES")
        {
            if (a.Length != 8) return false;
            return a[4].Equals(b[4], StringComparison.OrdinalIgnoreCase) &&
                   a[6].Equals(b[6], StringComparison.OrdinalIgnoreCase) &&
                   a[7].Equals(b[7], StringComparison.OrdinalIgnoreCase);
        }
        if (module is "CASH" or "JOURNAL")
        {
            if (a.Length != 6) return false;
            return a[4].Equals(b[4], StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildStableRecordAnchor(string module, ClientProfile client, IEnumerable<long> persistentIds)
    {
        var ids = persistentIds.Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();
        return ids.Length == 0
            ? string.Empty
            : $"AAH|{module}|{client.Id}|{client.FinancialYear}|PID:{string.Join(",", ids)}";
    }

    private void AddPrepared(
        ClientProfile client, TallyCompanyInfo company, ICollection<PreparedVoucher> target,
        TallyVoucherExportResult result, PreparedVoucher incoming,
        bool skipTallyDuplicateChecks = false,
        IReadOnlyDictionary<string, string>? ledgerMasterIdByKey = null,
        ICollection<PreparedVoucher>? missingRecovery = null,
        bool explicitMissingRecovery = false,
        IReadOnlySet<string>? explicitRestoreSourceAnchors = null)
    {
        incoming = CanonicalizePreparedVoucher(client, incoming);
        var detailedAmount = incoming.Entries.Where(entry => entry.IsDebit).Sum(entry => Math.Abs(entry.Amount)) +
                             (incoming.InventoryEntries?.Where(entry => entry.IsDeemedPositive).Sum(entry => Math.Abs(entry.AccountingAmount)) ?? 0m);
        var inventoryDetail = string.Join("|", (incoming.InventoryEntries ?? []).Select(entry =>
            $"ITEM:{entry.ItemName}:{entry.Unit}:{entry.ActualQuantity:0.####}:{entry.BilledQuantity:0.####}:{entry.Rate:0.####}:{entry.AccountingAmount:0.00}:{entry.AccountingLedger}:{entry.Godown}:{entry.BatchName}:{entry.IsDeemedPositive}"));
        var detail = incoming.Narration + "|" + string.Join("|", incoming.Entries.Select(entry =>
            $"{entry.LedgerName}:{entry.Amount:0.00}:{entry.IsDebit}:{entry.IsParty}")) + "|" + inventoryDetail;
        var contentHash = TallyVoucherFingerprint.BuildContentHash(
            incoming.Date, incoming.VoucherType, incoming.SourceKey, incoming.PartyLedger, detailedAmount, detail);

        var sourceAnchor = incoming.SourceAnchor.Trim().Length > 0 ? incoming.SourceAnchor.Trim() : incoming.SourceKey.Trim();
        if (explicitRestoreSourceAnchors is { Count: > 0 } && !explicitRestoreSourceAnchors.Contains(sourceAnchor))
            return;
        var desiredVersion = BuildPreparedVoucherVersion(incoming, ledgerMasterIdByKey);
        var explicitRestoreRequested = false;
        var holdCreateUntilMasterIdRecovered = false;
        var missingBoundNeedsResolution = false;
        var missingBoundMasterId = string.Empty;
        var missingBindingState = string.Empty;

        // Fix85 durable source identity. XML is disposable; a live bound MASTERID remains
        // authoritative for ALTER. If complete fresh FY authority no longer contains the
        // MASTERID, resolve an exact replacement or external Tally deletion. Never turn
        // a missing previously-bound Voucher into automatic CREATE permission.
        if (!skipTallyDuplicateChecks)
        {
            var binding = _tallyRepository.GetSourceBinding(
                client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType, sourceAnchor);
            var versionBinding = _tallyRepository.GetSourceBindingVersionState(
                client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType, sourceAnchor);
            if (binding is not null)
            {
                var (boundMasterId, boundGuid, lastHash, state) = binding.Value;
                explicitRestoreRequested = state.Equals("RestoreRequested", StringComparison.OrdinalIgnoreCase);
                if (boundMasterId.Length > 0)
                {
                    var liveBound = _tallyRepository.FindVoucherByMasterId(
                        company.CompanyKey, client.FinancialYear, boundMasterId);
                    if (liveBound is not null)
                    {
                        var liveVersion = BuildLiveVoucherVersion(liveBound, ledgerMasterIdByKey);
                        // Fix75: VOUCHERNUMBER is Tally-owned when the Voucher Type uses
                        // automatic numbering. AAH's source document number remains in
                        // REFERENCE. Once MASTERID is bound, preserve Tally's actual live
                        // Voucher Number in the desired version instead of repeatedly
                        // trying to force the source document number into that field.
                        desiredVersion = BuildPreparedVoucherVersion(incoming, ledgerMasterIdByKey, liveBound.VoucherNumber);
                        var verifiedVersion = versionBinding?.VerifiedVersion ?? string.Empty;
                        // Fix82 recovery for the 12 Journals that Fix81 actually ALTERed in
                        // Tally but could not post-write verify because Tally's scalar Amount
                        // was not the Journal total. The prior desired version is already
                        // durable in PendingVersion. Complete that accepted write only when
                        // (a) it is still AwaitingVerification, (b) today's desired version is
                        // exactly that saved pending version, and (c) the same live MASTERID now
                        // matches the complete desired Journal postings under the corrected
                        // side+Ledger+amount verifier. This is never permission to resend it.
                        if (IsJournalVoucher(incoming) &&
                            versionBinding is not null &&
                            versionBinding.State.Equals("AcceptedAwaitingVerification", StringComparison.OrdinalIgnoreCase) &&
                            versionBinding.PendingVersion.Length > 0 &&
                            versionBinding.PendingVersion.Equals(desiredVersion, StringComparison.Ordinal) &&
                            VoucherStrictlyMatchesPrepared(liveBound, incoming, ledgerMasterIdByKey))
                        {
                            _tallyRepository.ConfirmSourceBindingVersion(
                                client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
                                sourceAnchor, incoming.SourceKey, boundMasterId,
                                boundGuid.Length > 0 ? boundGuid : liveBound.Guid,
                                contentHash, liveVersion, ParseAlterId(liveBound.AlterId),
                                "Fix82 completed prior accepted Journal ALTER verification from the durable PendingVersion after exact live posting verification; no resend occurred.");
                            result.ExactDuplicatesSkipped++;
                            return;
                        }
                        // Legacy migration: an already-bound MASTERID is authoritative. Capture
                        // its current live state once as the previous verified version before the
                        // first Fix74 modification; never infer the previous state from the new AAH row.
                        if (verifiedVersion.Length == 0)
                        {
                            _tallyRepository.ConfirmSourceBindingVersion(
                                client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
                                sourceAnchor, incoming.SourceKey, boundMasterId, boundGuid.Length > 0 ? boundGuid : liveBound.Guid,
                                lastHash, liveVersion, ParseAlterId(liveBound.AlterId),
                                "Fix74 captured the current live MASTERID state as the previous verified version before modification.");
                            verifiedVersion = liveVersion;
                        }

                        // Fix87 Journal no-op authority: the 31-Aug diagnostic proved that
                        // the 12 GST Journals were being ALTERed again even though both the
                        // current AAH desired content hash and Tally's bound MASTERID/ALTERID
                        // were unchanged from AAH's last successful verification. In that
                        // state neither side changed; a representation/posting reconstruction
                        // difference must never cause another ALTER. Rebase the serialized
                        // live version for audit continuity and classify the Voucher as exact.
                        var liveBoundAlterId = ParseAlterId(liveBound.AlterId);
                        var sameVerifiedJournalDesiredHash = IsJournalVoucher(incoming) &&
                            versionBinding is not null &&
                            versionBinding.LastVerifiedAlterId > 0 &&
                            liveBoundAlterId == versionBinding.LastVerifiedAlterId &&
                            versionBinding.VerifiedContentHash.Length > 0 &&
                            versionBinding.VerifiedContentHash.Equals(contentHash, StringComparison.OrdinalIgnoreCase);
                        if (sameVerifiedJournalDesiredHash)
                        {
                            _tallyRepository.ConfirmSourceBindingVersion(
                                client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
                                sourceAnchor, incoming.SourceKey, boundMasterId,
                                boundGuid.Length > 0 ? boundGuid : liveBound.Guid,
                                contentHash, liveVersion, liveBoundAlterId,
                                "Fix87 Journal no-op authority: same MASTERID + same last-verified ALTERID + same verified desired content hash; unchanged Journal classified IGNORE/exact and not resent.");
                            result.ExactDuplicatesSkipped++;
                            return;
                        }

                        if (!verifiedVersion.Equals(liveVersion, StringComparison.Ordinal))
                        {
                            // Fix86 Journal authority recovery: if the same bound MASTERID has
                            // the exact same ALTERID that AAH last verified, Tally has not changed
                            // the Voucher since that verification. Any string-version mismatch is
                            // therefore representation/master-identity reconstruction drift, not an
                            // external Tally edit. Rebase PreviousVersion to today's exact live
                            // representation first; the normal desired-version comparison below then
                            // decides IGNORE versus a genuine new AAH ALTER. This never resends merely
                            // because the stored version string was reconstructed differently.
                            var sameVerifiedJournalAuthority = IsJournalVoucher(incoming) &&
                                versionBinding is not null &&
                                versionBinding.LastVerifiedAlterId > 0 &&
                                ParseAlterId(liveBound.AlterId) == versionBinding.LastVerifiedAlterId;
                            if (sameVerifiedJournalAuthority)
                            {
                                _tallyRepository.ConfirmSourceBindingVersion(
                                    client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
                                    sourceAnchor, incoming.SourceKey, boundMasterId,
                                    boundGuid.Length > 0 ? boundGuid : liveBound.Guid,
                                    lastHash, liveVersion, ParseAlterId(liveBound.AlterId),
                                    "Fix86 reconciled Journal PreviousVersion from unchanged same-MASTERID/same-ALTERID live authority; no Tally resend occurred.");
                                verifiedVersion = liveVersion;
                            }
                            // Fix76 one-time Fix74->Fix75 compatibility migration. Fix74
                            // stored the source document number in NO= while Fix75 correctly
                            // made NO= Tally-owned and kept the source document in REF=. Rebase
                            // only when NO is the SOLE difference; every other previous-version
                            // field (type/date/reference/party/amount/narration/DR/CR MASTERIDs)
                            // must still match exactly. This is not a relaxed duplicate rule.
                            else if (VoucherVersionsDifferOnlyByVoucherNumber(verifiedVersion, liveVersion) ||
                                VoucherVersionsMatchAfterLegacyIdentityRebase(verifiedVersion, liveVersion, ledgerMasterIdByKey) ||
                                VoucherVersionsMatchAfterLegacyJournalScalarAmountRebase(
                                    verifiedVersion, liveVersion, liveBound,
                                    versionBinding?.LastVerifiedAlterId ?? 0, ledgerMasterIdByKey))
                            {
                                _tallyRepository.ConfirmSourceBindingVersion(
                                    client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
                                    sourceAnchor, incoming.SourceKey, boundMasterId,
                                    boundGuid.Length > 0 ? boundGuid : liveBound.Guid,
                                    lastHash, liveVersion, ParseAlterId(liveBound.AlterId),
                                    "Fix78 migrated legacy Voucher Number / Ledger identity representation only; date, type, reference, amount, narration and uniquely resolved Ledger MASTERIDs matched the exact live MASTERID state.");
                                verifiedVersion = liveVersion;
                            }
                            else
                            {
                                BlockConflict(result, incoming,
                                    $"Bound Tally MASTERID {boundMasterId} no longer matches AAH's previous verified version. MODIFY/CREATE is blocked until the external Tally change is reconciled.");
                                return;
                            }
                        }
                        if (VoucherStrictlyMatchesPrepared(liveBound, incoming, ledgerMasterIdByKey))
                        {
                            _tallyRepository.ConfirmSourceBindingVersion(
                                client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
                                sourceAnchor, incoming.SourceKey, boundMasterId, liveBound.Guid,
                                contentHash, liveVersion, ParseAlterId(liveBound.AlterId),
                                "Bound MASTERID already equals the complete desired AAH voucher version; Tally-owned Voucher Number preserved.");
                            result.ExactDuplicatesSkipped++;
                            return;
                        }
                        _tallyRepository.StageSourceBindingChange(
                            client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
                            sourceAnchor, incoming.SourceKey, boundMasterId, liveBound.Guid,
                            contentHash, desiredVersion, "ALTER", "ModifyPending",
                            $"MASTERID {boundMasterId} matched the saved previous version; desired new version staged for ALTER.");
                        target.Add(incoming with
                        {
                            ContentHash = contentHash,
                            ExistingTallyMasterId = boundMasterId,
                            SourceAnchor = sourceAnchor
                        });
                        return;
                    }

                    // Fix85: preserve the missing MASTERID until this source finishes
                    // strict replacement/deletion resolution. DeletedInTally means the user
                    // already removed it from Tally and it must remain non-CREATE. A
                    // RebindRequired row may recover a new exact live MASTERID after a
                    // company restore/rebuild, but still may not CREATE automatically.
                    missingBoundMasterId = boundMasterId;
                    missingBindingState = state;
                    if (state.Equals("DeletedInTally", StringComparison.OrdinalIgnoreCase))
                    {
                        // Keep searching current authority for a later exact manual
                        // replacement/re-created Voucher. If none exists, the final
                        // missing-resolution gate below keeps it DeletedInTally.
                        missingBoundNeedsResolution = true;
                    }
                    if (state.Equals("ExternalDeleteConflict", StringComparison.OrdinalIgnoreCase))
                    {
                        BlockConflict(result, incoming,
                            $"Bound Tally MASTERID {boundMasterId} is missing while AAH also has an unverified/pending change. External deletion conflicts with the local change; CREATE/ALTER are blocked.");
                        return;
                    }
                    missingBoundNeedsResolution = true;
                }

                if (boundMasterId.Length == 0)
                {
                    if (state.Equals("AcceptedPending", StringComparison.OrdinalIgnoreCase) ||
                        state.Equals("AcceptedAwaitingMasterId", StringComparison.OrdinalIgnoreCase) ||
                        state.Equals("AcceptedAwaitingVerification", StringComparison.OrdinalIgnoreCase) ||
                        state.Equals("NeedsReview", StringComparison.OrdinalIgnoreCase) ||
                        state.Equals("ModifyPending", StringComparison.OrdinalIgnoreCase) ||
                        state.Equals("StaleMissingLive", StringComparison.OrdinalIgnoreCase) ||
                        state.Equals("RebindRequired", StringComparison.OrdinalIgnoreCase))
                    {
                        // Accepted/uncertain/rebind states are NEVER CREATE candidates. The
                        // strict live classifiers below may recover one exact live MASTERID;
                        // otherwise the source remains fail-closed.
                        holdCreateUntilMasterIdRecovered = true;
                        missingBindingState = state;
                    }
                    else if (state.Equals("DeletedInTally", StringComparison.OrdinalIgnoreCase))
                    {
                        result.DeletedInTallySkipped++;
                        return;
                    }
                }
            }
        }

        // Live XML is a local full-state preview, not a send queue. It deliberately
        // includes vouchers that may already exist in Tally, but it still protects
        // against duplicate rows inside the current local source set.
        if (skipTallyDuplicateChecks)
        {
            var localDuplicate = target.FirstOrDefault(item =>
                item.SourceType.Equals(incoming.SourceType, StringComparison.OrdinalIgnoreCase) &&
                item.SourceKey.Equals(incoming.SourceKey, StringComparison.OrdinalIgnoreCase));
            if (localDuplicate is not null)
            {
                if (localDuplicate.ContentHash.Equals(contentHash, StringComparison.OrdinalIgnoreCase))
                    result.RepeatedSourceCandidatesSkipped++;
                else
                    BlockConflict(result, incoming,
                        "The same logical source key was generated more than once in the current local selection with different content.");
                return;
            }

            target.Add(incoming with { ContentHash = contentHash });
            return;
        }

        var tallyReference = incoming.ReferenceNumber.Length > 0 ? incoming.ReferenceNumber : incoming.SourceKey;
        var tallyAmount = incoming.Entries.FirstOrDefault(entry => entry.IsParty)?.Amount ??
                          incoming.Entries.Where(entry => entry.IsDebit).Sum(entry => entry.Amount);

        // Fix62 voucher verification: AAH-owned deterministic references such as GST
        // monthly Journal keys are stronger identity than mutable Ledger/amount content.
        // Resolve them across the complete fresh FY authority before generic duplicate
        // classification. One unique MASTERID means this is the same voucher: SKIP when
        // current content still matches, otherwise ALTER that exact MASTERID. Never CREATE
        // another voucher merely because the accounting content changed.
        if (tallyReference.StartsWith("AAH|", StringComparison.OrdinalIgnoreCase))
        {
            var ownedReferenceCandidates = _tallyRepository.FindVoucherCandidatesByReference(
                company.CompanyKey, client.FinancialYear, incoming.VoucherType, tallyReference);
            if (ownedReferenceCandidates.Count == 1 && ownedReferenceCandidates[0].MasterId.Trim().Length > 0)
            {
                var existing = ownedReferenceCandidates[0];
                var existingDesiredVersion = BuildPreparedVoucherVersion(incoming, ledgerMasterIdByKey, existing.VoucherNumber);
                var exactOwnedReference = VoucherStrictlyMatchesPrepared(existing, incoming, ledgerMasterIdByKey);
                if (exactOwnedReference)
                {
                    _tallyRepository.ConfirmSourceBindingVersion(
                        client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
                        sourceAnchor, incoming.SourceKey, existing.MasterId, existing.Guid,
                        contentHash, BuildLiveVoucherVersion(existing, ledgerMasterIdByKey), ParseAlterId(existing.AlterId),
                        explicitRestoreRequested
                            ? "R37 Restore-to-Tally found the exact live AAH-owned Voucher and rebound it without resending."
                            : "Fresh FY voucher verification bound exact AAH-owned reference and complete version. Tally Voucher Number preserved.");
                    result.ExactDuplicatesSkipped++;
                    return;
                }
                if (explicitRestoreRequested)
                {
                    // R37 fail-closed: a restore request is never permission to ALTER an
                    // existing same-reference Voucher. Do not mutate the durable binding
                    // state before reporting the conflict.
                    BlockConflict(result, incoming,
                        "Restore-to-Tally found a live Voucher with the same deterministic AAH reference but different content. The live Voucher was not altered; review the difference first.");
                    return;
                }
                _tallyRepository.ConfirmSourceBindingVersion(
                    client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
                    sourceAnchor, incoming.SourceKey, existing.MasterId, existing.Guid,
                    existing.ContentHash, BuildLiveVoucherVersion(existing, ledgerMasterIdByKey), ParseAlterId(existing.AlterId),
                    "Captured previous live AAH-owned reference version before ALTER.");
                _tallyRepository.StageSourceBindingChange(
                    client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
                    sourceAnchor, incoming.SourceKey, existing.MasterId, existing.Guid,
                    contentHash, existingDesiredVersion, "ALTER", "ModifyPending",
                    "AAH-owned reference MASTERID matched; desired complete version staged for ALTER with Tally Voucher Number preserved.");
                target.Add(incoming with
                {
                    ContentHash = contentHash,
                    ExistingTallyMasterId = existing.MasterId.Trim(),
                    SourceAnchor = sourceAnchor
                });
                return;
            }
            if (ownedReferenceCandidates.Count > 1)
            {
                BlockConflict(result, incoming,
                    "The deterministic AAH reference resolves to multiple live Tally vouchers. Automatic CREATE/ALTER is blocked until the duplicate MASTERIDs are resolved.");
                return;
            }
        }
        // Fix27 Advanced Closure historical Cash repair: ACLOSE references are
        // deterministic AAH-owned identities. If an earlier Closure Cash row was
        // already exported and Fix27 corrected its amount/date, locate that same
        // reference across the live Tally index regardless of its old date and ALTER
        // the existing MASTERID. This prevents a corrected local closure row from
        // creating a second Tally voucher merely because its date moved.
        if (incoming.SourceType.Equals("Cash Voucher", StringComparison.OrdinalIgnoreCase) &&
            incoming.ReferenceNumber.StartsWith("ACLOSE-", StringComparison.OrdinalIgnoreCase))
        {
            var existingClosure = _tallyRepository.FindVoucherByReference(company.CompanyKey, incoming.ReferenceNumber);
            if (existingClosure is not null &&
                existingClosure.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase) &&
                existingClosure.MasterId.Trim().Length > 0)
            {
                var closureDesiredVersion = BuildPreparedVoucherVersion(incoming, ledgerMasterIdByKey, existingClosure.VoucherNumber);
                var closureAlreadyExact = VoucherStrictlyMatchesPrepared(existingClosure, incoming, ledgerMasterIdByKey);
                if (closureAlreadyExact)
                {
                    _tallyRepository.ConfirmSourceBindingVersion(
                        client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
                        sourceAnchor, incoming.SourceKey, existingClosure.MasterId, existingClosure.Guid,
                        contentHash, BuildLiveVoucherVersion(existingClosure, ledgerMasterIdByKey), ParseAlterId(existingClosure.AlterId),
                        "Fresh FY closure reference bound exact live Tally Voucher version with Tally Voucher Number preserved.");
                    result.ExactDuplicatesSkipped++;
                    return;
                }

                _tallyRepository.ConfirmSourceBindingVersion(
                    client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
                    sourceAnchor, incoming.SourceKey, existingClosure.MasterId, existingClosure.Guid,
                    existingClosure.ContentHash, BuildLiveVoucherVersion(existingClosure, ledgerMasterIdByKey), ParseAlterId(existingClosure.AlterId),
                    "Captured previous live Closure voucher version before correction.");
                _tallyRepository.StageSourceBindingChange(
                    client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
                    sourceAnchor, incoming.SourceKey, existingClosure.MasterId, existingClosure.Guid,
                    contentHash, closureDesiredVersion, "ALTER", "ModifyPending",
                    "Closure reference MASTERID matched; desired correction staged for ALTER with Tally Voucher Number preserved.");
                target.Add(incoming with
                {
                    ContentHash = contentHash,
                    ExistingTallyMasterId = existingClosure.MasterId.Trim(),
                    SourceAnchor = sourceAnchor
                });
                return;
            }
        }

        var importedSourceKeys = _tallyRepository.GetImportedSourceKeys(
            client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType);
        var legacyOwnedSource = importedSourceKeys.Any(key => LegacySourceIdentityEquivalent(key, incoming.SourceKey));

        var liveCandidates = _tallyRepository.FindVoucherCandidates(
            company.CompanyKey, client.FinancialYear, incoming.Date, incoming.VoucherType, tallyReference);
        if (liveCandidates.Count == 0 && legacyOwnedSource)
        {
            var crossDateCandidates = _tallyRepository.FindVoucherCandidatesByReference(
                company.CompanyKey, client.FinancialYear, incoming.VoucherType, tallyReference);
            if (crossDateCandidates.Count == 1 && crossDateCandidates[0].MasterId.Trim().Length > 0)
            {
                var prior = crossDateCandidates[0];
                if (explicitRestoreRequested)
                {
                    if (VoucherStrictlyMatchesPrepared(prior, incoming, ledgerMasterIdByKey))
                    {
                        _tallyRepository.ConfirmSourceBindingVersion(
                            client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType, sourceAnchor, incoming.SourceKey,
                            prior.MasterId, prior.Guid, contentHash, BuildLiveVoucherVersion(prior, ledgerMasterIdByKey), ParseAlterId(prior.AlterId),
                            "R37 Restore-to-Tally found the exact live replacement Voucher and rebound it without resending.");
                        result.ExactDuplicatesSkipped++;
                    }
                    else
                    {
                        BlockConflict(result, incoming,
                            "Restore-to-Tally found a live Voucher with the same prior source reference on another date/content. It was not altered and no duplicate was created.");
                    }
                    return;
                }
                _tallyRepository.ConfirmSourceBindingVersion(
                    client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType, sourceAnchor, incoming.SourceKey,
                    prior.MasterId, prior.Guid, prior.ContentHash, BuildLiveVoucherVersion(prior, ledgerMasterIdByKey), ParseAlterId(prior.AlterId),
                    "Legacy AAH source identity resolved across FY by unique Tally reference; captured previous live version.");
                _tallyRepository.StageSourceBindingChange(
                    client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType, sourceAnchor, incoming.SourceKey,
                    prior.MasterId, prior.Guid, contentHash, BuildPreparedVoucherVersion(incoming, ledgerMasterIdByKey, prior.VoucherNumber), "ALTER", "ModifyPending",
                    "Unique live MASTERID matched legacy source; desired new version staged for ALTER with Tally Voucher Number preserved.");
                target.Add(incoming with
                {
                    ContentHash = contentHash, ExistingTallyMasterId = prior.MasterId.Trim(), SourceAnchor = sourceAnchor
                });
                return;
            }
            if (crossDateCandidates.Count > 0)
            {
                BlockConflict(result, incoming,
                    "Previously imported AAH source matches multiple/no-MASTERID live vouchers across the FY. CREATE is blocked until the original Tally MASTERID is unambiguous.");
                return;
            }
        }
        if (liveCandidates.Count > 0)
        {
            if (explicitRestoreRequested)
            {
                var restoreExactRows = liveCandidates
                    .Where(existing => VoucherStrictlyMatchesPrepared(existing, incoming, ledgerMasterIdByKey))
                    .ToArray();
                if (restoreExactRows.Length == 1 && restoreExactRows[0].MasterId.Trim().Length > 0)
                {
                    var exact = restoreExactRows[0];
                    _tallyRepository.ConfirmSourceBindingVersion(
                        client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
                        sourceAnchor, incoming.SourceKey, exact.MasterId, exact.Guid,
                        contentHash, BuildLiveVoucherVersion(exact, ledgerMasterIdByKey), ParseAlterId(exact.AlterId),
                        "R37 Restore-to-Tally found the exact live Voucher and rebound it without CREATE/ALTER.");
                    result.ExactDuplicatesSkipped++;
                    return;
                }
                BlockConflict(result, incoming,
                    restoreExactRows.Length > 1
                        ? "Restore-to-Tally found multiple exact live Voucher candidates. No Voucher was created or altered."
                        : "Restore-to-Tally found a live same-reference/date Voucher with different content. The live Voucher was protected and no duplicate was created.");
                return;
            }

            // Fix 7 migration/synchronization: vouchers created by this Hub under the
            // prior Hub RCM posting variants must be repairable in place. Only unambiguously
            // Hub-owned RCM Purchases and automatic GST Journals are eligible, and the
            // fresh live Master ID is used as Tally's official alteration identifier.
            // Once this exact corrected content has been live-verified and recorded as
            // Imported, do not ALTER it again on every later export.
            var correctedContentAlreadyApplied = _tallyRepository.HasImportedContentFingerprint(
                client.Id, client.FinancialYear, company.CompanyKey,
                incoming.SourceType, incoming.SourceKey, contentHash);
            var alterCandidate = correctedContentAlreadyApplied
                ? null
                : ResolveHubOwnedRcmAlterCandidate(incoming, liveCandidates);

            if (alterCandidate is null &&
                !correctedContentAlreadyApplied &&
                _tallyRepository.HasImportedSourceFingerprint(
                    client.Id,
                    client.FinancialYear,
                    company.CompanyKey,
                    incoming.SourceType,
                    incoming.SourceKey))
            {
                alterCandidate = ResolveHubOwnedDocumentRegroupAlterCandidate(
                    incoming,
                    liveCandidates);
            }

            if (alterCandidate is not null)
            {
                _tallyRepository.ConfirmSourceBindingVersion(
                    client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
                    sourceAnchor, incoming.SourceKey, alterCandidate.MasterId, alterCandidate.Guid,
                    alterCandidate.ContentHash, BuildLiveVoucherVersion(alterCandidate, ledgerMasterIdByKey), ParseAlterId(alterCandidate.AlterId),
                    "Captured previous live AAH-owned migration/regroup voucher version before ALTER.");
                _tallyRepository.StageSourceBindingChange(
                    client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
                    sourceAnchor, incoming.SourceKey, alterCandidate.MasterId, alterCandidate.Guid,
                    contentHash, BuildPreparedVoucherVersion(incoming, ledgerMasterIdByKey, alterCandidate.VoucherNumber), "ALTER", "ModifyPending",
                    "AAH-owned migration/regroup MASTERID matched; desired new version staged for ALTER with Tally Voucher Number preserved.");
                target.Add(incoming with
                {
                    ContentHash = contentHash,
                    ExistingTallyMasterId = alterCandidate.MasterId.Trim(),
                    SourceAnchor = sourceAnchor
                });
                return;
            }

            // Fix74 strict non-Bank duplicate identity. Purchase/Sales/Cash/Journal
            // must agree on the complete source accounting identity: Voucher Type, date,
            // reference/document number, party, amount, narration and BOTH ledger sides.
            // Tally VOUCHERNUMBER can be auto-assigned; it becomes strict previous-version
            // authority immediately after MASTERID binding rather than a pre-binding key. A Ledger
            // difference is therefore a MODIFY signal for a proven AAH-owned MASTERID,
            // never an "exact duplicate" and never permission to CREATE another voucher.
            var exactRows = liveCandidates.Where(existing => VoucherStrictlyMatchesPrepared(existing, incoming, ledgerMasterIdByKey)).ToArray();
            if (exactRows.Length > 0)
            {
                if (exactRows.Length == 1 && exactRows[0].MasterId.Trim().Length > 0)
                {
                    _tallyRepository.ConfirmSourceBindingVersion(
                        client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
                        sourceAnchor, incoming.SourceKey, exactRows[0].MasterId, exactRows[0].Guid,
                        contentHash, BuildLiveVoucherVersion(exactRows[0], ledgerMasterIdByKey), ParseAlterId(exactRows[0].AlterId),
                        "Fresh strict duplicate check bound exact live Tally voucher and complete verified version with Tally Voucher Number preserved.");
                }
                result.ExactDuplicatesSkipped++;
                return;
            }

            // FIX4 universal safe MODIFY decision. A differing live voucher may be
            // altered only when there is exactly one live candidate, Tally supplied a
            // MASTERID, and this exact logical source was previously verified as an
            // AAH import. Manual/unowned Tally vouchers remain REVIEW/conflict and are
            // never overwritten merely because date/type/reference happen to match.
            var hubOwnsSource = _tallyRepository.HasImportedSourceFingerprint(
                client.Id, client.FinancialYear, company.CompanyKey,
                incoming.SourceType, incoming.SourceKey);
            var safeAlter = hubOwnsSource && liveCandidates.Count == 1 &&
                            liveCandidates[0].MasterId.Trim().Length > 0
                ? liveCandidates[0]
                : null;
            if (safeAlter is not null)
            {
                _tallyRepository.ConfirmSourceBindingVersion(
                    client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
                    sourceAnchor, incoming.SourceKey, safeAlter.MasterId, safeAlter.Guid,
                    safeAlter.ContentHash, BuildLiveVoucherVersion(safeAlter, ledgerMasterIdByKey), ParseAlterId(safeAlter.AlterId),
                    "Fresh duplicate check captured the previous live version of the AAH-owned source.");
                _tallyRepository.StageSourceBindingChange(
                    client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
                    sourceAnchor, incoming.SourceKey, safeAlter.MasterId, safeAlter.Guid,
                    contentHash, BuildPreparedVoucherVersion(incoming, ledgerMasterIdByKey, safeAlter.VoucherNumber), "ALTER", "ModifyPending",
                    "Previous live version matched unique AAH-owned MASTERID; desired new version staged for ALTER with Tally Voucher Number preserved.");
                target.Add(incoming with
                {
                    ContentHash = contentHash,
                    ExistingTallyMasterId = safeAlter.MasterId.Trim(),
                    SourceAnchor = sourceAnchor
                });
                return;
            }

            BlockConflict(result, incoming,
                hubOwnsSource
                    ? "The AAH-owned source matches multiple/no-MASTERID live vouchers; MODIFY is unsafe and requires review."
                    : "A live Tally voucher exists for this date/type/reference but differs and is not proven AAH-owned; manual Tally data was not altered.");
            return;
        }

        // CL017 restore correction: explicit user-approved Reimport recreates CURRENT AAH
        // treatment, even when ledger, group, amount or date changed after Tally deletion.
        // The original verified version is historical evidence, NOT the desired CREATE
        // payload. A fresh full-FY check, live MASTERID/reference uniqueness checks and
        // all Master-first CREATE dependencies above still apply unchanged.
        if (explicitRestoreRequested)
        {
            var restoreState = _tallyRepository.GetSourceBindingVersionState(
                client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType, sourceAnchor);
            var previousVerified = restoreState?.VerifiedVersion ?? string.Empty;
            // The old bound MASTERID/version were archived by the R37 explicit restore
            // permission gate. Require that durable prior-version evidence, but do not
            // demand equality with today's CURRENT AAH voucher after user edits.
            if (previousVerified.Length == 0 ||
                !string.Equals(restoreState?.State, "RestoreRequested", StringComparison.OrdinalIgnoreCase) ||
                restoreState?.PendingVersion.Length > 0)
            {
                BlockConflict(result, incoming,
                    "Restore-to-Tally lacks a previous verified version or has unverified pending work; CREATE remains blocked.");
                return;
            }
            if (target.Any(item => item.SourceType.Equals(incoming.SourceType, StringComparison.OrdinalIgnoreCase) &&
                                   item.SourceAnchor.Equals(sourceAnchor, StringComparison.OrdinalIgnoreCase)))
            {
                BlockConflict(result, incoming, "Restore-to-Tally source occurs more than once in the selected recovery batch.");
                return;
            }
            _tallyRepository.StageSourceBindingChange(
                client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
                sourceAnchor, incoming.SourceKey, string.Empty, string.Empty,
                contentHash, desiredVersion, "CREATE", "CreatePending",
                "CL017 explicit Restore-to-Tally: no live replacement after fresh authority; current AAH contents pass ordinary Master-first CREATE rules. Old version and MASTERID remain in history.");
            target.Add(incoming with { ContentHash = contentHash, SourceAnchor = sourceAnchor });
            return;
        }

        // Fix85 source-level missing-MASTERID resolution happens only after every strict
        // live reference/content classifier above had a chance to recover an exact
        // replacement Voucher. If none exists, a stable isolated loss is adopted as an
        // external Tally deletion. Widespread identity loss stays RebindRequired. Neither
        // path is allowed to fall through into CREATE.
        if (missingBoundNeedsResolution)
        {
            if (missingBindingState.Equals("MissingLiveCandidate", StringComparison.OrdinalIgnoreCase) ||
                missingBindingState.Equals("Verified", StringComparison.OrdinalIgnoreCase) ||
                missingBindingState.Equals("DeletedInTally", StringComparison.OrdinalIgnoreCase))
            {
                var deletionVersionState = _tallyRepository.GetSourceBindingVersionState(
                    client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType, sourceAnchor);
                var previousVerified = deletionVersionState?.VerifiedVersion ?? string.Empty;
                var localStillEqualsPrevious = previousVerified.Length > 0 &&
                    (previousVerified.Equals(desiredVersion, StringComparison.Ordinal) ||
                     VoucherVersionsDifferOnlyByVoucherNumber(previousVerified, desiredVersion) ||
                     VoucherVersionsMatchAfterLegacyIdentityRebase(previousVerified, desiredVersion, ledgerMasterIdByKey));
                if (!localStillEqualsPrevious)
                {
                    BlockConflict(result, incoming,
                        $"Bound Tally MASTERID {missingBoundMasterId} is missing, but current AAH data no longer equals the saved previous verified version. Tally deletion and local modification conflict; neither CREATE nor ALTER is automatic.");
                    return;
                }
                if (missingBindingState.Equals("DeletedInTally", StringComparison.OrdinalIgnoreCase))
                {
                    result.DeletedInTallySkipped++;
                    return;
                }

                var deleted = _tallyRepository.MarkSourceBindingDeletedInTally(
                    client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
                    sourceAnchor, missingBoundMasterId,
                    $"Fix85 adopted deletion in Tally: bound Voucher MASTERID {missingBoundMasterId} is absent from complete fresh FY authority and no exact replacement Voucher was found. AAH source data is retained; automatic CREATE is forbidden.");
                if (deleted)
                {
                    result.DeletedInTallySkipped++;
                    return;
                }
                BlockConflict(result, incoming,
                    $"Bound Tally MASTERID {missingBoundMasterId} disappeared, but the deletion could not be adopted because AAH has a concurrent/pending change. CREATE/ALTER are blocked.");
                return;
            }

            result.GenerationRebindBlocked++;
            BlockConflict(result, incoming,
                $"Bound Tally MASTERID {missingBoundMasterId} is absent during company-generation/rebind recovery. Only an exact live replacement MASTERID may be rebound; automatic CREATE is forbidden.");
            return;
        }

        if (holdCreateUntilMasterIdRecovered)
        {
            var recoveryState = _tallyRepository.GetSourceBindingVersionState(
                client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType, sourceAnchor);
            if (CanOfferMissingRecovery(recoveryState))
            {
                if (explicitMissingRecovery && incoming.ContentHash.Equals(contentHash, StringComparison.Ordinal))
                {
                    if (target.Any(item => item.SourceType == incoming.SourceType && item.SourceKey == incoming.SourceKey))
                    {
                        BlockConflict(result, incoming, "Recovery source already occurs in the prepared batch.");
                        return;
                    }
                    _tallyRepository.StageSourceBindingChange(client.Id, client.FinancialYear, company.CompanyKey,
                        incoming.SourceType, sourceAnchor, incoming.SourceKey, string.Empty, string.Empty,
                        contentHash, desiredVersion, "CREATE", "CreatePending",
                        "User confirmed missing unbound voucher recovery; fresh full-FY authority found no existing source reference. No bound identity was reset.");
                    target.Add(incoming with { ContentHash = contentHash, SourceAnchor = sourceAnchor });
                    return;
                }
                if (!explicitMissingRecovery && missingRecovery is not null)
                {
                    missingRecovery.Add(incoming with { ContentHash = contentHash, SourceAnchor = sourceAnchor });
                    return;
                }
            }

            if (missingBindingState.Equals("RebindRequired", StringComparison.OrdinalIgnoreCase) ||
                missingBindingState.Equals("StaleMissingLive", StringComparison.OrdinalIgnoreCase))
                result.GenerationRebindBlocked++;
            BlockConflict(result, incoming,
                "Tally previously accepted or may have accepted this source, but a unique live MASTERID has not yet been recovered. Source remains HOLD/RebindRequired and CREATE is forbidden.");
            return;
        }

        if (legacyOwnedSource || _tallyRepository.HasImportedSourceFingerprint(
                client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType, incoming.SourceKey))
        {
            // Fix85 removes the old Fix68 missing-MASTERID CREATE exception. Any known
            // AAH-owned source without a current exact MASTERID remains fail-closed until
            // it is rebound, explicitly restored, or adopted as DeletedInTally.
            BlockConflict(result, incoming,
                "This AAH source was previously imported but has no uniquely recovered live Tally MASTERID. Automatic CREATE is forbidden; exact rebind or explicit Restore-to-Tally is required.");
            return;
        }

        var decision = _tallyRepository.EvaluateImportFingerprint(
            client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType, incoming.SourceKey, contentHash);
        if (decision.Kind == TallyDuplicateKind.ExactDuplicate) { result.ExactDuplicatesSkipped++; return; }
        if (decision.Kind == TallyDuplicateKind.Conflict)
        {
            BlockConflict(result, incoming, decision.Reason);
            return;
        }
        var batch = target.FirstOrDefault(item => item.SourceType.Equals(incoming.SourceType, StringComparison.OrdinalIgnoreCase) &&
                                                 item.SourceKey.Equals(incoming.SourceKey, StringComparison.OrdinalIgnoreCase));
        if (batch is not null)
        {
            if (batch.ContentHash.Equals(contentHash, StringComparison.OrdinalIgnoreCase)) result.RepeatedSourceCandidatesSkipped++;
            else BlockConflict(result, incoming,
                "The same logical source key appeared twice in the prepared export batch with different content.");
            return;
        }
        _tallyRepository.StageSourceBindingChange(
            client.Id, client.FinancialYear, company.CompanyKey, incoming.SourceType,
            sourceAnchor, incoming.SourceKey, string.Empty, string.Empty,
            contentHash, desiredVersion, "CREATE", "CreatePending",
            "Strict fresh authority found no existing Tally identity; CREATE staged after Master comparison completed.");
        target.Add(incoming with { ContentHash = contentHash, SourceAnchor = sourceAnchor });
    }

    private static bool CanOfferMissingRecovery(TallySourceBindingVersionState? state) =>
        state is not null && state.MasterId.Length == 0 && state.Guid.Length == 0 &&
        state.VerifiedVersion.Length == 0 && state.LastVerifiedAlterId == 0 &&
        (state.State.Equals("AcceptedPending", StringComparison.OrdinalIgnoreCase) ||
         state.State.Equals("AcceptedAwaitingMasterId", StringComparison.OrdinalIgnoreCase) ||
         state.State.Equals("AcceptedAwaitingVerification", StringComparison.OrdinalIgnoreCase) ||
         state.State.Equals("NeedsReview", StringComparison.OrdinalIgnoreCase));

    private static string RecoveryStateSnapshot(TallySourceBindingVersionState? state) =>
        state is null ? string.Empty : System.Text.Json.JsonSerializer.Serialize(state);

    private static void BlockConflict(
        TallyVoucherExportResult result,
        PreparedVoucher incoming,
        string reason)
    {
        result.ConflictsBlocked++;
        result.ConflictDetails.Add(
            $"{incoming.SourceType} | {incoming.SourceKey} | " +
            $"Ref={incoming.ReferenceNumber} | Party={incoming.PartyLedger} | {reason}");
    }

    private static TallyVoucherIndexRecord? ResolveHubOwnedRcmAlterCandidate(
        PreparedVoucher incoming,
        IReadOnlyCollection<TallyVoucherIndexRecord> liveCandidates)
    {
        if (liveCandidates.Count != 1) return null;
        var candidate = liveCandidates.Single();
        if (candidate.MasterId.Trim().Length == 0) return null;

        if (incoming.IsReverseCharge &&
            incoming.SourceType.Equals("Purchase Voucher", StringComparison.OrdinalIgnoreCase))
        {
            return candidate.Narration.Trim().Equals(
                    incoming.Narration.Trim(),
                    StringComparison.OrdinalIgnoreCase)
                ? candidate
                : null;
        }

        if (incoming.HasRcmAdjustment &&
            incoming.SourceType.Equals("GST Journal", StringComparison.OrdinalIgnoreCase) &&
            incoming.SourceKey.StartsWith("AAH|GSTJ|", StringComparison.OrdinalIgnoreCase))
        {
            return candidate.Reference.Trim().Equals(
                    incoming.ReferenceNumber.Trim(),
                    StringComparison.OrdinalIgnoreCase)
                ? candidate
                : null;
        }

        return null;
    }

    private static TallyVoucherIndexRecord? ResolveHubOwnedDocumentRegroupAlterCandidate(
        PreparedVoucher incoming,
        IReadOnlyCollection<TallyVoucherIndexRecord> liveCandidates)
    {
        if (liveCandidates.Count != 1) return null;
        if (!incoming.SourceType.Equals("Purchase Voucher", StringComparison.OrdinalIgnoreCase) &&
            !incoming.SourceType.Equals("Sales Voucher", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var expectedPrefix = incoming.SourceType.Equals(
            "Purchase Voucher",
            StringComparison.OrdinalIgnoreCase)
            ? "AAH|PURCHASE|"
            : "AAH|SALES|";
        if (!incoming.SourceKey.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var candidate = liveCandidates.Single();
        if (candidate.MasterId.Trim().Length == 0) return null;

        var reference = incoming.ReferenceNumber.Trim();
        return reference.Length > 0 &&
               candidate.Reference.Trim().Equals(reference, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }

    // R21: item-invoice accounting allocations are real ledger postings too.
    // The writer places these under inventory entries, not in voucher.Entries.
    private static IEnumerable<VoucherLedgerEntry> PreparedAccountingEntries(PreparedVoucher voucher) =>
        voucher.Entries.Concat((voucher.InventoryEntries ?? Array.Empty<PreparedInventoryEntry>())
            .Select(item => new VoucherLedgerEntry(item.AccountingLedger,
                Math.Abs(item.AccountingAmount), item.IsDeemedPositive, false)));

    private static bool VoucherStrictlyMatchesPrepared(
        TallyVoucherIndexRecord existing,
        PreparedVoucher incoming,
        IReadOnlyDictionary<string, string>? ledgerMasterIdByKey = null)
    {
        if (existing.VoucherDate?.Date != incoming.Date.Date) return false;
        if (!NormalizeComparisonText(existing.VoucherType).Equals(NormalizeComparisonText(incoming.VoucherType), StringComparison.OrdinalIgnoreCase)) return false;
        if (!NormalizeComparisonText(existing.Reference).Equals(NormalizeComparisonText(incoming.ReferenceNumber), StringComparison.OrdinalIgnoreCase)) return false;
        if (!NormalizeLedgerIdentity(existing.PartyLedger, ledgerMasterIdByKey).Equals(
                NormalizeLedgerIdentity(incoming.PartyLedger, ledgerMasterIdByKey), StringComparison.Ordinal)) return false;
        // Fix75: do not compare AAH's source document number to Tally's VOUCHERNUMBER.
        // Tally can auto-number the voucher even when AAH supplied a source number. The
        // source document number is verified through REFERENCE; after MASTERID binding
        // the exact live Voucher Number is retained in the previous-version authority.
        var incomingAmount = PreparedVoucherComparableAmount(incoming);
        if (IsJournalVoucher(incoming) && existing.LedgerPostings.Count > 0)
        {
            // Fix82: Tally's scalar Voucher Amount is not a Journal total. It can expose
            // one posting amount (for example 70,202.25) while the balanced Journal debit
            // and credit totals are much larger. Verify the exact live ledger postings
            // instead: same side, same MASTERID/name identity, and same amount per ledger.
            if (Math.Abs(LiveVoucherComparableAmount(existing) - incomingAmount) > 0.01m) return false;
            var expectedDebitPostings = NormalizePreparedPostingSide(
                incoming.Entries.Where(entry => entry.IsDebit), ledgerMasterIdByKey);
            var expectedCreditPostings = NormalizePreparedPostingSide(
                incoming.Entries.Where(entry => !entry.IsDebit), ledgerMasterIdByKey);
            var liveDebitPostings = NormalizeLivePostingSide(
                existing.LedgerPostings.Where(posting => posting.IsDebit), ledgerMasterIdByKey);
            var liveCreditPostings = NormalizeLivePostingSide(
                existing.LedgerPostings.Where(posting => !posting.IsDebit), ledgerMasterIdByKey);
            if (!expectedDebitPostings.Equals(liveDebitPostings, StringComparison.Ordinal) ||
                !expectedCreditPostings.Equals(liveCreditPostings, StringComparison.Ordinal)) return false;
        }
        else if (Math.Abs(Math.Abs(existing.Amount) - incomingAmount) > 0.01m)
        {
            return false;
        }

        if (!NormalizeComparisonText(existing.Narration).Equals(NormalizeComparisonText(incoming.Narration), StringComparison.OrdinalIgnoreCase)) return false;

        var expectedDebit = NormalizeLedgerIdentitySide(PreparedAccountingEntries(incoming).Where(entry => entry.IsDebit).Select(entry => entry.LedgerName), ledgerMasterIdByKey);
        var expectedCredit = NormalizeLedgerIdentitySide(PreparedAccountingEntries(incoming).Where(entry => !entry.IsDebit).Select(entry => entry.LedgerName), ledgerMasterIdByKey);
        var liveDebit = NormalizeLedgerIdentitySide(SplitLedgerSide(existing.DebitLedgers), ledgerMasterIdByKey);
        var liveCredit = NormalizeLedgerIdentitySide(SplitLedgerSide(existing.CreditLedgers), ledgerMasterIdByKey);
        return expectedDebit.Equals(liveDebit, StringComparison.Ordinal) &&
               expectedCredit.Equals(liveCredit, StringComparison.Ordinal);
    }

    private static string BuildPreparedVoucherVersion(
        PreparedVoucher incoming,
        IReadOnlyDictionary<string, string>? ledgerMasterIdByKey = null,
        string? tallyVoucherNumberOverride = null)
    {
        var amount = PreparedVoucherComparableAmount(incoming);
        return string.Join(";", new[]
        {
            "DATE=" + incoming.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            "TYPE=" + NormalizeVersionText(incoming.VoucherType),
            "NO=" + NormalizeVersionText(tallyVoucherNumberOverride ?? incoming.VoucherNumber),
            "REF=" + NormalizeVersionText(incoming.ReferenceNumber),
            "PARTY=" + NormalizeLedgerIdentity(incoming.PartyLedger, ledgerMasterIdByKey),
            "AMT=" + amount.ToString("0.00", CultureInfo.InvariantCulture),
            "NARR=" + NormalizeVersionText(incoming.Narration),
            "DR=" + NormalizeLedgerIdentitySide(PreparedAccountingEntries(incoming).Where(entry => entry.IsDebit).Select(entry => entry.LedgerName), ledgerMasterIdByKey),
            "CR=" + NormalizeLedgerIdentitySide(PreparedAccountingEntries(incoming).Where(entry => !entry.IsDebit).Select(entry => entry.LedgerName), ledgerMasterIdByKey)
        });
    }

    private static string BuildLiveVoucherVersion(
        TallyVoucherIndexRecord existing,
        IReadOnlyDictionary<string, string>? ledgerMasterIdByKey = null)
    {
        return string.Join(";", new[]
        {
            "DATE=" + (existing.VoucherDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty),
            "TYPE=" + NormalizeVersionText(existing.VoucherType),
            "NO=" + NormalizeVersionText(existing.VoucherNumber),
            "REF=" + NormalizeVersionText(existing.Reference),
            "PARTY=" + NormalizeLedgerIdentity(existing.PartyLedger, ledgerMasterIdByKey),
            "AMT=" + LiveVoucherComparableAmount(existing).ToString("0.00", CultureInfo.InvariantCulture),
            "NARR=" + NormalizeVersionText(existing.Narration),
            "DR=" + NormalizeLedgerIdentitySide(SplitLedgerSide(existing.DebitLedgers), ledgerMasterIdByKey),
            "CR=" + NormalizeLedgerIdentitySide(SplitLedgerSide(existing.CreditLedgers), ledgerMasterIdByKey)
        });
    }

    private static bool IsJournalVoucher(PreparedVoucher voucher) =>
        voucher.VoucherType.Equals("Journal", StringComparison.OrdinalIgnoreCase);

    private static decimal PreparedVoucherComparableAmount(PreparedVoucher voucher) =>
        Math.Abs(voucher.Entries.FirstOrDefault(entry => entry.IsParty)?.Amount ??
                 voucher.Entries.Where(entry => entry.IsDebit).Sum(entry => entry.Amount));

    private static decimal LiveVoucherComparableAmount(TallyVoucherIndexRecord voucher)
    {
        if (voucher.VoucherType.Equals("Journal", StringComparison.OrdinalIgnoreCase) &&
            voucher.LedgerPostings.Count > 0)
        {
            var debitTotal = voucher.LedgerPostings.Where(posting => posting.IsDebit).Sum(posting => posting.Amount);
            if (debitTotal > 0.005m) return Math.Abs(debitTotal);
            var creditTotal = voucher.LedgerPostings.Where(posting => !posting.IsDebit).Sum(posting => posting.Amount);
            if (creditTotal > 0.005m) return Math.Abs(creditTotal);
        }
        return Math.Abs(voucher.Amount);
    }

    private static string NormalizePreparedPostingSide(
        IEnumerable<VoucherLedgerEntry> entries,
        IReadOnlyDictionary<string, string>? ledgerMasterIdByKey) =>
        string.Join(",", entries
            .Select(entry => (Identity: NormalizeLedgerIdentity(entry.LedgerName, ledgerMasterIdByKey), Amount: Math.Abs(entry.Amount)))
            .Where(item => item.Identity.Length > 0 && item.Amount > 0.005m)
            .GroupBy(item => item.Identity, StringComparer.Ordinal)
            .Select(group => $"{group.Key}@{group.Sum(item => item.Amount).ToString("0.00", CultureInfo.InvariantCulture)}")
            .OrderBy(value => value, StringComparer.Ordinal));

    private static string NormalizeLivePostingSide(
        IEnumerable<TallyLedgerPostingRecord> postings,
        IReadOnlyDictionary<string, string>? ledgerMasterIdByKey) =>
        string.Join(",", postings
            .Select(posting => (Identity: NormalizeLedgerIdentity(posting.LedgerName, ledgerMasterIdByKey), Amount: posting.Amount))
            .Where(item => item.Identity.Length > 0 && item.Amount > 0.005m)
            .GroupBy(item => item.Identity, StringComparer.Ordinal)
            .Select(group => $"{group.Key}@{group.Sum(item => item.Amount).ToString("0.00", CultureInfo.InvariantCulture)}")
            .OrderBy(value => value, StringComparer.Ordinal));

    private static IEnumerable<string> SplitLedgerSide(string value)
    {
        // ReadVoucherLedgerSides added exactly one ordinary space on either
        // side of '|'. Remove that separator padding only, not Ledger CR/LF.
        var parts = (value ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries);
        return parts.Select((part, index) =>
        {
            if (index > 0 && part.StartsWith(" ", StringComparison.Ordinal)) part = part[1..];
            if (index < parts.Length - 1 && part.EndsWith(" ", StringComparison.Ordinal)) part = part[..^1];
            return part;
        }).Where(part => !string.IsNullOrWhiteSpace(part));
    }

    private static string ResolveLedgerMasterId(
        string? name, IReadOnlyDictionary<string, string>? ledgerMasterIdByKey)
    {
        if (ledgerMasterIdByKey is null || string.IsNullOrWhiteSpace(name)) return string.Empty;
        // An exact name (including trailing CR/LF) takes precedence over all aliases.
        if (ledgerMasterIdByKey.TryGetValue("EXACT:" + name, out var exactMasterId) &&
            exactMasterId.Trim().Length > 0)
            return exactMasterId.Trim();
        var key = TallyMasterKey(name);
        if (key.Length == 0) return string.Empty;
        if (ledgerMasterIdByKey.TryGetValue("NORMALIZED:" + key, out var uniqueMasterId) &&
            uniqueMasterId.Trim().Length > 0)
            return uniqueMasterId.Trim();
        // Compatibility with existing callers that already supply a proven unique key.
        return ledgerMasterIdByKey.TryGetValue(key, out var legacyUnique) ? legacyUnique.Trim() : string.Empty;
    }

    private static string NormalizeLedgerIdentity(
        string? name,
        IReadOnlyDictionary<string, string>? ledgerMasterIdByKey)
    {
        var normalized = NormalizeVersionText(name);
        if (normalized.Length == 0) return string.Empty;
        if (ledgerMasterIdByKey is null) return "NAME:" + normalized;
        var masterId = ResolveLedgerMasterId(name, ledgerMasterIdByKey);
        return masterId.Length > 0 ? "MID:" + masterId.ToUpperInvariant() : "UNBOUND:" + normalized;
    }

    private static string NormalizeLedgerIdentitySide(
        IEnumerable<string> names,
        IReadOnlyDictionary<string, string>? ledgerMasterIdByKey) =>
        string.Join(",", names.Select(name => NormalizeLedgerIdentity(name, ledgerMasterIdByKey))
            .Where(identity => identity.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(identity => identity, StringComparer.Ordinal));

    private static string NormalizeVersionText(string? value) =>
        string.Join(" ", (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToUpperInvariant();

    private static bool VoucherVersionsDifferOnlyByVoucherNumber(string savedVersion, string liveVersion)
    {
        static Dictionary<string, string> Parse(string value) =>
            (value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Split('=', 2))
                .Where(parts => parts.Length == 2 && parts[0].Trim().Length > 0)
                .GroupBy(parts => parts[0].Trim(), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.Ordinal);

        var saved = Parse(savedVersion);
        var live = Parse(liveVersion);
        if (saved.Count == 0 || live.Count == 0 || saved.Count != live.Count) return false;
        if (!saved.Keys.OrderBy(key => key, StringComparer.Ordinal).SequenceEqual(
                live.Keys.OrderBy(key => key, StringComparer.Ordinal), StringComparer.Ordinal)) return false;
        var numberDiffers = !saved.GetValueOrDefault("NO", string.Empty)
            .Equals(live.GetValueOrDefault("NO", string.Empty), StringComparison.Ordinal);
        if (!numberDiffers) return false;
        foreach (var key in saved.Keys.Where(key => !key.Equals("NO", StringComparison.Ordinal)))
        {
            if (!saved[key].Equals(live[key], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool VoucherVersionsMatchAfterLegacyJournalScalarAmountRebase(
        string savedVersion,
        string liveVersion,
        TallyVoucherIndexRecord liveVoucher,
        long lastVerifiedAlterId,
        IReadOnlyDictionary<string, string>? ledgerMasterIdByKey)
    {
        // Fix82 one-time representation migration for already-verified Journals that have
        // not changed in Tally since their previous verification. Pre-Fix82 live versions
        // stored TallyVoucherIndexRecord.Amount in AMT= even though that scalar can be one
        // Journal posting. The corrected representation stores the balanced debit posting
        // total. Prove the old AMT equals today's unchanged scalar and the new AMT equals
        // today's exact posting total; every accounting identity/text field must still match.
        if (!liveVoucher.VoucherType.Equals("Journal", StringComparison.OrdinalIgnoreCase) ||
            liveVoucher.LedgerPostings.Count == 0 || lastVerifiedAlterId <= 0 ||
            ParseAlterId(liveVoucher.AlterId) != lastVerifiedAlterId)
            return false;

        static Dictionary<string, string> Parse(string value) =>
            (value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Split('=', 2))
                .Where(parts => parts.Length == 2 && parts[0].Trim().Length > 0)
                .GroupBy(parts => parts[0].Trim(), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.Ordinal);

        var saved = Parse(savedVersion);
        var live = Parse(liveVersion);
        if (saved.Count == 0 || live.Count == 0 || saved.Count != live.Count) return false;
        if (!saved.Keys.OrderBy(key => key, StringComparer.Ordinal).SequenceEqual(
                live.Keys.OrderBy(key => key, StringComparer.Ordinal), StringComparer.Ordinal)) return false;

        foreach (var key in new[] { "DATE", "TYPE", "REF", "NARR" })
        {
            if (!saved.GetValueOrDefault(key, string.Empty)
                    .Equals(live.GetValueOrDefault(key, string.Empty), StringComparison.Ordinal))
                return false;
        }

        string Resolve(string value) => ledgerMasterIdByKey is null
            ? (value ?? string.Empty).Trim().ToUpperInvariant()
            : ResolveLegacyVersionIdentity(value, ledgerMasterIdByKey);
        string ResolveSide(string value) => ledgerMasterIdByKey is null
            ? string.Join(",", (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(token => token.ToUpperInvariant()).Distinct(StringComparer.Ordinal).OrderBy(token => token, StringComparer.Ordinal))
            : ResolveLegacyVersionIdentitySide(value, ledgerMasterIdByKey);

        if (!Resolve(saved.GetValueOrDefault("PARTY", string.Empty))
                .Equals(Resolve(live.GetValueOrDefault("PARTY", string.Empty)), StringComparison.Ordinal)) return false;
        if (!ResolveSide(saved.GetValueOrDefault("DR", string.Empty))
                .Equals(ResolveSide(live.GetValueOrDefault("DR", string.Empty)), StringComparison.Ordinal)) return false;
        if (!ResolveSide(saved.GetValueOrDefault("CR", string.Empty))
                .Equals(ResolveSide(live.GetValueOrDefault("CR", string.Empty)), StringComparison.Ordinal)) return false;

        if (!decimal.TryParse(saved.GetValueOrDefault("AMT", string.Empty), NumberStyles.Number, CultureInfo.InvariantCulture, out var savedAmount) ||
            !decimal.TryParse(live.GetValueOrDefault("AMT", string.Empty), NumberStyles.Number, CultureInfo.InvariantCulture, out var liveAmount))
            return false;
        var legacyScalar = Math.Abs(liveVoucher.Amount);
        var postingTotal = LiveVoucherComparableAmount(liveVoucher);
        if (Math.Abs(postingTotal - legacyScalar) <= 0.01m) return false;
        if (Math.Abs(Math.Abs(savedAmount) - legacyScalar) > 0.01m) return false;
        if (Math.Abs(Math.Abs(liveAmount) - postingTotal) > 0.01m) return false;

        // NO may differ only because Tally owns the bound Voucher Number. Same ALTERID
        // proves it was not changed in Tally after the saved previous version.
        return !savedVersion.Equals(liveVersion, StringComparison.Ordinal);
    }

    private static bool VoucherVersionsMatchAfterLegacyIdentityRebase(
        string savedVersion,
        string liveVersion,
        IReadOnlyDictionary<string, string>? ledgerMasterIdByKey)
    {
        if (ledgerMasterIdByKey is null || ledgerMasterIdByKey.Count == 0) return false;

        static Dictionary<string, string> Parse(string value) =>
            (value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Split('=', 2))
                .Where(parts => parts.Length == 2 && parts[0].Trim().Length > 0)
                .GroupBy(parts => parts[0].Trim(), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.Ordinal);

        var saved = Parse(savedVersion);
        var live = Parse(liveVersion);
        if (saved.Count == 0 || live.Count == 0 || saved.Count != live.Count) return false;
        if (!saved.Keys.OrderBy(key => key, StringComparer.Ordinal).SequenceEqual(
                live.Keys.OrderBy(key => key, StringComparer.Ordinal), StringComparer.Ordinal)) return false;

        // These fields are never relaxed by the compatibility migration. NO is Tally-owned
        // after binding, so its legacy source-number representation may differ.
        foreach (var key in new[] { "DATE", "TYPE", "REF", "AMT", "NARR" })
        {
            if (!saved.GetValueOrDefault(key, string.Empty)
                    .Equals(live.GetValueOrDefault(key, string.Empty), StringComparison.Ordinal))
                return false;
        }

        if (!ResolveLegacyVersionIdentity(saved.GetValueOrDefault("PARTY", string.Empty), ledgerMasterIdByKey)
                .Equals(ResolveLegacyVersionIdentity(live.GetValueOrDefault("PARTY", string.Empty), ledgerMasterIdByKey), StringComparison.Ordinal))
            return false;
        if (!ResolveLegacyVersionIdentitySide(saved.GetValueOrDefault("DR", string.Empty), ledgerMasterIdByKey)
                .Equals(ResolveLegacyVersionIdentitySide(live.GetValueOrDefault("DR", string.Empty), ledgerMasterIdByKey), StringComparison.Ordinal))
            return false;
        if (!ResolveLegacyVersionIdentitySide(saved.GetValueOrDefault("CR", string.Empty), ledgerMasterIdByKey)
                .Equals(ResolveLegacyVersionIdentitySide(live.GetValueOrDefault("CR", string.Empty), ledgerMasterIdByKey), StringComparison.Ordinal))
            return false;

        // At least one legacy representation must actually differ. This prevents this helper
        // from becoming a second ordinary equality path.
        return !savedVersion.Equals(liveVersion, StringComparison.Ordinal);
    }

    private static string ResolveLegacyVersionIdentity(
        string value,
        IReadOnlyDictionary<string, string> ledgerMasterIdByKey)
    {
        var token = (value ?? string.Empty).Trim();
        if (token.Length == 0) return string.Empty;
        if (token.StartsWith("MID:", StringComparison.Ordinal))
            return "MID:" + token[4..].Trim().ToUpperInvariant();

        var separator = token.IndexOf(':');
        if (separator <= 0) return token.ToUpperInvariant();
        var prefix = token[..separator];
        if (!prefix.Equals("NAME", StringComparison.Ordinal) &&
            !prefix.Equals("UNBOUND", StringComparison.Ordinal))
            return token.ToUpperInvariant();

        var name = token[(separator + 1)..];
        var masterId = ResolveLedgerMasterId(name, ledgerMasterIdByKey);
        return masterId.Length > 0 ? "MID:" + masterId.ToUpperInvariant() : token.ToUpperInvariant();
    }

    private static string ResolveLegacyVersionIdentitySide(
        string value,
        IReadOnlyDictionary<string, string> ledgerMasterIdByKey) =>
        string.Join(",", (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => ResolveLegacyVersionIdentity(token, ledgerMasterIdByKey))
            .Where(token => token.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(token => token, StringComparer.Ordinal));

    private static long ParseAlterId(string? value) =>
        long.TryParse((value ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0L;

    private static bool TextCompatible(string left, string right)
    {
        var a = NormalizeComparisonText(left);
        var b = NormalizeComparisonText(right);
        return a.Length == 0 || b.Length == 0 || a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeComparisonText(string value) =>
        string.Join(" ", (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private PreparedVoucher CanonicalizePreparedVoucher(ClientProfile client, PreparedVoucher incoming)
    {
        string Canonical(string value) =>
            _masterRepository.ResolveCanonicalName(client.Id, client.FinancialYear, "Ledger", value);

        var entries = incoming.Entries
            .Select(entry => entry with { LedgerName = Canonical(entry.LedgerName) })
            .ToArray();
        var inventoryEntries = incoming.InventoryEntries?
            .Select(entry => entry with { AccountingLedger = Canonical(entry.AccountingLedger) })
            .ToArray();

        return incoming with
        {
            PartyLedger = Canonical(incoming.PartyLedger),
            Entries = entries,
            InventoryEntries = inventoryEntries
        };
    }

    private static bool HasRequiredPurchaseLedgers(
        IEnumerable<PurchaseImportRecord> rows,
        bool isRcm)
    {
        foreach (var row in rows)
        {
            var purchaseAmount =
                PurchaseTreatmentPolicy.EffectivePurchaseLedgerAmount(row);

            if (Math.Abs(purchaseAmount) > 0.005m &&
                row.PurchaseLedger.Trim().Length == 0)
            {
                return false;
            }

            if (isRcm ||
                PurchaseTreatmentPolicy.IsGstIncludedInCost(row.GstApplicability))
            {
                continue;
            }

            if (Math.Abs(row.Igst) > 0.005m && row.IgstLedger.Trim().Length == 0)
                return false;
            if (Math.Abs(row.Cgst) > 0.005m && row.CgstLedger.Trim().Length == 0)
                return false;
            if (Math.Abs(row.Sgst) > 0.005m && row.SgstLedger.Trim().Length == 0)
                return false;
        }
        return true;
    }

    private static bool HasRequiredSalesLedgers(IEnumerable<SalesImportRecord> rows)
    {
        foreach (var row in rows)
        {
            if (Math.Abs(row.TaxableValue) > 0.005m && row.SalesLedger.Trim().Length == 0)
                return false;
            if (Math.Abs(row.Igst) > 0.005m && row.IgstLedger.Trim().Length == 0)
                return false;
            if (Math.Abs(row.Cgst) > 0.005m && row.CgstLedger.Trim().Length == 0)
                return false;
            if (Math.Abs(row.Sgst) > 0.005m && row.SgstLedger.Trim().Length == 0)
                return false;
        }
        return true;
    }

    private static IReadOnlyList<VoucherLedgerEntry> BuildPurchaseEntries(
        IReadOnlyCollection<PurchaseImportRecord> rows,
        string voucherType,
        string party,
        bool isRcm,
        JournalGenerationSettings settings,
        bool inventoryInvoice,
        IReadOnlyCollection<PreparedInventoryEntry> inventoryEntries)
    {
        if (isRcm && voucherType.Equals("Purchase", StringComparison.OrdinalIgnoreCase))
            return BuildRcmPurchaseEntries(rows, party, settings, inventoryInvoice, inventoryEntries);

        // The existing Debit Note tab remains a Tally Debit Note for supplier credit-note /
        // purchase-return accounting (Purchase and Input GST credited, supplier debited).
        // Debit Note Reverse is a supplier-issued debit note that increases the buyer's
        // Purchase/Input GST. For Tally compatibility it is exported as Credit Note with
        // Purchase/Input GST debit and supplier credit.
        var normalPurchase =
            voucherType.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ||
            rows.Any(PurchaseTreatmentPolicy.IsDebitNoteReverse);
        var entries = new List<VoucherLedgerEntry>();

        // Excel parity for GST included in cost:
        //   - the GST is not posted to Input GST ledgers;
        //   - it increases the Purchase/Expense ledger debit instead;
        //   - the supplier remains credited with the full invoice amount.
        // Source GST figures stay preserved on Purchase records for audit and
        // reconciliation, while the accounting voucher uses the effective values.
        if (!inventoryInvoice)
            AddGrouped(
                entries,
                rows,
                row => row.PurchaseLedger,
                PurchaseTreatmentPolicy.EffectivePurchaseLedgerAmount,
                normalPurchase);
        AddGrouped(
            entries,
            rows,
            row => row.IgstLedger,
            PurchaseTreatmentPolicy.EffectiveInputIgst,
            normalPurchase);
        AddGrouped(
            entries,
            rows,
            row => row.CgstLedger,
            PurchaseTreatmentPolicy.EffectiveInputCgst,
            normalPurchase);
        AddGrouped(
            entries,
            rows,
            row => row.SgstLedger,
            PurchaseTreatmentPolicy.EffectiveInputSgst,
            normalPurchase);
        AddGrouped(
            entries,
            rows,
            _ => "Input Cess",
            PurchaseTreatmentPolicy.EffectiveInputCess,
            normalPurchase);
        AddGrouped(entries, rows, _ => "Round Off", row => row.RoundOff, normalPurchase);
        var debit = entries.Where(entry => entry.IsDebit).Sum(entry => entry.Amount);
        var credit = entries.Where(entry => !entry.IsDebit).Sum(entry => entry.Amount);
        var inventoryAmount = Round(inventoryEntries.Sum(entry => Math.Abs(entry.AccountingAmount)));
        if (inventoryAmount > 0.005m)
        {
            if (normalPurchase) debit += inventoryAmount;
            else credit += inventoryAmount;
        }
        var partyAmount = normalPurchase ? debit - credit : credit - debit;
        if (partyAmount <= 0.005m) return [];
        entries.Add(new VoucherLedgerEntry(party, Round(partyAmount), !normalPurchase, true));
        return SortEntries(entries);
    }

    private static IReadOnlyList<VoucherLedgerEntry> BuildRcmPurchaseEntries(
        IReadOnlyCollection<PurchaseImportRecord> rows,
        string party,
        JournalGenerationSettings settings,
        bool inventoryInvoice,
        IReadOnlyCollection<PreparedInventoryEntry> inventoryEntries)
    {
        var entries = new List<VoucherLedgerEntry>();

        // Confirmed source RCM voucher: both sides are posted here.
        // Purchase/Expense Dr base + rate-wise RCM Input Dr tax
        // against Supplier Cr base + rate-wise RCM Payable Cr tax.
        if (!inventoryInvoice)
            AddGrouped(entries, rows, row => row.PurchaseLedger, row => row.TaxableValue, true);
        AddGrouped(entries, rows, _ => "Round Off", row => row.RoundOff, true);

        var baseDebit = entries.Where(entry => entry.IsDebit).Sum(entry => entry.Amount) +
                        Round(inventoryEntries.Sum(entry => Math.Abs(entry.AccountingAmount)));
        var baseCredit = entries.Where(entry => !entry.IsDebit).Sum(entry => entry.Amount);
        var partyBase = Round(baseDebit - baseCredit);
        if (partyBase <= 0.005m) return [];

        AddRcmTaxPairs(entries, rows, "IGST", row => row.IgstLedger, row => row.Igst,
            row => row.Rate, settings.TemporaryRcmIgstLedger);
        AddRcmTaxPairs(entries, rows, "CGST", row => row.CgstLedger, row => row.Cgst,
            row => row.Rate / 2m, settings.TemporaryRcmCgstLedger);
        AddRcmTaxPairs(entries, rows, "SGST", row => row.SgstLedger, row => row.Sgst,
            row => row.Rate / 2m, settings.TemporaryRcmSgstLedger);

        entries.Add(new VoucherLedgerEntry(party, partyBase, false, true));
        return SortEntries(entries);
    }

    private static void AddRcmTaxPairs(
        ICollection<VoucherLedgerEntry> target,
        IEnumerable<PurchaseImportRecord> rows,
        string taxType,
        Func<PurchaseImportRecord, string> sourceLedgerSelector,
        Func<PurchaseImportRecord, decimal> amountSelector,
        Func<PurchaseImportRecord, decimal> rateSelector,
        string configuredInputLedger)
    {
        var grouped = rows
            .Select(row => new
            {
                SourceLedger = sourceLedgerSelector(row),
                Amount = amountSelector(row),
                Rate = rateSelector(row)
            })
            .Where(row => Math.Abs(row.Amount) > 0.005m)
            .GroupBy(row => new
            {
                InputLedger = PurchaseTreatmentPolicy.BuildRcmInputLedgerName(
                    row.SourceLedger, configuredInputLedger, taxType, row.Rate),
                PayableLedger = PurchaseTreatmentPolicy.BuildRcmPayableLedgerName(taxType, row.Rate)
            });

        foreach (var group in grouped)
        {
            var amount = Round(group.Sum(row => row.Amount));
            if (Math.Abs(amount) <= 0.005m) continue;
            var positive = amount > 0m;
            target.Add(new VoucherLedgerEntry(group.Key.InputLedger, Math.Abs(amount), positive, false));
            target.Add(new VoucherLedgerEntry(group.Key.PayableLedger, Math.Abs(amount), !positive, false));
        }
    }

    private static string FormatRate(decimal rate) =>
        PurchaseTreatmentPolicy.FormatRate(rate);

    private static IReadOnlyList<PreparedInventoryEntry> BuildPurchaseInventoryEntries(
        IEnumerable<PurchaseImportRecord> rows,
        string voucherType,
        bool isRcm)
    {
        var result = new List<PreparedInventoryEntry>();
        foreach (var row in rows.Where(row => row.HasInventory))
        {
            var billed = row.BilledQuantity != 0m ? row.BilledQuantity : row.ActualQuantity;
            var actual = row.ActualQuantity != 0m ? row.ActualQuantity : billed;
            if (row.FreeQuantity != 0m && actual <= billed)
                actual = billed + row.FreeQuantity;
            var amount = row.ItemAmount != 0m ? row.ItemAmount : row.TaxableValue;
            var effectivePurchaseAmount = isRcm
                ? row.TaxableValue
                : PurchaseTreatmentPolicy.EffectivePurchaseLedgerAmount(row);
            var accountingAmount = row.AccountingAmount != 0m
                ? row.AccountingAmount
                : effectivePurchaseAmount != 0m ? effectivePurchaseAmount : amount;
            var rate = row.ItemRate;
            if (rate == 0m && billed != 0m && amount != 0m)
                rate = amount / billed;
            var batchActual = row.BatchActualQuantity != 0m ? row.BatchActualQuantity : actual;
            var batchBilled = row.BatchBilledQuantity != 0m ? row.BatchBilledQuantity : billed;
            var batchAmount = row.BatchAmount != 0m ? row.BatchAmount : amount;
            var inventoryIsDebit =
                voucherType.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ||
                PurchaseTreatmentPolicy.IsDebitNoteReverse(row);
            result.Add(new PreparedInventoryEntry(
                row.ItemName.Trim(), row.Unit.Trim(), actual, billed, rate, Math.Abs(amount),
                (row.ItemPurchaseLedger.Trim().Length > 0 ? row.ItemPurchaseLedger : row.PurchaseLedger).Trim(),
                Math.Abs(accountingAmount),
                row.Godown.Trim().Length > 0 ? row.Godown.Trim() : "Main Location",
                row.BatchName.Trim().Length > 0 ? row.BatchName.Trim() : "Primary Batch",
                batchActual, batchBilled, Math.Abs(batchAmount), row.DiscountPercent, Math.Abs(row.DiscountAmount),
                row.OrderNumber.Trim(), row.TrackingNumber.Trim(), row.ManufacturingDate, row.ExpiryDate,
                row.ItemDescription.Trim(), row.CostCentre.Trim(), inventoryIsDebit,
                row.StockItemPersistentId, row.TallyStockMasterId.Trim(), row.RateUnit.Trim()));
        }
        return result;
    }

    private static IReadOnlyList<VoucherLedgerEntry> BuildSalesEntries(
        IReadOnlyCollection<SalesImportRecord> rows,
        string voucherType,
        string party,
        bool inventoryInvoice,
        IReadOnlyCollection<PreparedInventoryEntry> inventoryEntries)
    {
        var normalSales = voucherType.Equals("Sales", StringComparison.OrdinalIgnoreCase);
        var entries = new List<VoucherLedgerEntry>();
        if (!inventoryInvoice)
            AddGrouped(entries, rows, row => row.SalesLedger, row => row.TaxableValue, !normalSales);
        AddGrouped(entries, rows, row => row.IgstLedger, row => row.Igst, !normalSales);
        AddGrouped(entries, rows, row => row.CgstLedger, row => row.Cgst, !normalSales);
        AddGrouped(entries, rows, row => row.SgstLedger, row => row.Sgst, !normalSales);
        AddGrouped(entries, rows, row => row.CessRate > 0m ? $"Output Cess @ {FormatRate(row.CessRate)}%" : "Output Cess", row => row.Cess, !normalSales);
        AddGrouped(entries, rows, _ => "Round Off", row => row.RoundOff, !normalSales);

        var debit = entries.Where(entry => entry.IsDebit).Sum(entry => entry.Amount);
        var credit = entries.Where(entry => !entry.IsDebit).Sum(entry => entry.Amount);
        var inventoryAmount = Round(inventoryEntries.Sum(entry => Math.Abs(entry.AccountingAmount)));
        if (inventoryAmount > 0.005m)
        {
            if (normalSales) credit += inventoryAmount;
            else debit += inventoryAmount;
        }
        var partyAmount = normalSales ? credit - debit : debit - credit;
        if (partyAmount <= 0.005m) return [];
        entries.Add(new VoucherLedgerEntry(party, Round(partyAmount), normalSales, true));
        return SortEntries(entries);
    }

    private static IReadOnlyList<PreparedInventoryEntry> BuildSalesInventoryEntries(
        IEnumerable<SalesImportRecord> rows,
        string voucherType)
    {
        var result = new List<PreparedInventoryEntry>();
        foreach (var row in rows.Where(row => row.HasInventory))
        {
            var billed = row.BilledQuantity != 0m ? row.BilledQuantity : row.ActualQuantity;
            var actual = row.ActualQuantity != 0m ? row.ActualQuantity : billed;
            if (row.FreeQuantity != 0m && actual <= billed)
                actual = billed + row.FreeQuantity;
            var amount = row.ItemAmount != 0m ? row.ItemAmount : row.TaxableValue;
            var accountingAmount = row.AccountingAmount != 0m ? row.AccountingAmount : amount;
            var rate = row.ItemRate;
            if (rate == 0m && billed != 0m && amount != 0m)
                rate = amount / billed;
            var batchActual = row.BatchActualQuantity != 0m ? row.BatchActualQuantity : actual;
            var batchBilled = row.BatchBilledQuantity != 0m ? row.BatchBilledQuantity : billed;
            var batchAmount = row.BatchAmount != 0m ? row.BatchAmount : amount;
            result.Add(new PreparedInventoryEntry(
                row.ItemName.Trim(), row.Unit.Trim(), actual, billed, rate, Math.Abs(amount),
                (row.ItemSalesLedger.Trim().Length > 0 ? row.ItemSalesLedger : row.SalesLedger).Trim(),
                Math.Abs(accountingAmount),
                row.Godown.Trim().Length > 0 ? row.Godown.Trim() : "Main Location",
                row.BatchName.Trim().Length > 0 ? row.BatchName.Trim() : "Primary Batch",
                batchActual, batchBilled, Math.Abs(batchAmount), row.DiscountPercent, Math.Abs(row.DiscountAmount),
                row.OrderNumber.Trim(), row.TrackingNumber.Trim(), row.ManufacturingDate, row.ExpiryDate,
                row.ItemDescription.Trim(), row.CostCentre.Trim(),
                voucherType.Equals("Credit Note", StringComparison.OrdinalIgnoreCase),
                row.StockItemPersistentId, row.TallyStockMasterId.Trim(), row.RateUnit.Trim()));
        }
        return result;
    }

    private static bool HasSalesBusinessData(SalesImportRecord row) =>
        row.PartyMailingName.Length > 0 || row.BuyerName.Length > 0 || row.ConsigneeName.Length > 0 ||
        row.Irn.Length > 0 || row.EWayBillNumber.Length > 0 || row.DispatchThrough.Length > 0 ||
        row.DeliveryNoteNumber.Length > 0 || row.BuyerOrderNumber.Length > 0;

    private static IReadOnlyList<VoucherLedgerEntry> SortEntries(IEnumerable<VoucherLedgerEntry> entries) =>
        entries.OrderBy(entry => entry.IsParty ? 0 : entry.LedgerName.Equals("Round Off", StringComparison.OrdinalIgnoreCase) ? 3 :
            entry.LedgerName.Contains("GST", StringComparison.OrdinalIgnoreCase) || entry.LedgerName.Contains("Cess", StringComparison.OrdinalIgnoreCase) ? 2 : 1)
            .ThenBy(entry => entry.LedgerName, StringComparer.OrdinalIgnoreCase).ToArray();

    private static void AddGrouped<T>(ICollection<VoucherLedgerEntry> target, IEnumerable<T> rows,
        Func<T, string> ledgerSelector, Func<T, decimal> amountSelector, bool positiveIsDebit)
    {
        foreach (var group in rows.GroupBy(row => ledgerSelector(row).Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Key.Length > 0))
        {
            var amount = Round(group.Sum(amountSelector));
            if (Math.Abs(amount) > 0.005m)
                target.Add(new VoucherLedgerEntry(group.Key, Math.Abs(amount), positiveIsDebit == (amount > 0m), false));
        }
    }

    private static bool IsBalanced(IEnumerable<VoucherLedgerEntry> entries) =>
        IsBalanced(entries, [], false);

    private static bool IsBalanced(
        IEnumerable<VoucherLedgerEntry> entries,
        IEnumerable<PreparedInventoryEntry> inventoryEntries,
        bool inventoryIsDebit)
    {
        var debit = entries.Where(entry => entry.IsDebit).Sum(entry => entry.Amount);
        var credit = entries.Where(entry => !entry.IsDebit).Sum(entry => entry.Amount);
        var inventoryAmount = inventoryEntries.Sum(entry => Math.Abs(entry.AccountingAmount));
        if (inventoryIsDebit) debit += inventoryAmount;
        else credit += inventoryAmount;
        return Math.Abs(debit - credit) <= 0.01m;
    }

    private static HashSet<string> PeriodKeysForRange(DateTime fromDate, DateTime toDate, string frequency)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cursor = new DateTime(fromDate.Year, fromDate.Month, 1);
        var end = new DateTime(toDate.Year, toDate.Month, 1);
        while (cursor <= end) { keys.Add(GstJournalGenerationService.PeriodKey(cursor, frequency)); cursor = cursor.AddMonths(1); }
        return keys;
    }

    private static string NormalizePurchaseDestination(string destination, string documentType)
    {
        // A supplier-issued GSTR-2B debit note is kept as Debit Note Reverse inside AAH
        // because it increases Purchase/Input GST. Tally receives it as Credit Note so
        // the voucher type accepts the buyer-side increase while ledger amounts remain
        // Purchase/Input GST Dr and Supplier Cr. Ordinary purchase-return notes remain
        // Debit Note exactly as before.
        if (destination.Equals(
                PurchaseTreatmentPolicy.DebitNoteReverseDestination,
                StringComparison.OrdinalIgnoreCase))
        {
            return "Credit Note";
        }

        return destination.Contains("Debit", StringComparison.OrdinalIgnoreCase) ||
               documentType.Contains("Debit", StringComparison.OrdinalIgnoreCase) ||
               documentType.Contains("Credit Note", StringComparison.OrdinalIgnoreCase)
            ? "Debit Note"
            : "Purchase";
    }

    private static string NormalizeSalesDestination(string destination, string documentType) =>
        destination.Contains("Credit", StringComparison.OrdinalIgnoreCase) || documentType.Contains("Credit", StringComparison.OrdinalIgnoreCase)
            ? "Credit Note" : "Sales";

    private string ResolveLedgerCanonicalName(ClientProfile client, string? value) =>
        _masterRepository.ResolveCanonicalName(
            client.Id,
            client.FinancialYear,
            "Ledger",
            value ?? string.Empty);

    private string ResolveDocumentPartyName(
        ClientProfile client,
        IEnumerable<string> names,
        string normalizedGstin)
    {
        var candidates = names
            .Select(name => ResolveLedgerCanonicalName(client, name))
            .Where(name => name.Length > 0)
            .GroupBy(TallyMasterIdentity.Key, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(name => name.Any(char.IsLower))
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                .First())
            .ToArray();

        if (candidates.Length == 0) return string.Empty;

        // A valid GSTIN is the primary party identity for a GST document. Different
        // capitalization of the displayed party name must not split the document.
        // If GSTIN is unavailable, grouping already uses Tally's normalized Master key.
        return candidates[0];
    }

    private static string BuildDocumentPartyIdentity(string normalizedGstin, string canonicalParty)
    {
        if (normalizedGstin.Length == 15)
        {
            return "GSTIN:" + normalizedGstin;
        }

        var partyKey = TallyMasterIdentity.Key(canonicalParty);
        return partyKey.Length > 0 ? "NAME:" + partyKey : string.Empty;
    }

    private static string ResolveDocumentNumber(IEnumerable<string> values) =>
        values
            .Select(value => AccountingText.CleanWhitespace(value))
            .Where(value => value.Length > 0)
            .GroupBy(NormalizeDocumentNumberKey, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .FirstOrDefault() ?? string.Empty;

    private static string NormalizeDocumentNumberKey(string? value) =>
        AccountingText.CleanWhitespace(value).ToUpperInvariant();

    private static string BuildDocumentSourceKey(string module, ClientProfile client, string voucherType,
        string documentNumber, string partyGstin, DateTime date) =>
        $"AAH|{module}|{client.Id}|{client.FinancialYear}|{voucherType.Trim().ToUpperInvariant()}|{date:yyyyMMdd}|{partyGstin.Trim().ToUpperInvariant()}|{NormalizeDocumentNumberKey(documentNumber)}";

    private static string NormalizeGstin(string value)
    {
        var normalized = new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        return normalized.Length == 15 ? normalized : string.Empty;
    }

    private static string BuildImportXml(
        string companyName,
        DateTime gstApplicableFrom,
        IReadOnlyCollection<PreparedVoucher> vouchers,
        IReadOnlyCollection<MasterRecord> masters,
        IReadOnlyCollection<BankAccountRecord> bankAccounts,
        IReadOnlyCollection<StockItemRecord> stockItems,
        IReadOnlyCollection<StockItemUnitProfileRecord> stockItemUnitProfiles,
        IReadOnlyCollection<StockUnitRecord> stockUnits,
        IReadOnlyCollection<StockSimpleMasterRecord> stockGroups,
        IReadOnlyCollection<StockSimpleMasterRecord> stockCategories,
        IReadOnlyCollection<StockSimpleMasterRecord> godowns,
        IReadOnlyCollection<StockSimpleMasterRecord> costCentres,
        IReadOnlySet<string>? freshLiveLedgerKeys = null,
        IReadOnlySet<string>? freshLiveGroupKeys = null,
        bool enforceFreshLiveMasterPreflight = false)
    {
        var builder = new StringBuilder();
        // Proven workbook modXMLWriter: one All Masters envelope, masters first, vouchers second.
        TallyExcelParityXmlWriter.BeginAllMastersImport(builder, companyName);

        var requiredLedgers = BuildRequiredLedgers(vouchers, masters);
        AppendRequiredAccountingMasters(
            builder, gstApplicableFrom, requiredLedgers, masters, bankAccounts,
            freshLiveLedgerKeys, freshLiveGroupKeys, enforceFreshLiveMasterPreflight);
        AppendRequiredStockMasters(builder, gstApplicableFrom, vouchers, stockItems, stockItemUnitProfiles, stockUnits, stockGroups, stockCategories, godowns, costCentres);
        foreach (var voucher in vouchers) AppendVoucher(builder, voucher);
        TallyExcelParityXmlWriter.EndAllMastersImport(builder);
        return builder.ToString();
    }

    private static bool IsFix83GstAssetLedger(RequiredLedger ledger)
    {
        var name = (ledger.Name ?? string.Empty).Trim();
        var parent = (ledger.Parent ?? string.Empty).Trim();
        if (name.Length == 0 || parent.Length == 0) return false;
        if (GstMasterClassificationPolicy.IsNotShownInTwoBLedger(name)) return true;
        if (name.Equals(GstMasterClassificationPolicy.SingleGstReceivableLedger, StringComparison.OrdinalIgnoreCase) &&
            parent.Equals(GstMasterClassificationPolicy.LoansAndAdvancesAssetGroup, StringComparison.OrdinalIgnoreCase))
            return true;
        return parent.Equals(GstMasterClassificationPolicy.GstReceivableGroup, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<RequiredLedger> BuildFix83GstAssetRequiredLedgers(
        IReadOnlyCollection<PreparedVoucher> vouchers,
        IReadOnlyCollection<MasterRecord> masters,
        JournalGenerationSettings settings)
    {
        var required = BuildRequiredLedgers(vouchers, masters)
            .Where(IsFix83GstAssetLedger)
            .ToDictionary(ledger => TallyMasterKey(ledger.Name), ledger => ledger, StringComparer.OrdinalIgnoreCase);

        // The Not-shown-in-2B parent rule is Master-wide, not export-range-wide. Once
        // such a live Ledger exists in the active AAH Master authority it must move to
        // Loans & Advances even when today's selected Voucher range does not happen to
        // reference that tax head. MASTERID remains unchanged because this is Parent ALTER.
        foreach (var master in masters.Where(master =>
                     master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                     !master.IsArchived && GstMasterClassificationPolicy.IsNotShownInTwoBLedger(master.Name)))
        {
            var key = TallyMasterKey(master.Name);
            if (!required.ContainsKey(key))
                required[key] = new RequiredLedger(master.Name.Trim(), GstMasterClassificationPolicy.InputNotShownInTwoBGroup,
                    string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        void AddSettlement(string name, string parent)
        {
            name = (name ?? string.Empty).Trim();
            if (name.Length == 0) return;
            var key = TallyMasterKey(name);
            if (!required.ContainsKey(key))
                required[key] = new RequiredLedger(name, parent, string.Empty, string.Empty, string.Empty,
                    string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        if (GstMasterClassificationPolicy.IsSeparateReceivableMode(settings))
        {
            AddSettlement(GstMasterClassificationPolicy.EffectiveIgstSettlementLedger(settings), GstMasterClassificationPolicy.GstReceivableGroup);
            AddSettlement(GstMasterClassificationPolicy.EffectiveCgstSettlementLedger(settings), GstMasterClassificationPolicy.GstReceivableGroup);
            AddSettlement(GstMasterClassificationPolicy.EffectiveSgstSettlementLedger(settings), GstMasterClassificationPolicy.GstReceivableGroup);
        }
        else if (GstMasterClassificationPolicy.IsReceivableMode(settings))
        {
            AddSettlement(GstMasterClassificationPolicy.EffectiveSingleSettlementLedger(settings), GstMasterClassificationPolicy.LoansAndAdvancesAssetGroup);
        }

        return required.Values.OrderBy(ledger => ledger.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string BuildFix83GstAssetMasterXml(
        string companyName,
        IReadOnlyCollection<PreparedVoucher> vouchers,
        IReadOnlyCollection<MasterRecord> masters,
        JournalGenerationSettings settings,
        IReadOnlyDictionary<string,string> liveLedgerParents,
        IReadOnlySet<string> liveGroupNames)
    {
        var required = BuildFix83GstAssetRequiredLedgers(vouchers, masters, settings);
        var builder = new StringBuilder();
        TallyExcelParityXmlWriter.BeginAllMastersImport(builder, companyName);
        if (required.Count == 0)
        {
            TallyExcelParityXmlWriter.EndAllMastersImport(builder);
            return builder.ToString();
        }

        var localGroups = masters
            .Where(master => master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) &&
                             master.Name.Trim().Length > 0)
            .GroupBy(master => TallyMasterKey(master.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var localLedgers = masters
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                             master.Name.Trim().Length > 0)
            .GroupBy(master => TallyMasterKey(master.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var liveGroupKeys = liveGroupNames
            .Select(TallyMasterKey)
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var liveParentsByKey = liveLedgerParents
            .Where(pair => TallyMasterKey(pair.Key).Length > 0)
            .GroupBy(pair => TallyMasterKey(pair.Key), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value.Trim(), StringComparer.OrdinalIgnoreCase);

        void AppendRequiredAssetGroup(string groupName, string parent, string purpose)
        {
            var groupKey = TallyMasterKey(groupName);
            if (liveGroupKeys.Contains(groupKey)) return;
            localGroups.TryGetValue(groupKey, out var candidates);
            candidates ??= [];
            var eligible = candidates.Where(master => !master.IsArchived && IsLocalTallyCreateMaster(master)).ToArray();
            if (eligible.Length != 1)
                throw new InvalidOperationException(
                    $"{purpose} subgroup '{groupName}' is required under {parent}, but exactly one active local Group authority was not available (found {eligible.Length}).");
            if (!eligible[0].Parent.Trim().Equals(parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"{purpose} subgroup '{groupName}' parent must be {parent}; AAH currently stores '{eligible[0].Parent}'.");
            AppendFix83MinimalGroupMaster(builder, groupName, parent, "Create");
        }

        var needsNotShownGroup = required.Any(ledger =>
            ledger.Parent.Equals(GstMasterClassificationPolicy.InputNotShownInTwoBGroup, StringComparison.OrdinalIgnoreCase));
        if (needsNotShownGroup)
            AppendRequiredAssetGroup(
                GstMasterClassificationPolicy.InputNotShownInTwoBGroup,
                GstMasterClassificationPolicy.LoansAndAdvancesAssetGroup,
                "Input Not Shown in 2B");

        var needsReceivableGroup = required.Any(ledger =>
            ledger.Parent.Equals(GstMasterClassificationPolicy.GstReceivableGroup, StringComparison.OrdinalIgnoreCase));
        if (needsReceivableGroup)
            AppendRequiredAssetGroup(
                GstMasterClassificationPolicy.GstReceivableGroup,
                GstMasterClassificationPolicy.LoansAndAdvancesAssetGroup,
                "GST Receivable");

        foreach (var ledger in required)
        {
            var key = TallyMasterKey(ledger.Name);
            if (liveParentsByKey.TryGetValue(key, out var liveParent))
            {
                if (!TallyMasterKey(liveParent).Equals(TallyMasterKey(ledger.Parent), StringComparison.OrdinalIgnoreCase))
                {
                    // Existing Not-shown-in-2B and generated receivable Ledgers keep the
                    // same Tally identity. Only the Parent is modified.
                    TallyExcelParityXmlWriter.AppendErp9MinimalLedgerMaster(
                        builder, ledger.Name, ledger.Parent, "Alter");
                }
                continue;
            }

            localLedgers.TryGetValue(key, out var candidates);
            candidates ??= [];
            var eligible = candidates.Where(master => !master.IsArchived && IsLocalTallyCreateMaster(master)).ToArray();
            if (eligible.Length != 1)
                throw new InvalidOperationException(
                    $"Required GST asset Ledger '{ledger.Name}' is not live in Tally and exactly one active local CREATE authority was not available (found {eligible.Length}).");
            TallyExcelParityXmlWriter.AppendErp9MinimalLedgerMaster(
                builder, ledger.Name, ledger.Parent, "Create");
        }

        TallyExcelParityXmlWriter.EndAllMastersImport(builder);
        return builder.ToString();
    }

    /// <summary>
    /// Fix83 R1 keeps the protected ERP 9 parity writer byte-identical. This local helper
    /// emits only the minimal NAME/PARENT Group contract needed by the isolated GST asset
    /// Master stage, so the runtime Fix83 behavior is retained without changing the
    /// protected Bank/Excel XML serialization authority.
    /// </summary>
    private static void AppendFix83MinimalGroupMaster(
        StringBuilder builder,
        string name,
        string parent,
        string action = "Create")
    {
        name = (name ?? string.Empty).Trim();
        parent = (parent ?? string.Empty).Trim();
        action = string.IsNullOrWhiteSpace(action) ? "Create" : action.Trim();
        if (name.Length == 0 || parent.Length == 0) return;

        var safeName = SecurityElement.Escape(name) ?? string.Empty;
        var safeParent = SecurityElement.Escape(parent) ?? string.Empty;
        var safeAction = SecurityElement.Escape(action) ?? string.Empty;
        builder.AppendLine("      <TALLYMESSAGE>");
        builder.Append("        <GROUP NAME=\"").Append(safeName).Append("\" Action=\"")
            .Append(safeAction).AppendLine("\">");
        builder.Append("          <NAME>").Append(safeName).AppendLine("</NAME>");
        builder.Append("          <PARENT>").Append(safeParent).AppendLine("</PARENT>");
        builder.AppendLine("        </GROUP>");
        builder.AppendLine("      </TALLYMESSAGE>");
    }

    private static void VerifyFix83GstAssetMasters(
        IReadOnlyCollection<PreparedVoucher> vouchers,
        IReadOnlyCollection<MasterRecord> masters,
        JournalGenerationSettings settings,
        IReadOnlyDictionary<string,string> liveLedgerParents,
        IReadOnlySet<string> liveGroupNames)
    {
        var required = BuildFix83GstAssetRequiredLedgers(vouchers, masters, settings);
        if (required.Count == 0) return;

        var needsNotShownGroup = required.Any(ledger =>
            ledger.Parent.Equals(GstMasterClassificationPolicy.InputNotShownInTwoBGroup, StringComparison.OrdinalIgnoreCase));
        if (needsNotShownGroup && !liveGroupNames.Any(name =>
                TallyMasterKey(name).Equals(TallyMasterKey(GstMasterClassificationPolicy.InputNotShownInTwoBGroup), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Tally did not expose the required '{GstMasterClassificationPolicy.InputNotShownInTwoBGroup}' Group after CREATE.");

        var needsReceivableGroup = required.Any(ledger =>
            ledger.Parent.Equals(GstMasterClassificationPolicy.GstReceivableGroup, StringComparison.OrdinalIgnoreCase));
        if (needsReceivableGroup && !liveGroupNames.Any(name =>
                TallyMasterKey(name).Equals(TallyMasterKey(GstMasterClassificationPolicy.GstReceivableGroup), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Tally did not expose the required '{GstMasterClassificationPolicy.GstReceivableGroup}' Group after CREATE.");

        foreach (var ledger in required)
        {
            var matches = liveLedgerParents
                .Where(pair => TallyMasterKey(pair.Key).Equals(TallyMasterKey(ledger.Name), StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(
                    $"Tally live authority contains {matches.Length} exact Ledger parent record(s) for '{ledger.Name}' after the GST asset Master stage.");
            if (!TallyMasterKey(matches[0].Value).Equals(TallyMasterKey(ledger.Parent), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Tally Ledger '{ledger.Name}' still has Parent '{matches[0].Value}' instead of required '{ledger.Parent}'.");
        }
    }

    private static string BuildMastersOnlyXml(
        string companyName, DateTime gstApplicableFrom, IReadOnlyCollection<PreparedVoucher> vouchers,
        IReadOnlyCollection<MasterRecord> masters, IReadOnlyCollection<BankAccountRecord> bankAccounts,
        IReadOnlyCollection<StockItemRecord> stockItems, IReadOnlyCollection<StockItemUnitProfileRecord> stockItemUnitProfiles, IReadOnlyCollection<StockUnitRecord> stockUnits,
        IReadOnlyCollection<StockSimpleMasterRecord> stockGroups, IReadOnlyCollection<StockSimpleMasterRecord> stockCategories,
        IReadOnlyCollection<StockSimpleMasterRecord> godowns, IReadOnlyCollection<StockSimpleMasterRecord> costCentres,
        IReadOnlySet<string>? freshLiveLedgerKeys, IReadOnlySet<string>? freshLiveGroupKeys)
    {
        var builder = new StringBuilder();
        TallyExcelParityXmlWriter.BeginAllMastersImport(builder, companyName);
        var requiredLedgers = BuildRequiredLedgers(vouchers, masters);
        AppendRequiredAccountingMasters(builder, gstApplicableFrom, requiredLedgers, masters, bankAccounts,
            freshLiveLedgerKeys, freshLiveGroupKeys, true);
        AppendRequiredStockMasters(builder, gstApplicableFrom, vouchers, stockItems, stockItemUnitProfiles, stockUnits, stockGroups, stockCategories, godowns, costCentres);
        TallyExcelParityXmlWriter.EndAllMastersImport(builder);
        return builder.ToString();
    }

    private static bool HasImportPayload(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return false;
        try
        {
            var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            return document.Descendants().Any(element =>
                element.Name.LocalName.Equals("TALLYMESSAGE", StringComparison.OrdinalIgnoreCase) &&
                element.Elements().Any());
        }
        catch
        {
            // Fail closed: malformed XML must still be sent to the normal validator/import
            // path where it will be rejected, rather than being mistaken for an empty stage.
            return true;
        }
    }

    private static string BuildVoucherOnlyXml(string companyName, IReadOnlyCollection<PreparedVoucher> vouchers)
    {
        var builder = new StringBuilder();
        // Fix80: Voucher-only batches must use Tally.ERP 9's Data/Vouchers import
        // envelope. The old All Masters report envelope can parse the payload yet return
        // Ignored for valid voucher ALTERs. Masters remain in the separate Master stage.
        TallyExcelParityXmlWriter.BeginErp9DataImport(builder, companyName, "Vouchers");
        foreach (var voucher in vouchers) AppendVoucher(builder, voucher);
        TallyExcelParityXmlWriter.EndErp9DataImport(builder);
        return builder.ToString();
    }

    private static void AppendRequiredAccountingMasters(
        StringBuilder builder,
        DateTime applicableFrom,
        IReadOnlyCollection<RequiredLedger> requiredLedgers,
        IReadOnlyCollection<MasterRecord> masters,
        IReadOnlyCollection<BankAccountRecord> bankAccounts,
        IReadOnlySet<string>? freshLiveLedgerKeys,
        IReadOnlySet<string>? freshLiveGroupKeys,
        bool enforceFreshLiveMasterPreflight)
    {
        var groups = masters
            .Where(master => master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) && master.Name.Trim().Length > 0)
            .GroupBy(master => TallyMasterKey(master.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var ledgerCandidates = masters
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && master.Name.Trim().Length > 0)
            .GroupBy(master => TallyMasterKey(master.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var liveGroups = freshLiveGroupKeys is not null
            ? new HashSet<string>(freshLiveGroupKeys, StringComparer.OrdinalIgnoreCase)
            : groups.Values.Where(IsLiveTallyMaster)
                .Select(master => TallyMasterKey(master.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Fix83 GST asset Masters are handled and live-verified in their own minimal
        // Data/All Masters stage. Do not resend them through the inherited generic
        // All Masters envelope or a successful CREATE/ALTER could be turned into an
        // IGNORE/Exception by the duplicate policy.
        var ordinaryRequiredLedgers = requiredLedgers.Where(ledger => !IsFix83GstAssetLedger(ledger)).ToArray();
        var requiredGroups = new Dictionary<string, MasterRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var ledger in ordinaryRequiredLedgers)
            AddRequiredGroup(ledger.Parent, groups, requiredGroups);

        // Excel collector emits parent groups before dependent child groups/ledgers.
        foreach (var group in OrderRequiredGroups(requiredGroups.Values, groups))
        {
            if (liveGroups.Contains(TallyMasterKey(group.Name))) continue;
            if (!IsLocalTallyCreateMaster(group))
                throw new InvalidOperationException(
                    $"Required Group '{group.Name}' is not live in Tally and is sourced from '{group.Source}'. " +
                    "Only local/non-Tally Groups are eligible for automatic CREATE; Tally/imported/system Masters are never regenerated.");
            if (string.IsNullOrWhiteSpace(group.Parent) || group.Parent.Trim().Equals("Primary", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Local Group '{group.Name}' is directly under Primary. Automatic Tally CREATE is blocked because the local Group does not store Tally's required Group Nature. " +
                    "Edit the local Group and select an existing accounting parent Group before export.");
            AppendGroupMaster(builder, group.Name, group.Parent);
        }

        var bankIndex = bankAccounts
            .Where(account => account.AuthorityName.Trim().Length > 0)
            .GroupBy(account => TallyMasterKey(account.AuthorityName), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);

        foreach (var ledger in ordinaryRequiredLedgers)
        {
            var key = TallyMasterKey(ledger.Name);
            ledgerCandidates.TryGetValue(key, out var candidates);
            candidates ??= [];
            bankIndex.TryGetValue(key, out var bankAccount);
            var liveExists = freshLiveLedgerKeys is not null
                ? freshLiveLedgerKeys.Contains(key)
                : candidates.Any(IsLiveTallyMaster);

            // When the fresh direct collection says the Ledger is missing, stale Tally
            // provenance must not outrank a real Manual/local Master. Require one
            // unambiguous local authority; duplicate locals are review-first.
            var localCandidates = candidates.Where(IsLocalTallyCreateMaster).ToArray();
            if (!liveExists && localCandidates.Length > 1)
                throw new InvalidOperationException(
                    $"Duplicate local Ledger Master review is required before Tally CREATE: {ledger.Name}. " +
                    "Merge/archive the duplicate local Ledger Masters in Master Centre first.");
            var existing = liveExists
                ? candidates.OrderByDescending(IsLiveTallyMaster).ThenBy(master => master.PersistentId).FirstOrDefault()
                : localCandidates.SingleOrDefault();

            if (bankAccount is not null)
            {
                // A linked Bank Account is app-side authority even when no separate
                // local Ledger Master row exists yet. Use the exact Bank Ledger Create
                // master emitted by the proven Excel/VBA path whenever it is absent live.
                if (!liveExists)
                    TallyExcelParityXmlWriter.AppendWorkbookBankLedgerCreateMaster(
                        builder, ledger.Name);
                continue;
            }
            if (liveExists)
            {
                // General accounting Masters remain create-only. The sole exception is
                // the Hub's own deterministic RCM ledger family: older Hub builds could
                // create RCM Expense/Pending ledgers under the wrong parent Group. Repair
                // only those exact generated names when fresh Tally authority confirms
                // that the Ledger already exists and its parent disagrees.
                if (existing is not null &&
                    (IsGeneratedRcmClassificationLedger(ledger.Name) ||
                     GstMasterClassificationPolicy.ExpectedKnownAssetParent(ledger.Name).Length > 0) &&
                    !TallyMasterKey(existing.Parent).Equals(
                        TallyMasterKey(ledger.Parent),
                        StringComparison.OrdinalIgnoreCase))
                {
                    AppendGeneratedLedgerParentAlter(builder, ledger);
                }
                continue;
            }

            // Do not manufacture a Master merely because a transaction mentions a
            // Ledger name. For a real export, fail closed before sending ANY vouchers
            // if a required name has no eligible local Master authority. This prevents
            // a whole batch from reaching Tally with missing accounting masters.
            if (existing is null)
            {
                if (enforceFreshLiveMasterPreflight)
                    throw new InvalidOperationException(
                        $"Required Ledger '{ledger.Name}' is not live in Tally and has no local Master eligible for CREATE.");
                continue;
            }
            if (!IsLocalTallyCreateMaster(existing))
            {
                if (enforceFreshLiveMasterPreflight)
                    throw new InvalidOperationException(
                        $"Required Ledger '{ledger.Name}' is not live in Tally. Local Master source is '{existing.Source}', which is not eligible for CREATE.");
                continue;
            }
            AppendLedgerMaster(builder, ledger, null, applicableFrom, "Create");
        }
    }

    private static void AddRequiredGroup(
        string groupName,
        IReadOnlyDictionary<string, MasterRecord> groupIndex,
        IDictionary<string, MasterRecord> target)
    {
        var key = TallyMasterKey(groupName);
        if (key.Length == 0 || key.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)) return;
        if (!groupIndex.TryGetValue(key, out var group)) return;
        var parentKey = TallyMasterKey(group.Parent);
        // The required custom Group itself must be emitted even when it sits directly
        // under Primary. Only recursion stops at Primary; otherwise a Manual Group such
        // as Room Rent can be omitted while its dependent Ledger is still emitted.
        if (!target.ContainsKey(key)) target[key] = group;
        if (parentKey.Length == 0 || parentKey.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)) return;
        AddRequiredGroup(group.Parent, groupIndex, target);
    }

    private static IReadOnlyList<MasterRecord> OrderRequiredGroups(
        IEnumerable<MasterRecord> required,
        IReadOnlyDictionary<string, MasterRecord> allGroups)
    {
        int Depth(MasterRecord group)
        {
            var depth = 0;
            var current = group;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var parentKey = TallyMasterKey(current.Parent);
                if (parentKey.Length == 0 || parentKey.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase) || !seen.Add(parentKey)) break;
                if (!allGroups.TryGetValue(parentKey, out var parent)) break;
                current = parent;
                depth++;
            }
            return depth;
        }
        return required.OrderBy(Depth).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsLiveTallyMaster(MasterRecord master) =>
        master.Source.Contains("Tally", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalTallyCreateMaster(MasterRecord master) =>
        MasterAuthority.IsLocalEditableSource(master.Source);

    private static void AppendRequiredStockMasters(
        StringBuilder builder,
        DateTime gstApplicableFrom,
        IEnumerable<PreparedVoucher> vouchers,
        IReadOnlyCollection<StockItemRecord> stockItems,
        IReadOnlyCollection<StockItemUnitProfileRecord> stockItemUnitProfiles,
        IReadOnlyCollection<StockUnitRecord> stockUnits,
        IReadOnlyCollection<StockSimpleMasterRecord> stockGroups,
        IReadOnlyCollection<StockSimpleMasterRecord> stockCategories,
        IReadOnlyCollection<StockSimpleMasterRecord> godowns,
        IReadOnlyCollection<StockSimpleMasterRecord> costCentres)
    {
        var inventory = vouchers.SelectMany(voucher => voucher.InventoryEntries ?? []).ToArray();
        if (inventory.Length == 0) return;
        var existingItems = stockItems.Where(item => item.Name.Trim().Length > 0)
            .GroupBy(item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in inventory)
        {
            if (entry.StockItemPersistentId <= 0)
                throw new InvalidOperationException($"Inventory export blocked: Stock Item '{entry.ItemName}' has no AAH Stock Item ID. Normalize/remap the inventory source through Stock Master first.");
            if (!existingItems.TryGetValue(entry.ItemName.Trim(), out var item))
                throw new InvalidOperationException($"Inventory export blocked: Stock Item '{entry.ItemName}' is not linked to AAH Stock Master.");
            if (item.PersistentId != entry.StockItemPersistentId)
                throw new InvalidOperationException($"Inventory export blocked: '{entry.ItemName}' points to Stock Item ID {entry.StockItemPersistentId}, but current Stock Master ID is {item.PersistentId}. Refresh/remap the inventory source.");
            if (entry.TallyStockMasterId.Length > 0 && item.TallyMasterId.Length > 0 && !entry.TallyStockMasterId.Equals(item.TallyMasterId,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Inventory export blocked: Tally Stock MASTERID changed for '{entry.ItemName}'. Refresh Tally authority before export.");
            if (item.BaseUnit.Trim().Length == 0)
                throw new InvalidOperationException($"Inventory export blocked: Stock Item '{entry.ItemName}' has no Base/Inventory Unit.");
            if (!UnitEquivalentForInventory(entry.Unit, item.BaseUnit))
                throw new InvalidOperationException($"Inventory export blocked: '{entry.ItemName}' is still in Unit '{entry.Unit}', but Stock Master Base Unit is '{item.BaseUnit}'. Normalize through the Stock Item Unit Profile first.");
            var expected = Math.Round(entry.BilledQuantity * entry.Rate, 2, MidpointRounding.AwayFromZero);
            if (entry.Amount > 0.005m && Math.Abs(expected-entry.Amount) > 0.02m)
                throw new InvalidOperationException($"Inventory export blocked: '{entry.ItemName}' has Billed Qty × Rate = {expected:0.00}, but Item Amount = {entry.Amount:0.00}. Review Rate Unit / Unit Profile.");
        }

        var existingUnits = stockUnits.Where(unit => unit.Name.Trim().Length > 0)
            .GroupBy(unit => unit.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var groupIndex = stockGroups.Where(item => item.Name.Trim().Length > 0)
            .GroupBy(item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var godownIndex = godowns.Where(item => item.Name.Trim().Length > 0)
            .GroupBy(item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var costCentreIndex = costCentres.Where(item => item.Name.Trim().Length > 0)
            .GroupBy(item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var requiredItemNames = inventory.Select(entry => entry.ItemName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var requiredItemRecords = requiredItemNames
            .Select(name => existingItems.GetValueOrDefault(name))
            .Where(item => item is not null)
            .Cast<StockItemRecord>()
            .ToArray();

        // Fix117: Units are dependencies of the Stock Item master itself, not only of
        // the voucher quantity. Emit Base and configured Tally Alternate units before
        // the Stock Item even when the voucher happens to use only the Base unit.
        var requiredUnitNames = inventory.Select(entry => entry.Unit.Trim())
            .Concat(requiredItemRecords.Select(item => item.BaseUnit.Trim()))
            .Concat(requiredItemRecords.Select(item => item.AdditionalUnit.Trim()))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var unitName in requiredUnitNames)
        {
            existingUnits.TryGetValue(unitName, out var unit);
            if (unit is null && !AahUnitCatalog.TryGetDefinition(unitName, out _))
                throw new InvalidOperationException($"Inventory export blocked: Unit '{unitName}' is referenced by a Stock Item but is not a saved AAH/Tally Unit Master.");
            AppendUnitMaster(builder, unitName, unit);
        }
        foreach (var groupName in requiredItemRecords.Select(item => item.StockGroup.Trim())
                     .Where(value => value.Length > 0 && !value.Equals("Primary", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            groupIndex.TryGetValue(groupName, out var group);
            AppendSimpleInventoryMaster(builder, "STOCKGROUP", groupName, group?.Parent ?? "Primary", group?.Source ?? string.Empty);
        }
        // Primary / Not Applicable are ERP9 root sentinels, not categories to
        // CREATE. Resolve real category parents first and reject missing/cyclic
        // dependencies before any Master XML is sent.
        TallyInventoryRootPolicy.AppendRequiredCategories(builder,
            requiredItemRecords.Select(item => item.StockCategory), stockCategories);
        foreach (var godownName in inventory.Select(entry => entry.Godown.Trim())
                     .Where(value => value.Length > 0 && !value.Equals("Main Location", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            godownIndex.TryGetValue(godownName, out var godown);
            AppendSimpleInventoryMaster(builder, "GODOWN", godownName, godown?.Parent ?? "Primary", godown?.Source ?? string.Empty);
        }
        foreach (var costCentreName in inventory.Select(entry => entry.CostCentre.Trim())
                     .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            costCentreIndex.TryGetValue(costCentreName, out var centre);
            AppendCostCentreMaster(builder, costCentreName, centre?.Source ?? string.Empty);
        }
        foreach (var itemName in requiredItemNames)
        {
            existingItems.TryGetValue(itemName, out var item);
            if (item is null) throw new InvalidOperationException($"Inventory export blocked: Stock Item '{itemName}' is not present in AAH Stock Master.");
            var itemProfiles = stockItemUnitProfiles.Where(profile => profile.StockItemId == item.PersistentId && profile.IsAllowed).ToArray();
            AppendStockItemMaster(builder, itemName, item.BaseUnit, item, itemProfiles, gstApplicableFrom);
        }
    }

    private async Task<string> RebindAcceptedMastersAfterFailedMasterBatchAsync(
        ClientProfile client,
        TallyCompanyInfo company,
        TallyVoucherExportResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_httpService.IsLocalErp9Runtime)
                _httpService.RequireFreshErp9MasterAuthorityForStockBaseline();

            var refreshedMasterXml = await _httpService.ExportMastersAsync(company, cancellationToken).ConfigureAwait(false);
            var refreshedLedgerParents = await _httpService.ExportLedgerParentsAsync(company, cancellationToken).ConfigureAwait(false);
            var refreshedGroupNames = await _httpService.ExportGroupNamesAsync(company, cancellationToken).ConfigureAwait(false);
            var sync = _liveMasterSyncService.Synchronize(
                refreshedMasterXml, client.Id, client.FinancialYear, company.Name,
                refreshedLedgerParents, refreshedGroupNames, ledgerGroupOnlyAuthority: true);
            var refreshedStockItems = _stockMasterRepository.GetStockItems(client.Id, client.FinancialYear).ToArray();
            RebindDurableInventoryStockIdentity(client, refreshedStockItems);
            return $"Partial Master recovery reread live Tally authority and rebound {sync.UpsertedObjects:N0} Master object(s); retry will send only still-missing Masters.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result.LiveReadErrors++;
            result.LiveReadErrorDetails.Add("Partial Master-stage recovery readback failed: " + exception.Message);
            return "Partial Master recovery could not reread accepted MASTERIDs: " + exception.Message;
        }
    }

    private void RebindDurableInventoryStockIdentity(ClientProfile client, IReadOnlyList<StockItemRecord> stockItems)
    {
        var byId = stockItems.Where(item => item.PersistentId > 0).ToDictionary(item => item.PersistentId);
        var byName = stockItems
            .Where(item => item.PersistentId > 0 && item.Name.Trim().Length > 0)
            .GroupBy(item => AccountingText.DuplicateKey(item.Name), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var purchaseChanged = new List<PurchaseImportRecord>();
        foreach (var row in _purchaseRepository.GetRecords(client.Id, client.FinancialYear).Where(row => row.PersistentId > 0 && row.ItemName.Trim().Length > 0))
        {
            StockItemRecord? item = null;
            if (row.StockItemPersistentId > 0) byId.TryGetValue(row.StockItemPersistentId, out item);
            if (item is null) byName.TryGetValue(AccountingText.DuplicateKey(row.ItemName), out item);
            if (item is null) continue;
            var changed = false;
            if (row.StockItemPersistentId != item.PersistentId) { row.StockItemPersistentId = item.PersistentId; changed = true; }
            if (!row.TallyStockMasterId.Equals(item.TallyMasterId, StringComparison.OrdinalIgnoreCase)) { row.TallyStockMasterId = item.TallyMasterId; changed = true; }
            if (changed) purchaseChanged.Add(row);
        }
        if (purchaseChanged.Count > 0) _purchaseRepository.UpdateStockIdentityReferences(client.Id, purchaseChanged);

        var salesChanged = new List<SalesImportRecord>();
        foreach (var row in _salesRepository.GetRecords(client.Id, client.FinancialYear).Where(row => row.PersistentId > 0 && row.ItemName.Trim().Length > 0))
        {
            StockItemRecord? item = null;
            if (row.StockItemPersistentId > 0) byId.TryGetValue(row.StockItemPersistentId, out item);
            if (item is null) byName.TryGetValue(AccountingText.DuplicateKey(row.ItemName), out item);
            if (item is null) continue;
            var changed = false;
            if (row.StockItemPersistentId != item.PersistentId) { row.StockItemPersistentId = item.PersistentId; changed = true; }
            if (!row.TallyStockMasterId.Equals(item.TallyMasterId, StringComparison.OrdinalIgnoreCase)) { row.TallyStockMasterId = item.TallyMasterId; changed = true; }
            if (changed) salesChanged.Add(row);
        }
        if (salesChanged.Count > 0) _salesRepository.UpdateStockIdentityReferences(client.Id, salesChanged);
    }

    private static string MasterAction(string? source) =>
        !string.IsNullOrWhiteSpace(source) && source.Contains("Tally", StringComparison.OrdinalIgnoreCase)
            ? "Alter"
            : "Create";

    private static string StockMasterAction(StockItemRecord? item) =>
        item is not null && (item.TallyMasterId.Trim().Length > 0 || item.Source.Contains("Tally",StringComparison.OrdinalIgnoreCase)) ? "Alter" : "Create";

    private static bool UnitEquivalentForInventory(string left,string right) =>
        left.Trim().Equals(right.Trim(),StringComparison.OrdinalIgnoreCase) ||
        (AahUnitCatalog.TryGetFactor(left,right,out var factor) && factor==1m);

    private static void AppendSimpleInventoryMaster(StringBuilder builder, string tag, string name, string parent, string source) =>
        TallyInventoryRootPolicy.AppendSimpleInventoryMaster(builder, tag, name, parent, source);

    private static void AppendCostCentreMaster(StringBuilder builder, string name, string source)
    {
        builder.AppendLine("  <TALLYMESSAGE>");
        builder.Append("    <COSTCENTRE NAME=\"").Append(Xml(name)).Append("\" ACTION=\"").Append(MasterAction(source)).AppendLine("\">");
        builder.Append("      <NAME>").Append(Xml(name)).AppendLine("</NAME>");
        builder.AppendLine("      <CATEGORY>Primary Cost Category</CATEGORY>");
        builder.AppendLine("    </COSTCENTRE>");
        builder.AppendLine("  </TALLYMESSAGE>");
    }

    private static void AppendUnitMaster(StringBuilder builder, string name, StockUnitRecord? unit)
    {
        builder.AppendLine("  <TALLYMESSAGE>");
        builder.Append("    <UNIT NAME=\"").Append(Xml(name)).Append("\" ACTION=\"").Append(MasterAction(unit?.Source)).AppendLine("\">");
        builder.Append("      <NAME>").Append(Xml(name)).AppendLine("</NAME>");
        // Transaction quantity conversion is handled before XML generation. A scalar
        // conversion factor alone is not sufficient to define a Tally compound Unit
        // (which also requires explicit base/additional units), so export these as
        // simple units and never invent a compound-unit relationship.
        builder.AppendLine("      <ISSIMPLEUNIT>Yes</ISSIMPLEUNIT>");
        var formalName = unit?.FormalName?.Trim() ?? string.Empty;
        var reportingUqc = unit?.ReportingUqc?.Trim() ?? string.Empty;
        if (AahUnitCatalog.TryGetDefinition(name, out var unitDefinition))
        {
            if (formalName.Length == 0) formalName = unitDefinition.Description.Trim();
            if (reportingUqc.Length == 0) reportingUqc = unitDefinition.ReportingUqc.Trim();
        }
        if (formalName.Length > 0)
            builder.Append("      <ORIGINALNAME>").Append(Xml(formalName)).AppendLine("</ORIGINALNAME>");
        builder.Append("      <DECIMALPLACES>").Append(Math.Clamp(unit?.DecimalPlaces ?? 0, 0, 4).ToString(CultureInfo.InvariantCulture)).AppendLine("</DECIMALPLACES>");
        if (reportingUqc.Length > 0)
            builder.Append("      <GSTREPUOM>").Append(Xml(reportingUqc)).AppendLine("</GSTREPUOM>");
        builder.AppendLine("    </UNIT>");
        builder.AppendLine("  </TALLYMESSAGE>");
    }

    private static void AppendStockItemMaster(StringBuilder builder, string name, string fallbackUnit, StockItemRecord? item, IReadOnlyCollection<StockItemUnitProfileRecord> itemProfiles, DateTime gstApplicableFrom)
    {
        var parent = item?.StockGroup?.Trim() ?? string.Empty;
        // Fix131: AAH uses blank/Primary as the local root marker. For a STOCKITEM, Tally
        // interprets <PARENT> as a real Stock Group name; emitting <PARENT>Primary</PARENT>
        // therefore fails on ERP9 when no Stock Group named Primary exists. Root Stock Items
        // omit PARENT; only a genuine custom Stock Group is emitted.
        var hasRealParent = parent.Length > 0 && !parent.Equals("Primary", StringComparison.OrdinalIgnoreCase);
        var baseUnit = item?.BaseUnit?.Trim();
        if (string.IsNullOrWhiteSpace(baseUnit)) baseUnit = fallbackUnit;
        builder.AppendLine("  <TALLYMESSAGE>");
        builder.Append("    <STOCKITEM NAME=\"").Append(Xml(name)).Append("\" ACTION=\"").Append(StockMasterAction(item)).AppendLine("\">");
        builder.Append("      <NAME>").Append(Xml(name)).AppendLine("</NAME>");
        if (item is not null && item.PersistentId > 0)
        {
            // Tally's documented third-party Stock Item mapping contract uses NAME.LIST
            // for aliases. Keep one stable AAH alias so mutable display names are never
            // the only cross-system identity; MASTERID remains the live Tally authority.
            builder.AppendLine("      <NAME.LIST TYPE=\"String\">");
            builder.Append("        <NAME>").Append(Xml(name)).AppendLine("</NAME>");
            builder.Append("        <NAME>").Append(Xml($"AAH-STOCK-{item.PersistentId}")).AppendLine("</NAME>");
            builder.AppendLine("      </NAME.LIST>");
            if (!string.IsNullOrWhiteSpace(item.PartNumberSku))
            {
                builder.AppendLine("      <MAILINGNAME.LIST TYPE=\"String\">");
                builder.Append("        <MAILINGNAME>").Append(Xml(item.PartNumberSku)).AppendLine("</MAILINGNAME>");
                builder.AppendLine("      </MAILINGNAME.LIST>");
            }
        }
        if (hasRealParent) builder.Append("      <PARENT>").Append(Xml(parent)).AppendLine("</PARENT>");
        if (!string.IsNullOrWhiteSpace(baseUnit)) builder.Append("      <BASEUNITS>").Append(Xml(baseUnit)).AppendLine("</BASEUNITS>");
        if (item is not null && !string.IsNullOrWhiteSpace(item.AdditionalUnit))
        {
            var alternate = itemProfiles.LastOrDefault(profile => UnitEquivalentForInventory(profile.UnitCode, item.AdditionalUnit) && profile.FactorToBase > 0m);
            if (alternate is not null)
            {
                builder.Append("      <ADDITIONALUNITS>").Append(Xml(item.AdditionalUnit)).AppendLine("</ADDITIONALUNITS>");
                // Tally Stock Item alternate-unit ratio: Denominator / Conversion = Base Units per Alternate Unit.
                // AAH stores exactly that quotient as FactorToBase, so emit 1 alternate = FactorToBase base.
                builder.AppendLine("      <CONVERSION>1</CONVERSION>");
                builder.Append("      <DENOMINATOR>").Append(alternate.FactorToBase.ToString("0.################", CultureInfo.InvariantCulture)).AppendLine("</DENOMINATOR>");
            }
        }
        TallyInventoryRootPolicy.AppendItemCategory(builder, item?.StockCategory);
        AppendStockItemGstDetails(builder, item, gstApplicableFrom);
        if (item is not null)
        {
            builder.Append("      <ISBATCHWISEON>").Append(item.BatchWise ? "Yes" : "No").AppendLine("</ISBATCHWISEON>");
            builder.Append("      <ISPERISHABLEON>").Append(item.Perishable ? "Yes" : "No").AppendLine("</ISPERISHABLEON>");
            builder.Append("      <HASMFGDATE>").Append(item.HasManufacturingDate ? "Yes" : "No").AppendLine("</HASMFGDATE>");
            if (!string.IsNullOrWhiteSpace(item.CostingMethod)) builder.Append("      <COSTINGMETHOD>").Append(Xml(item.CostingMethod)).AppendLine("</COSTINGMETHOD>");
            if (!string.IsNullOrWhiteSpace(item.ValuationMethod)) builder.Append("      <VALUATIONMETHOD>").Append(Xml(item.ValuationMethod)).AppendLine("</VALUATIONMETHOD>");
        }
        builder.AppendLine("    </STOCKITEM>");
        builder.AppendLine("  </TALLYMESSAGE>");
    }

    private static void AppendStockItemGstDetails(StringBuilder builder, StockItemRecord? item, DateTime applicableFrom)
    {
        if (item is null) return;
        var hasHsn = !string.IsNullOrWhiteSpace(item.HsnSac);
        var hasRate = item.GstRate.HasValue;
        var hasSupply = !string.IsNullOrWhiteSpace(item.GstTypeOfSupply);
        if (!hasHsn && !hasRate && !hasSupply) return;

        builder.AppendLine("      <GSTAPPLICABLE>&#4; Applicable</GSTAPPLICABLE>");
        if (hasSupply) builder.Append("      <GSTTYPEOFSUPPLY>").Append(Xml(item.GstTypeOfSupply)).AppendLine("</GSTTYPEOFSUPPLY>");
        builder.AppendLine("      <GSTDETAILS.LIST>");
        builder.Append("        <APPLICABLEFROM>").Append(applicableFrom.ToString("yyyyMMdd", CultureInfo.InvariantCulture)).AppendLine("</APPLICABLEFROM>");
        builder.AppendLine("        <CALCULATIONTYPE>On Value</CALCULATIONTYPE>");
        if (hasHsn) builder.Append("        <HSNCODE>").Append(Xml(item.HsnSac)).AppendLine("</HSNCODE>");
        if (hasRate) builder.Append("        <TAXABILITY>").Append(item.GstRate!.Value > 0m ? "Taxable" : "Nil Rated").AppendLine("</TAXABILITY>");
        if (hasRate)
        {
            var integrated = item.GstRate!.Value;
            var split = integrated / 2m;
            builder.AppendLine("        <STATEWISEDETAILS.LIST>");
            builder.AppendLine("          <STATENAME>&#4; Any</STATENAME>");
            AppendGstRateDetail(builder, "Central Tax", split);
            AppendGstRateDetail(builder, "State Tax", split);
            AppendGstRateDetail(builder, "Integrated Tax", integrated);
            builder.AppendLine("        </STATEWISEDETAILS.LIST>");
        }
        builder.AppendLine("      </GSTDETAILS.LIST>");
    }

    private static void AppendGstRateDetail(StringBuilder builder, string dutyHead, decimal rate)
    {
        builder.AppendLine("          <RATEDETAILS.LIST>");
        builder.Append("            <GSTRATEDUTYHEAD>").Append(dutyHead).AppendLine("</GSTRATEDUTYHEAD>");
        builder.AppendLine("            <GSTRATEVALUATIONTYPE>Based on Value</GSTRATEVALUATIONTYPE>");
        builder.Append("            <GSTRATE>").Append(rate.ToString("0.####", CultureInfo.InvariantCulture)).AppendLine("</GSTRATE>");
        builder.AppendLine("          </RATEDETAILS.LIST>");
    }

    private static T[][] SplitFailedVoucherBatch<T>(T[] remaining)
    {
        if (remaining.Length == 0) return Array.Empty<T[]>();
        if (remaining.Length == 1) return new[] { remaining };
        var middle = remaining.Length / 2;
        return new[] { remaining.Take(middle).ToArray(), remaining.Skip(middle).ToArray() };
    }

    private static void AppendVoucher(StringBuilder builder, PreparedVoucher voucher)
    {
        // Fix94 inventory parity: Purchase/Debit Note/Credit Note uses the same Tally
        // item-invoice collection family as Sales when inventory rows exist. Purchase
        // documents without inventory retain their existing accounting-voucher shape.
        var salesDocument =
            voucher.SourceType.Equals("Sales Voucher", StringComparison.OrdinalIgnoreCase) &&
            (voucher.VoucherType.Equals("Sales", StringComparison.OrdinalIgnoreCase) ||
             voucher.VoucherType.Equals("Credit Note", StringComparison.OrdinalIgnoreCase));
        var purchaseDocument =
            voucher.SourceType.Equals("Purchase Voucher", StringComparison.OrdinalIgnoreCase) &&
            (voucher.VoucherType.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ||
             voucher.VoucherType.Equals("Debit Note", StringComparison.OrdinalIgnoreCase) ||
             voucher.VoucherType.Equals("Credit Note", StringComparison.OrdinalIgnoreCase));
        var inventoryInvoice = (salesDocument || purchaseDocument) && (voucher.InventoryEntries?.Count ?? 0) > 0;
        // R18: ledger-only documents use Accounting Voucher View; no item invoice lines exist.
        var invoice = inventoryInvoice && voucher.ReferenceNumber.Length > 0;
        var view = invoice ? "Invoice Voucher View" : "Accounting Voucher View";
        var entryMode = inventoryInvoice ? "Item Invoice" : invoice ? "Accounting Invoice" : "As Voucher";
        if (voucher.ExistingTallyMasterId.Length > 0)
        {
            BeginVoucherAlter(
                builder, voucher.VoucherType, view, voucher.Date, voucher.ReferenceDate,
                voucher.ReferenceNumber, voucher.PartyLedger, voucher.ExistingTallyMasterId);
        }
        else
        {
            TallyExcelParityXmlWriter.BeginVoucher(
                builder, voucher.VoucherType, view, voucher.Date, voucher.ReferenceDate,
                voucher.VoucherNumber, voucher.ReferenceNumber, voucher.PartyLedger);
        }
        var gstDocument = voucher.SourceType is "Purchase Voucher" or "Sales Voucher";
        if (gstDocument && voucher.PartyLedger.Length > 0)
            builder.Append("      <GSTREGISTRATIONTYPE>").Append(voucher.PartyGstin.Length == 15 ? "Regular" : "Unregistered/Consumer").AppendLine("</GSTREGISTRATIONTYPE>");
        if (gstDocument)
        {
            var companyGstRegistration = voucher.BusinessRecord?.CompanyGstRegistration?.Trim() ?? string.Empty;
            builder.Append("      <GSTREGISTRATION TAXTYPE=\"GST\" TAXREGISTRATION=\"")
                .Append(Xml(companyGstRegistration)).AppendLine("\"></GSTREGISTRATION>");
        }
        if (gstDocument && voucher.PartyGstin.Length == 15) builder.Append("      <PARTYGSTIN>").Append(Xml(voucher.PartyGstin)).AppendLine("</PARTYGSTIN>");
        if (gstDocument && voucher.PlaceOfSupply.Length > 0)
        {
            builder.Append("      <PLACEOFSUPPLY>").Append(Xml(voucher.PlaceOfSupply)).AppendLine("</PLACEOFSUPPLY>");
            builder.Append("      <STATENAME>").Append(Xml(voucher.PlaceOfSupply)).AppendLine("</STATENAME>");
        }
        AppendSalesBusinessFields(builder, voucher.BusinessRecord);
        var eWayExcluded = voucher.BusinessRecord is null ? (bool?)null : voucher.BusinessRecord.ExcludeFromEWayBill;
        var eWayApplicable = voucher.BusinessRecord is not null && !voucher.BusinessRecord.ExcludeFromEWayBill &&
                             voucher.BusinessRecord.EWayBillNumber.Trim().Length > 0;
        TallyExcelParityXmlWriter.AppendVoucherModeAndNarration(
            builder, view, entryMode, invoice, inventoryInvoice,
            (voucher.InventoryEntries ?? []).Any(item => item.CostCentre.Length > 0),
            voucher.Narration, eWayExcluded, eWayApplicable);
        if (inventoryInvoice)
            foreach (var entry in voucher.InventoryEntries ?? []) AppendInventoryEntry(builder, entry);
        foreach (var entry in voucher.Entries) AppendLedgerEntry(builder, entry, voucher);
        TallyExcelParityXmlWriter.EndVoucher(builder);
    }

    private static void BeginVoucherAlter(
        StringBuilder builder,
        string voucherType,
        string view,
        DateTime date,
        DateTime? referenceDate,
        string reference,
        string partyLedger,
        string masterId)
    {
        builder.AppendLine("  <TALLYMESSAGE>");
        builder.Append("    <VOUCHER DATE=\"")
            .Append(date.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture))
            .Append("\" TAGNAME=\"MASTER ID\" TAGVALUE=\"")
            .Append(Xml(masterId))
            .Append("\" ACTION=\"Alter\" VCHTYPE=\"")
            .Append(Xml(voucherType))
            .Append("\" OBJVIEW=\"")
            .Append(Xml(view))
            .AppendLine("\">");
        AppendTag(builder, "DATE", date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), 6);
        if (referenceDate is DateTime refDate)
            AppendTag(builder, "REFERENCEDATE", refDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture), 6);
        AppendTag(builder, "VOUCHERTYPENAME", voucherType, 6);
        AppendTag(builder, "REFERENCE", reference, 6);
        if (!string.IsNullOrWhiteSpace(partyLedger))
        {
            AppendTag(builder, "PARTYNAME", partyLedger, 6);
            AppendTag(builder, "PARTYLEDGERNAME", partyLedger, 6);
            AppendTag(builder, "BASICBASEPARTYNAME", partyLedger, 6);
        }
    }

    private static void AppendSalesBusinessFields(StringBuilder builder, SalesImportRecord? row)
    {
        if (row is null) return;
        AppendTag(builder, "PARTYMAILINGNAME", row.PartyMailingName);
        AppendTag(builder, "STATENAME", row.PartyState);
        AppendTag(builder, "COUNTRYOFRESIDENCE", row.PartyCountry);
        AppendTag(builder, "PARTYPINCODE", row.PartyPincode);
        AppendAddressList(builder, "ADDRESS.LIST", row.PartyAddressLine1, row.PartyAddressLine2, row.PartyAddressLine3);
        AppendTag(builder, "BASICBUYERNAME", row.BuyerName);
        AppendAddressList(builder, "BASICBUYERADDRESS.LIST", row.BuyerAddressLine1, row.BuyerAddressLine2, row.BuyerAddressLine3);
        if (!string.IsNullOrWhiteSpace(row.BuyerOrderTerms))
        {
            builder.AppendLine("      <BASICORDERTERMS.LIST TYPE=\"String\">");
            AppendTag(builder, "BASICORDERTERMS", row.BuyerOrderTerms, 8);
            builder.AppendLine("      </BASICORDERTERMS.LIST>");
        }
        AppendTag(builder, "CONSIGNEEMAILINGNAME", row.ConsigneeName);
        AppendTag(builder, "CONSIGNEEGSTIN", row.ConsigneeGstin);
        AppendTag(builder, "CONSIGNEESTATENAME", row.ConsigneeState);
        AppendTag(builder, "CONSIGNEECOUNTRYNAME", row.ConsigneeCountry);
        AppendTag(builder, "CONSIGNEEPINCODE", row.ConsigneePincode);
        AppendAddressList(builder, "BASICSHIPADDRESS.LIST", row.ConsigneeAddressLine1, row.ConsigneeAddressLine2, row.ConsigneeAddressLine3);
        AppendTag(builder, "BASICSHIPDOCUMENTNO", row.DispatchDocumentNumber);
        AppendTag(builder, "BASICSHIPPEDBY", row.DispatchThrough);
        AppendTag(builder, "BASICFINALDESTINATION", row.FinalDestination);
        AppendTag(builder, "BASICDESTINATIONCOUNTRY", row.DestinationCountry);
        AppendTag(builder, "BASICORDERREF", row.BuyerOrderReference.Length > 0 ? row.BuyerOrderReference : row.BuyerOrderNumber);
        AppendTag(builder, "BASICBUYERSSALEORDERNO", row.BuyerOrderNumber);
        if (row.BuyerOrderDate is DateTime orderDate) AppendTag(builder, "BASICORDERDATE", orderDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        AppendTag(builder, "BASICDUEDATEOFPYMT", row.PaymentDueDays > 0 ? row.PaymentDueDays.ToString(CultureInfo.InvariantCulture) : string.Empty);
        AppendTag(builder, "BASICSHIPDELIVERYNOTE", row.DeliveryNoteNumber);
        if (row.DeliveryNoteDate is DateTime deliveryDate) AppendTag(builder, "BASICSHIPPINGDATE", deliveryDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        AppendTag(builder, "BILLOFLADINGNO", row.LrBillOfLadingNumber);
        if (row.LrBillOfLadingDate is DateTime lrDate) AppendTag(builder, "BILLOFLADINGDATE", lrDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        AppendTag(builder, "BASICSHIPVESSELNO", row.VehicleVesselNumber);
        AppendTag(builder, "BASICPLACEOFRECEIPT", row.PlaceOfReceipt);
        AppendTag(builder, "BASICPORTOFLOADING", row.PortOfLoading);
        AppendTag(builder, "BASICPORTOFDISCHARGE", row.PortOfDischarge);
        AppendTag(builder, "SHIPPINGBILLNO", row.ShippingBillNumber);
        if (row.ShippingBillDate is DateTime shipDate) AppendTag(builder, "SHIPPINGBILLDATE", shipDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        AppendTag(builder, "PORTCODE", row.PortCode);
        AppendTag(builder, "EICHECKPOST", row.CheckPost);
        AppendTag(builder, "CMPGSTREGISTRATIONTYPE", row.CompanyGstRegistrationType);
        AppendTag(builder, "CMPGSTSTATE", row.CompanyGstState);
        AppendTag(builder, "TAXUNITNAME", row.TaxUnitName);
        AppendTag(builder, "BILLTOPLACE", row.BillToPlace);
        AppendTag(builder, "SHIPTOPLACE", row.ShipToPlace);
        AppendTag(builder, "DISPATCHFROMNAME", row.DispatchFromName);
        AppendTag(builder, "DISPATCHFROMSTATENAME", row.DispatchFromState);
        AppendTag(builder, "DISPATCHFROMPINCODE", row.DispatchFromPincode);
        AppendTag(builder, "DISPATCHFROMPLACE", row.DispatchFromPlace);
        AppendAddressList(builder, "DISPATCHFROMADDRESS.LIST", row.DispatchFromAddressLine1, row.DispatchFromAddressLine2, row.DispatchFromAddressLine3);
        if (row.InvoiceDateTime is DateTime invoiceDateTime) AppendTag(builder, "BASICDATETIMEOFINVOICE", invoiceDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
        AppendTag(builder, "IRN", row.Irn);
        AppendTag(builder, "IRNACKNO", row.IrnAcknowledgementNumber);
        if (row.IrnAcknowledgementDate is DateTime ack) AppendTag(builder, "IRNACKDATE", ack.ToString("yyyyMMdd", CultureInfo.InvariantCulture));

        if (row.BuyerOrderNumber.Trim().Length > 0 || row.BuyerOrderDate is not null)
        {
            builder.AppendLine("      <INVOICEORDERLIST.LIST>");
            if (row.BuyerOrderDate is DateTime d) AppendTag(builder, "BASICORDERDATE", d.ToString("yyyyMMdd", CultureInfo.InvariantCulture), 8);
            AppendTag(builder, "BASICPURCHASEORDERNO", row.BuyerOrderNumber, 8);
            builder.AppendLine("      </INVOICEORDERLIST.LIST>");
        }
        if (row.DeliveryNoteNumber.Trim().Length > 0 || row.DeliveryNoteDate is not null ||
            row.DeliveryNoteNumber2.Trim().Length > 0 || row.DeliveryNoteDate2 is not null)
        {
            void Delivery(string number, DateTime? date)
            {
                if (number.Trim().Length == 0 && date is null) return;
                builder.AppendLine("      <INVOICEDELNOTES.LIST>");
                if (date is DateTime d) AppendTag(builder, "BASICSHIPPINGDATE", d.ToString("yyyyMMdd", CultureInfo.InvariantCulture), 8);
                AppendTag(builder, "BASICSHIPDELIVERYNOTE", number, 8);
                builder.AppendLine("      </INVOICEDELNOTES.LIST>");
            }
            Delivery(row.DeliveryNoteNumber, row.DeliveryNoteDate);
            Delivery(row.DeliveryNoteNumber2, row.DeliveryNoteDate2);
        }

        if (!string.IsNullOrWhiteSpace(row.EWayBillNumber) && !row.ExcludeFromEWayBill)
        {
            builder.AppendLine("      <EWAYBILLDETAILS.LIST>");
            AppendAddressListAt(builder, "CONSIGNEEADDRESS.LIST", 8, "CONSIGNEEADDRESS", row.ConsigneeAddressLine1, row.ConsigneeAddressLine2, row.ConsigneeAddressLine3);
            if (row.EWayBillDate is DateTime date) AppendTag(builder, "BILLDATE", date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), 8);
            AppendTag(builder, "CONSIGNEEPINCODE", row.ConsigneePincode, 8);
            AppendTag(builder, "BILLNUMBER", row.EWayBillNumber, 8);
            AppendTag(builder, "CONSIGNORPLACE", row.DispatchFromPlace, 8);
            AppendTag(builder, "CONSIGNEEPLACE", row.ShipToPlace, 8);
            AppendTag(builder, "SHIPPEDFROMSTATE", row.DispatchFromState, 8);
            AppendTag(builder, "ISTYPEOFGOODSGOLD", row.TypeOfGoodsGold, 8);
            builder.AppendLine("        <TRANSPORTDETAILS.LIST>");
            var docDate = row.TransportDocumentDate ?? row.LrBillOfLadingDate;
            if (docDate is DateTime d) AppendTag(builder, "DOCUMENTDATE", d.ToString("yyyyMMdd", CultureInfo.InvariantCulture), 10);
            AppendTag(builder, "TRANSPORTERID", row.TransporterId, 10);
            AppendTag(builder, "DOCUMENTNUMBER", row.TransportDocumentNumber.Length > 0 ? row.TransportDocumentNumber : row.LrBillOfLadingNumber, 10);
            AppendTag(builder, "TRANSPORTERNAME", row.TransporterName, 10);
            AppendTag(builder, "TRANSPORTMODE", row.TransportMode, 10);
            AppendTag(builder, "VEHICLENUMBER", row.EWayVehicleNumber.Length > 0 ? row.EWayVehicleNumber : row.VehicleVesselNumber, 10);
            AppendTag(builder, "VEHICLETYPE", row.VehicleType, 10);
            builder.AppendLine("        </TRANSPORTDETAILS.LIST>");
            builder.AppendLine("      </EWAYBILLDETAILS.LIST>");
        }
    }

    private static void AppendAddressListAt(StringBuilder builder, string listTag, int indent, string itemTag, params string[] lines)
    {
        var values = lines.Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => line.Trim()).ToArray();
        if (values.Length == 0) return;
        builder.Append(' ', indent).Append('<').Append(listTag).AppendLine(">");
        foreach (var value in values) AppendTag(builder, itemTag, value, indent + 2);
        builder.Append(' ', indent).Append("</").Append(listTag).AppendLine(">");
    }

    private static void AppendAddressList(StringBuilder builder, string listTag, params string[] lines)
    {
        var values = lines.Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => line.Trim()).ToArray();
        if (values.Length == 0) return;
        builder.Append("      <").Append(listTag).AppendLine(">");
        foreach (var value in values) AppendTag(builder, "ADDRESS", value, 8);
        builder.Append("      </").Append(listTag).AppendLine(">");
    }

    private static void AppendTag(StringBuilder builder, string tag, string value, int indent = 6)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        builder.Append(' ', indent).Append('<').Append(tag).Append('>')
            .Append(Xml(value.Trim())).Append("</").Append(tag).AppendLine(">");
    }

    private static void AppendInventoryEntry(StringBuilder builder, PreparedInventoryEntry entry)
    {
        var deemedPositive = entry.IsDeemedPositive;
        var signedAmount = entry.IsDeemedPositive ? -Math.Abs(entry.Amount) : Math.Abs(entry.Amount);
        var signedAccounting = entry.IsDeemedPositive ? -Math.Abs(entry.AccountingAmount) : Math.Abs(entry.AccountingAmount);
        builder.AppendLine("      <ALLINVENTORYENTRIES.LIST>");
        builder.Append("        <STOCKITEMNAME>").Append(Xml(entry.ItemName)).AppendLine("</STOCKITEMNAME>");
        builder.Append("        <ISDEEMEDPOSITIVE>").Append(deemedPositive ? "Yes" : "No").AppendLine("</ISDEEMEDPOSITIVE>");
        builder.Append("        <ISLASTDEEMEDPOSITIVE>").Append(deemedPositive ? "Yes" : "No").AppendLine("</ISLASTDEEMEDPOSITIVE>");
        if (entry.Rate != 0m) builder.Append("        <RATE>").Append(entry.Rate.ToString("0.####", CultureInfo.InvariantCulture)).Append('/').Append(Xml(entry.Unit)).AppendLine("</RATE>");
        builder.Append("        <AMOUNT>").Append(signedAmount.ToString("0.00", CultureInfo.InvariantCulture)).AppendLine("</AMOUNT>");
        builder.Append("        <ACTUALQTY>").Append(entry.ActualQuantity.ToString("0.####", CultureInfo.InvariantCulture)).Append(' ').Append(Xml(entry.Unit)).AppendLine("</ACTUALQTY>");
        builder.Append("        <BILLEDQTY>").Append(entry.BilledQuantity.ToString("0.####", CultureInfo.InvariantCulture)).Append(' ').Append(Xml(entry.Unit)).AppendLine("</BILLEDQTY>");
        if (entry.Description.Length > 0)
        {
            builder.AppendLine("        <BASICUSERDESCRIPTION.LIST TYPE=\"String\">");
            AppendTag(builder, "BASICUSERDESCRIPTION", entry.Description, 10);
            builder.AppendLine("        </BASICUSERDESCRIPTION.LIST>");
        }
        if (entry.DiscountPercent != 0m) AppendTag(builder, "DISCOUNT", entry.DiscountPercent.ToString("0.####", CultureInfo.InvariantCulture), 8);
        if (entry.OrderNumber.Length > 0) AppendTag(builder, "ORDERNO", entry.OrderNumber, 8);
        if (entry.TrackingNumber.Length > 0) AppendTag(builder, "TRACKINGNUMBER", entry.TrackingNumber, 8);
        if (entry.Godown.Length > 0 || entry.BatchName.Length > 0 || entry.OrderNumber.Length > 0 ||
            entry.TrackingNumber.Length > 0 || entry.ManufacturingDate is not null || entry.ExpiryDate is not null ||
            entry.BatchAmount != 0m || entry.BatchActualQuantity != 0m || entry.BatchBilledQuantity != 0m)
        {
            builder.AppendLine("        <BATCHALLOCATIONS.LIST>");
            AppendTag(builder, "GODOWNNAME", entry.Godown, 10);
            AppendTag(builder, "BATCHNAME", entry.BatchName, 10);
            AppendTag(builder, "ORDERNO", entry.OrderNumber, 10);
            AppendTag(builder, "TRACKINGNUMBER", entry.TrackingNumber, 10);
            if (entry.ManufacturingDate is DateTime mfg) AppendTag(builder, "MFDON", mfg.ToString("yyyyMMdd", CultureInfo.InvariantCulture), 10);
            if (entry.ExpiryDate is DateTime exp) AppendTag(builder, "EXPIRYPERIOD", exp.ToString("yyyyMMdd", CultureInfo.InvariantCulture), 10);
            if (entry.DiscountPercent != 0m) AppendTag(builder, "DISCOUNT", entry.DiscountPercent.ToString("0.####", CultureInfo.InvariantCulture), 10);
            if (entry.BatchAmount != 0m) builder.Append("          <AMOUNT>").Append((entry.IsDeemedPositive ? -entry.BatchAmount : entry.BatchAmount).ToString("0.00", CultureInfo.InvariantCulture)).AppendLine("</AMOUNT>");
            if (entry.BatchActualQuantity != 0m) builder.Append("          <ACTUALQTY>").Append(entry.BatchActualQuantity.ToString("0.####", CultureInfo.InvariantCulture)).Append(' ').Append(Xml(entry.Unit)).AppendLine("</ACTUALQTY>");
            if (entry.BatchBilledQuantity != 0m) builder.Append("          <BILLEDQTY>").Append(entry.BatchBilledQuantity.ToString("0.####", CultureInfo.InvariantCulture)).Append(' ').Append(Xml(entry.Unit)).AppendLine("</BILLEDQTY>");
            builder.AppendLine("        </BATCHALLOCATIONS.LIST>");
        }
        builder.AppendLine("        <ACCOUNTINGALLOCATIONS.LIST>");
        builder.Append("          <LEDGERNAME>").Append(Xml(entry.AccountingLedger)).AppendLine("</LEDGERNAME>");
        builder.Append("          <ISDEEMEDPOSITIVE>").Append(deemedPositive ? "Yes" : "No").AppendLine("</ISDEEMEDPOSITIVE>");
        builder.Append("          <ISLASTDEEMEDPOSITIVE>").Append(deemedPositive ? "Yes" : "No").AppendLine("</ISLASTDEEMEDPOSITIVE>");
        builder.Append("          <AMOUNT>").Append(signedAccounting.ToString("0.00", CultureInfo.InvariantCulture)).AppendLine("</AMOUNT>");
        if (entry.CostCentre.Length > 0)
        {
            builder.AppendLine("          <CATEGORYALLOCATIONS.LIST>");
            AppendTag(builder, "CATEGORY", "Primary Cost Category", 12);
            AppendTag(builder, "ISDEEMEDPOSITIVE", deemedPositive ? "Yes" : "No", 12);
            builder.AppendLine("            <COSTCENTREALLOCATIONS.LIST>");
            AppendTag(builder, "NAME", entry.CostCentre, 14);
            builder.Append("              <AMOUNT>").Append(signedAccounting.ToString("0.00", CultureInfo.InvariantCulture)).AppendLine("</AMOUNT>");
            builder.AppendLine("            </COSTCENTREALLOCATIONS.LIST>");
            builder.AppendLine("          </CATEGORYALLOCATIONS.LIST>");
        }
        builder.AppendLine("        </ACCOUNTINGALLOCATIONS.LIST>");
        builder.AppendLine("      </ALLINVENTORYENTRIES.LIST>");
    }

    private static void AppendLedgerEntry(StringBuilder builder, VoucherLedgerEntry entry, PreparedVoucher voucher)
    {
        var invoiceLedgerList =
            (voucher.InventoryEntries?.Count ?? 0) > 0 &&
            voucher.ReferenceNumber.Length > 0 &&
            (voucher.SourceType.Equals("Sales Voucher", StringComparison.OrdinalIgnoreCase) ||
             voucher.SourceType.Equals("Purchase Voucher", StringComparison.OrdinalIgnoreCase));
        var isRcmPurchaseLine = voucher.IsReverseCharge && !entry.IsParty &&
                                (entry.PreferredParent.Equals("Purchase Accounts", StringComparison.OrdinalIgnoreCase) ||
                                 entry.LedgerName.Contains("Purchase", StringComparison.OrdinalIgnoreCase));
        var storedNature = voucher.Entries.Any(line => line.LedgerName.Contains("IGST", StringComparison.OrdinalIgnoreCase))
            ? "Interstate Purchase Taxable"
            : "Purchase Taxable";
        TallyExcelParityXmlWriter.AppendLedgerLine(
            builder, entry.LedgerName, entry.Amount, entry.IsDebit, entry.IsParty,
            invoiceLedgerList, voucher.ReferenceNumber, isRcmPurchaseLine, storedNature);
    }

    private static IReadOnlyList<RequiredLedger> BuildRequiredLedgers(
        IEnumerable<PreparedVoucher> vouchers,
        IReadOnlyCollection<MasterRecord> masters)
    {
        var ledgerMasters = masters
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && master.Name.Trim().Length > 0)
            .GroupBy(master => TallyMasterKey(master.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var ledgers = new Dictionary<string, RequiredLedger>(StringComparer.OrdinalIgnoreCase);
        foreach (var voucher in vouchers)
        {
            foreach (var entry in voucher.Entries)
            {
                var generatedRcmParent = ExpectedGeneratedRcmParent(entry.LedgerName);
                var generatedGstAssetParent = GstMasterClassificationPolicy.ExpectedKnownAssetParent(entry.LedgerName);
                var deterministicGeneratedParent = generatedRcmParent.Length > 0
                    ? generatedRcmParent
                    : generatedGstAssetParent;
                var parent = deterministicGeneratedParent.Length > 0
                    ? deterministicGeneratedParent
                    : ResolveRequiredLedgerParent(voucher, entry);
                if (deterministicGeneratedParent.Length == 0 &&
                    ledgerMasters.TryGetValue(TallyMasterKey(entry.LedgerName), out var stored) &&
                    stored.Parent.Trim().Length > 0)
                {
                    parent = stored.Parent.Trim();
                }
                var business = entry.IsParty ? voucher.BusinessRecord : null;
                var state = business?.PartyState?.Trim();
                if (string.IsNullOrWhiteSpace(state)) state = entry.IsParty ? voucher.PlaceOfSupply : string.Empty;
                var incoming = new RequiredLedger(
                    entry.LedgerName,
                    parent,
                    entry.IsParty ? voucher.PartyGstin : string.Empty,
                    state ?? string.Empty,
                    business?.PartyCountry?.Trim() ?? string.Empty,
                    business?.PartyPincode?.Trim() ?? string.Empty,
                    business?.PartyMailingName?.Trim() ?? string.Empty,
                    business?.PartyAddressLine1?.Trim() ?? string.Empty,
                    business?.PartyAddressLine2?.Trim() ?? string.Empty,
                    business?.PartyAddressLine3?.Trim() ?? string.Empty);
                var key = TallyMasterKey(entry.LedgerName);
                if (!ledgers.TryGetValue(key, out var existing) || LedgerInformationScore(incoming) > LedgerInformationScore(existing))
                    ledgers[key] = incoming;
            }
            foreach (var inventory in voucher.InventoryEntries ?? [])
            {
                if (inventory.AccountingLedger.Length == 0) continue;
                var key = TallyMasterKey(inventory.AccountingLedger);
                var parent = ledgerMasters.TryGetValue(key, out var stored) && stored.Parent.Trim().Length > 0
                    ? stored.Parent.Trim()
                    : voucher.SourceType.Equals("Purchase Voucher", StringComparison.OrdinalIgnoreCase)
                        ? "Purchase Accounts"
                        : "Sales Accounts";
                if (!ledgers.ContainsKey(key))
                    ledgers[key] = new RequiredLedger(inventory.AccountingLedger, parent, string.Empty, string.Empty,
                        string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
            }
        }
        return ledgers.Values.ToArray();
    }

    private static int LedgerInformationScore(RequiredLedger ledger) =>
        new[] { ledger.Gstin, ledger.State, ledger.Country, ledger.Pincode, ledger.MailingName,
            ledger.Address1, ledger.Address2, ledger.Address3 }.Count(value => !string.IsNullOrWhiteSpace(value));

    private static string ResolveRequiredLedgerParent(
        PreparedVoucher voucher,
        VoucherLedgerEntry entry)
    {
        if (entry.PreferredParent.Length > 0) return entry.PreferredParent;

        var generatedRcmParent = ExpectedGeneratedRcmParent(entry.LedgerName);
        if (generatedRcmParent.Length > 0) return generatedRcmParent;

        var generatedGstAssetParent = GstMasterClassificationPolicy.ExpectedKnownAssetParent(entry.LedgerName);
        if (generatedGstAssetParent.Length > 0) return generatedGstAssetParent;

        if (entry.IsParty)
        {
            if (voucher.SourceType.Equals("Purchase Voucher", StringComparison.OrdinalIgnoreCase))
                return "Sundry Creditors";
            return voucher.VoucherType is "Purchase" or "Debit Note"
                ? "Sundry Creditors"
                : "Sundry Debtors";
        }
        if (entry.LedgerName.Contains("IGST", StringComparison.OrdinalIgnoreCase) ||
            entry.LedgerName.Contains("CGST", StringComparison.OrdinalIgnoreCase) ||
            entry.LedgerName.Contains("SGST", StringComparison.OrdinalIgnoreCase) ||
            entry.LedgerName.Contains("Cess", StringComparison.OrdinalIgnoreCase))
            return "Duties & Taxes";
        if (entry.LedgerName.Equals("Round Off", StringComparison.OrdinalIgnoreCase))
            return "Indirect Expenses";
        if (voucher.SourceType.Equals("Purchase Voucher", StringComparison.OrdinalIgnoreCase)) return "Purchase Accounts";
        if (voucher.VoucherType is "Purchase" or "Debit Note") return "Purchase Accounts";
        if (voucher.VoucherType is "Sales" or "Credit Note") return "Sales Accounts";
        return "Current Liabilities";
    }

    private static string ExpectedGeneratedRcmParent(string? ledgerName) =>
        PurchaseTreatmentPolicy.ExpectedGeneratedRcmParent(ledgerName);

    private static bool IsGeneratedRcmClassificationLedger(string? ledgerName) =>
        PurchaseTreatmentPolicy.IsGeneratedRcmClassificationLedger(ledgerName);

    private static void AppendGroupMaster(StringBuilder builder, string name, string parent)
    {
        var billWise = IsBillWiseGroup(name, parent);
        var costCentres = IsCostCentreGroup(name, parent);
        var revenue = IsRevenueGroup(name, parent);
        var affectsGrossProfit = AffectsGrossProfitGroup(name, parent);
        var deemedPositive = IsDeemedPositiveGroup(name, parent);
        var trackNegative = TracksNegativeGroup(name, parent);
        builder.AppendLine("  <TALLYMESSAGE xmlns:UDF=\"TallyUDF\">");
        builder.Append("    <GROUP NAME=\"").Append(Xml(name)).AppendLine("\" RESERVEDNAME=\"\" ACTION=\"Create\">");
        AppendTag(builder, "PARENT", parent, 6);
        AppendTag(builder, "BASICGROUPISCALCULABLE", revenue ? "Yes" : "No", 6);
        AppendTag(builder, "ISBILLWISEON", billWise ? "Yes" : "No", 6);
        AppendTag(builder, "ISCOSTCENTRESON", costCentres ? "Yes" : "No", 6);
        AppendTag(builder, "ISSUBLEDGER", billWise ? "Yes" : "No", 6);
        AppendTag(builder, "ISREVENUE", revenue ? "Yes" : "No", 6);
        AppendTag(builder, "AFFECTSGROSSPROFIT", affectsGrossProfit ? "Yes" : "No", 6);
        AppendTag(builder, "ISDEEMEDPOSITIVE", deemedPositive ? "Yes" : "No", 6);
        AppendTag(builder, "TRACKNEGATIVEBALANCES", trackNegative ? "Yes" : "No", 6);
        builder.AppendLine("    </GROUP>");
        builder.AppendLine("  </TALLYMESSAGE>");
    }

    private static void AppendLedgerMaster(
        StringBuilder builder,
        RequiredLedger ledger,
        BankAccountRecord? bank,
        DateTime applicableFrom,
        string action)
    {
        TallyExcelParityXmlWriter.AppendLedgerMaster(builder, new TallyExcelLedgerMaster(
            ledger.Name, ledger.Parent, applicableFrom, action, ledger.Gstin, ledger.State, ledger.Country,
            ledger.Pincode, ledger.MailingName, ledger.Address1, ledger.Address2, ledger.Address3,
            IsBank: bank is not null, BankName: bank?.BankName ?? string.Empty, BankBranchName: bank?.BranchName ?? string.Empty,
            AccountNumber: bank?.AccountNumber ?? string.Empty, IfscCode: bank?.IfscCode ?? string.Empty,
            AccountType: bank?.AccountType ?? string.Empty, OdLimit: bank?.OdLimit, DrawingPower: bank?.DrawingPower));
    }

    private static void AppendGeneratedLedgerParentAlter(
        StringBuilder builder,
        RequiredLedger ledger)
    {
        builder.AppendLine("  <TALLYMESSAGE xmlns:UDF=\"TallyUDF\">");
        builder.Append("    <LEDGER NAME=\"")
            .Append(Xml(ledger.Name))
            .AppendLine("\" RESERVEDNAME=\"\" ACTION=\"Alter\">");
        AppendTag(builder, "NAME", ledger.Name, 6);
        AppendTag(builder, "PARENT", ledger.Parent, 6);
        builder.AppendLine("    </LEDGER>");
        builder.AppendLine("  </TALLYMESSAGE>");
    }

    private static bool HasLedgerExtraData(RequiredLedger ledger, BankAccountRecord? bank) =>
        LedgerInformationScore(ledger) > 0 || HasBankMetadata(bank);

    private static bool HasBankMetadata(BankAccountRecord? bank) => bank is not null &&
        (bank.BankName.Trim().Length > 0 || bank.BranchName.Trim().Length > 0 || bank.AccountNumber.Trim().Length > 0 ||
         bank.IfscCode.Trim().Length > 0 || bank.AccountType.Trim().Length > 0 || bank.OdLimit.HasValue || bank.DrawingPower.HasValue);

    private static void AppendBankMasterTags(StringBuilder builder, BankAccountRecord? bank, int indent)
    {
        if (!HasBankMetadata(bank)) return;
        AppendTag(builder, "ISBANKINGLEDGER", "Yes", indent);
        AppendTag(builder, "BANKNAME", bank!.BankName, indent);
        AppendTag(builder, "BANKBRANCHNAME", bank.BranchName, indent);
        AppendTag(builder, "ACCOUNTNUMBER", bank.AccountNumber, indent);
        AppendTag(builder, "IFSCODE", bank.IfscCode, indent);
        AppendTag(builder, "ACCOUNTTYPE", bank.AccountType, indent);
        if (bank.OdLimit.HasValue) AppendTag(builder, "ODLIMIT", bank.OdLimit.Value.ToString("0.##", CultureInfo.InvariantCulture), indent);
        if (bank.DrawingPower.HasValue) AppendTag(builder, "DRAWINGPOWER", bank.DrawingPower.Value.ToString("0.##", CultureInfo.InvariantCulture), indent);
    }

    private static void AppendPartyMasterLists(
        StringBuilder builder,
        bool partyLedger,
        string gstin,
        string mailingName,
        string country,
        DateTime applicableFrom,
        int indent)
    {
        if (!partyLedger) return;
        var spaces = new string(' ', indent);
        builder.Append(spaces).AppendLine("<LEDGSTREGDETAILS.LIST>");
        AppendTag(builder, "APPLICABLEFROM", applicableFrom.ToString("yyyyMMdd", CultureInfo.InvariantCulture), indent + 2);
        AppendTag(builder, "GSTREGISTRATIONTYPE", gstin.Length == 15 ? "Regular" : "Unregistered/Consumer", indent + 2);
        AppendTag(builder, "ISOTHTERRITORYASSESSEE", "No", indent + 2);
        AppendTag(builder, "CONSIDERPURCHASEFOREXPORT", "No", indent + 2);
        AppendTag(builder, "ISTRANSPORTER", "No", indent + 2);
        AppendTag(builder, "ISCOMMONPARTY", "No", indent + 2);
        builder.Append(spaces).AppendLine("</LEDGSTREGDETAILS.LIST>");
        if (mailingName.Length == 0 && country.Length == 0) return;
        builder.Append(spaces).AppendLine("<LEDMAILINGDETAILS.LIST>");
        AppendTag(builder, "APPLICABLEFROM", applicableFrom.ToString("yyyyMMdd", CultureInfo.InvariantCulture), indent + 2);
        AppendTag(builder, "MAILINGNAME", mailingName, indent + 2);
        AppendTag(builder, "COUNTRY", country, indent + 2);
        builder.Append(spaces).AppendLine("</LEDMAILINGDETAILS.LIST>");
    }

    private static bool IsPartyLedgerGroup(string groupName) =>
        groupName.Trim().Equals("Sundry Debtors", StringComparison.OrdinalIgnoreCase) ||
        groupName.Trim().Equals("Sundry Creditors", StringComparison.OrdinalIgnoreCase);

    private static bool IsBillWiseGroup(string groupName, string parentName)
    {
        var key = $"{groupName} {parentName}";
        return key.Contains("SUNDRY DEBTOR", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("SUNDRY CREDITOR", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCostCentreGroup(string groupName, string parentName)
    {
        var key = $"{groupName} {parentName}";
        return key.Contains("SALES", StringComparison.OrdinalIgnoreCase) || key.Contains("PURCHASE", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("EXPENSE", StringComparison.OrdinalIgnoreCase) || key.Contains("INCOME", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRevenueGroup(string groupName, string parentName) => IsCostCentreGroup(groupName, parentName);
    private static bool AffectsGrossProfitGroup(string groupName, string parentName)
    {
        var key = $"{groupName} {parentName}";
        return key.Contains("SALES", StringComparison.OrdinalIgnoreCase) || key.Contains("PURCHASE", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("DIRECT EXPENSE", StringComparison.OrdinalIgnoreCase) || key.Contains("DIRECT INCOME", StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsDeemedPositiveGroup(string groupName, string parentName)
    {
        var key = $"{groupName} {parentName}";
        return !(key.Contains("LIABIL", StringComparison.OrdinalIgnoreCase) || key.Contains("CREDITOR", StringComparison.OrdinalIgnoreCase) ||
                 key.Contains("SALES", StringComparison.OrdinalIgnoreCase) || key.Contains("INCOME", StringComparison.OrdinalIgnoreCase) ||
                 key.Contains("CAPITAL", StringComparison.OrdinalIgnoreCase));
    }
    private static bool TracksNegativeGroup(string groupName, string parentName)
    {
        var key = $"{groupName} {parentName}";
        return key.Contains("BANK", StringComparison.OrdinalIgnoreCase) || key.Contains("CASH", StringComparison.OrdinalIgnoreCase);
    }

    private static string TallyMasterKey(string value)
    {
        // Tally master uniqueness ignores spacing/punctuation that are not relevant
        // to the name. Use the same identity rule as the strict ERP 9 Bank preflight
        // so an existing Tally master is skipped rather than CREATE/ALTER duplicated.
        var normalized = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormKC);
        return new string(normalized
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    private static string StateFromGstin(string gstin)
    {
        if (gstin.Length < 2) return string.Empty;
        return gstin[..2] switch
        {
            "01" => "Jammu & Kashmir", "02" => "Himachal Pradesh", "03" => "Punjab", "04" => "Chandigarh",
            "05" => "Uttarakhand", "06" => "Haryana", "07" => "Delhi", "08" => "Rajasthan", "09" => "Uttar Pradesh",
            "10" => "Bihar", "11" => "Sikkim", "12" => "Arunachal Pradesh", "13" => "Nagaland", "14" => "Manipur",
            "15" => "Mizoram", "16" => "Tripura", "17" => "Meghalaya", "18" => "Assam", "19" => "West Bengal",
            "20" => "Jharkhand", "21" => "Odisha", "22" => "Chhattisgarh", "23" => "Madhya Pradesh", "24" => "Gujarat",
            "26" => "Dadra & Nagar Haveli and Daman & Diu", "27" => "Maharashtra", "29" => "Karnataka", "30" => "Goa",
            "31" => "Lakshadweep", "32" => "Kerala", "33" => "Tamil Nadu", "34" => "Puducherry", "35" => "Andaman & Nicobar Islands",
            "36" => "Telangana", "37" => "Andhra Pradesh", "38" => "Ladakh", "97" => "Other Territory", "99" => "Centre Jurisdiction",
            _ => string.Empty
        };
    }

    private static string BuildSummary(TallyVoucherExportResult result, bool imported)
    {
        var action = imported ? $"Imported {result.Imported:N0} accounting voucher(s)." :
            "No accounting voucher was imported.";
        return $"{action} Selected {result.Selected:N0}; ready {result.Ready:N0} (CREATE {result.ReadyCreates:N0}, ALTER {result.ReadyAlters:N0}); live Tally exact duplicates {result.ExactDuplicatesSkipped:N0}; " +
               $"repeated source candidates {result.RepeatedSourceCandidatesSkipped:N0}; " +
               $"deleted in Tally {result.DeletedInTallySkipped:N0}; rebind-blocked {result.GenerationRebindBlocked:N0}; " +
               $"conflicts {result.ConflictsBlocked:N0}; missing details {result.MissingLedgers:N0}; unsupported {result.Unsupported:N0}; " +
               $"Tally created {result.TallyCreated:N0}, altered {result.TallyAltered:N0}, ignored {result.TallyIgnored:N0}, errors {result.TallyErrors:N0}; " +
               $"live-unverified {result.UnverifiedAfterImport:N0}.";
    }

    private static string SaveResponseLog(
        ClientProfile client, DateTime fromDate, DateTime toDate, string request, string response, string stage = "accounting")
    {
        var folder = Path.Combine(ApplicationDataPaths.RootFolder, "Logs", "Tally", DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        var safeClient = string.Concat(client.ClientCode.Where(character => !Path.GetInvalidFileNameChars().Contains(character)));
        if (safeClient.Length == 0) safeClient = client.Id.ToString(CultureInfo.InvariantCulture);
        var safeStage = string.Concat((stage ?? "accounting").Where(character => char.IsLetterOrDigit(character) || character is '-' or '_'));
        if (safeStage.Length == 0) safeStage = "accounting";
        var stem = $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeClient}-accounting-{safeStage}-{fromDate:yyyyMMdd}-{toDate:yyyyMMdd}";
        File.WriteAllText(Path.Combine(folder, stem + "-request.xml"), request, Encoding.UTF8);
        var responsePath = Path.Combine(folder, stem + "-response.xml");
        File.WriteAllText(responsePath, response, Encoding.UTF8);
        // Fix37: retain only the latest two completed accounting protocol pairs.
        var responses = Directory.GetFiles(folder, $"*-{safeClient}-accounting-*-response.xml")
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

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
    private static string Xml(string value) => SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;

    private sealed record PreparedVoucher(
        string SourceType, string SourceKey, string ContentHash, DateTime Date, DateTime? ReferenceDate,
        string VoucherType, string VoucherNumber, string ReferenceNumber, string PartyLedger,
        string PartyGstin, string PlaceOfSupply, string Narration, IReadOnlyList<VoucherLedgerEntry> Entries,
        IReadOnlyList<PreparedInventoryEntry>? InventoryEntries = null,
        SalesImportRecord? BusinessRecord = null,
        bool IsReverseCharge = false,
        bool HasRcmAdjustment = false,
        string ExistingTallyMasterId = "",
        string SourceAnchor = "");
    private sealed record PreparedInventoryEntry(
        string ItemName, string Unit, decimal ActualQuantity, decimal BilledQuantity, decimal Rate, decimal Amount,
        string AccountingLedger, decimal AccountingAmount, string Godown, string BatchName,
        decimal BatchActualQuantity, decimal BatchBilledQuantity, decimal BatchAmount,
        decimal DiscountPercent, decimal DiscountAmount, string OrderNumber, string TrackingNumber,
        DateTime? ManufacturingDate, DateTime? ExpiryDate, string Description, string CostCentre, bool IsDeemedPositive,
        long StockItemPersistentId, string TallyStockMasterId, string RateUnit);
    private sealed record RequiredLedger(
        string Name, string Parent, string Gstin, string State, string Country, string Pincode, string MailingName,
        string Address1, string Address2, string Address3);
    private sealed record VoucherLedgerEntry(
        string LedgerName, decimal Amount, bool IsDebit, bool IsParty,
        string PreferredParent = "");
}
