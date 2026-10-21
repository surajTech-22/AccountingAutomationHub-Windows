using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Masters;

namespace AccountingAutomationHub.Infrastructure.Tally;

public sealed class TallyLiveMasterSynchronizationService
{
    private static readonly HashSet<string> StockTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Stock Item", "Stock Group", "Stock Category", "Unit", "Godown",
        "Cost Category", "Cost Centre", "Tax Classification", "TaxUnit"
    };

    private readonly SqliteMasterRepository _masterRepository;
    private readonly IStockMasterRepository _stockRepository;
    private readonly TallyMasterImportService _importService;

    public TallyLiveMasterSynchronizationService(
        SqliteMasterRepository masterRepository,
        IStockMasterRepository stockRepository,
        TallyMasterImportService importService)
    {
        _masterRepository = masterRepository;
        _stockRepository = stockRepository;
        _importService = importService;
    }

    public TallyMasterSyncResult Synchronize(
        string xmlContent,
        long clientId,
        string financialYear,
        string companyName,
        IReadOnlyDictionary<string, string>? liveLedgerParents = null,
        IReadOnlySet<string>? liveGroupNames = null,
        bool ledgerGroupOnlyAuthority = false)
    {
        var parsed = _importService.ParseXmlContent(
            xmlContent,
            clientId,
            financialYear,
            companyName,
            "Live Tally");

        var parsedAll = parsed.Records.Select(record =>
        {
            record.ClientId = clientId;
            record.FinancialYear = financialYear;
            record.Company = companyName;
            record.Source = "Live Tally";
            return record;
        }).ToArray();

        // The direct Ledger/Group collections are the existence authority. Some ERP 9
        // "all masters" exports can echo stale/object names even when the Ledger is no
        // longer present in the company's actual Ledger collection. Build the accounting
        // identity from the direct live collections whenever they are available.
        var parsedGroups = parsedAll
            .Where(record => record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
            .GroupBy(record => TallyMasterIdentity.Key(record.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        // Preserve the full Tally Ledger payload (especially OPENINGBALANCE) while the
        // direct Ledger collection remains the existence/parent authority.  Earlier the
        // synchronization rebuilt each authoritative Ledger from Name + Parent only,
        // which silently discarded the opening balance already parsed from ExportMasters.
        var parsedLedgers = parsedAll
            .Where(record => record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
            .Where(record => !string.IsNullOrWhiteSpace(record.Name))
            .GroupBy(record => TallyMasterIdentity.Key(record.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(record => record.OpeningBalance.HasValue)
                    .ThenBy(record => record.PersistentId)
                    .First(),
                StringComparer.OrdinalIgnoreCase);

        var parsedLedgerCount = parsedAll.Count(record =>
            record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase));
        var parsedGroupCount = parsedAll.Count(record =>
            record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase));
        if (liveLedgerParents is not null && liveLedgerParents.Count == 0 && parsedLedgerCount > 0)
            throw new InvalidOperationException(
                "Tally returned an empty direct Ledger collection while the full Master export still contains Ledgers. " +
                "Live Master reconciliation was stopped without archiving any Tally Master.");
        if (liveGroupNames is not null && liveGroupNames.Count == 0 && parsedGroupCount > 0)
            throw new InvalidOperationException(
                "Tally returned an empty direct Group collection while the full Master export still contains Groups. " +
                "Live Master reconciliation was stopped without archiving any Tally Master.");

        var parsedLedgerAuthority = parsedAll
            .Where(record => record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                             record.TallyMasterId.Trim().Length > 0)
            .GroupBy(record => record.TallyMasterId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(record => record.TallyAlterId).First())
            .ToArray();
        var parsedLedgerIdentityComplete = parsedLedgerCount > 0 &&
            parsedLedgerAuthority.Length == parsedLedgerCount &&
            parsedLedgerAuthority.All(record => record.TallyGuid.Trim().Length > 0);

        // Fix72: a complete Tally MASTERID/GUID response is the Ledger existence and
        // identity authority itself. Do not rebuild that authority through a name-keyed
        // dictionary: Tally can contain two different MASTERIDs with the same Ledger
        // display name, and a name-keyed collection necessarily drops one of them.
        // Direct Name/Parent collections remain only as compatibility fallback for an
        // older response that genuinely lacks stable identity.
        var authoritativeLedgers = parsedLedgerIdentityComplete
            ? parsedLedgerAuthority
            : liveLedgerParents is null
                ? parsedAll.Where(record => record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)).ToArray()
                : liveLedgerParents
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                    .Select(pair =>
                    {
                        var ledgerName = pair.Key.Trim();
                        parsedLedgers.TryGetValue(TallyMasterIdentity.Key(ledgerName), out var parsedLedger);
                        return new MasterRecord
                        {
                            ClientId = clientId,
                            FinancialYear = financialYear,
                            Company = companyName,
                            Name = ledgerName,
                            Parent = string.IsNullOrWhiteSpace(pair.Value) ? "Primary" : pair.Value.Trim(),
                            Type = "Ledger",
                            Source = "Live Tally",
                            OpeningBalance = parsedLedger?.OpeningBalance,
                            TallyMasterId = parsedLedger?.TallyMasterId ?? string.Empty,
                            TallyGuid = parsedLedger?.TallyGuid ?? string.Empty,
                            TallyAlterId = parsedLedger?.TallyAlterId ?? 0L
                        };
                    })
                    .ToArray();

        var parsedGroupAuthority = parsedAll
            .Where(record => record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) &&
                             record.TallyMasterId.Trim().Length > 0)
            .GroupBy(record => record.TallyMasterId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(record => record.TallyAlterId).First())
            .ToArray();
        var parsedGroupIdentityComplete = parsedGroupCount > 0 &&
            parsedGroupAuthority.Length == parsedGroupCount &&
            parsedGroupAuthority.All(record => record.TallyGuid.Trim().Length > 0);

        var authoritativeGroups = parsedGroupIdentityComplete
            ? parsedGroupAuthority
            : liveGroupNames is null
                ? parsedAll.Where(record => record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase)).ToArray()
                : liveGroupNames
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name =>
                    {
                        var key = TallyMasterIdentity.Key(name);
                        parsedGroups.TryGetValue(key, out var parsedGroup);
                        return new MasterRecord
                        {
                            ClientId = clientId,
                            FinancialYear = financialYear,
                            Company = companyName,
                            Name = name.Trim(),
                            Parent = parsedGroup is null || string.IsNullOrWhiteSpace(parsedGroup.Parent)
                                ? "Primary"
                                : parsedGroup.Parent.Trim(),
                            Type = "Group",
                            Source = "Live Tally",
                            TallyMasterId = parsedGroup?.TallyMasterId ?? string.Empty,
                            TallyGuid = parsedGroup?.TallyGuid ?? string.Empty,
                            TallyAlterId = parsedGroup?.TallyAlterId ?? 0L
                        };
                    })
                    .ToArray();

        var all = parsedAll
            .Where(record => !record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                             !record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
            .Concat(authoritativeGroups)
            .Concat(authoritativeLedgers)
            .ToArray();

        var accounting = all.Where(record => !StockTypes.Contains(record.Type)).ToArray();
        var incomingKeys = accounting.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = _masterRepository.Get(clientId, financialYear, includeArchived: true);
        var archived = 0;
        foreach (var record in existing.Where(record =>
                     MasterAuthority.IsTallySource(record.Source) &&
                     (record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) ||
                      record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) ||
                      (!ledgerGroupOnlyAuthority && record.Source.Equals("Live Tally", StringComparison.OrdinalIgnoreCase))) &&
                     !record.IsUserEdited && !record.IsArchived &&
                     !StockTypes.Contains(record.Type) &&
                     !incomingKeys.Contains(Key(record))))
        {
            if (_masterRepository.TryArchive(record.PersistentId, archived: true))
                archived++;
        }

        var accountingUpserted = _masterRepository.Upsert(clientId, financialYear, accounting);
        // Fix69 second authority layer: after the normal upsert, prove that every live
        // Tally Ledger MASTERID resolves to exactly one AAH row and that mutable Ledger
        // fields (name/parent/GUID/ALTERID) match the same MASTERID. A Tally-side rename
        // therefore changes AAH immediately instead of being treated as a new/name-only
        // Master. This check is local; it sends no additional request to Tally.
        _masterRepository.VerifyLiveLedgerMasterIdAuthority(clientId, financialYear, authoritativeLedgers);
        _masterRepository.VerifyLiveGroupMasterIdAuthority(clientId, financialYear, authoritativeGroups);
        // Fix80: if an older alias/stale-sync path archived a Master that still has an
        // opening balance, AAH usage, Bank Account binding or live Tally Voucher usage,
        // restore it immediately in this same Master-authority cycle. MASTERID-backed
        // restores also reassert Source=Live Tally. Explicit unresolved Merge FROM rows
        // are intentionally excluded inside the repository barrier.
        _masterRepository.ReactivateProtectedArchivedMasters(clientId, financialYear);
        var stockItems = all.Where(record => record.Type.Equals("Stock Item", StringComparison.OrdinalIgnoreCase))
            .Select(record => new StockItemRecord
            {
                ClientId=clientId,FinancialYear=financialYear,Name=record.Name,StockGroup=record.Parent,
                StockCategory=record.StockCategoryName,BaseUnit=record.BaseUnit,AdditionalUnit=record.AdditionalUnit,PartNumberSku=record.PartNumberSku,
                TallyMasterId=record.TallyMasterId,TallyGuid=record.TallyGuid,TallyAlterId=record.TallyAlterId,
                HsnSac=record.HsnSac,GstRate=record.GstRate,GstTypeOfSupply=record.GstTypeOfSupply,
                BatchWise=record.BatchWise,Perishable=record.Perishable,HasManufacturingDate=record.HasManufacturingDate,
                CostingMethod=record.CostingMethod,ValuationMethod=record.ValuationMethod,Company=companyName,Source="Live Tally"
            }).ToArray();
        var units = all.Where(record => record.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase))
            .Select(record => new StockUnitRecord
            {
                ClientId=clientId,FinancialYear=financialYear,Name=record.Name,FormalName=record.FormalName,DecimalPlaces=Math.Clamp(record.DecimalPlaces,0,4),
                ReportingUqc=record.ReportingUqc,Conversion=record.Conversion,Company=companyName,Source="Live Tally"
            }).ToArray();
        var simple = all.Where(record => StockTypes.Contains(record.Type) &&
                                         !record.Type.Equals("Stock Item",StringComparison.OrdinalIgnoreCase) &&
                                         !record.Type.Equals("Unit",StringComparison.OrdinalIgnoreCase))
            .Select(record => new StockSimpleMasterRecord
            {
                ClientId=clientId,FinancialYear=financialYear,Type=record.Type,Name=record.Name,Parent=record.Parent,
                Company=companyName,Source="Live Tally"
            }).ToArray();
        var stockItemUpserted = _stockRepository.UpsertStockItems(clientId,financialYear,stockItems);
        var unitUpserted = _stockRepository.UpsertUnits(clientId,financialYear,units);
        var simpleUpserted = _stockRepository.UpsertSimpleMasters(clientId,financialYear,simple);
        var stockProfileUpserted = SynchronizeTallyStockItemUnitProfiles(all, clientId, financialYear);
        var stockUpserted = stockItemUpserted + unitUpserted + simpleUpserted + stockProfileUpserted;

        return new TallyMasterSyncResult
        {
            ParsedObjects = parsed.ParsedObjects,
            UniqueObjects = parsed.ImportedObjects,
            DuplicateObjects = parsed.DuplicateObjects,
            ConflictingObjects = parsed.ConflictingObjects,
            UpsertedObjects = accountingUpserted + stockUpserted,
            ArchivedObjects = archived,
            GroupCount = Count(all, "Group"),
            LedgerCount = Count(all, "Ledger"),
            VoucherTypeCount = Count(all, "Voucher Type"),
            StockItemCount = Count(all, "Stock Item"),
            UnitCount = Count(all, "Unit"),
            GodownCount = Count(all, "Godown"),
            Status = $"Synchronized live Tally masters with direct Ledger/Group authority: {authoritativeLedgers.Length:N0} Ledgers, " +
                     $"{authoritativeGroups.Length:N0} Groups; {parsed.DuplicateObjects:N0} duplicate object(s), " +
                     $"{parsed.ConflictingObjects:N0} conflict(s), {archived:N0} stale Tally accounting master(s) archived."
        };
    }

    // Fix131 R3 diagnostic/manual authority path: synchronize ONLY inventory Master
    // families from one explicit Tally Master response. This deliberately does not
    // archive or mutate Ledger/Group accounting authority and does not touch vouchers.
    // It gives Stock Master a direct way to prove whether Tally returned Stock Items
    // and whether AAH persisted them by MASTERID.
    public TallyMasterSyncResult SynchronizeStockOnly(
        string xmlContent,
        long clientId,
        string financialYear,
        string companyName)
    {
        var parsed = _importService.ParseXmlContent(
            xmlContent, clientId, financialYear, companyName, "Live Tally");
        var all = parsed.Records.Select(record =>
        {
            record.ClientId = clientId;
            record.FinancialYear = financialYear;
            record.Company = companyName;
            record.Source = "Live Tally";
            record.IsArchived = false;
            return record;
        }).Where(record => StockTypes.Contains(record.Type)).ToArray();

        var stockItems = all.Where(record => record.Type.Equals("Stock Item", StringComparison.OrdinalIgnoreCase))
            .Select(record => new StockItemRecord
            {
                ClientId=clientId,FinancialYear=financialYear,Name=record.Name,StockGroup=record.Parent,
                StockCategory=record.StockCategoryName,BaseUnit=record.BaseUnit,AdditionalUnit=record.AdditionalUnit,PartNumberSku=record.PartNumberSku,
                TallyMasterId=record.TallyMasterId,TallyGuid=record.TallyGuid,TallyAlterId=record.TallyAlterId,
                HsnSac=record.HsnSac,GstRate=record.GstRate,GstTypeOfSupply=record.GstTypeOfSupply,
                BatchWise=record.BatchWise,Perishable=record.Perishable,HasManufacturingDate=record.HasManufacturingDate,
                CostingMethod=record.CostingMethod,ValuationMethod=record.ValuationMethod,Company=companyName,Source="Live Tally"
            }).ToArray();
        var units = all.Where(record => record.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase))
            .Select(record => new StockUnitRecord
            {
                ClientId=clientId,FinancialYear=financialYear,Name=record.Name,FormalName=record.FormalName,DecimalPlaces=Math.Clamp(record.DecimalPlaces,0,4),
                ReportingUqc=record.ReportingUqc,Conversion=record.Conversion,Company=companyName,Source="Live Tally"
            }).ToArray();
        var simple = all.Where(record => !record.Type.Equals("Stock Item",StringComparison.OrdinalIgnoreCase) &&
                                         !record.Type.Equals("Unit",StringComparison.OrdinalIgnoreCase))
            .Select(record => new StockSimpleMasterRecord
            {
                ClientId=clientId,FinancialYear=financialYear,Type=record.Type,Name=record.Name,Parent=record.Parent,
                Company=companyName,Source="Live Tally"
            }).ToArray();

        var stockItemUpserted = _stockRepository.UpsertStockItems(clientId,financialYear,stockItems);
        var unitUpserted = _stockRepository.UpsertUnits(clientId,financialYear,units);
        var simpleUpserted = _stockRepository.UpsertSimpleMasters(clientId,financialYear,simple);
        var profileUpserted = SynchronizeTallyStockItemUnitProfiles(all, clientId, financialYear);

        return new TallyMasterSyncResult
        {
            ParsedObjects=parsed.ParsedObjects, UniqueObjects=parsed.ImportedObjects,
            DuplicateObjects=parsed.DuplicateObjects, ConflictingObjects=parsed.ConflictingObjects,
            UpsertedObjects=stockItemUpserted+unitUpserted+simpleUpserted+profileUpserted, ArchivedObjects=0,
            GroupCount=0, LedgerCount=0, VoucherTypeCount=0,
            StockItemCount=Count(all,"Stock Item"), UnitCount=Count(all,"Unit"), GodownCount=Count(all,"Godown"),
            Status=$"Stock-only Tally synchronization returned {Count(all,"Stock Item"):N0} Stock Item(s), {Count(all,"Unit"):N0} Unit(s) and {Count(all,"Godown"):N0} Godown(s); no Ledger/Group/Voucher authority was changed."
        };
    }

    public TallyMasterSyncResult SynchronizeDelta(
        string xmlContent, long clientId, string financialYear, string companyName)
    {
        var parsed = _importService.ParseXmlContent(
            xmlContent, clientId, financialYear, companyName, "Live Tally");
        var all = parsed.Records.Select(record =>
        {
            record.ClientId = clientId;
            record.FinancialYear = financialYear;
            record.Company = companyName;
            record.Source = "Live Tally";
            record.IsArchived = false;
            return record;
        }).ToArray();

        // Fix37 incremental authority: delta payloads contain only changed masters.
        // Upsert those objects in-place and never archive objects absent from the delta.
        var accounting = all.Where(record => !StockTypes.Contains(record.Type)).ToArray();
        var accountingUpserted = accounting.Length == 0 ? 0 : _masterRepository.Upsert(clientId, financialYear, accounting);
        // Fix69 applies the same MASTERID-first mutation check to ALTERID deltas. A
        // changed Ledger object is matched by stable Tally MASTERID first, then its new
        // display name/parent/GUID/ALTERID is verified against the saved AAH row.
        _masterRepository.VerifyLiveLedgerMasterIdAuthority(
            clientId, financialYear, accounting.Where(record => record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)));
        _masterRepository.VerifyLiveGroupMasterIdAuthority(
            clientId, financialYear, accounting.Where(record => record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase)));
        var stockItems = all.Where(record => record.Type.Equals("Stock Item", StringComparison.OrdinalIgnoreCase))
            .Select(record => new StockItemRecord
            {
                ClientId=clientId,FinancialYear=financialYear,Name=record.Name,StockGroup=record.Parent,
                StockCategory=record.StockCategoryName,BaseUnit=record.BaseUnit,AdditionalUnit=record.AdditionalUnit,PartNumberSku=record.PartNumberSku,
                TallyMasterId=record.TallyMasterId,TallyGuid=record.TallyGuid,TallyAlterId=record.TallyAlterId,
                HsnSac=record.HsnSac,GstRate=record.GstRate,GstTypeOfSupply=record.GstTypeOfSupply,
                BatchWise=record.BatchWise,Perishable=record.Perishable,HasManufacturingDate=record.HasManufacturingDate,
                CostingMethod=record.CostingMethod,ValuationMethod=record.ValuationMethod,Company=companyName,Source="Live Tally"
            }).ToArray();
        var units = all.Where(record => record.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase))
            .Select(record => new StockUnitRecord
            {
                ClientId=clientId,FinancialYear=financialYear,Name=record.Name,FormalName=record.FormalName,DecimalPlaces=Math.Clamp(record.DecimalPlaces,0,4),
                ReportingUqc=record.ReportingUqc,Conversion=record.Conversion,Company=companyName,Source="Live Tally"
            }).ToArray();
        var simple = all.Where(record => StockTypes.Contains(record.Type) &&
                                         !record.Type.Equals("Stock Item",StringComparison.OrdinalIgnoreCase) &&
                                         !record.Type.Equals("Unit",StringComparison.OrdinalIgnoreCase))
            .Select(record => new StockSimpleMasterRecord
            {
                ClientId=clientId,FinancialYear=financialYear,Type=record.Type,Name=record.Name,Parent=record.Parent,
                Company=companyName,Source="Live Tally"
            }).ToArray();
        var stockItemUpserted = _stockRepository.UpsertStockItems(clientId,financialYear,stockItems);
        var unitUpserted = _stockRepository.UpsertUnits(clientId,financialYear,units);
        var simpleUpserted = _stockRepository.UpsertSimpleMasters(clientId,financialYear,simple);
        var stockProfileUpserted = SynchronizeTallyStockItemUnitProfiles(all, clientId, financialYear);
        var stockUpserted = stockItemUpserted + unitUpserted + simpleUpserted + stockProfileUpserted;
        return new TallyMasterSyncResult
        {
            ParsedObjects=parsed.ParsedObjects, UniqueObjects=parsed.ImportedObjects,
            DuplicateObjects=parsed.DuplicateObjects, ConflictingObjects=parsed.ConflictingObjects,
            UpsertedObjects=accountingUpserted+stockUpserted, ArchivedObjects=0,
            GroupCount=Count(all,"Group"), LedgerCount=Count(all,"Ledger"),
            VoucherTypeCount=Count(all,"Voucher Type"), StockItemCount=Count(all,"Stock Item"),
            UnitCount=Count(all,"Unit"), GodownCount=Count(all,"Godown"),
            Status=$"Merged {all.Length:N0} changed Tally master object(s) without archiving untouched authority."
        };
    }

    private int SynchronizeTallyStockItemUnitProfiles(IEnumerable<MasterRecord> records,long clientId,string financialYear)
    {
        // Fix117: Tally Stock Item alternate-unit relationships become item-owned AAH
        // Unit Profiles. Tally documents alternate conversion as item-specific; never
        // flatten it into the global AAH deterministic unit catalogue.
        var sourceItems=records.Where(record=>record.Type.Equals("Stock Item",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(sourceItems.Length==0) return 0;
        var saved=_stockRepository.GetStockItems(clientId,financialYear,includeArchived:true).ToArray();
        var byMasterId=saved.Where(item=>item.TallyMasterId.Trim().Length>0)
            .GroupBy(item=>item.TallyMasterId.Trim(),StringComparer.OrdinalIgnoreCase)
            .Where(group=>group.Count()==1).ToDictionary(group=>group.Key,group=>group.First(),StringComparer.OrdinalIgnoreCase);
        var byName=saved.GroupBy(item=>item.Name.Trim(),StringComparer.OrdinalIgnoreCase)
            .Where(group=>group.Key.Length>0&&group.Count()==1).ToDictionary(group=>group.Key,group=>group.First(),StringComparer.OrdinalIgnoreCase);
        var count=0;
        foreach(var source in sourceItems)
        {
            StockItemRecord? item=null;
            if(source.TallyMasterId.Trim().Length>0) byMasterId.TryGetValue(source.TallyMasterId.Trim(),out item);
            if(item is null) byName.TryGetValue(source.Name.Trim(),out item);
            if(item is null||item.PersistentId<=0||item.BaseUnit.Trim().Length==0) continue;
            try
            {
                _stockRepository.SaveStockItemUnitProfile(new StockItemUnitProfileRecord
                {
                    ClientId=clientId,FinancialYear=financialYear,StockItemId=item.PersistentId,StockItemName=item.Name,
                    UnitCode=item.BaseUnit,FactorToBase=1m,IsAllowed=true,Source="Live Tally Base Unit",UpdatedUtc=DateTime.UtcNow
                });
                count++;
                var tallyAlternateFactor = source.Denominator is > 0m && source.Conversion is > 0m
                    ? source.Denominator.Value / source.Conversion.Value
                    : source.Conversion is > 0m ? source.Conversion.Value : 0m;
                if(source.AdditionalUnit.Trim().Length>0 && tallyAlternateFactor>0m)
                {
                    _stockRepository.SaveStockItemUnitProfile(new StockItemUnitProfileRecord
                    {
                        ClientId=clientId,FinancialYear=financialYear,StockItemId=item.PersistentId,StockItemName=item.Name,
                        UnitCode=source.AdditionalUnit.Trim(),FactorToBase=tallyAlternateFactor,IsAllowed=true,IsTallyAlternateUnit=true,
                        IsPackageConversion=!AahUnitCatalog.TryGetFactor(source.AdditionalUnit,item.BaseUnit,out _),Source="Live Tally Alternate Unit",UpdatedUtc=DateTime.UtcNow
                    });
                    count++;
                }
            }
            catch(InvalidOperationException)
            {
                // A malformed/unknown Tally alternate relationship must never make the
                // Master baseline fail. The Stock Item identity remains synchronized and
                // the invalid unit remains blocked by the inventory preflight.
            }
        }
        return count;
    }

    private static int Count(IEnumerable<MasterRecord> records,string type)=>records.Count(record=>record.Type.Equals(type,StringComparison.OrdinalIgnoreCase));
    private static string Key(MasterRecord record)
    {
        var type = record.Type.Trim().ToUpperInvariant();
        var name = type.Equals("LEDGER", StringComparison.OrdinalIgnoreCase)
            ? TallyMasterIdentity.Key(record.Name)
            : AccountingText.DuplicateKey(record.Name);
        return $"{type}\u001f{name}";
    }
}
