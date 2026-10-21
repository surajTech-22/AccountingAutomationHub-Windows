using AccountingAutomationHub.Application.Imports;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Domain.Imports;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Infrastructure.Masters;

namespace AccountingAutomationHub.Infrastructure.Purchases;

public sealed class PurchaseStockNormalizationService
{
    private readonly IStockMasterRepository _stockRepository;
    private readonly IActionRequiredRepository _actionRepository;

    public PurchaseStockNormalizationService(IStockMasterRepository stockRepository, IActionRequiredRepository actionRepository)
    {
        _stockRepository = stockRepository;
        _actionRepository = actionRepository;
    }

    public StockNormalizationSummary Normalize(
        long clientId,
        string financialYear,
        string company,
        IEnumerable<PurchaseImportRecord> records)
    {
        var itemList = _stockRepository.GetStockItems(clientId, financialYear).ToArray();
        var identityResolver = new StockItemIdentityResolver(
            itemList, _stockRepository.GetItemAliases(clientId, financialYear));
        var unitMappings = _stockRepository.GetUnitMappings(clientId, financialYear)
            .ToDictionary(row => UnitMapKey(row.StockItemName, row.ImportedUnit), row => row.FinalUnit, StringComparer.OrdinalIgnoreCase);
        var rules = _stockRepository.GetQuantityConversionRules(clientId, financialYear)
            // Fix123: every rule that reached persistent storage was explicitly saved/
            // confirmed. Legacy Remember=false rows are still authoritative now; the
            // old optional Remember behavior is retired.
            .Where(rule => rule.Factor > 0m)
            .ToDictionary(rule => ConversionKey(rule.StockItemName, rule.ImportedUnit, rule.FinalUnit), rule => rule, StringComparer.OrdinalIgnoreCase);
        var profiles = _stockRepository.GetStockItemUnitProfiles(clientId, financialYear)
            .GroupBy(profile => profile.StockItemId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var summary = new StockNormalizationSummary();
        foreach (var row in records)
        {
            if (row.ItemName.Trim().Length == 0) continue;
            summary.InventoryRows++;
            row.OriginalItemName = row.OriginalItemName.Length == 0 ? row.ItemName : row.OriginalItemName;
            row.ItemName = row.OriginalItemName; // Fix118: live alias/master changes always re-resolve from immutable source identity.
            row.ItemAliasApplied = false;
            row.OriginalUnit = row.OriginalUnit.Length == 0 ? row.Unit : row.OriginalUnit;
            row.OriginalActualQuantity ??= row.ActualQuantity;
            row.OriginalBilledQuantity ??= row.BilledQuantity;
            row.OriginalItemRate ??= row.ItemRate;
            row.OriginalItemAmount ??= row.ItemAmount;
            RestorePreviousQuantityConversion(row);

            var importedName = row.ItemName.Trim();
            StockItemRecord? master = null;
            if (identityResolver.TryResolve(importedName, out master, out var explicitAliasApplied) && master is not null)
            {
                row.ItemAliasApplied = explicitAliasApplied;
                if (row.ItemAliasApplied) summary.ItemAliasesApplied++;
            }
            else
            {
                var reviewCandidates = identityResolver.FindReviewCandidates(importedName, row.PartNumberSku);
                master = new StockItemRecord
                {
                    ClientId=clientId, FinancialYear=financialYear, Name=importedName, PartNumberSku=row.PartNumberSku.Trim(), StockGroup="Primary", BaseUnit=ResolveCanonicalBaseUnit(row.OriginalUnit.Length > 0 ? row.OriginalUnit : row.Unit),
                    DefaultPurchaseUnit=ResolveCanonicalBaseUnit(row.OriginalUnit.Length > 0 ? row.OriginalUnit : row.Unit), DefaultSalesUnit=ResolveCanonicalBaseUnit(row.OriginalUnit.Length > 0 ? row.OriginalUnit : row.Unit), RateUnit=ResolveCanonicalBaseUnit(row.OriginalUnit.Length > 0 ? row.OriginalUnit : row.Unit), HsnSac=row.HsnCode.Trim(), GstRate=row.Rate == 0m ? null : row.Rate, GstTypeOfSupply="Goods",
                    BatchWise=row.BatchName.Trim().Length>0, Perishable=row.ExpiryDate is not null, HasManufacturingDate=row.ManufacturingDate is not null,
                    DefaultGodown=row.Godown.Trim(), DefaultPurchaseLedger=row.ItemPurchaseLedger.Trim(), Company=company, Source="Purchase / Debit Note", IsUserEdited=false
                };
                _stockRepository.UpsertStockItems(clientId,financialYear,[master]);
                master = _stockRepository.GetStockItems(clientId,financialYear,includeArchived:true).FirstOrDefault(item =>
                    item.Name.Trim().Equals(importedName, StringComparison.OrdinalIgnoreCase));
                if (master is null)
                {
                    AddAction(clientId, financialYear, "Item Name Review", importedName,
                        $"Stock Item '{importedName}' could not be read back after creation. Refresh Stock Master and retry; the Purchase row was left unchanged.", row);
                    summary.ActionsRequired++;
                    continue;
                }
                identityResolver.Add(master);
                summary.StockItemsCreated++;

                if (reviewCandidates.Count > 0)
                {
                    AddAction(clientId, financialYear, "Item Name Review", importedName,
                        $"Imported item '{importedName}' was kept as a distinct Stock Item. Similar existing Stock Item(s): {string.Join(", ", reviewCandidates)}. Use Item Rename/Mapping only if they are genuinely the same item.", row);
                    summary.ActionsRequired++;
                }
            }

            row.ItemName = master.Name;
            row.StockItemPersistentId = master.PersistentId;
            row.TallyStockMasterId = master.TallyMasterId;
            if (row.PartNumberSku.Trim().Length==0) row.PartNumberSku=master.PartNumberSku;
            if (row.HsnCode.Trim().Length==0) row.HsnCode=master.HsnSac;
            if (row.Godown.Trim().Length==0) row.Godown=master.DefaultGodown;
            if (row.ItemPurchaseLedger.Trim().Length==0) row.ItemPurchaseLedger=master.DefaultPurchaseLedger;
            ResolveAction(clientId, financialYear, row, "Item Name Review", row.OriginalItemName.Length > 0 ? row.OriginalItemName : importedName);

            // Fix133: import normalization may fill a missing Base Unit, but it must
            // never rebase an existing non-blank Stock Master. Unit changes belong to
            // the explicit Stock Master / confirmed conversion workflow only.
            if (master.BaseUnit.Trim().Length==0)
            {
                var detected=ResolveCanonicalBaseUnit(row.OriginalUnit.Length>0?row.OriginalUnit:row.Unit);
                if(detected.Length==0)
                {
                    summary.ActionsRequired++; AddAction(clientId,financialYear,"Unit Review",$"{master.Name}|NO_BASE_UNIT",$"Stock Item '{master.Name}' has no Base/Inventory Unit. Set the Base Unit in Stock Master before inventory can be posted.",row); continue;
                }
                master.BaseUnit=detected; _stockRepository.SaveStockItem(master); summary.BaseUnitsFilled++;
            }
            EnsureStandardUnit(clientId,financialYear,master.BaseUnit);
            EnsureBaseProfile(master, profiles);

            var rawSource=(row.OriginalUnit.Length>0?row.OriginalUnit:row.Unit).Trim();
            var inherited=rawSource.Length==0;
            if(inherited) rawSource=master.BaseUnit.Trim(); // Fix118: one Stock Item transaction Unit is shared by Purchase and Sales.
            var mappedSource=rawSource;
            var unitMappingFactor=1m;
            if(unitMappings.TryGetValue(UnitMapKey(master.Name,rawSource),out var mapped)&&mapped.Trim().Length>0)
            {
                var candidateMappedSource=mapped.Trim();
                // Fix123: StockUnitMappings are spelling/deterministic mappings only.
                // Legacy Quantity Conversion code could save non-deterministic package
                // relationships here (for example QTL -> PCS); treating those as factor
                // 1 was the reason confirmed conversion factors appeared to be lost.
                // Ignore such legacy mappings and let the saved Quantity Conversion /
                // Stock Item Unit Profile provide the real factor instead.
                if(AahUnitCatalog.TryGetFactor(rawSource,candidateMappedSource,out var deterministicMappingFactor))
                {
                    mappedSource=candidateMappedSource;
                    unitMappingFactor=deterministicMappingFactor;
                }
            }
            var itemProfiles=profiles.TryGetValue(master.PersistentId,out var list) ? list : new List<StockItemUnitProfileRecord>();
            if(!TryResolveConfiguredUnit(mappedSource,itemProfiles,out var sourceCanonical,out var sourceToCanonical))
            {
                summary.ActionsRequired++; AddAction(clientId,financialYear,"Unit Review",$"{master.Name}|{rawSource}",$"Unit '{rawSource}' is not recognized or permitted for Stock Item '{master.Name}'. Add it to this Stock Item's Unit Profile; package contents are never guessed.",row); continue;
            }
            EnsureStandardUnit(clientId,financialYear,sourceCanonical);

            if(!TryFactorToBase(master,itemProfiles,sourceCanonical,rules,out var profileFactor))
            {
                summary.ActionsRequired++; AddAction(clientId,financialYear,"Unit Review",$"{master.Name}|{rawSource}",$"Unit '{sourceCanonical}' is not permitted for '{master.Name}'. Add it to this Stock Item's Unit Profile with its factor to Base Unit '{master.BaseUnit}'.",row); continue;
            }
            var quantityFactor=unitMappingFactor*sourceToCanonical*profileFactor;
            var rateUnit=(master.RateUnit.Trim().Length>0?master.RateUnit:sourceCanonical).Trim();
            if(!TryResolveConfiguredUnit(rateUnit,itemProfiles,out var rateCanonical,out _))
            {
                summary.ActionsRequired++; AddAction(clientId,financialYear,"Unit Review",$"{master.Name}|RATE|{rateUnit}",$"Rate Unit '{rateUnit}' is not configured for '{master.Name}'. Add it to the Stock Item Unit Profile.",row); continue;
            }
            row.RateUnit=rateCanonical;
            if(!TryFactorToBase(master,itemProfiles,rateCanonical,rules,out var rateFactorToBase))
            {
                summary.ActionsRequired++; AddAction(clientId,financialYear,"Unit Review",$"{master.Name}|RATE|{rateCanonical}",$"Rate Unit '{rateCanonical}' is not configured for '{master.Name}'. Add it to the Stock Item Unit Profile.",row); continue;
            }

            // Fix117: quantity and rate units are independent authorities. If the source
            // arithmetic cannot be explained by the configured Rate Unit, do not silently
            // reinterpret the imported rate as a Base-Unit rate. This catches package-rate
            // workbooks such as 2.6 QTL @ 1,200 per 26KG bag = 12,000 and requires the
            // Stock Item to explicitly own BAG -> Base conversion instead of guessing it.
            var sourcePricingQuantity = (row.OriginalBilledQuantity ?? 0m) != 0m
                ? row.OriginalBilledQuantity!.Value
                : (row.OriginalActualQuantity ?? 0m);
            var sourceRate = row.OriginalItemRate ?? 0m;
            var sourceAmount = row.OriginalItemAmount ?? 0m;
            if (sourcePricingQuantity != 0m && sourceRate != 0m && sourceAmount != 0m &&
                row.DiscountPercent == 0m && row.DiscountAmount == 0m)
            {
                var semanticExpected = Math.Round(sourcePricingQuantity * quantityFactor * (sourceRate / rateFactorToBase), 2, MidpointRounding.AwayFromZero);
                // Source rates are commonly rounded to 2 decimals. Allow only the exact
                // rounding envelope implied by the number of source Rate Units; anything
                // larger still requires an explicit Rate Unit review.
                var sourceRateUnits = Math.Abs(sourcePricingQuantity * quantityFactor / rateFactorToBase);
                var semanticTolerance = Math.Max(0.02m, sourceRateUnits * 0.005m + 0.01m);
                if (Math.Abs(semanticExpected - sourceAmount) > semanticTolerance)
                {
                    var impliedRateFactor = Math.Abs(sourceAmount) > 0.0000001m
                        ? Math.Abs(sourcePricingQuantity * quantityFactor * sourceRate / sourceAmount)
                        : 0m;
                    summary.ActionsRequired++;
                    AddAction(clientId,financialYear,"Unit Review",$"{master.Name}|RATE_AMOUNT_SOURCE",
                        $"Source inventory arithmetic for '{master.Name}' cannot be explained by configured Rate Unit '{rateCanonical}': normalized Qty × normalized Rate = {semanticExpected:0.00}, but Item Amount = {sourceAmount:0.00}. The implied Rate-Unit factor is about {impliedRateFactor:0.########} Base Units per Rate Unit. Configure the actual Rate Unit and its Stock Item Unit Profile; AAH will not guess package contents.",row);
                    continue;
                }
            }

            ApplyConversion(row,master.BaseUnit,quantityFactor);
            if((row.OriginalItemAmount??0m)==0m && (row.OriginalItemRate??0m)!=0m)
            {
                row.ItemRate=(row.OriginalItemRate??0m)/rateFactorToBase;
                row.ItemAmount=Math.Round(row.BilledQuantity*row.ItemRate,2,MidpointRounding.AwayFromZero);
                row.AccountingAmount=row.AccountingAmount==0m?row.ItemAmount:row.AccountingAmount;
                row.BatchAmount=row.BatchAmount==0m?row.ItemAmount:row.BatchAmount;
            }
            row.Unit=master.BaseUnit;
            if(quantityFactor!=1m) summary.QuantityConversionsApplied++;
            if(!inherited && !rawSource.Equals(sourceCanonical,StringComparison.OrdinalIgnoreCase)) { row.UnitMappingApplied=true; summary.UnitMappingsApplied++; }
            ResolveAction(clientId,financialYear,row,"Unit Review",$"{master.Name}|{rawSource}");

            var expected=Math.Round(row.BilledQuantity*row.ItemRate,2,MidpointRounding.AwayFromZero);
            if(Math.Abs(row.ItemAmount)>0.005m && Math.Abs(expected-row.ItemAmount)>0.02m)
            {
                summary.ActionsRequired++; AddAction(clientId,financialYear,"Unit Review",$"{master.Name}|RATE_AMOUNT",$"Inventory value mismatch for '{master.Name}': Billed Qty × normalized Rate = {expected:0.00}, but Item Amount = {row.ItemAmount:0.00}. Review Quantity Unit / Rate Unit before Tally export.",row);
            }
        }
        return summary;
    }

    private void EnsureBaseProfile(StockItemRecord master, IDictionary<long,List<StockItemUnitProfileRecord>> profiles)
    {
        if(!profiles.TryGetValue(master.PersistentId,out var rows)) { rows=[]; profiles[master.PersistentId]=rows; }
        if(rows.Any(row=>UnitEquivalent(row.UnitCode,master.BaseUnit))) return;
        var profile=new StockItemUnitProfileRecord{ClientId=master.ClientId,FinancialYear=master.FinancialYear,StockItemId=master.PersistentId,StockItemName=master.Name,UnitCode=master.BaseUnit,FactorToBase=1m,IsAllowed=true,Source="Fix117 Base Unit",UpdatedUtc=DateTime.UtcNow};
        _stockRepository.SaveStockItemUnitProfile(profile); rows.Add(profile);
    }

    private bool TryFactorToBase(StockItemRecord master,List<StockItemUnitProfileRecord> profiles,string unit,IReadOnlyDictionary<string,QuantityConversionRule> rules,out decimal factor)
    {
        if(UnitEquivalent(unit,master.BaseUnit)) { factor=1m; return true; }
        var profile=profiles.LastOrDefault(row=>row.IsAllowed&&UnitEquivalent(row.UnitCode,unit));
        if(profile is not null && profile.FactorToBase>0m) { factor=profile.FactorToBase; return true; }
        // Fix123: a different imported Unit never becomes convertible merely because
        // it happens to appear in RateUnit/AdditionalUnit or because a physical factor
        // exists in the catalogue. A changed Stock Master Unit must first be confirmed
        // through Unit Mapping (deterministic spelling/physical mapping), Quantity
        // Conversion, or an explicit allowed Unit Profile. This is the confirmation gate.
        var legacy=rules.Values.LastOrDefault(rule=>rule.StockItemName.Equals(master.Name,StringComparison.OrdinalIgnoreCase)&&UnitEquivalent(rule.ImportedUnit,unit)&&UnitEquivalent(rule.FinalUnit,master.BaseUnit)&&rule.Factor>0m);
        if(legacy is not null)
        {
            var migrated=new StockItemUnitProfileRecord{ClientId=master.ClientId,FinancialYear=master.FinancialYear,StockItemId=master.PersistentId,StockItemName=master.Name,UnitCode=unit,FactorToBase=legacy.Factor,IsAllowed=true,IsPackageConversion=!AahUnitCatalog.TryGetFactor(unit,master.BaseUnit,out _),Source="Migrated Fix116 Quantity Conversion",UpdatedUtc=DateTime.UtcNow};
            _stockRepository.SaveStockItemUnitProfile(migrated); profiles.Add(migrated); factor=legacy.Factor; return true;
        }
        factor=0m; return false;
    }

    private static bool TryResolveConfiguredUnit(string value,IReadOnlyList<StockItemUnitProfileRecord> profiles,out string canonical,out decimal sourceToCanonical)
    {
        if(AahUnitCatalog.TryResolvePreferred(value,out canonical,out sourceToCanonical)) return true;
        var profile=profiles.LastOrDefault(row=>row.IsAllowed&&UnitEquivalent(row.UnitCode,value));
        if(profile is not null){canonical=profile.UnitCode.Trim();sourceToCanonical=1m;return canonical.Length>0;}
        canonical=string.Empty;sourceToCanonical=0m;return false;
    }

    public void SaveItemAlias(long clientId,string financialYear,string importedAlias,string finalItemName,string source = "Manual Item Rename") =>
        _stockRepository.SaveItemAlias(clientId,financialYear,importedAlias,finalItemName,source);

    public void SaveUnitMapping(long clientId,string financialYear,string stockItemName,string importedUnit,string finalUnit)
    {
        if (!AahUnitCatalog.TryGetDefinition(finalUnit, out var definition))
            throw new InvalidOperationException($"'{finalUnit}' is not an AAH standard Unit.");
        if (!AahUnitCatalog.TryGetFactor(importedUnit, definition.Code, out var unitAliasFactor) || unitAliasFactor != 1m)
            throw new InvalidOperationException($"{importedUnit} → {definition.Code} changes quantity. Use Quantity Conversion and confirm the factor instead of Unit Mapping.");
        EnsureStandardUnit(clientId, financialYear, definition.Code);
        _stockRepository.SaveUnitMapping(clientId,financialYear,stockItemName,importedUnit,definition.Code);

        // Fix121: when Unit Mapping uses a deterministic conversion (for example
        // QTL -> KGS or GMS -> KGS), the mapped Unit must also be a permitted
        // Stock Item profile so live reprojection can continue from the original
        // source quantity without losing the conversion factor.
        var master = _stockRepository.GetStockItems(clientId, financialYear)
            .FirstOrDefault(item => item.Name.Equals(stockItemName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (master is not null && master.PersistentId > 0 && master.BaseUnit.Trim().Length > 0 &&
            AahUnitCatalog.TryGetFactor(definition.Code, master.BaseUnit, out var factorToBase))
        {
            _stockRepository.SaveStockItemUnitProfile(new StockItemUnitProfileRecord
            {
                ClientId = clientId,
                FinancialYear = financialYear,
                StockItemId = master.PersistentId,
                StockItemName = master.Name,
                UnitCode = definition.Code,
                FactorToBase = factorToBase,
                IsAllowed = true,
                Source = "Fix121 Deterministic Unit Mapping",
                UpdatedUtc = DateTime.UtcNow
            });
        }
    }

    public void ApplyAndRememberConversion(long clientId,string financialYear,PurchaseImportRecord row,string finalUnit,decimal factor,bool remember)
    {
        var imported = row.OriginalUnit.Length>0 ? row.OriginalUnit : row.Unit;
        if (!AahUnitCatalog.TryGetDefinition(finalUnit, out var finalDefinition))
            throw new InvalidOperationException($"'{finalUnit}' is not an AAH standard Unit.");
        var canonicalFinal = finalDefinition.Code;
        EnsureStandardUnit(clientId, financialYear, canonicalFinal);
        var isDeterministic = AahUnitCatalog.TryGetFactor(imported, canonicalFinal, out var authoritativeFactor);
        if (isDeterministic) factor = authoritativeFactor;
        if (factor <= 0m) throw new InvalidOperationException("Enter a conversion factor greater than zero for this item-specific package conversion.");
        ApplyConversion(row,canonicalFinal,factor);
        // Fix123: every user-confirmed real conversion is durable by definition.
        // Same-unit canonicalization such as Qtls -> QTL does not create a conversion rule.
        if (!UnitEquivalent(imported, canonicalFinal))
            _stockRepository.SaveQuantityConversionRule(new QuantityConversionRule{ClientId=clientId,FinancialYear=financialYear,StockItemName=row.ItemName,ImportedUnit=imported,FinalUnit=canonicalFinal,Factor=factor,IsUniversal=isDeterministic,RememberForClient=true,UpdatedUtc=DateTime.UtcNow});
    }

    public static void ApplyConversion(PurchaseImportRecord row,string finalUnit,decimal factor)
    {
        if (factor<=0m) throw new ArgumentOutOfRangeException(nameof(factor));
        row.OriginalUnit = row.OriginalUnit.Length==0 ? row.Unit : row.OriginalUnit;
        row.OriginalActualQuantity ??= row.ActualQuantity; row.OriginalBilledQuantity ??= row.BilledQuantity;
        row.OriginalItemRate ??= row.ItemRate; row.OriginalItemAmount ??= row.ItemAmount;
        var originalActual=row.OriginalActualQuantity??0m; var originalBilled=row.OriginalBilledQuantity??0m;
        var originalAmount=row.OriginalItemAmount??row.ItemAmount; var originalRate=row.OriginalItemRate??row.ItemRate;
        var previousFactor = row.IsQuantityConverted && row.QuantityConversionFactor > 0m ? row.QuantityConversionFactor : 1m;
        var originalFreeQuantity = row.FreeQuantity / previousFactor;
        row.Unit=finalUnit.Trim(); row.ActualQuantity=originalActual*factor; row.BilledQuantity=originalBilled*factor; row.FreeQuantity=originalFreeQuantity*factor;
        var pricingQuantity = row.BilledQuantity != 0m ? row.BilledQuantity : row.ActualQuantity;
        if (pricingQuantity!=0m && originalAmount!=0m) row.ItemRate=originalAmount/pricingQuantity;
        else if(originalRate!=0m) row.ItemRate=originalRate/factor;
        row.ItemAmount=originalAmount!=0m?originalAmount:Math.Round(row.BilledQuantity*row.ItemRate,2,MidpointRounding.AwayFromZero);
        row.AccountingAmount=row.AccountingAmount==0m?row.ItemAmount:row.AccountingAmount;
        row.BatchActualQuantity=row.ActualQuantity; row.BatchBilledQuantity=row.BilledQuantity; if(row.BatchAmount==0m)row.BatchAmount=row.ItemAmount;
        row.QuantityConversionFactor=factor; row.QuantityConversionKey=$"{row.ItemName}~~~QCV~~~{row.OriginalUnit}~~~{row.Unit}"; row.IsQuantityConverted=true;
    }

    public static bool TryUniversalFactor(string from,string to,out decimal factor) =>
        AahUnitCatalog.TryGetFactor(from, to, out factor);

    private static string ResolveCanonicalBaseUnit(string value) =>
        AahUnitCatalog.TryResolvePreferred(value, out var canonical, out var factor) && factor == 1m
            ? canonical
            : string.Empty;

    private void EnsureStandardUnit(long clientId, string financialYear, string canonicalCode)
    {
        if (_stockRepository.GetUnits(clientId, financialYear)
            .Any(unit => unit.Name.Equals(canonicalCode, StringComparison.OrdinalIgnoreCase)))
            return;
        if(!AahUnitCatalog.TryGetDefinition(canonicalCode,out _))
            throw new InvalidOperationException($"Unit '{canonicalCode}' is not a saved AAH/Tally Unit Master.");
        _stockRepository.UpsertUnits(clientId, financialYear,
            [AahUnitCatalog.CreateUnitRecord(clientId, financialYear, canonicalCode, string.Empty)]);
    }

    private void NormalizeImportedUnit(
        long clientId,
        string financialYear,
        string company,
        PurchaseImportRecord row,
        IDictionary<string, StockUnitRecord> units,
        StockNormalizationSummary summary)
    {
        var imported = (row.OriginalUnit.Length > 0 ? row.OriginalUnit : row.Unit).Trim();
        if (imported.Length == 0) return;
        row.OriginalUnit = row.OriginalUnit.Length == 0 ? imported : row.OriginalUnit;

        if (!AahUnitCatalog.TryResolvePreferred(imported, out var canonical, out var factor))
        {
            summary.ActionsRequired++;
            AddAction(clientId, financialYear, "Unit Review", $"{row.ItemName}|{imported}",
                $"Imported unit '{imported}' is not an AAH standard Unit or deterministic source unit. Map it to an AAH Unit; package contents are never guessed.", row);
            return;
        }

        if (!units.ContainsKey(Key(canonical)))
        {
            var unit = AahUnitCatalog.CreateUnitRecord(clientId, financialYear, canonical, company);
            _stockRepository.UpsertUnits(clientId, financialYear, [unit]);
            units[Key(unit.Name)] = unit;
            summary.UnitsCreated++;
        }

        ApplyConversion(row, canonical, factor);
        if (factor != 1m) summary.QuantityConversionsApplied++;
    }

    private static void RestorePreviousQuantityConversion(PurchaseImportRecord row)
    {
        if (!row.IsQuantityConverted) return;
        var previousFactor = row.QuantityConversionFactor > 0m ? row.QuantityConversionFactor : 1m;
        var originalFreeQuantity = row.FreeQuantity / previousFactor;
        if (row.OriginalActualQuantity.HasValue) row.ActualQuantity = row.OriginalActualQuantity.Value;
        if (row.OriginalBilledQuantity.HasValue) row.BilledQuantity = row.OriginalBilledQuantity.Value;
        if (row.OriginalItemRate.HasValue) row.ItemRate = row.OriginalItemRate.Value;
        if (row.OriginalItemAmount.HasValue) row.ItemAmount = row.OriginalItemAmount.Value;
        row.FreeQuantity = originalFreeQuantity;
        row.BatchActualQuantity = row.ActualQuantity;
        row.BatchBilledQuantity = row.BilledQuantity;
        // Batch amount is monetary evidence and is never scaled by a quantity conversion.
        row.Unit = row.OriginalUnit;
        row.QuantityConversionFactor = 1m;
        row.QuantityConversionKey = string.Empty;
        row.IsQuantityConverted = false;
    }

    private void AddAction(long clientId,string financialYear,string type,string sourceKey,string detail,PurchaseImportRecord row)
    {
        // Stock Item / Unit / Quantity review is a normal Purchase/Stock workflow.
        // It must not populate the global Action Required queue after the Purchase
        // source has already imported successfully.
        if (!ActionRequiredPolicy.IsImportBlockingType(type)) return;
        _actionRepository.Upsert(new ActionRequiredItem{ClientId=clientId,FinancialYear=financialYear,Module=ActionModule(row),ActionType=type,Title=type,Detail=detail,SourceKey=ActionSourceKey(type,sourceKey,row)});
    }

    private void ResolveAction(long clientId,string financialYear,PurchaseImportRecord row,string type,string sourceKey)
    {
        if (sourceKey.Trim().Length == 0) return;
        _actionRepository.ResolveBySourceKey(clientId,financialYear,ActionModule(row),ActionSourceKey(type,sourceKey,row));
    }

    private static string ActionModule(PurchaseImportRecord row) =>
        row.Destination.Contains("Debit Note", StringComparison.OrdinalIgnoreCase)
            ? "Debit Note"
            : "Purchase";

    private static string ActionSourceKey(string type,string sourceKey,PurchaseImportRecord row) =>
        $"{type}|{sourceKey}|{row.DocumentNumber}";

    private static string UnitMapKey(string item,string unit)=>Key(item)+"\u001f"+Key(unit);
    private static string ConversionKey(string item,string from,string to)=>Key(item)+"\u001f"+Key(from)+"\u001f"+Key(to);
    private static bool UnitEquivalent(string a,string b) =>
        Key(a).Equals(Key(b),StringComparison.OrdinalIgnoreCase) ||
        (AahUnitCatalog.TryGetFactor(a,b,out var factor) && factor == 1m);
    private static bool IsExplicitItemAlias(StockItemAliasRecord alias) =>
        alias.Source.StartsWith("Manual", StringComparison.OrdinalIgnoreCase) ||
        alias.Source.StartsWith("Explicit", StringComparison.OrdinalIgnoreCase);

    private static string Key(string value)=>string.Concat((value??string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant));
    private static string Compact(string value)=>Key(value);
    private static bool HasCloseCandidate(string value,IEnumerable<string> candidates)
    {
        var key=Compact(value); if(key.Length<4)return false;
        foreach(var candidate in candidates){var other=Compact(candidate);if(other.Length<4)continue;if(key.StartsWith(other,StringComparison.OrdinalIgnoreCase)||other.StartsWith(key,StringComparison.OrdinalIgnoreCase))return true;var common=CommonPrefix(key,other);if(common>=Math.Min(key.Length,other.Length)*0.8)return true;}
        return false;
    }
    private static int CommonPrefix(string a,string b){var n=Math.Min(a.Length,b.Length);var i=0;for(;i<n&&a[i]==b[i];i++);return i;}
}

public sealed class StockNormalizationSummary
{
    public int InventoryRows { get; set; }
    public int StockItemsCreated { get; set; }
    public int UnitsCreated { get; set; }
    public int BaseUnitsFilled { get; set; }
    public int ItemAliasesApplied { get; set; }
    public int UnitMappingsApplied { get; set; }
    public int QuantityConversionsApplied { get; set; }
    public int ActionsRequired { get; set; }
}
