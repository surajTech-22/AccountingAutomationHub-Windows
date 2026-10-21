using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Journals;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Domain.Sales;
using AccountingAutomationHub.Infrastructure.Journals;
using AccountingAutomationHub.Infrastructure.Purchases;
using AccountingAutomationHub.Infrastructure.Sales;

namespace AccountingAutomationHub.Infrastructure.Masters;

public sealed class MasterSynchronizationService(
    SqliteMasterRepository masterRepository,
    SqlitePurchaseRecordRepository purchaseRepository,
    SqliteSalesRecordRepository salesRepository,
    SqliteJournalRepository journalRepository)
{
    private static readonly (string Name, string Parent)[] StandardGroups =
    [
        ("Capital Account", "Primary"),
        ("Reserves & Surplus", "Capital Account"),
        ("Current Assets", "Primary"),
        ("Bank Accounts", "Current Assets"),
        ("Cash-in-Hand", "Current Assets"),
        ("Deposits (Asset)", "Current Assets"),
        ("Loans & Advances (Asset)", "Current Assets"),
        ("Stock-in-Hand", "Current Assets"),
        ("Sundry Debtors", "Current Assets"),
        ("Current Liabilities", "Primary"),
        ("Duties & Taxes", "Current Liabilities"),
        ("Provisions", "Current Liabilities"),
        ("Sundry Creditors", "Current Liabilities"),
        ("Direct Expenses", "Primary"),
        ("Direct Incomes", "Primary"),
        ("Indirect Expenses", "Primary"),
        ("Indirect Incomes", "Primary"),
        ("Fixed Assets", "Primary"),
        ("Investments", "Primary"),
        ("Loans (Liability)", "Primary"),
        ("Bank OD A/c", "Loans (Liability)"),
        ("Secured Loans", "Loans (Liability)"),
        ("Unsecured Loans", "Loans (Liability)"),
        ("Purchase Accounts", "Primary"),
        ("Sales Accounts", "Primary"),
        ("Suspense A/c", "Primary"),
        ("Branch / Divisions", "Primary"),
        ("Misc. Expenses (ASSET)", "Primary")
    ];

    /// <summary>
    /// Canonical Tally-format prebuilt Groups that are valid Ledger parents.
    /// Kept in the same order as the application's standard Tally hierarchy so
    /// dropdowns can show the familiar Tally group list before client-specific groups.
    /// </summary>
    public static IReadOnlyList<string> TallyPrebuiltLedgerGroupNames { get; } = StandardGroups
        .Select(group => group.Name)
        .Where(name => !name.Equals("Primary", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static bool TryResolveTallyPrebuiltLedgerGroup(string? value, out string canonical)
    {
        var requested = AccountingText.DuplicateKey(value ?? string.Empty);
        foreach (var name in TallyPrebuiltLedgerGroupNames)
        {
            if (AccountingText.DuplicateKey(name) != requested) continue;
            canonical = name;
            return true;
        }
        canonical = string.Empty;
        return false;
    }

    public static IReadOnlyList<MasterRecord> StandardStructure(string company) =>
        StandardGroups.Select(group => Create(group.Name, group.Parent, "Group", company, "Standard hierarchy"))
            .Concat(new[]
            {
                Create("Cash", "Cash-in-Hand", "Ledger", company, "Standard hierarchy"),
                Create("Profit & Loss A/c", "Primary", "Ledger", company, "Standard hierarchy")
            }).ToArray();

    public int Synchronize(
        long clientId,
        string financialYear,
        string company)
    {
        var records = new List<MasterRecord>();

        records.AddRange(StandardGroups.Select(group =>
            Create(group.Name, group.Parent, "Group", company, "Standard hierarchy")));

        IReadOnlyList<PurchaseImportRecord> purchases;
        IReadOnlyList<SalesImportRecord> sales;
        IReadOnlyList<JournalVoucher> vouchers;
        JournalGenerationSettings journalSettings;

        try { purchases = purchaseRepository.GetRecords(clientId, financialYear); }
        catch { purchases = []; }

        try { sales = salesRepository.GetAutomaticRecords(clientId, financialYear); }
        catch { sales = []; }

        try { vouchers = journalRepository.GetVouchers(clientId, financialYear); }
        catch { vouchers = []; }
        try { journalSettings = journalRepository.GetSettings(clientId, financialYear); }
        catch { journalSettings = new JournalGenerationSettings { ClientId = clientId, FinancialYear = financialYear }; }

        // Fix84: Not-shown-in-2B Input GST is a distinct asset bucket. Keep the three
        // existing tax Ledgers on their own MASTERIDs, but parent them below one
        // Input Not Shown in 2B subgroup under Loans & Advances (Asset). The subgroup
        // is created whenever current Purchase data or existing Master authority proves
        // that at least one Not-shown Ledger exists.
        var hasNotShownInTwoB = purchases.Any(row =>
                !PurchaseTreatmentPolicy.IsRcm(row) &&
                PurchaseTreatmentPolicy.IsNotShownInTwoB(row.GstApplicability)) ||
            masterRepository.Get(clientId, financialYear, includeArchived: false).Any(master =>
                master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                GstMasterClassificationPolicy.IsNotShownInTwoBLedger(master.Name));
        if (hasNotShownInTwoB)
        {
            records.Add(Create(
                GstMasterClassificationPolicy.InputNotShownInTwoBGroup,
                GstMasterClassificationPolicy.LoansAndAdvancesAssetGroup,
                "Group", company, "Purchase sync"));
        }

        // Fix82: multiple Receivable mode uses one asset subgroup. Single mode has no
        // subgroup; GST Receivable itself is the Ledger directly under Loans & Advances.
        if (GstMasterClassificationPolicy.IsSeparateReceivableMode(journalSettings))
        {
            records.Add(Create(
                GstMasterClassificationPolicy.GstReceivableGroup,
                GstMasterClassificationPolicy.LoansAndAdvancesAssetGroup,
                "Group", company, "Automatic Journal sync"));
            // Fix83: multiple Receivable mode owns all three settlement Ledgers even
            // when a particular tax head currently has a zero balance. This keeps the
            // Master hierarchy deterministic and prevents a later CGST/SGST Journal
            // from stopping at the Master barrier merely because only IGST was used
            // in the previous period. Custom configured names are preserved.
            AddLedger(records, CanonicalLedger(clientId, financialYear,
                    GstMasterClassificationPolicy.EffectiveIgstSettlementLedger(journalSettings)),
                GstMasterClassificationPolicy.GstReceivableGroup, company, "Automatic Journal sync");
            AddLedger(records, CanonicalLedger(clientId, financialYear,
                    GstMasterClassificationPolicy.EffectiveCgstSettlementLedger(journalSettings)),
                GstMasterClassificationPolicy.GstReceivableGroup, company, "Automatic Journal sync");
            AddLedger(records, CanonicalLedger(clientId, financialYear,
                    GstMasterClassificationPolicy.EffectiveSgstSettlementLedger(journalSettings)),
                GstMasterClassificationPolicy.GstReceivableGroup, company, "Automatic Journal sync");
        }
        else if (GstMasterClassificationPolicy.IsReceivableMode(journalSettings))
        {
            AddLedger(records, CanonicalLedger(clientId, financialYear,
                    GstMasterClassificationPolicy.EffectiveSingleSettlementLedger(journalSettings)),
                GstMasterClassificationPolicy.LoansAndAdvancesAssetGroup, company, "Automatic Journal sync");
        }

        var existingLedgerParents = masterRepository
            .Get(clientId, financialYear, includeArchived: false)
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
            .GroupBy(master => TallyMasterIdentity.Key(master.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Parent, StringComparer.OrdinalIgnoreCase);

        foreach (var row in purchases)
        {
            AddLedger(records, CanonicalLedger(clientId, financialYear, row.SupplierName), "Sundry Creditors", company, "Purchase sync");
            var purchaseLedgerKey = TallyMasterIdentity.Key(row.PurchaseLedger);
            var purchaseParent = PurchaseTreatmentPolicy.IsGstIncludedInCost(row.GstApplicability)
                ? "Indirect Expenses"
                : PurchaseTreatmentPolicy.IsAutomaticPurchaseLedger(row.PurchaseLedger)
                    ? "Purchase Accounts"
                    : existingLedgerParents.TryGetValue(purchaseLedgerKey, out var existingParent) &&
                      !string.IsNullOrWhiteSpace(existingParent)
                        ? existingParent
                        : "Indirect Expenses";
            AddLedger(records, CanonicalLedger(clientId, financialYear, row.PurchaseLedger), purchaseParent, company, "Purchase sync");

            if (PurchaseTreatmentPolicy.IsRcm(row))
            {
                var treatment = PurchaseTreatmentPolicy.NormalizeRcmTreatment(row.GstApplicability);
                AddRcmGeneratedLedger(records, clientId, financialYear, company, "IGST", row.IgstLedger, row.Igst, row.Rate,
                    journalSettings.TemporaryRcmIgstLedger, journalSettings.RcmExpenseIgstLedger, treatment);
                AddRcmGeneratedLedger(records, clientId, financialYear, company, "CGST", row.CgstLedger, row.Cgst, row.Rate / 2m,
                    journalSettings.TemporaryRcmCgstLedger, journalSettings.RcmExpenseCgstLedger, treatment);
                AddRcmGeneratedLedger(records, clientId, financialYear, company, "SGST", row.SgstLedger, row.Sgst, row.Rate / 2m,
                    journalSettings.TemporaryRcmSgstLedger, journalSettings.RcmExpenseSgstLedger, treatment);
            }
            else
            {
                var purchaseTaxParent = PurchaseTreatmentPolicy.IsNotShownInTwoB(row.GstApplicability)
                    ? GstMasterClassificationPolicy.InputNotShownInTwoBGroup
                    : "Duties & Taxes";
                AddTaxLedgers(records,
                    CanonicalLedger(clientId, financialYear, row.IgstLedger),
                    CanonicalLedger(clientId, financialYear, row.CgstLedger),
                    CanonicalLedger(clientId, financialYear, row.SgstLedger), company, "Purchase sync", purchaseTaxParent);
            }

            if (Math.Abs(row.Cess) > 0.005m && !PurchaseTreatmentPolicy.IsGstIncludedInCost(row.GstApplicability))
                AddLedger(records, CanonicalLedger(clientId, financialYear, "Input Cess"), "Duties & Taxes", company, "Purchase sync");
            if (Math.Abs(row.RoundOff) > 0.005m)
                AddLedger(records, CanonicalLedger(clientId, financialYear, "Round Off"), "Indirect Expenses", company, "Purchase sync");
        }

        foreach (var row in sales)
        {
            var isRegisteredB2b = !string.IsNullOrWhiteSpace(row.CustomerGstin) &&
                                  (row.SourceSection.Equals("B2B", StringComparison.OrdinalIgnoreCase) ||
                                   row.SourceSection.Equals("B2BA", StringComparison.OrdinalIgnoreCase) ||
                                   row.SourceSection.Equals("CDNR", StringComparison.OrdinalIgnoreCase) ||
                                   row.SourceSection.Equals("CDNRA", StringComparison.OrdinalIgnoreCase));
            var automaticFallback = row.CustomerNameSource.Equals("GSTIN Fallback", StringComparison.OrdinalIgnoreCase) ||
                                    row.CustomerNameSource.Equals("Generic Fallback", StringComparison.OrdinalIgnoreCase) ||
                                    row.CustomerName.StartsWith("Customer - ", StringComparison.OrdinalIgnoreCase);

            // Registered B2B fallback labels are temporary display values, not Masters.
            // A user-forced/customer-edited name remains eligible; B2C generic ledgers retain
            // their existing behavior.
            if (!(isRegisteredB2b && automaticFallback && !row.IsUserEdited))
                AddLedger(records, CanonicalLedger(clientId, financialYear, row.CustomerName), "Sundry Debtors", company, "Sales sync");
            AddLedger(records, CanonicalLedger(clientId, financialYear, row.SalesLedger), "Sales Accounts", company, "Sales sync");
            AddTaxLedgers(records,
                CanonicalLedger(clientId, financialYear, row.IgstLedger),
                CanonicalLedger(clientId, financialYear, row.CgstLedger),
                CanonicalLedger(clientId, financialYear, row.SgstLedger), company, "Sales sync");
            if (Math.Abs(row.Cess) > 0.005m)
            {
                var cessLedger = row.CessRate > 0m
                    ? $"Output Cess @ {PurchaseTreatmentPolicy.FormatRate(row.CessRate)}%"
                    : "Output Cess";
                AddLedger(records, CanonicalLedger(clientId, financialYear, cessLedger), "Duties & Taxes", company, "Sales sync");
            }
            if (Math.Abs(row.RoundOff) > 0.005m)
                AddLedger(records, CanonicalLedger(clientId, financialYear, "Round Off"), "Indirect Expenses", company, "Sales sync");
        }

        foreach (var voucher in vouchers.Where(voucher =>
                     voucher.GenerationMode.Equals("Automatic", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var line in voucher.Lines)
            {
                AddLedger(
                    records,
                    CanonicalLedger(clientId, financialYear, line.LedgerName),
                    ParentForJournalLine(line, journalSettings),
                    company,
                    "Automatic Journal sync");
            }
        }

        // Manual Journal ledgers are intentionally not created silently. Unknown
        // manual ledgers are routed through Journal Ledger Mapping so the user
        // explicitly chooses the same Master Group used in the Excel workbook.

        var changed = masterRepository.Upsert(clientId, financialYear, records);
        changed += ArchiveObsoleteGeneratedRcmAliases(clientId, financialYear, records);
        changed += ArchiveObsoleteFalseZeroRateLedgers(clientId, financialYear, records);
        return changed;
    }

    private int ArchiveObsoleteFalseZeroRateLedgers(
        long clientId,
        string financialYear,
        IReadOnlyCollection<MasterRecord> generatedRecords)
    {
        var generatedKeys = generatedRecords
            .Where(row => row.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(row.Name))
            .Select(row => TallyMasterIdentity.Key(row.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        static bool IsFalseZeroRateLedger(string? value)
        {
            var name = (value ?? string.Empty).Trim();
            return name.Equals("Purchase @ 0%", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Input IGST @ 0%", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Input CGST @ 0%", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Input SGST @ 0%", StringComparison.OrdinalIgnoreCase);
        }

        var archived = 0;
        foreach (var row in masterRepository.Get(clientId, financialYear, true)
                     .Where(row => !row.IsArchived &&
                                   row.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                   IsFalseZeroRateLedger(row.Name) &&
                                   MasterAuthority.IsLocalEditableSource(row.Source) &&
                                   (row.Source.Equals("Purchase sync", StringComparison.OrdinalIgnoreCase) ||
                                    row.Source.Equals("Automatic Journal sync", StringComparison.OrdinalIgnoreCase))))
        {
            // A repaired Purchase/Journal pass no longer generates these names. Only
            // retire the stale local generated alias when it is absent from the current
            // authoritative generated set; never archive a live Tally Master here.
            if (generatedKeys.Contains(TallyMasterIdentity.Key(row.Name))) continue;
            masterRepository.Archive(row.PersistentId, true);
            archived++;
        }
        return archived;
    }

    private int ArchiveObsoleteGeneratedRcmAliases(
        long clientId,
        string financialYear,
        IReadOnlyCollection<MasterRecord> generatedRecords)
    {
        var generatedKeys = generatedRecords
            .Where(row => row.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(row.Name))
            .Select(row => TallyMasterIdentity.Key(row.Name))
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        static bool IsObsoleteLocalGeneratedRcmName(string? value)
        {
            var name = (value ?? string.Empty).Trim();
            if (name.Length == 0 ||
                name.Equals(PurchaseTreatmentPolicy.RcmInputExpensesLedger, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (name.Equals("RCM Expense IGST", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("RCM Expense CGST", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("RCM Expense SGST", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (name.StartsWith("RCM ", StringComparison.OrdinalIgnoreCase) &&
                name.Contains(" Expense", StringComparison.OrdinalIgnoreCase) &&
                (name.Contains("IGST", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("CGST", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("SGST", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return name.Contains("RCM Input Pending", StringComparison.OrdinalIgnoreCase);
        }

        var archived = 0;
        foreach (var row in masterRepository.Get(clientId, financialYear, true)
                     .Where(row => !row.IsArchived &&
                                   row.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                   IsObsoleteLocalGeneratedRcmName(row.Name) &&
                                   MasterAuthority.IsLocalEditableSource(row.Source) &&
                                   (row.Source.Equals("Automatic Journal sync", StringComparison.OrdinalIgnoreCase) ||
                                    row.Source.Equals("Purchase RCM sync", StringComparison.OrdinalIgnoreCase))))
        {
            // If the user deliberately kept a legacy alias as an active generated
            // ledger, do not archive it. Only stale aliases absent from current
            // accounting generation are retired. Tally Masters are never altered here.
            if (generatedKeys.Contains(TallyMasterIdentity.Key(row.Name))) continue;
            masterRepository.Archive(row.PersistentId, true);
            archived++;
        }
        return archived;
    }

    private string CanonicalLedger(long clientId, string financialYear, string name) =>
        masterRepository.ResolveCanonicalName(clientId, financialYear, "Ledger", name);

    private void AddRcmGeneratedLedger(
        ICollection<MasterRecord> records,
        long clientId,
        string financialYear,
        string company,
        string taxType,
        string sourceLedger,
        decimal taxAmount,
        decimal taxRate,
        string configuredInputLedger,
        string configuredExpenseLedger,
        string treatment)
    {
        if (Math.Abs(taxAmount) <= 0.005m) return;

        var payable = PurchaseTreatmentPolicy.BuildRcmPayableLedgerName(taxType, taxRate);
        AddLedger(records, CanonicalLedger(clientId, financialYear, payable), "Duties & Taxes", company, "Purchase RCM sync");

        // Every RCM Purchase posts the rate-wise RCM Input debit, including Reversed
        // rows. Journal treatment takes or moves that same Input to Receivable/Expenses later.
        var input = PurchaseTreatmentPolicy.BuildRcmInputLedgerName(
            sourceLedger, configuredInputLedger, taxType, taxRate);
        AddLedger(records, CanonicalLedger(clientId, financialYear, input), "Duties & Taxes", company, "Purchase RCM sync");

        if (treatment == PurchaseTreatmentPolicy.RcmReversed)
        {
            var expense = PurchaseTreatmentPolicy.BuildRcmExpenseLedgerName(
                configuredExpenseLedger, taxType, taxRate);
            AddLedger(records, CanonicalLedger(clientId, financialYear, expense), "Indirect Expenses", company, "Purchase RCM sync");
            return;
        }

        if (treatment == PurchaseTreatmentPolicy.RcmNextMonth)
        {
            var pending = PurchaseTreatmentPolicy.BuildRcmReceivableLedgerName(taxType, taxRate);
            AddLedger(records, CanonicalLedger(clientId, financialYear, pending), "Current Assets", company, "Purchase RCM sync");
        }
    }

    private static void AddTaxLedgers(
        ICollection<MasterRecord> records,
        string igst,
        string cgst,
        string sgst,
        string company,
        string source,
        string parent = "Duties & Taxes")
    {
        AddLedger(records, igst, parent, company, source);
        AddLedger(records, cgst, parent, company, source);
        AddLedger(records, sgst, parent, company, source);
    }

    private static void AddLedger(
        ICollection<MasterRecord> records,
        string name,
        string parent,
        string company,
        string source)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        records.Add(Create(name, parent, "Ledger", company, source));
    }

    private static MasterRecord Create(
        string name,
        string parent,
        string type,
        string company,
        string source) =>
        new()
        {
            Name = name.Trim(),
            Parent = parent,
            Type = type,
            Company = company,
            Source = source
        };

    private static string ParentForJournalLine(JournalLine line, JournalGenerationSettings settings)
    {
        var generatedRcmParent =
            PurchaseTreatmentPolicy.ExpectedGeneratedRcmParent(line.LedgerName);
        if (generatedRcmParent.Length > 0)
        {
            return generatedRcmParent;
        }

        var gstAssetParent = GstMasterClassificationPolicy.ExpectedSettlementParent(line.LedgerName, settings);
        if (gstAssetParent.Length > 0)
        {
            return gstAssetParent;
        }

        if (line.LineRole.Contains("Tax", StringComparison.OrdinalIgnoreCase) ||
            line.LedgerName.Contains("GST", StringComparison.OrdinalIgnoreCase))
        {
            return "Duties & Taxes";
        }
        return "Current Liabilities";
    }
}
