using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Infrastructure.Clients;
using AccountingAutomationHub.Infrastructure.Masters;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Fix137 R41: carries reusable Master/configuration authority from the immediate
/// Previous FY into the active Client/FY. Transaction rows are intentionally outside
/// this service: Purchase, Sales, Banking, Journal, GST and Tally voucher data are never
/// copied by this operation.
/// </summary>
public sealed class PreviousFinancialYearMasterImportService(
    SqliteClientRepository clientRepository,
    SqliteMasterRepository masterRepository,
    SqliteStockMasterRepository stockMasterRepository)
{
    private static readonly string[] StockSimpleTypes =
    [
        "Stock Group", "Stock Category", "Godown", "Cost Category", "Cost Centre",
        "Tax Classification", "TaxUnit"
    ];

    public sealed record Result(
        ClientProfile SourceClient,
        int AccountingMasters,
        int StockItems,
        int Units,
        int SimpleMasters,
        int ItemAliases,
        int UnitMappings,
        int QuantityConversions,
        int UnitProfiles)
    {
        public int Total => AccountingMasters + StockItems + Units + SimpleMasters +
                            ItemAliases + UnitMappings + QuantityConversions + UnitProfiles;
    }

    public Result Import(ClientProfile target)
    {
        if (target.Id <= 0 || string.IsNullOrWhiteSpace(target.FinancialYear))
            throw new InvalidOperationException("Select a saved Client / Financial Year first.");

        var previousFy = PreviousFinancialYear(target.FinancialYear);
        var source = ResolveSource(target, previousFy)
            ?? throw new InvalidOperationException(
                $"No matching Previous FY Client was found for FY {previousFy}. " +
                "Create/import the Previous FY Client profile first, then retry.");

        var company = string.IsNullOrWhiteSpace(target.LegalName) ? target.ClientName : target.LegalName;

        // Always retain AAH's canonical Tally baseline in the target. Source standard
        // rows are skipped below so they cannot replace the Standard hierarchy authority.
        var standard = MasterSynchronizationService.StandardStructure(company);
        masterRepository.Upsert(target.Id, target.FinancialYear, standard);
        var standardKeys = standard
            .Select(record => MasterKey(record.Type, record.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sourceMasters = masterRepository.Get(source.Id, source.FinancialYear, includeArchived: false)
            .Where(record => !standardKeys.Contains(MasterKey(record.Type, record.Name)))
            .Select(record => CloneMaster(record, company))
            .ToArray();
        var accountingCount = sourceMasters.Length == 0
            ? 0
            : masterRepository.Upsert(target.Id, target.FinancialYear, sourceMasters);

        var sourceUnits = stockMasterRepository.GetUnits(source.Id, source.FinancialYear, includeArchived: false)
            .Select(record => CloneUnit(record, company))
            .ToArray();
        var unitCount = sourceUnits.Length == 0
            ? 0
            : stockMasterRepository.UpsertUnits(target.Id, target.FinancialYear, sourceUnits);

        var sourceSimple = StockSimpleTypes
            .SelectMany(type => stockMasterRepository.GetSimpleMasters(source.Id, source.FinancialYear, type, includeArchived: false))
            .Select(record => CloneSimple(record, company))
            .ToArray();
        var simpleCount = sourceSimple.Length == 0
            ? 0
            : stockMasterRepository.UpsertSimpleMasters(target.Id, target.FinancialYear, sourceSimple);

        var sourceItems = stockMasterRepository.GetStockItems(source.Id, source.FinancialYear, includeArchived: false)
            .Select(record => CloneStockItem(record, company))
            .ToArray();
        var stockItemCount = sourceItems.Length == 0
            ? 0
            : stockMasterRepository.UpsertStockItems(target.Id, target.FinancialYear, sourceItems);

        var aliasCount = 0;
        foreach (var alias in stockMasterRepository.GetItemAliases(source.Id, source.FinancialYear))
        {
            stockMasterRepository.SaveItemAlias(
                target.Id, target.FinancialYear, alias.ImportedAlias, alias.StockItemName,
                "Previous FY carry-forward");
            aliasCount++;
        }

        var mappingCount = 0;
        foreach (var mapping in stockMasterRepository.GetUnitMappings(source.Id, source.FinancialYear))
        {
            stockMasterRepository.SaveUnitMapping(
                target.Id, target.FinancialYear, mapping.StockItemName,
                mapping.ImportedUnit, mapping.FinalUnit);
            mappingCount++;
        }

        var conversionCount = 0;
        foreach (var rule in stockMasterRepository.GetQuantityConversionRules(source.Id, source.FinancialYear))
        {
            stockMasterRepository.SaveQuantityConversionRule(new QuantityConversionRule
            {
                ClientId = target.Id,
                FinancialYear = target.FinancialYear,
                StockItemName = rule.StockItemName,
                ImportedUnit = rule.ImportedUnit,
                FinalUnit = rule.FinalUnit,
                Factor = rule.Factor,
                IsUniversal = rule.IsUniversal,
                RememberForClient = rule.RememberForClient,
                UpdatedUtc = DateTime.UtcNow
            });
            conversionCount++;
        }

        // Unit profiles reference the Stock Item's local SQLite row id. Resolve the newly
        // created/current target item by normalized name instead of copying the PY row id.
        var targetItemsByName = stockMasterRepository.GetStockItems(target.Id, target.FinancialYear, includeArchived: false)
            .GroupBy(item => AccountingText.DuplicateKey(item.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.PersistentId).First(), StringComparer.OrdinalIgnoreCase);
        var profileCount = 0;
        foreach (var profile in stockMasterRepository.GetStockItemUnitProfiles(source.Id, source.FinancialYear))
        {
            if (!targetItemsByName.TryGetValue(AccountingText.DuplicateKey(profile.StockItemName), out var targetItem))
                continue;
            stockMasterRepository.SaveStockItemUnitProfile(new StockItemUnitProfileRecord
            {
                ClientId = target.Id,
                FinancialYear = target.FinancialYear,
                StockItemId = targetItem.PersistentId,
                StockItemName = targetItem.Name,
                UnitCode = profile.UnitCode,
                FactorToBase = profile.FactorToBase,
                IsAllowed = profile.IsAllowed,
                IsTallyAlternateUnit = profile.IsTallyAlternateUnit,
                IsPackageConversion = profile.IsPackageConversion,
                Source = "Previous FY carry-forward",
                UpdatedUtc = DateTime.UtcNow
            });
            profileCount++;
        }

        return new Result(source, accountingCount, stockItemCount, unitCount, simpleCount,
            aliasCount, mappingCount, conversionCount, profileCount);
    }

    private ClientProfile? ResolveSource(ClientProfile target, string previousFy)
    {
        var candidates = clientRepository.GetAll(includeArchived: true)
            .Where(client => client.Id != target.Id &&
                             client.FinancialYear.Equals(previousFy, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var gstin = (target.Gstin ?? string.Empty).Trim();
        if (gstin.Length > 0)
        {
            var gstMatch = candidates
                .Where(client => client.Gstin.Trim().Equals(gstin, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(client => client.UpdatedUtc)
                .FirstOrDefault();
            if (gstMatch is not null) return gstMatch;
        }

        var clientNameKey = AccountingText.DuplicateKey(target.ClientName);
        return candidates
            .Where(client => AccountingText.DuplicateKey(client.ClientName) == clientNameKey)
            .OrderByDescending(client => client.UpdatedUtc)
            .FirstOrDefault();
    }

    private static string PreviousFinancialYear(string financialYear)
    {
        var text = financialYear.Trim();
        var parts = text.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var start) || start < 1900 || start > 2200)
            throw new InvalidOperationException($"Financial Year '{financialYear}' is not in the supported YYYY-YY format.");
        var previousStart = start - 1;
        return $"{previousStart:0000}-{(previousStart + 1) % 100:00}";
    }

    private static string MasterKey(string type, string name) =>
        $"{AccountingText.DuplicateKey(type)}\u001f{AccountingText.DuplicateKey(name)}";

    private static MasterRecord CloneMaster(MasterRecord source, string company) => new()
    {
        Name = source.Name,
        Parent = source.Parent,
        Type = source.Type,
        Company = company,
        Source = "Previous FY carry-forward",
        // Opening balances belong to the new FY's opening/Tally authority, not PY structure.
        OpeningBalance = null,
        IsArchived = false,
        IsUserEdited = source.IsUserEdited,
        // Never transplant a PY local binding as proof of current-FY live Tally authority.
        TallyMasterId = string.Empty,
        TallyGuid = string.Empty,
        TallyAlterId = 0
    };

    private static StockItemRecord CloneStockItem(StockItemRecord source, string company) => new()
    {
        Name = source.Name,
        StockGroup = source.StockGroup,
        StockCategory = source.StockCategory,
        BaseUnit = source.BaseUnit,
        AdditionalUnit = source.AdditionalUnit,
        PartNumberSku = source.PartNumberSku,
        DefaultPurchaseUnit = source.DefaultPurchaseUnit,
        DefaultSalesUnit = source.DefaultSalesUnit,
        RateUnit = source.RateUnit,
        DefaultGodown = source.DefaultGodown,
        DefaultPurchaseLedger = source.DefaultPurchaseLedger,
        DefaultSalesLedger = source.DefaultSalesLedger,
        ReorderPoint = source.ReorderPoint,
        MinimumStock = source.MinimumStock,
        MaximumStock = source.MaximumStock,
        HsnSac = source.HsnSac,
        GstRate = source.GstRate,
        GstTypeOfSupply = source.GstTypeOfSupply,
        BatchWise = source.BatchWise,
        Perishable = source.Perishable,
        HasManufacturingDate = source.HasManufacturingDate,
        CostingMethod = source.CostingMethod,
        ValuationMethod = source.ValuationMethod,
        Company = company,
        Source = "Previous FY carry-forward",
        IsUserEdited = source.IsUserEdited,
        TallyMasterId = string.Empty,
        TallyGuid = string.Empty,
        TallyAlterId = 0
    };

    private static StockUnitRecord CloneUnit(StockUnitRecord source, string company) => new()
    {
        Name = source.Name,
        FormalName = source.FormalName,
        DecimalPlaces = source.DecimalPlaces,
        ReportingUqc = source.ReportingUqc,
        Conversion = source.Conversion,
        Company = company,
        Source = "Previous FY carry-forward",
        IsUserEdited = source.IsUserEdited
    };

    private static StockSimpleMasterRecord CloneSimple(StockSimpleMasterRecord source, string company) => new()
    {
        Type = source.Type,
        Name = source.Name,
        Parent = source.Parent,
        Company = company,
        Source = "Previous FY carry-forward",
        IsUserEdited = source.IsUserEdited
    };
}
