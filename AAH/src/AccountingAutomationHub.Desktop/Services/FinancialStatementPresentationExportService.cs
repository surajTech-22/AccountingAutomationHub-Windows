using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Services;

public static class FinancialStatementPresentationExportService
{
    private const string NumberFormat = "#,##0.00;(#,##0.00);\"-\"";

    public static bool Supports(string entityType)
    {
        var profile = FinancialStatementWorkbookService.ResolveProfileSheet(entityType);
        return profile is "NCE" or "C_AS";
    }


    public static bool HasPresentationMappingCatalog(string entityType)
        => FinancialStatementWorkbookService.ResolveProfileSheet(entityType) == "NCE";

    /// <summary>
    /// Reproduces the factory A:B source-group gate from FSforNCE.xlsm/Dropdownmasters NCE.
    /// The original workbook restricts the first statutory mapping dropdown by Tally group,
    /// then applies dependent hierarchy lists. AAH's C1/C2 are Statement/Nature, so this method
    /// returns the equivalent allowed C3 groups used by the canonical AAH hierarchy.
    /// Unknown/custom Tally groups return an empty set so they remain fully user-reviewable.
    /// </summary>
    public static IReadOnlySet<string> GetFactoryAllowedClass3(FinancialStatementLedgerRow row, string profile)
    {
        if (row is null || !(string.Equals(profile, "NCE", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(profile, "C_AS", StringComparison.OrdinalIgnoreCase)))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // R9 FIX5: one source-gate authority only. Do not maintain a second hard-coded copy
        // of the workbook A:B table here; the exact punctuation-preserving source list lives
        // in FinancialStatementWorkbookRules and is shared by both FY/PY dropdown chains.
        var values = FinancialStatementWorkbookRules.GetSourceAllowedC1(profile, row.ParentGroup);
        if (values.Count == 0)
            values = FinancialStatementWorkbookRules.GetSourceAllowedC1(profile, row.PrimaryGroup);
        return new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tally MASTERID is a positive numeric identity. Historic Financial Statements builds
    /// sometimes persisted placeholder values such as 0 and then treated that placeholder as
    /// a shared identity across unrelated ledgers. Never allow a placeholder/non-Tally value to
    /// participate in mapping lookup or FY/PY row matching.
    /// </summary>
    public static bool IsUsableTallyMasterId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0;
    }

    /// <summary>
    /// Reject a saved *partial* hierarchy when its Statement/Nature is impossible for the live
    /// Tally group. Full MappingKey rows are additionally checked by IsObviouslyIncompatibleMapping.
    /// This repairs old cross-row contamination without guessing a replacement C3/C4 classification.
    /// </summary>
    public static bool IsObviouslyIncompatibleHierarchy(
        FinancialStatementLedgerRow row,
        string? class1,
        string? class2,
        string? class3,
        string? class4,
        string profile)
    {
        if (row is null || !profile.Equals("NCE", StringComparison.OrdinalIgnoreCase)) return false;
        var c1 = NormalizeMappingText(class1);
        var c2 = NormalizeMappingText(class2);
        var c3 = NormalizeMappingText(class3);
        if (c1.Length == 0 && c2.Length == 0 && c3.Length == 0) return false;

        var parent = NormalizeMappingText(row.ParentGroup);
        var primary = NormalizeMappingText(row.PrimaryGroup);
        var ledger = NormalizeMappingText(row.Ledger);
        var group = string.IsNullOrWhiteSpace(primary) ? parent : primary;
        var groupText = $"{parent} {primary} {ledger}";
        bool Has(string value) => groupText.Contains(value, StringComparison.Ordinal);

        var isIncome = group is "SALES ACCOUNTS" or "DIRECT INCOMES" or "INDIRECT INCOMES" || Has("SALES ACCOUNT");
        var isPurchase = group is "PURCHASE ACCOUNTS" or "DIRECT EXPENSES" || parent is "PURCHASE ACCOUNTS" or "DIRECT EXPENSES";
        var isIndirectExpense = group == "INDIRECT EXPENSES" || parent == "INDIRECT EXPENSES";
        var isExpense = isPurchase || isIndirectExpense;

        // R15 R14: the supplied trial workbook is the authority for balance nature. Tally's
        // signed closing is not a BS-side classifier: its Cash and Bank examples carry negative
        // debit-nature balances and still remain Assets. Input GST/TDS/advance-tax ledgers can
        // also sit under the Tally Duties & Taxes parent while remaining tax receivables.
        var isInputTaxReceivable = ledger.Contains("INPUT GST", StringComparison.Ordinal) ||
            ledger.Contains("INPUT CGST", StringComparison.Ordinal) || ledger.Contains("INPUT SGST", StringComparison.Ordinal) ||
            ledger.Contains("INPUT IGST", StringComparison.Ordinal) || ledger.Contains("NOT SHOWN IN 2B", StringComparison.Ordinal) ||
            ledger.Contains("GST RECEIVABLE", StringComparison.Ordinal) || ledger.Contains("TDS RECEIVABLE", StringComparison.Ordinal) ||
            ledger.Contains("TCS RECEIVABLE", StringComparison.Ordinal) || ledger.Contains("ADVANCE TAX", StringComparison.Ordinal);
        var isOutputTaxPayable = ledger.Contains("OUTPUT GST", StringComparison.Ordinal) ||
            ledger.Contains("OUTPUT CGST", StringComparison.Ordinal) || ledger.Contains("OUTPUT SGST", StringComparison.Ordinal) ||
            ledger.Contains("OUTPUT IGST", StringComparison.Ordinal) || ledger.Contains("GST PAYABLE", StringComparison.Ordinal) ||
            ledger.Contains("TDS PAYABLE", StringComparison.Ordinal) || ledger.Contains("TCS PAYABLE", StringComparison.Ordinal);

        var isLiability = group is "CAPITAL ACCOUNT" or "RESERVES SURPLUS" or "RESERVES AND SURPLUS" or
            "LOANS LIABILITY" or "SECURED LOANS" or "UNSECURED LOANS" or "SUNDRY CREDITORS" or
            "DUTIES TAXES" or "DUTIES AND TAXES" or "PROVISIONS" or "CURRENT LIABILITIES" or "SUSPENSE AC" ||
            parent is "CAPITAL ACCOUNT" or "SUNDRY CREDITORS" or "TRADE PAYABLES" or "BANK OD AC" ||
            Has("BORROW") || Has("OVERDRAFT");

        var isAsset = group is "FIXED ASSETS" or "INVESTMENTS" or "LOANS ADVANCES ASSET" or
            "LOANS AND ADVANCES ASSET" or "SUNDRY DEBTORS" or "CURRENT ASSETS" or "MISC EXPENSES ASSET" or
            "STOCK IN HAND" or "BANK ACCOUNTS" or "CASH IN HAND" ||
            parent is "FIXED ASSETS" or "INVESTMENTS" or "SUNDRY DEBTORS" or "TRADE RECEIVABLES" or
            "STOCK IN HAND" or "BANK ACCOUNTS" or "CASH IN HAND" or "CASH ON HAND" ||
            isInputTaxReceivable;

        if (isInputTaxReceivable)
        {
            isAsset = true;
            isLiability = false;
        }
        else if (isOutputTaxPayable)
        {
            isLiability = true;
            isAsset = false;
        }

        if (isIncome && (c1 == "BALANCE SHEET" || (c1 == "P L ACCOUNT" && c2 == "EXPENSES"))) return true;
        if (isExpense && (c1 == "BALANCE SHEET" || (c1 == "P L ACCOUNT" && c2 == "INCOME"))) return true;
        if (isAsset && (c1 == "P L ACCOUNT" || (c1 == "BALANCE SHEET" && c2 == "LIABILITIES"))) return true;
        if (isLiability && (c1 == "P L ACCOUNT" || (c1 == "BALANCE SHEET" && c2 == "ASSETS"))) return true;

        // The supplied trial workbook treats Indirect Expenses as P&L > Expenses and requires
        // the user to select the correct expense group. It must never inherit Cost of Goods Sold.
        if (isIndirectExpense && c3 == "COST OF GOODS SOLD") return true;

        return false;
    }

    /// <summary>
    /// Rejects only clearly impossible saved mappings. This is intentionally conservative:
    /// ambiguous ledgers remain user-reviewable instead of being reclassified by name.
    /// It protects the preparation grid from stale R15/R15 R1 mappings such as a Provision
    /// or Sundry Creditor being shown under Cash and bank balances.
    /// </summary>
    public static bool IsObviouslyIncompatibleMapping(FinancialStatementLedgerRow row, string mappingKey, string profile)
    {
        if (row is null || string.IsNullOrWhiteSpace(mappingKey)) return false;
        var key = mappingKey.Trim().ToUpperInvariant();
        var parent = NormalizeMappingText(row.ParentGroup);
        var primary = NormalizeMappingText(row.PrimaryGroup);
        var ledger = NormalizeMappingText(row.Ledger);
        var groupText = $"{parent} {primary}";

        bool Has(string value) => groupText.Contains(value, StringComparison.Ordinal) || ledger.Contains(value, StringComparison.Ordinal);
        bool IsCapital = Has("CAPITAL ACCOUNT") || Has("PARTNER CAPITAL") || Has("PROPRIETOR CAPITAL");
        bool IsBank = Has("BANK ACCOUNT") || Has("CASH CREDIT") || Has("BANK OD") || Has("OVERDRAFT");
        bool IsCash = Has("CASH IN HAND") || Has("CASH ON HAND");
        bool IsCreditor = Has("SUNDRY CREDITOR") || Has("TRADE PAYABLE") || Has("CREDITOR");
        bool IsDebtor = Has("SUNDRY DEBTOR") || Has("TRADE RECEIV") || Has("DEBTOR");
        bool IsProvision = Has("PROVISION");
        bool IsLoan = Has("LOANS LIABILITY") || Has("SECURED LOAN") || Has("UNSECURED LOAN") || Has("BORROW") || Has("BANK OD") || Has("OVERDRAFT");
        bool IsInventory = Has("STOCK IN HAND") || Has("INVENTOR") || Has("STOCK-IN-TRADE") || Has("STOCK IN TRADE");
        bool IsInvestment = Has("INVESTMENT");
        bool IsFixedAsset = Has("FIXED ASSET") || Has("PROPERTY PLANT") || Has("INTANGIBLE") || Has("CWIP") || Has("CAPITAL WORK IN PROGRESS");
        bool IsSales = Has("SALES ACCOUNT") || Has("REVENUE");
        bool IsPurchase = Has("PURCHASE ACCOUNT") || Has("PURCHASES");
        bool IsTaxReceivable = ledger.Contains("INPUT GST", StringComparison.Ordinal) ||
            ledger.Contains("INPUT CGST", StringComparison.Ordinal) || ledger.Contains("INPUT SGST", StringComparison.Ordinal) ||
            ledger.Contains("INPUT IGST", StringComparison.Ordinal) || ledger.Contains("NOT SHOWN IN 2B", StringComparison.Ordinal) ||
            ledger.Contains("GST RECEIVABLE", StringComparison.Ordinal) || ledger.Contains("TDS RECEIVABLE", StringComparison.Ordinal) ||
            ledger.Contains("TCS RECEIVABLE", StringComparison.Ordinal) || ledger.Contains("ADVANCE TAX", StringComparison.Ordinal);
        bool IsTaxPayable = !IsTaxReceivable && (Has("DUTIES TAXES") || Has("DUTIES AND TAXES") ||
            ledger.Contains("GST PAYABLE", StringComparison.Ordinal) || ledger.Contains("OUTPUT GST", StringComparison.Ordinal) ||
            ledger.Contains("OUTPUT CGST", StringComparison.Ordinal) || ledger.Contains("OUTPUT SGST", StringComparison.Ordinal) ||
            ledger.Contains("OUTPUT IGST", StringComparison.Ordinal) || ledger.Contains("TDS PAYABLE", StringComparison.Ordinal) ||
            ledger.Contains("TCS PAYABLE", StringComparison.Ordinal));

        if (key.StartsWith("CASH_", StringComparison.Ordinal) || key.StartsWith("BANK_", StringComparison.Ordinal))
        {
            // Sign is deliberately not tested here. The example workbook keeps Cash/Bank under
            // Assets even when its Tally closing is negative; only the live Tally group/ledger
            // identity determines whether a cash/bank mapping is structurally possible.
            return !(IsCash || IsBank);
        }
        if (key.StartsWith("TP_", StringComparison.Ordinal)) return !IsCreditor;
        if (key == "TR_ALLOW") return !(IsDebtor || IsProvision || Has("DOUBTFUL"));
        if (key.StartsWith("TR_", StringComparison.Ordinal)) return !IsDebtor;
        if (key.StartsWith("PROV_", StringComparison.Ordinal)) return !IsProvision;
        if (key.StartsWith("BORR_", StringComparison.Ordinal) || key.StartsWith("LTB_", StringComparison.Ordinal) || key.StartsWith("STB_", StringComparison.Ordinal)) return !IsLoan;
        if (key.StartsWith("CAP_", StringComparison.Ordinal) || key.StartsWith("CURR_", StringComparison.Ordinal)) return !IsCapital;
        if (key.StartsWith("INV_NC_", StringComparison.Ordinal) || key.StartsWith("INV_CURR_", StringComparison.Ordinal)) return !IsInvestment;
        if (key.StartsWith("INV_", StringComparison.Ordinal)) return !IsInventory;
        if (key.StartsWith("PPE_", StringComparison.Ordinal) || key.StartsWith("INTANG_", StringComparison.Ordinal) || key is "CWIP" or "IAUD") return !IsFixedAsset;
        if (key.StartsWith("REV_", StringComparison.Ordinal))
            return !(IsSales || Has("DIRECT INCOME") || Has("INDIRECT INCOME") || Has("DIRECT EXPENSE") ||
                     Has("INDIRECT EXPENSE") || Has("PRIMARY INCOME") || Has("PRIMARY EXPENSES"));
        if (key is "PUR_SIT" or "MAT_CONS")
            return !(IsPurchase || Has("DIRECT EXPENSE") || Has("INDIRECT EXPENSE") || Has("DIRECT INCOME") ||
                     Has("INDIRECT INCOME") || Has("PRIMARY INCOME") || Has("PRIMARY EXPENSES"));
        if (key is "OCA_TAX" or "LA_LT_TAX" or "LA_ST_TAX") return !IsTaxReceivable;
        if (key == "OCL_STAT") return !IsTaxPayable;
        if (key.StartsWith("DTL_", StringComparison.Ordinal)) return !Has("DEFERRED TAX");
        if (key.StartsWith("DTA_", StringComparison.Ordinal)) return !Has("DEFERRED TAX");

        // For all other keys there may be legitimate entity-specific judgement. Do not
        // guess; leave the user's explicit mapping intact.
        return false;
    }


    // Exact Excel/VBA source-group comparison used by the original workbook dropdown gate:
    // convert NBSP to a normal space, trim only the ends, and compare case-insensitively.
    // Internal spaces and punctuation remain significant exactly as in the workbook VBA.
    private static string NormalizeWorkbookGateText(string? value)
        => (value ?? string.Empty).Replace('\u00A0', ' ').Trim().ToUpperInvariant();

    private static string NormalizeMappingText(string? value)
    {
        var chars = (value ?? string.Empty).ToUpperInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public static bool ShouldReReviewLegacyMapping(string? mappingKey)
    {
        var key = (mappingKey ?? string.Empty).Trim().ToUpperInvariant();
        return key is "REV_GOODS" or "REV_SERV" or "REV_OTHER" or "PUR_SIT" or "MAT_CONS" or "CHG_INV" or
               "EMP" or "FIN" or "OE" or "PPE" or "INTANG" or "INV_NC" or "INV_CURR" or
               "LA_LT" or "LA_ST" or "ONCA" or "OLTL" or "PROV_LT" or "PROV_ST" or
               "BORR_LT_SEC" or "BORR_LT_UNSEC" or "BORR_ST_SEC" or "BORR_ST_UNSEC" or "TP_OTHER";
    }

    public static IReadOnlyList<FinancialStatementMappingOption> BuildPresentationMappingCatalog(string entityType)
    {
        if (!HasPresentationMappingCatalog(entityType)) return Array.Empty<FinancialStatementMappingOption>();
        var rows = new List<FinancialStatementMappingOption>();
        void Add(string fsHead, string note, string noteHead, string subHead, string key,
            string c1, string c2, string c3, string c4, string c5 = "")
            => rows.Add(new FinancialStatementMappingOption(fsHead, noteHead, subHead, key, note, c1, c2, c3, c4, c5));

        const string BS = "Balance Sheet";
        const string PL = "P&L Account";
        const string Assets = "Assets";
        const string Liabilities = "Liabilities";
        const string Income = "Income";
        const string Expenses = "Expenses";

        // R15 R10: reproduce the supplied preparation workbook H1-H5 hierarchy.
        // C1 = statement, C2 = nature, C3 = statutory group, C4 = detailed note line,
        // C5 = optional user detail for 'other/specify' classifications.
        // Internal FsHead/NoteHead/NoteSubHead remain the clean-renderer authority.

        // Note 3 - owners / partners capital and current accounts.
        Add("Owners' capital account", "3", "Owners' Capital Account", "Opening capital", "CAP_OPEN", BS, Liabilities, "Owners' Capital Account", "Capital A/c");
        Add("Owners' capital account", "3", "Owners' Capital Account", "Capital introduced", "CAP_ADD", BS, Liabilities, "Owners' Capital Account", "Capital Introduced");
        Add("Owners' capital account", "3", "Owners' Capital Account", "Withdrawals / drawings", "CAP_DRAW", BS, Liabilities, "Owners' Capital Account", "Capital Drawings A/c");
        Add("Owners' capital account", "3", "Owners' Capital Account", "Opening current account", "CURR_OPEN", BS, Liabilities, "Owners' Capital Account", "Current A/c");
        Add("Owners' capital account", "3", "Owners' Capital Account", "Current account credits", "CURR_CR", BS, Liabilities, "Owners' Capital Account", "Current A/c - Credits");
        Add("Owners' capital account", "3", "Owners' Capital Account", "Current account debits / drawings", "CURR_DR", BS, Liabilities, "Owners' Capital Account", "Current A/c - Debits / Drawings");
        Add("Owners' capital account", "3", "Owners' Capital Account", "Share of profit / (loss)", "CAP_SHAREPL", BS, Liabilities, "Owners' Capital Account", "Share of Profit / (Loss)");
        Add("Owners' capital account", "3", "Owners' Capital Account", "Current account share of profit / (loss)", "CURR_SHAREPL", BS, Liabilities, "Owners' Capital Account", "Current A/c - Share of Profit / (Loss)");

        // Note 4 - reserves and surplus.
        Add("Reserves and surplus", "4", "Reserves and Surplus", "Capital reserve", "RES_CAP", BS, Liabilities, "Reserves and Surplus", "Capital Reserve");
        Add("Reserves and surplus", "4", "Reserves and Surplus", "Securities premium", "RES_SECURITIES", BS, Liabilities, "Reserves and Surplus", "Securities Premium");
        Add("Reserves and surplus", "4", "Reserves and Surplus", "General reserve", "RES_GENERAL", BS, Liabilities, "Reserves and Surplus", "General Reserve");
        Add("Reserves and surplus", "4", "Reserves and Surplus", "Retained earnings / Surplus", "RES_SURPLUS", BS, Liabilities, "Reserves and Surplus", "Retained Earnings / Surplus");
        Add("Reserves and surplus", "4", "Reserves and Surplus", "Other reserves", "RES_OTHER", BS, Liabilities, "Reserves and Surplus", "Other Reserves", "Specify nature");

        // Note 5 - long-term borrowings.
        Add("Long-term borrowings", "5", "Long-term Borrowings", "Term loans from banks", "BORR_LT_BANK", BS, Liabilities, "Borrowings", "Term Loans from Banks");
        Add("Long-term borrowings", "5", "Long-term Borrowings", "Term loans from other parties", "BORR_LT_OTHER_PARTY", BS, Liabilities, "Borrowings", "Term Loans from Other Parties");
        Add("Long-term borrowings", "5", "Long-term Borrowings", "Loans from partners / related parties", "BORR_LT_PARTNER", BS, Liabilities, "Borrowings", "Long-term - Loans from Partners / Related Parties");
        Add("Long-term borrowings", "5", "Long-term Borrowings", "Deposits", "BORR_LT_DEPOSIT", BS, Liabilities, "Borrowings", "Deposits");
        Add("Long-term borrowings", "5", "Long-term Borrowings", "Finance lease obligations", "BORR_LT_LEASE", BS, Liabilities, "Borrowings", "Finance Lease Obligations");
        Add("Long-term borrowings", "5", "Long-term Borrowings", "Other borrowings", "BORR_LT_OTHER", BS, Liabilities, "Borrowings", "Other Borrowings", "Specify nature");

        // Note 6 - deferred-tax balances.
        Add("Deferred tax liabilities (Net)", "6", "Deferred Tax Balances", "Deferred tax liability – depreciation / amortisation", "DTL_DEP", BS, Liabilities, "Deferred Tax Liabilities (Net)", "Depreciation / Amortisation Timing Difference");
        Add("Deferred tax liabilities (Net)", "6", "Deferred Tax Balances", "Deferred tax liability – other timing differences", "DTL_TIMING", BS, Liabilities, "Deferred Tax Liabilities (Net)", "Other Timing Differences");
        Add("Deferred tax liabilities (Net)", "6", "Deferred Tax Balances", "Other deferred tax liabilities", "DTL_OTHER", BS, Liabilities, "Deferred Tax Liabilities (Net)", "Other Deferred Tax Liabilities", "Specify nature");
        Add("Deferred tax assets (Net)", "6", "Deferred Tax Balances", "Deferred tax asset – unabsorbed losses / allowances", "DTA_LOSSES", BS, Assets, "Deferred Tax Assets (Net)", "Unabsorbed Losses / Allowances");
        Add("Deferred tax assets (Net)", "6", "Deferred Tax Balances", "Deferred tax asset – provisions / expenses", "DTA_PROVISIONS", BS, Assets, "Deferred Tax Assets (Net)", "Provisions / Expenses");
        Add("Deferred tax assets (Net)", "6", "Deferred Tax Balances", "Other deferred tax assets", "DTA_OTHER", BS, Assets, "Deferred Tax Assets (Net)", "Other Deferred Tax Assets", "Specify nature");

        // Note 7 - other long-term liabilities.
        Add("Other long-term liabilities", "7", "Other Long-term Liabilities", "Interest accrued but not due on borrowings", "OLTL_INTEREST", BS, Liabilities, "Other Liabilities", "Interest Accrued but not Due on Borrowings");
        Add("Other long-term liabilities", "7", "Other Long-term Liabilities", "Trade / security deposits", "OLTL_DEPOSITS", BS, Liabilities, "Other Liabilities", "Trade / Security Deposits");
        Add("Other long-term liabilities", "7", "Other Long-term Liabilities", "Deferred revenue", "OLTL_DEFERRED_REVENUE", BS, Liabilities, "Other Liabilities", "Deferred Revenue");
        Add("Other long-term liabilities", "7", "Other Long-term Liabilities", "Other long-term liabilities", "OLTL_OTHER", BS, Liabilities, "Other Liabilities", "Other Long-term Liabilities", "Specify nature");

        // Note 8 - long-term provisions.
        Add("Long-term provisions", "8", "Long-term Provisions", "Provision for employee benefits", "PROV_LT_EMP", BS, Liabilities, "Provisions", "Long-term - Employee Benefits");
        Add("Long-term provisions", "8", "Long-term Provisions", "Provision for taxation", "PROV_LT_TAX", BS, Liabilities, "Provisions", "Long-term - Taxation");
        Add("Long-term provisions", "8", "Long-term Provisions", "Provision for warranties / obligations", "PROV_LT_WARRANTY", BS, Liabilities, "Provisions", "Long-term - Warranties / Obligations");
        Add("Long-term provisions", "8", "Long-term Provisions", "Other long-term provisions", "PROV_LT_OTHER", BS, Liabilities, "Provisions", "Other Long-term Provisions", "Specify nature");

        // Note 9 - short-term borrowings.
        Add("Short-term borrowings", "9", "Short-term Borrowings", "Loans repayable on demand from banks", "BORR_ST_DEMAND_BANK", BS, Liabilities, "Borrowings", "Loans Repayable on Demand from Banks");
        Add("Short-term borrowings", "9", "Short-term Borrowings", "Loans from banks", "BORR_ST_BANK", BS, Liabilities, "Borrowings", "Loans from Banks");
        Add("Short-term borrowings", "9", "Short-term Borrowings", "Loans from other parties", "BORR_ST_OTHER_PARTY", BS, Liabilities, "Borrowings", "Loans from Other Parties");
        Add("Short-term borrowings", "9", "Short-term Borrowings", "Loans from partners / related parties", "BORR_ST_PARTNER", BS, Liabilities, "Borrowings", "Short-term - Loans from Partners / Related Parties");
        Add("Short-term borrowings", "9", "Short-term Borrowings", "Other short-term borrowings", "BORR_ST_OTHER", BS, Liabilities, "Borrowings", "Other Short-term Borrowings", "Specify nature");

        // Note 10 - trade payables.
        Add("Trade payables", "10", "Trade Payables", "MSME dues", "TP_MSME", BS, Liabilities, "Trade Payables", "MSME Dues");
        Add("Trade payables", "10", "Trade Payables", "Other trade payables", "TP_OTHER", BS, Liabilities, "Trade Payables", "Other Trade Payables");
        Add("Trade payables", "10", "Trade Payables", "Interest due under MSMED Act", "TP_MSME_INTEREST", BS, Liabilities, "Trade Payables", "Interest due under MSMED Act");
        Add("Trade payables", "10", "Trade Payables", "Unbilled trade payables", "TP_UNBILLED", BS, Liabilities, "Trade Payables", "Unbilled Trade Payables");
        Add("Trade payables", "10", "Trade Payables", "Other trade payable balances", "TP_BAL_OTHER", BS, Liabilities, "Trade Payables", "Other Trade Payable Balances", "Specify nature");

        // Note 11 - other current liabilities.
        Add("Other current liabilities", "11", "Other Current Liabilities", "Current maturities of long-term debt", "OCL_MATURITY", BS, Liabilities, "Other Liabilities", "Current Maturities of Long-term Debt");
        Add("Other current liabilities", "11", "Other Current Liabilities", "Interest accrued and due", "OCL_INT_DUE", BS, Liabilities, "Other Liabilities", "Interest Accrued and Due");
        Add("Other current liabilities", "11", "Other Current Liabilities", "Statutory dues payable", "OCL_STAT", BS, Liabilities, "Other Liabilities", "Statutory Dues Payable");
        Add("Other current liabilities", "11", "Other Current Liabilities", "Advances from customers", "OCL_ADV", BS, Liabilities, "Other Liabilities", "Advances from Customers");
        Add("Other current liabilities", "11", "Other Current Liabilities", "Expenses payable", "OCL_ACCR", BS, Liabilities, "Other Liabilities", "Expenses Payable");
        Add("Other current liabilities", "11", "Other Current Liabilities", "Other current liabilities", "OCL_OTHER", BS, Liabilities, "Other Liabilities", "Other Current Liabilities", "Specify nature");

        // Note 12 - short-term provisions.
        Add("Short-term provisions", "12", "Short-term Provisions", "Provision for employee benefits", "PROV_ST_EMP", BS, Liabilities, "Provisions", "Short-term - Employee Benefits");
        Add("Short-term provisions", "12", "Short-term Provisions", "Provision for tax", "PROV_ST_TAX", BS, Liabilities, "Provisions", "Short-term - Tax");
        Add("Short-term provisions", "12", "Short-term Provisions", "Provision for expenses / obligations", "PROV_ST_EXP", BS, Liabilities, "Provisions", "Short-term - Expenses / Obligations");
        Add("Short-term provisions", "12", "Short-term Provisions", "Other short-term provisions", "PROV_ST_OTHER", BS, Liabilities, "Provisions", "Other Short-term Provisions", "Specify nature");

        // Note 13 - PPE / intangible / CWIP.
        Add("Property, Plant and Equipment", "13", "Property, Plant and Equipment and Intangible Assets", "Freehold land", "PPE_LAND", BS, Assets, "PPE & IA (Net)", "Freehold Land");
        Add("Property, Plant and Equipment", "13", "Property, Plant and Equipment and Intangible Assets", "Buildings", "PPE_BUILDING", BS, Assets, "PPE & IA (Net)", "Buildings");
        Add("Property, Plant and Equipment", "13", "Property, Plant and Equipment and Intangible Assets", "Plant and equipment", "PPE_PLANT", BS, Assets, "PPE & IA (Net)", "Plant and Equipment");
        Add("Property, Plant and Equipment", "13", "Property, Plant and Equipment and Intangible Assets", "Furniture and fixtures", "PPE_FURNITURE", BS, Assets, "PPE & IA (Net)", "Furniture and Fixtures");
        Add("Property, Plant and Equipment", "13", "Property, Plant and Equipment and Intangible Assets", "Vehicles", "PPE_VEHICLES", BS, Assets, "PPE & IA (Net)", "Vehicles");
        Add("Property, Plant and Equipment", "13", "Property, Plant and Equipment and Intangible Assets", "Office equipment", "PPE_OFFICE", BS, Assets, "PPE & IA (Net)", "Office Equipment");
        Add("Property, Plant and Equipment", "13", "Property, Plant and Equipment and Intangible Assets", "Computers and peripherals", "PPE_COMPUTERS", BS, Assets, "PPE & IA (Net)", "Computers and Peripherals");
        Add("Property, Plant and Equipment", "13", "Property, Plant and Equipment and Intangible Assets", "Other PPE", "PPE_OTHER", BS, Assets, "PPE & IA (Net)", "Other PPE", "Specify nature");
        Add("Intangible assets", "13", "Property, Plant and Equipment and Intangible Assets", "Goodwill", "INTANG_GOODWILL", BS, Assets, "PPE & IA (Net)", "Goodwill");
        Add("Intangible assets", "13", "Property, Plant and Equipment and Intangible Assets", "Computer software", "INTANG_SOFTWARE", BS, Assets, "PPE & IA (Net)", "Computer Software");
        Add("Intangible assets", "13", "Property, Plant and Equipment and Intangible Assets", "Other intangible assets", "INTANG_OTHER", BS, Assets, "PPE & IA (Net)", "Other Intangible Assets", "Specify nature");
        Add("Capital work-in-progress", "13", "Property, Plant and Equipment and Intangible Assets", "Capital work-in-progress", "CWIP", BS, Assets, "PPE & IA (Net)", "Capital Work-in-progress");
        Add("Intangible assets under development", "13", "Property, Plant and Equipment and Intangible Assets", "Intangible assets under development", "IAUD", BS, Assets, "PPE & IA (Net)", "Intangible Assets under Development");

        // Note 14 - investments.
        Add("Non-current investments", "14", "Investments", "Non-current – Equity instruments", "INV_NC_EQUITY", BS, Assets, "Investments", "Non-current - Equity Instruments");
        Add("Non-current investments", "14", "Investments", "Non-current – Preference shares", "INV_NC_PREF", BS, Assets, "Investments", "Non-current - Preference Shares");
        Add("Non-current investments", "14", "Investments", "Non-current – Government / debt securities", "INV_NC_DEBT", BS, Assets, "Investments", "Non-current - Government / Debt Securities");
        Add("Non-current investments", "14", "Investments", "Non-current – Mutual funds", "INV_NC_MF", BS, Assets, "Investments", "Non-current - Mutual Funds");
        Add("Non-current investments", "14", "Investments", "Non-current – Other investments", "INV_NC_OTHER", BS, Assets, "Investments", "Non-current - Other Investments", "Specify nature");
        Add("Current investments", "14", "Investments", "Current – Equity / debt instruments", "INV_CURR_EQUITY_DEBT", BS, Assets, "Investments", "Current - Equity / Debt Instruments");
        Add("Current investments", "14", "Investments", "Current – Mutual funds", "INV_CURR_MF", BS, Assets, "Investments", "Current - Mutual Funds");
        Add("Current investments", "14", "Investments", "Current – Other investments", "INV_CURR_OTHER", BS, Assets, "Investments", "Current - Other Investments", "Specify nature");

        // Note 15 - long-term loans and advances.
        Add("Long-term loans and advances", "15", "Long-term Loans and Advances", "Capital advances", "LA_LT_CAPITAL", BS, Assets, "Loans and Advances", "Long-term - Capital Advances");
        Add("Long-term loans and advances", "15", "Long-term Loans and Advances", "Loans to partners / related parties", "LA_LT_PARTNER", BS, Assets, "Loans and Advances", "Long-term - Loans to Partners / Related Parties");
        Add("Long-term loans and advances", "15", "Long-term Loans and Advances", "Security deposits", "LA_LT_SECURITY", BS, Assets, "Loans and Advances", "Long-term - Security Deposits");
        Add("Long-term loans and advances", "15", "Long-term Loans and Advances", "Advance tax / TDS", "LA_LT_TAX", BS, Assets, "Loans and Advances", "Long-term - Advance Tax / TDS");
        Add("Long-term loans and advances", "15", "Long-term Loans and Advances", "Other long-term loans and advances", "LA_LT_OTHER", BS, Assets, "Loans and Advances", "Other Long-term Loans and Advances", "Specify nature");

        // Note 16 - other non-current assets.
        Add("Other non-current assets", "16", "Other Non-current Assets", "Interest accrued on deposits", "ONCA_INTEREST", BS, Assets, "Other Assets", "Non-current - Interest Accrued on Deposits");
        Add("Other non-current assets", "16", "Other Non-current Assets", "Security deposits", "ONCA_SECURITY", BS, Assets, "Other Assets", "Non-current - Security Deposits");
        Add("Other non-current assets", "16", "Other Non-current Assets", "Prepaid expenses", "ONCA_PREPAID", BS, Assets, "Other Assets", "Non-current - Prepaid Expenses");
        Add("Other non-current assets", "16", "Other Non-current Assets", "Balances with government authorities", "ONCA_GOVT", BS, Assets, "Other Assets", "Non-current - Balances with Government Authorities");
        Add("Other non-current assets", "16", "Other Non-current Assets", "Other non-current assets", "ONCA_OTHER", BS, Assets, "Other Assets", "Other Non-current Assets", "Specify nature");

        // Note 17 - inventories.
        Add("Inventories", "17", "Inventories", "Raw materials", "INV_RM", BS, Assets, "Inventories", "Raw Materials");
        Add("Inventories", "17", "Inventories", "Work-in-progress", "INV_WIP", BS, Assets, "Inventories", "Work-in-progress");
        Add("Inventories", "17", "Inventories", "Finished goods", "INV_FG", BS, Assets, "Inventories", "Finished Goods");
        Add("Inventories", "17", "Inventories", "Stock-in-trade", "INV_SIT", BS, Assets, "Inventories", "Stock-in-trade");
        Add("Inventories", "17", "Inventories", "Stores and spares", "INV_STORES", BS, Assets, "Inventories", "Stores and Spares");
        Add("Inventories", "17", "Inventories", "Packing materials", "INV_PACKING", BS, Assets, "Inventories", "Packing Materials");
        Add("Inventories", "17", "Inventories", "Other inventories", "INV_OTHER", BS, Assets, "Inventories", "Other Inventories", "Specify nature");

        // Note 18 - trade receivables.
        Add("Trade receivables", "18", "Trade Receivables", "Secured, considered good", "TR_SEC_GOOD", BS, Assets, "Trade Receivables", "Secured, Considered Good");
        Add("Trade receivables", "18", "Trade Receivables", "Unsecured, considered good", "TR_UNSEC_GOOD", BS, Assets, "Trade Receivables", "Unsecured, Considered Good");
        Add("Trade receivables", "18", "Trade Receivables", "Doubtful receivables", "TR_DOUBT", BS, Assets, "Trade Receivables", "Doubtful Receivables");
        Add("Trade receivables", "18", "Trade Receivables", "Less: Provision for doubtful receivables", "TR_ALLOW", BS, Assets, "Trade Receivables", "Less: Provision for Doubtful Receivables");
        Add("Trade receivables", "18", "Trade Receivables", "Unbilled receivables", "TR_UNBILLED", BS, Assets, "Trade Receivables", "Unbilled Receivables");

        // Note 19 - cash and bank balances.
        Add("Cash and Cash Equivalents", "19", "Cash and Cash Equivalents", "Cash on hand", "CASH_HAND", BS, Assets, "Cash and Bank Balance", "Cash on Hand");
        Add("Cash and Cash Equivalents", "19", "Cash and Cash Equivalents", "Balances with banks – current accounts", "CASH_BANK_CURRENT", BS, Assets, "Cash and Bank Balance", "Current Accounts");
        Add("Cash and Cash Equivalents", "19", "Cash and Cash Equivalents", "Deposits with original maturity up to three months", "CASH_DEP_3M", BS, Assets, "Cash and Bank Balance", "Deposits up to Three Months");
        Add("Cash and Cash Equivalents", "19", "Cash and Cash Equivalents", "Other bank balances", "BANK_OTHER", BS, Assets, "Cash and Bank Balance", "Other Bank Balances", "Specify nature");
        Add("Cash and Cash Equivalents", "19", "Cash and Cash Equivalents", "Earmarked / lien deposits", "BANK_EARMARKED", BS, Assets, "Cash and Bank Balance", "Earmarked / Lien Deposits");

        // Note 20 - short-term loans and advances.
        Add("Short-term loans and advances", "20", "Short-term Loans and Advances", "Capital advances", "LA_ST_CAPITAL", BS, Assets, "Loans and Advances", "Short-term - Capital Advances");
        Add("Short-term loans and advances", "20", "Short-term Loans and Advances", "Loans to partners / related parties", "LA_ST_PARTNER", BS, Assets, "Loans and Advances", "Short-term - Loans to Partners / Related Parties");
        Add("Short-term loans and advances", "20", "Short-term Loans and Advances", "Prepaid expenses", "LA_ST_PREPAID", BS, Assets, "Loans and Advances", "Short-term - Prepaid Expenses");
        Add("Short-term loans and advances", "20", "Short-term Loans and Advances", "Advance tax / TDS", "LA_ST_TAX", BS, Assets, "Loans and Advances", "Short-term - Advance Tax / TDS");
        Add("Short-term loans and advances", "20", "Short-term Loans and Advances", "Balances with government authorities", "LA_ST_GOVT", BS, Assets, "Loans and Advances", "Short-term - Balances with Government Authorities");
        Add("Short-term loans and advances", "20", "Short-term Loans and Advances", "Other advances", "LA_ST_OTHER", BS, Assets, "Loans and Advances", "Other Short-term Advances", "Specify nature");

        // Note 21 - other current assets.
        Add("Other current assets", "21", "Other Current Assets", "Interest accrued but not due", "OCA_INT_NOT_DUE", BS, Assets, "Other Assets", "Current - Interest Accrued but not Due");
        Add("Other current assets", "21", "Other Current Assets", "Interest accrued and due", "OCA_INT_DUE", BS, Assets, "Other Assets", "Current - Interest Accrued and Due");
        Add("Other current assets", "21", "Other Current Assets", "Security deposits", "OCA_SECURITY", BS, Assets, "Other Assets", "Current - Security Deposits");
        Add("Other current assets", "21", "Other Current Assets", "Advance to suppliers", "OCA_SUPPLIER_ADV", BS, Assets, "Other Assets", "Current - Advance to Suppliers");
        Add("Other current assets", "21", "Other Current Assets", "Prepaid expenses", "OCA_PRE", BS, Assets, "Other Assets", "Current - Prepaid Expenses");
        Add("Other current assets", "21", "Other Current Assets", "Tax / GST receivables", "OCA_TAX", BS, Assets, "Other Assets", "Current - Tax / GST Receivables");
        Add("Other current assets", "21", "Other Current Assets", "Other current assets", "OCA_OTHER", BS, Assets, "Other Assets", "Other Current Assets", "Specify nature");

        // Note 22 - revenue from operations.
        Add("Revenue from operations", "22", "Revenue from Operations", "Sale of products", "REV_GOODS", PL, Income, "Revenue from Operations", "Sale of Products");
        Add("Revenue from operations", "22", "Revenue from Operations", "Sale of services", "REV_SERV", PL, Income, "Revenue from Operations", "Sale of Services");
        Add("Revenue from operations", "22", "Revenue from Operations", "Grants / donations received", "REV_GRANTS", PL, Income, "Revenue from Operations", "Grants / Donations Received");
        Add("Revenue from operations", "22", "Revenue from Operations", "Other operating revenue", "REV_OTHER", PL, Income, "Revenue from Operations", "Other Operating Revenue", "Specify nature");
        Add("Revenue from operations", "22", "Revenue from Operations", "Less: Excise duty / indirect taxes", "REV_LESS_TAX", PL, Income, "Revenue from Operations", "Less: Excise Duty / Indirect Taxes");

        // Note 23 - other income.
        Add("Other income", "23", "Other Income", "Interest income", "OI_INT", PL, Income, "Other Income", "Interest Income");
        Add("Other income", "23", "Other Income", "Dividend income", "OI_DIV", PL, Income, "Other Income", "Dividend Income");
        Add("Other income", "23", "Other Income", "Net gain on sale of investments", "OI_GAIN_INV", PL, Income, "Other Income", "Net Gain on Sale of Investments");
        Add("Other income", "23", "Other Income", "Rental income", "OI_RENT", PL, Income, "Other Income", "Rental Income");
        Add("Other income", "23", "Other Income", "Other non-operating income", "OI_OTHER", PL, Income, "Other Income", "Other Non-operating Income", "Specify nature");

        // Note 24 - cost of goods sold. Opening/closing inventory are generated by the renderer.
        Add("Cost of goods sold", "24", "Cost of Goods Sold", "Purchases", "PUR_SIT", PL, Expenses, "Cost of Goods Sold", "Purchases");
        Add("Cost of goods sold", "24", "Cost of Goods Sold", "Direct expenses", "MAT_CONS", PL, Expenses, "Cost of Goods Sold", "Direct Expenses");
        Add("Cost of goods sold", "24", "Cost of Goods Sold", "Other cost of goods sold", "CHG_INV", PL, Expenses, "Cost of Goods Sold", "Other Cost of Goods Sold", "Specify nature");

        // Note 25 - employee benefits.
        Add("Employee benefits expense", "25", "Employee Benefits Expense", "Salaries and wages", "EMP_SALARY", PL, Expenses, "Employee Benefits Expense", "Salaries and Wages");
        Add("Employee benefits expense", "25", "Employee Benefits Expense", "Partners / employee remuneration", "EMP_REMUN", PL, Expenses, "Employee Benefits Expense", "Partners / Employee Remuneration");
        Add("Employee benefits expense", "25", "Employee Benefits Expense", "Contribution to provident and other funds", "EMP_PF", PL, Expenses, "Employee Benefits Expense", "Contribution to Provident and Other Funds");
        Add("Employee benefits expense", "25", "Employee Benefits Expense", "Staff welfare expenses", "EMP_WELFARE", PL, Expenses, "Employee Benefits Expense", "Staff Welfare Expenses");
        Add("Employee benefits expense", "25", "Employee Benefits Expense", "Other employee benefits", "EMP_OTHER", PL, Expenses, "Employee Benefits Expense", "Other Employee Benefits", "Specify nature");

        // Note 26 - finance costs.
        Add("Finance costs", "26", "Finance Costs", "Interest expense", "FIN_INTEREST", PL, Expenses, "Finance Costs", "Interest Expense");
        Add("Finance costs", "26", "Finance Costs", "Bank charges", "FIN_BANK_CHARGES", PL, Expenses, "Finance Costs", "Bank Charges");
        Add("Finance costs", "26", "Finance Costs", "Interest on partner capital", "FIN_PARTNER_INT", PL, Expenses, "Finance Costs", "Interest on Partner Capital");
        Add("Finance costs", "26", "Finance Costs", "Other borrowing costs", "FIN_OTHER", PL, Expenses, "Finance Costs", "Other Borrowing Costs", "Specify nature");

        // Note 27 - depreciation / amortisation.
        Add("Depreciation and amortisation expense", "27", "Depreciation and Amortisation Expense", "Depreciation on Property, Plant and Equipment", "DEP", PL, Expenses, "Depreciation and Amortisation Expense", "Depreciation on PPE");
        Add("Depreciation and amortisation expense", "27", "Depreciation and Amortisation Expense", "Amortisation of Intangible Assets", "AMORT", PL, Expenses, "Depreciation and Amortisation Expense", "Amortisation of Intangible Assets");
        Add("Depreciation and amortisation expense", "27", "Depreciation and Amortisation Expense", "Impairment / other adjustments", "DEP_IMPAIR", PL, Expenses, "Depreciation and Amortisation Expense", "Impairment / Other Adjustments");
        Add("Depreciation and amortisation expense", "27", "Depreciation and Amortisation Expense", "Other depreciation / amortisation", "DEP_OTHER", PL, Expenses, "Depreciation and Amortisation Expense", "Other Depreciation / Amortisation", "Specify nature");

        // Note 28 - other expenses.
        Add("Other expenses", "28", "Other Expenses", "Consumption of stores and spares", "OE_STORES", PL, Expenses, "Other Expenses", "Consumption of Stores and Spares");
        Add("Other expenses", "28", "Other Expenses", "Power and fuel", "OE_POWER", PL, Expenses, "Other Expenses", "Power and Fuel");
        Add("Other expenses", "28", "Other Expenses", "Rent", "OE_RENT", PL, Expenses, "Other Expenses", "Rent");
        Add("Other expenses", "28", "Other Expenses", "Repairs and maintenance", "OE_REPAIRS", PL, Expenses, "Other Expenses", "Repairs and Maintenance");
        Add("Other expenses", "28", "Other Expenses", "Insurance", "OE_INSURANCE", PL, Expenses, "Other Expenses", "Insurance");
        Add("Other expenses", "28", "Other Expenses", "Rates and taxes", "OE_RATES_TAX", PL, Expenses, "Other Expenses", "Rates and Taxes");
        Add("Other expenses", "28", "Other Expenses", "Auditor remuneration", "OE_AUDITOR", PL, Expenses, "Other Expenses", "Auditor Remuneration");
        Add("Other expenses", "28", "Other Expenses", "Professional charges", "OE_PROFESSIONAL", PL, Expenses, "Other Expenses", "Professional Charges");
        Add("Other expenses", "28", "Other Expenses", "CSR expenditure", "OE_CSR", PL, Expenses, "Other Expenses", "CSR Expenditure");
        Add("Other expenses", "28", "Other Expenses", "Other expenses", "OE_OTHER", PL, Expenses, "Other Expenses", "Other Expenses", "Specify nature");

        // Face-statement-only buckets below numbered notes.
        Add("Exceptional items", "", "Exceptional Items", "Exceptional items", "EXC", PL, Expenses, "Exceptional Items", "Exceptional Items");
        Add("Extraordinary items", "", "Extraordinary Items", "Extraordinary items", "EXTRAORDINARY", PL, Expenses, "Extraordinary Items", "Extraordinary Items");
        Add("Partners' remuneration", "", "Partners' Remuneration", "Partners' remuneration", "PARTNER_REMUN", PL, Expenses, "Partners' Remuneration", "Partners' Remuneration");
        Add("Tax expense", "", "Tax Expense", "Current tax", "TAX_CURR", PL, Expenses, "Tax Expense", "Current Tax");
        Add("Tax expense", "", "Tax Expense", "Deferred tax", "TAX_DEFER", PL, Expenses, "Tax Expense", "Deferred Tax");
        Add("Tax expense", "", "Tax Expense", "Earlier-year tax adjustment", "TAX_PRIOR", PL, Expenses, "Tax Expense", "Earlier-year Tax Adjustment");
        Add("Profit/(Loss) from Discontinuing Operations", "", "Discontinuing Operations", "Profit/(Loss) from Discontinuing Operations", "DISCONT_PROFIT", PL, Expenses, "Other Profit and Loss Items", "Profit/(Loss) from Discontinuing Operations");
        Add("Tax Expense of Discontinuing Operations", "", "Discontinuing Operations", "Tax Expense of Discontinuing Operations", "DISCONT_TAX", PL, Expenses, "Other Profit and Loss Items", "Tax Expense of Discontinuing Operations");

        return FinancialStatementClassificationService.NormalizeProfileCatalog(entityType, rows);
    }

    public sealed record HierarchySuggestion(string Class1, string Class2, string Class3, string Class4 = "", string Class5 = "");

    public static HierarchySuggestion? SuggestTrialHierarchy(FinancialStatementLedgerRow row, string entityType)
    {
        if (!HasPresentationMappingCatalog(entityType) || row is null) return null;
        var parent = NormalizeMappingText(row.ParentGroup);
        var primary = NormalizeMappingText(row.PrimaryGroup);
        var ledger = NormalizeMappingText(row.Ledger);
        var group = string.IsNullOrWhiteSpace(primary) ? parent : primary;
        var profile = FinancialStatementWorkbookService.ResolveProfileSheet(entityType);

        const string BS = "Balance Sheet";
        const string PL = "P&L Account";
        const string A = "Assets";
        const string L = "Liabilities";
        const string I = "Income";
        const string E = "Expenses";

        bool LedgerHas(string value) => ledger.Contains(value, StringComparison.Ordinal);
        bool GroupIs(params string[] values) => values.Any(x => group.Equals(x, StringComparison.Ordinal) || parent.Equals(x, StringComparison.Ordinal));
        HierarchySuggestion Make(string statement, string nature, string c1 = "", string c2 = "", string c3 = "")
            => new(statement, nature, c1, c2, c3);

        // FIXED8 authority: automatic grouping mirrors the supplied TB examples and stops at
        // the highest level the workbook proves. It never invents a detailed C2 merely from a
        // broad Tally group. The dropdowns remain available for the user to complete the exact
        // workbook path.

        // R22 FIXED6 R5: the standard Tally Profit & Loss A/c is a Balance Sheet
        // liabilities/equity bridge in the supplied Financial Statements workbook. Its debit
        // or credit sign and a generic Primary parent can never move it to Assets.
        if (FinancialStatementTrialBalanceOrder.IsProfitLossAccountLedger(row.Ledger))
            return Make(BS, L, "Reserves and Surplus", "Surplus");

        // Stock-in-Hand is a Balance Sheet inventory source in the supplied TB. Earlier builds
        // incorrectly pushed Opening Stock to P&L/Purchases.
        if (GroupIs("STOCK IN HAND", "INVENTORIES") || LedgerHas("OPENING STOCK"))
            return Make(BS, A, "Inventories", "As per Stock Sheet");

        // NCE capital uses Owner/Partners Capital Account; Schedule III uses Share Capital.
        if (GroupIs("CAPITAL ACCOUNT"))
        {
            if (profile.Equals("NCE", StringComparison.OrdinalIgnoreCase))
            {
                var account = LedgerHas("CURRENT") || LedgerHas("DRAWING") ? "Current Account" : "Capital Account";
                return Make(BS, L, "Owner/Partners Capital Account", account);
            }
            return Make(BS, L, "Share Capital", "Subscribed and Fully Paid");
        }

        if (GroupIs("RESERVES SURPLUS", "RESERVES AND SURPLUS"))
        {
            // Profit & Loss A/c is the only sample row with a deterministic C2. Other reserve
            // ledgers keep C2 blank until the exact reserve type is selected.
            if (LedgerHas("PROFIT") && LedgerHas("LOSS")) return Make(BS, L, "Reserves and Surplus", "Surplus");
            return Make(BS, L, "Reserves and Surplus");
        }

        if (GroupIs("PRIMARY LIABILITIES") && LedgerHas("PROFIT") && LedgerHas("LOSS"))
            return Make(BS, L, "Reserves and Surplus", "Surplus");

        if (GroupIs("SUNDRY CREDITORS", "TRADE PAYABLES"))
            return Make(BS, L, "Trade Payables", "As per Creditors/Debtors Sheets");
        if (GroupIs("SUNDRY DEBTORS", "TRADE RECEIVABLES"))
            return Make(BS, A, "Trade Receivables", "As per Creditors/Debtors Sheets");

        if (GroupIs("CASH IN HAND", "CASH ON HAND"))
            return Make(BS, A, "Cash and Cash Equivalents", "Cash on Hand");
        if (GroupIs("BANK ACCOUNTS"))
        {
            if (LedgerHas("OVERDRAFT") || LedgerHas("BANK OD") || LedgerHas("CASH CREDIT"))
                return Make(BS, L, "Borrowings - Secured", "Loans Repayable on Demand from Bank");
            return Make(BS, A, "Cash and Cash Equivalents", "Balances with Banks", "Current Accounts");
        }
        if (GroupIs("BANK OD AC") || LedgerHas("OVERDRAFT"))
            return Make(BS, L, "Borrowings - Secured", "Loans Repayable on Demand from Bank");

        // Duties & Taxes can legitimately contain both receivables and payables in Tally. Only
        // explicit statutory wording chooses the side; otherwise leave the detailed hierarchy
        // for review instead of guessing from sign.
        var inputTax = LedgerHas("INPUT GST") || LedgerHas("INPUT CGST") || LedgerHas("INPUT SGST") ||
            LedgerHas("INPUT IGST") || LedgerHas("NOT SHOWN IN 2B") || LedgerHas("GST RECEIVABLE");
        var directTaxReceivable = LedgerHas("TDS RECEIVABLE") || LedgerHas("TCS RECEIVABLE") || LedgerHas("ADVANCE TAX");
        var tdsPayable = LedgerHas("TDS PAYABLE") || LedgerHas("TCS PAYABLE");
        if (inputTax) return Make(BS, A, "Other Assets", "GST Input Credit Receivable");
        if (directTaxReceivable) return Make(BS, A, "Other Assets", "Advance IT/TDS");
        if (LedgerHas("GST PAYABLE") || LedgerHas("OUTPUT GST") || LedgerHas("OUTPUT CGST") ||
            LedgerHas("OUTPUT SGST") || LedgerHas("OUTPUT IGST") || tdsPayable)
            return tdsPayable
                ? Make(BS, L, "Other Liabilities", "TDS/TCS Payable")
                : Make(BS, L, "Other Liabilities", "GST Payable");
        if (GroupIs("DUTIES TAXES", "DUTIES AND TAXES")) return Make(BS, L);

        // Borrowing groups establish C1, but C2 is left blank unless the source itself proves a
        // bank overdraft/demand borrowing. This avoids labelling every secured loan as a bank term loan.
        if (GroupIs("UNSECURED LOANS")) return Make(BS, L, "Borrowings - Unsecured");
        if (GroupIs("SECURED LOANS")) return Make(BS, L, "Borrowings - Secured");
        if (GroupIs("LOANS LIABILITY")) return Make(BS, L);

        if (GroupIs("PROVISIONS"))
        {
            if (LedgerHas("AUDIT") && LedgerHas("FEE")) return Make(BS, L, "Provisions", "Provision for Audit Fees");
            if (LedgerHas("GRATUITY")) return Make(BS, L, "Provisions", "Provision for Gratuity");
            if (LedgerHas("LEAVE") && LedgerHas("ENCASH")) return Make(BS, L, "Provisions", "Provision for Leave Encashment");
            if (LedgerHas("INCOME TAX") || LedgerHas("TAX PROVISION")) return Make(BS, L, "Provisions", "Provision for Income Tax");
            // The original sample sets C1=Provisions but leaves C2 blank for a generic payable.
            return Make(BS, L, "Provisions");
        }

        if (GroupIs("CURRENT LIABILITIES"))
        {
            if (LedgerHas("SALARY") || LedgerHas("SALARIES")) return Make(BS, L, "Other Liabilities", "Salaries Payable");
            if (LedgerHas("ADVANCE") && LedgerHas("CUSTOMER")) return Make(BS, L, "Other Liabilities", "Advance from Customers");
            return Make(BS, L);
        }

        // Suspense is explicitly unresolved in the supplied workbook. Do not auto-classify it.
        if (GroupIs("SUSPENSE AC")) return Make(BS, L);

        if (GroupIs("FIXED ASSETS"))
        {
            if (LedgerHas("VEHICLE") || LedgerHas("VEHICAL") || LedgerHas("TIPPER") || LedgerHas("CAR ") || LedgerHas("BIKE"))
                return Make(BS, A, "PPE (Net)", "Vehicles");
            if (LedgerHas("PLANT") || LedgerHas("MACHIN")) return Make(BS, A, "PPE (Net)", "Plant and Equipment");
            if (LedgerHas("FURNITURE") || LedgerHas("FIXTURE")) return Make(BS, A, "PPE (Net)", "Furniture and Fixtures");
            if (LedgerHas("COMPUTER") || LedgerHas("LAPTOP")) return Make(BS, A, "PPE (Net)", "Computers and Peripherals");
            if (LedgerHas("SOFTWARE")) return Make(BS, A, "Intangible Assets (Net)", "Computer Software");
            if (LedgerHas("BUILDING")) return Make(BS, A, "PPE (Net)", "Buildings");
            if (LedgerHas("LAND")) return Make(BS, A, "PPE (Net)", "Land");
            if (LedgerHas("OFFICE") || LedgerHas("UPS") || LedgerHas("EQUIPMENT") || LedgerHas("AIR COOLER") || LedgerHas("INVERTER"))
                return Make(BS, A, "PPE (Net)", "Office Equipment");
            return Make(BS, A, "PPE (Net)");
        }

        // These defaults match the populated rows in the supplied TB examples. C2 is deliberately
        // blank because the workbook expects the user to choose the detailed note line.
        if (GroupIs("INVESTMENTS")) return Make(BS, A, "Other Investments - Unquoted");
        if (GroupIs("DEPOSITS ASSET")) return Make(BS, A, "Other Assets");
        if (GroupIs("MISC EXPENSES ASSET")) return Make(BS, A, "Other Assets");

        if (GroupIs("LOANS ADVANCES ASSET", "LOANS AND ADVANCES ASSET", "CURRENT ASSETS"))
        {
            if (inputTax) return Make(BS, A, "Other Assets", "GST Input Credit Receivable");
            if (directTaxReceivable) return Make(BS, A, "Other Assets", "Advance IT/TDS");
            if (LedgerHas("PREPAID")) return Make(BS, A, "Other Assets", "Prepaid Expenses");
            if (LedgerHas("ADVANCE") && LedgerHas("SUPPL")) return Make(BS, A, "Other Assets", "Advance to Suppliers");
            return Make(BS, A);
        }

        if (GroupIs("SALES ACCOUNTS")) return Make(PL, I, "Revenue from Operations");
        if (GroupIs("INDIRECT INCOMES")) return Make(PL, I, "Other Income");
        if (GroupIs("DIRECT INCOMES")) return Make(PL, I);
        if (GroupIs("PURCHASE ACCOUNTS")) return Make(PL, E, "Purchases");
        if (GroupIs("INDIRECT EXPENSES")) return Make(PL, E, "Other Expenses");
        if (GroupIs("DIRECT EXPENSES")) return Make(PL, E);

        return null;
    }

    public static void Export(
        string presentationTemplatePath,
        string schemaTemplatePath,
        string destinationPath,
        FinancialStatementBasicInfo basicInfo,
        IEnumerable<FinancialStatementLedgerRow> fyRows,
        IEnumerable<FinancialStatementLedgerRow> pyRows,
        IEnumerable<EntitySignatoryRow> signatories,
        IEnumerable<OwnerPartnerRow> ownerPartners,
        bool keepLinks,
        string? pdfPath = null,
        bool allowIncompleteLinkedWorkbook = false,
        IEnumerable<FinancialStatementStockRow>? fyStock = null,
        IEnumerable<FinancialStatementStockRow>? pyStock = null,
        IEnumerable<FinancialStatementLedgerRow>? fyDebtors = null,
        IEnumerable<FinancialStatementLedgerRow>? pyDebtors = null,
        IEnumerable<FinancialStatementLedgerRow>? fyCreditors = null,
        IEnumerable<FinancialStatementLedgerRow>? pyCreditors = null)
    {
        if (Supports(basicInfo.EntityType) && !File.Exists(presentationTemplatePath))
            throw new FileNotFoundException("Financial Statement presentation template is missing.", presentationTemplatePath);

        if (!Supports(basicInfo.EntityType))
        {
            File.Copy(schemaTemplatePath, destinationPath, true);
            FinancialStatementWorkbookService.Export(schemaTemplatePath, destinationPath, basicInfo, fyRows, pyRows, signatories, ownerPartners, keepLinks, pdfPath);
            return;
        }

        var relaxedLinkedDraft = allowIncompleteLinkedWorkbook && keepLinks && string.IsNullOrWhiteSpace(pdfPath);
        if (!relaxedLinkedDraft)
        {
            var activeCatalog = FinancialStatementWorkbookService.BuildMappingCatalog(schemaTemplatePath, basicInfo.EntityType);
            var assessment = CleanFinancialStatementExportService.AssessPreparation(basicInfo, fyRows, pyRows, activeCatalog);
            if (assessment.FyPending > 0 || assessment.PyPending > 0)
                throw new InvalidOperationException($"Final Financial Statements export is blocked because {assessment.FyPending} FY and {assessment.PyPending} PY material ledger(s) still require classification.");
        }

        var fySnapshot = fyRows?.ToArray() ?? Array.Empty<FinancialStatementLedgerRow>();
        var pySnapshot = pyRows?.ToArray() ?? Array.Empty<FinancialStatementLedgerRow>();
        var fyStockSnapshot = fyStock?.ToArray() ?? Array.Empty<FinancialStatementStockRow>();
        var pyStockSnapshot = pyStock?.ToArray() ?? Array.Empty<FinancialStatementStockRow>();
        var fyDebtorSnapshot = fyDebtors?.ToArray() ?? Array.Empty<FinancialStatementLedgerRow>();
        var pyDebtorSnapshot = pyDebtors?.ToArray() ?? Array.Empty<FinancialStatementLedgerRow>();
        var fyCreditorSnapshot = fyCreditors?.ToArray() ?? Array.Empty<FinancialStatementLedgerRow>();
        var pyCreditorSnapshot = pyCreditors?.ToArray() ?? Array.Empty<FinancialStatementLedgerRow>();

        var excelType = Type.GetTypeFromProgID("Excel.Application") ?? throw new InvalidOperationException("Microsoft Excel is not installed.");
        dynamic? excel = null;
        dynamic? book = null;
        var workingTemplatePath = Path.Combine(Path.GetTempPath(), $"AAH_FS_Presentation_{Guid.NewGuid():N}.xlsx");
        try
        {
            // R15 R5: never ask Excel to open the published package asset directly. Downloaded
            // updater packages can cause Microsoft Office to treat a packaged template as an
            // Internet/Protected-View file. The presentation authority is macro-free and is
            // copied to a unique local working file before COM opens it.
            File.Copy(presentationTemplatePath, workingTemplatePath, true);
            TryRemoveZoneIdentifier(workingTemplatePath);
            if (!File.Exists(workingTemplatePath) || new FileInfo(workingTemplatePath).Length == 0)
                throw new InvalidOperationException("Financial Statement presentation working copy could not be prepared.");

            excel = Activator.CreateInstance(excelType)!;
            excel.Visible = false;
            excel.DisplayAlerts = false;
            excel.ScreenUpdating = false;
            try { excel.EnableEvents = false; } catch { }
            try { excel.AskToUpdateLinks = false; } catch { }
            // R49: build and calculate with Excel in manual mode. The old full-rebuild path ran
            // synchronously on every export and was the main visible pause even when only AAH
            // values changed. The workbook is explicitly calculated once after all identities and
            // presentation values are written.
            try { excel.Calculation = -4135; } catch { } // xlCalculationManual
            book = OpenWorkbookWithRepairFallback(excel, workingTemplatePath);

            var profile = FinancialStatementWorkbookService.ResolveProfileSheet(basicInfo.EntityType);
            dynamic basicSheet = book.Worksheets["Basic Info"];
            WriteBasicInfo(basicSheet, basicInfo, signatories, ownerPartners, profile);

            var fy = AggregateMappings(fySnapshot);
            var py = AggregateMappings(pySnapshot);
            NceFinancialStatementIdentityService.Snapshot? nceIdentities = null;
            if (profile == "NCE")
            {
                // R49: TB mapping calls independent Note identities first. Note visibility/data
                // then feeds BS/P&L and the grouped physical sheets. No workbook row is allowed
                // to create a Note merely because it exists in the template.
                var identityCatalog = BuildPresentationMappingCatalog(basicInfo.EntityType);
                nceIdentities = NceFinancialStatementIdentityService.Build(
                    basicInfo, fySnapshot, pySnapshot, identityCatalog,
                    fyStockSnapshot, pyStockSnapshot, fyDebtorSnapshot, pyDebtorSnapshot, fyCreditorSnapshot, pyCreditorSnapshot);
                // FIXED7: Excel with Links is the working/review workbook. Build the full mapped
                // identities and presentation, but defer strict accounting reconciliation to
                // values-only Excel/PDF. This restores the R40 linked-draft contract.
                if (!relaxedLinkedDraft) ValidateNceSourceChecks(nceIdentities);
                WriteNce(book, nceIdentities);
                WriteNceNarrativesAndSignatures(book, basicInfo, signatories);
                WritePreparationSheets(book, basicInfo, fySnapshot, pySnapshot, fyStockSnapshot, pyStockSnapshot,
                    fyDebtorSnapshot, pyDebtorSnapshot, fyCreditorSnapshot, pyCreditorSnapshot);
                DeleteExcept(book, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Basic Info", "TB", "Stock Summary", "Debtors", "Creditors",
                    "NCE Balance Sheet", "NCE Notes 1-3", "NCE Notes 4-6", "NCE Notes 7-10", "NCE Note 11", "NCE Notes 12-18"
                });
                book.Worksheets["NCE Balance Sheet"].Activate();
            }
            else
            {
                WriteDivisionI(book, fy, py);
                DeleteExcept(book, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Basic Info", "Div I Balance Sheet", "Div I Notes"
                });
                book.Worksheets["Div I Balance Sheet"].Activate();
            }

            // The example workbook's navigation formulas reference every profile. Once the
            // unrelated profile sheets are removed, keep Basic Info as clean output values.
            basicSheet.Range["F13"].Value2 = basicSheet.Range["C20"].Value2;
            basicSheet.Range["F14"].Value2 = "Balance Sheet";
            basicSheet.Range["F15"].Value2 = "Statement of Profit and Loss";
            basicSheet.Range["F16"].Value2 = "Notes to Financial Statements";
            basicSheet.Range["F17"].Value2 = "FINANCIAL STATEMENTS READY";

            if (profile == "NCE" && nceIdentities is not null)
            {
                // R51: prove the physical sheet-to-Note ownership table before visibility or
                // compaction can touch the template. Then compact only the declared Note blocks
                // and prove that every data-bearing Note survived on its one owning sheet.
                ValidateNceSheetNoteAuthority(book);
                ValidateNceNote13HeadingBindings(book, nceIdentities);
                ApplyNceIdentityPresentation(book, nceIdentities);
                CompactNcePresentationSheets(book);
                ValidateNceRenderedNoteAuthority(book, nceIdentities);
                RepairNceFaceLinksDynamic(book, nceIdentities);
            }

            // One bounded calculation after all TB -> source authority -> Note -> BS/P&L calls,
            // dynamic row compaction and final links are complete.
            try { book.Calculate(); } catch { try { excel.Calculate(); } catch { } }

            if (profile == "NCE" && nceIdentities is not null)
            {
                ApplyNceSheetFormatting(book, nceIdentities);
                if (!relaxedLinkedDraft)
                {
                    CrossCheckNceNotePresentationTotals(book, nceIdentities, BasicInfoHasPreviousYear(basicInfo));
                    CrossCheckNceIdentities(book, nceIdentities, BasicInfoHasPreviousYear(basicInfo));
                    CrossCheckNceStatementTotals(book, nceIdentities, BasicInfoHasPreviousYear(basicInfo));
                    VerifyNceExampleBalance(book, BasicInfoHasPreviousYear(basicInfo));
                }
                RenameNceSheetsToCanonical(book);
            }

            if (!keepLinks)
            {
                for (var i = 1; i <= book.Worksheets.Count; i++)
                {
                    dynamic ws = book.Worksheets[i];
                    dynamic used = ws.UsedRange;
                    used.Value2 = used.Value2;
                }
            }

            if (!string.IsNullOrWhiteSpace(pdfPath))
            {
                // Preparation sheets are part of Excel review/export, not the statutory PDF pack.
                foreach (var prepName in new[] { "Basic Info", "TB", "Stock Summary", "Debtors", "Creditors" })
                {
                    try { book.Worksheets[prepName].Visible = 0; } catch { }
                }
                book.ExportAsFixedFormat(0, pdfPath);
            }
            else
            {
                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                book.SaveAs(destinationPath, 51); // xlOpenXMLWorkbook (.xlsx)
            }
        }
        finally
        {
            try { book?.Close(false); } catch { }
            try { excel?.Quit(); } catch { }
            try { File.Delete(workingTemplatePath); } catch { }
            try { File.Delete(workingTemplatePath + ":Zone.Identifier"); } catch { }
        }
    }

    private static void RenameNceSheetsToCanonical(dynamic book)
    {
        // User-facing sheet names must describe the actual authority they contain. The historical
        // template names are internal only; Excel automatically rewrites formulas when a sheet is
        // renamed, so links remain live in Excel-with-Links output.
        foreach (var pair in NceFinancialStatementPresentationAuthority.CanonicalSheetNames)
        {
            try
            {
                dynamic sheet = book.Worksheets[pair.Key];
                sheet.Name = pair.Value;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Unable to apply canonical NCE sheet identity '{pair.Key}' -> '{pair.Value}'.", ex);
            }
        }
    }

    private static bool BasicInfoHasPreviousYear(FinancialStatementBasicInfo info)
        => !info.PreviousYearDataMode.Equals("No Previous Year / First Year", StringComparison.OrdinalIgnoreCase);

    private static void VerifyNceExampleBalance(dynamic book, bool verifyPreviousYear)
    {
        dynamic bs = book.Worksheets["NCE Balance Sheet"];
        decimal Read(string cell)
        {
            try { return Convert.ToDecimal(bs.Range[cell].Value2 ?? 0d, CultureInfo.InvariantCulture); }
            catch { return 0m; }
        }
        var fyDifference = Math.Abs(Read("E24") - Read("E46"));
        var pyDifference = Math.Abs(Read("F24") - Read("F46"));
        if (fyDifference > 0.01m || (verifyPreviousYear && pyDifference > 0.01m))
            throw new InvalidOperationException($"Final Balance Sheet matching failed. FY difference {fyDifference:N2}" +
                (verifyPreviousYear ? $", PY difference {pyDifference:N2}." : "."));
    }

    private static void ApplyNceExampleVisibility(dynamic book)
    {
        // Keep the exact example workbook's rows/styles/formulas. Optional detail rows with
        // no FY/PY information are hidden instead of being deleted, so formula locations and
        // print geometry remain identical to the supplied format while empty catalogue lines
        // no longer flood Notes to Accounts.
        HideZeroDetailRows(book.Worksheets["NCE Notes 7-10"], 1, 60, new[] { 3, 4 }, new[] { 7,8,9,10,11,12,13 });
        HideZeroDetailRows(book.Worksheets["NCE Note 11"], 1, 120, new[] { 3, 4, 7, 8 }, Array.Empty<int>());
        HideZeroDetailRows(book.Worksheets["NCE Notes 12-18"], 1, 200, new[] { 3, 4 }, Array.Empty<int>());
    }

    private static void HideZeroDetailRows(dynamic sheet, int firstRow, int lastRow, IReadOnlyList<int> amountColumns, IReadOnlyList<int> ownerRows)
    {
        for (var row = firstRow; row <= lastRow; row++)
        {
            try { sheet.Rows[row].Hidden = false; } catch { }
            if (ownerRows.Contains(row))
            {
                string owner = Convert.ToString((object?)sheet.Cells[row, 2].Value2, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                if (owner.Length == 0) { try { sheet.Rows[row].Hidden = true; } catch { } }
                continue;
            }

            string label = Convert.ToString((object?)sheet.Cells[row, 2].Value2, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (label.Length == 0 ||
                label.StartsWith("Note ", StringComparison.OrdinalIgnoreCase) ||
                label.StartsWith("Total", StringComparison.OrdinalIgnoreCase) ||
                label.Contains("Particulars", StringComparison.OrdinalIgnoreCase) ||
                label.Contains("Current Year", StringComparison.OrdinalIgnoreCase) ||
                label.Contains("Previous Year", StringComparison.OrdinalIgnoreCase) ||
                label.StartsWith("Additional", StringComparison.OrdinalIgnoreCase) ||
                label.StartsWith("Foot Note", StringComparison.OrdinalIgnoreCase) ||
                label.StartsWith("Secured", StringComparison.OrdinalIgnoreCase) ||
                label.StartsWith("Unsecured", StringComparison.OrdinalIgnoreCase))
                continue;

            var touched = false;
            var nonZero = false;
            foreach (var column in amountColumns)
            {
                dynamic cell = sheet.Cells[row, column];
                try { if (Convert.ToString(cell.Formula, CultureInfo.InvariantCulture)?.Length > 0) touched = true; } catch { }
                try
                {
                    var value = cell.Value2;
                    if (value is not null)
                    {
                        if (value is double or float or decimal or int or long) touched = true;
                        if (Math.Abs(Convert.ToDecimal(value, CultureInfo.InvariantCulture)) > 0.004m) nonZero = true;
                    }
                }
                catch { }
            }
            if (touched && !nonZero)
            {
                try { sheet.Rows[row].Hidden = true; } catch { }
            }
        }
    }


    // -----------------------------------------------------------------------------
    // R49 identity-driven presentation / preparation export
    // -----------------------------------------------------------------------------

    private static void WritePreparationSheets(
        dynamic book,
        FinancialStatementBasicInfo basicInfo,
        IReadOnlyList<FinancialStatementLedgerRow> fyTrialBalance,
        IReadOnlyList<FinancialStatementLedgerRow> pyTrialBalance,
        IReadOnlyList<FinancialStatementStockRow> fyStock,
        IReadOnlyList<FinancialStatementStockRow> pyStock,
        IReadOnlyList<FinancialStatementLedgerRow> fyDebtors,
        IReadOnlyList<FinancialStatementLedgerRow> pyDebtors,
        IReadOnlyList<FinancialStatementLedgerRow> fyCreditors,
        IReadOnlyList<FinancialStatementLedgerRow> pyCreditors)
    {
        // Basic Info remains the template-owned sheet. TB / Stock / Debtors / Creditors are
        // exported beside it from the exact preparation collections so the linked workbook no
        // longer exposes Basic Info while silently omitting the other preparation authorities.
        FormatBasicInfoSheet(book.Worksheets["Basic Info"]);
        WriteTrialBalanceSheet(GetOrCreateSheet(book, "TB"), fyTrialBalance, pyTrialBalance);
        WriteStockSummarySheet(GetOrCreateSheet(book, "Stock Summary"), fyStock, pyStock);
        var partyWise = basicInfo.DebtorCreditorDetail.Equals("Party-wise", StringComparison.OrdinalIgnoreCase);
        WritePartyScheduleSheet(GetOrCreateSheet(book, "Debtors"), "Debtors", fyDebtors, pyDebtors, partyWise);
        WritePartyScheduleSheet(GetOrCreateSheet(book, "Creditors"), "Creditors", fyCreditors, pyCreditors, partyWise);
    }

    private static dynamic GetOrCreateSheet(dynamic book, string name)
    {
        try { return book.Worksheets[name]; }
        catch
        {
            dynamic sheet = book.Worksheets.Add(Type.Missing, book.Worksheets[book.Worksheets.Count], 1, Type.Missing);
            sheet.Name = name;
            return sheet;
        }
    }

    private static void WriteTrialBalanceSheet(dynamic sheet,
        IReadOnlyList<FinancialStatementLedgerRow> fyRows,
        IReadOnlyList<FinancialStatementLedgerRow> pyRows)
    {
        string[] headers =
        [
            "Period", "Ledger", "Parent Group", "Primary Group", "Opening", "Debit", "Credit", "Closing",
            "Statement", "Nature", "C1", "C2", "C3", "Mapping Key", "Note", "Destination Identity", "Mapping Status"
        ];
        var rows = fyRows.Select(x => (Period: "FY", Row: x)).Concat(pyRows.Select(x => (Period: "PY", Row: x))).ToArray();
        var values = new object[rows.Length + 2, headers.Length];
        values[0, 0] = "TRIAL BALANCE — FINANCIAL STATEMENTS PREPARATION";
        for (var c = 0; c < headers.Length; c++) values[1, c] = headers[c];
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i].Row;
            var r = i + 2;
            values[r, 0] = rows[i].Period;
            values[r, 1] = row.Ledger;
            values[r, 2] = row.ParentGroup;
            values[r, 3] = row.PrimaryGroup;
            values[r, 4] = (double)row.Opening;
            values[r, 5] = (double)row.Debit;
            values[r, 6] = (double)row.Credit;
            values[r, 7] = (double)row.Closing;
            values[r, 8] = row.Classification1;
            values[r, 9] = row.Classification2;
            values[r, 10] = row.Classification3;
            values[r, 11] = row.Classification4;
            values[r, 12] = row.Classification5;
            values[r, 13] = row.Mapping;
            values[r, 14] = row.Note;
            values[r, 15] = BuildDestinationIdentity(row);
            values[r, 16] = row.MappedStatus;
        }
        WritePreparationMatrix(sheet, values, headers.Length);
        FormatTrialBalanceSheet(sheet, Math.Max(2, rows.Length + 2), headers.Length);
    }

    private static string BuildDestinationIdentity(FinancialStatementLedgerRow row)
    {
        var key = (row.Mapping ?? string.Empty).Trim();
        if (key.Length == 0) return string.Empty;
        if (!string.IsNullOrWhiteSpace(row.Note)) return $"NOTE:{row.Note}:KEY:{key}";
        if (!string.IsNullOrWhiteSpace(row.FsHead)) return $"FACE:{row.FsHead}:KEY:{key}";
        return $"KEY:{key}";
    }

    private sealed record StockExportRow(
        string Period,
        string Item,
        string Group,
        string Category,
        string Unit,
        string HsnSac,
        decimal GstRate,
        decimal? Opening,
        decimal? Closing,
        string Mapping,
        string Status);

    private static void WriteStockSummarySheet(dynamic sheet,
        IReadOnlyList<FinancialStatementStockRow> fyRows,
        IReadOnlyList<FinancialStatementStockRow> pyRows)
    {
        string[] headers =
        ["Period", "Item", "Group", "Category", "Unit", "HSN/SAC", "GST Rate", "Opening", "Closing", "Mapping", "Mapping Status"];

        IEnumerable<StockExportRow> Project(string period, IReadOnlyList<FinancialStatementStockRow> source)
        {
            var authority = source.FirstOrDefault(x => x.HasPeriodSourceAuthority);
            var hasAuthority = authority is not null;
            var officialOpen = authority?.PeriodSourceOpeningTotal ?? 0m;
            var officialClose = authority?.PeriodSourceClosingTotal ?? 0m;
            var detailOpen = source.Where(x => x.OpeningBalance.HasValue).Sum(x => x.OpeningBalance ?? 0m);
            var detailClose = source.Where(x => x.ClosingBalance.HasValue).Sum(x => x.ClosingBalance ?? 0m);
            var staleDetail = hasAuthority &&
                              (Math.Abs(detailOpen - officialOpen) > 0.01m ||
                               Math.Abs(detailClose - officialClose) > 0.01m);

            foreach (var row in source)
                yield return new StockExportRow(
                    period, row.Item, row.Group, row.Category, row.Unit, row.HsnSac, row.GstRate,
                    staleDetail ? null : row.OpeningBalance,
                    staleDetail ? null : row.ClosingBalance,
                    row.Mapping,
                    staleDetail ? "Detail classification retained — amount superseded by official NCE total" : row.MappingStatus);

            if (hasAuthority)
                yield return new StockExportRow(
                    period,
                    "OFFICIAL NCE STOCK TOTAL",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    0m,
                    officialOpen,
                    officialClose,
                    string.Empty,
                    staleDetail ? "Authoritative period total" : "Authoritative period total — reconciled");
        }

        var rows = Project("FY", fyRows).Concat(Project("PY", pyRows)).ToArray();
        var values = new object[rows.Length + 2, headers.Length];
        values[0, 0] = "STOCK SUMMARY — FINANCIAL STATEMENTS PREPARATION";
        for (var c = 0; c < headers.Length; c++) values[1, c] = headers[c];
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var r = i + 2;
            values[r, 0] = row.Period;
            values[r, 1] = row.Item;
            values[r, 2] = row.Group;
            values[r, 3] = row.Category;
            values[r, 4] = row.Unit;
            values[r, 5] = row.HsnSac;
            values[r, 6] = (double)row.GstRate;
            values[r, 7] = row.Opening.HasValue ? (object)(double)row.Opening.Value : null!;
            values[r, 8] = row.Closing.HasValue ? (object)(double)row.Closing.Value : null!;
            values[r, 9] = row.Mapping;
            values[r, 10] = row.Status;
        }
        WritePreparationMatrix(sheet, values, headers.Length);
        FormatStockSummarySheet(sheet, Math.Max(2, rows.Length + 2), headers.Length);
    }

    private sealed record PartyExportRow(
        string Period,
        string Ledger,
        string ParentGroup,
        decimal Opening,
        decimal Debit,
        decimal Credit,
        decimal Closing,
        string ScheduleClass1,
        string ScheduleClass2,
        string MappingKey,
        string Note,
        string DestinationIdentity,
        string Status);

    private static void WritePartyScheduleSheet(dynamic sheet, string title,
        IReadOnlyList<FinancialStatementLedgerRow> fyRows,
        IReadOnlyList<FinancialStatementLedgerRow> pyRows,
        bool partyWise)
    {
        string[] headers =
        [
            "Period", "Ledger", "Parent Group", "Opening", "Debit", "Credit", "Closing",
            "Schedule C1", "Schedule C2", "Mapping Key", "Note", "Destination Identity", "Mapping Status"
        ];

        var source = fyRows.Select(x => (Period: "FY", Row: x))
            .Concat(pyRows.Select(x => (Period: "PY", Row: x)))
            .Select(x =>
            {
                var id = ResolveScheduleExportIdentity(x.Row);
                return new PartyExportRow(
                    x.Period,
                    x.Row.Ledger,
                    x.Row.ParentGroup,
                    x.Row.Opening,
                    x.Row.Debit,
                    x.Row.Credit,
                    x.Row.Closing,
                    x.Row.ScheduleClass1,
                    x.Row.ScheduleClass2,
                    id.MappingKey,
                    id.Note,
                    id.DestinationIdentity,
                    id.Status);
            })
            .ToArray();

        // R52 FIXED17: the exported Party-wise preparation must actually be party-wise even if
        // an older prepared snapshot still contains bill-level rows. This is an export projection
        // only; it does not rewrite the user's saved preparation rows or mappings.
        var rows = partyWise
            ? source.GroupBy(x => new
                {
                    x.Period,
                    Ledger = (x.Ledger ?? string.Empty).Trim().ToUpperInvariant(),
                    C1 = (x.ScheduleClass1 ?? string.Empty).Trim().ToUpperInvariant(),
                    C2 = (x.ScheduleClass2 ?? string.Empty).Trim().ToUpperInvariant()
                })
                .Select(g =>
                {
                    var first = g.First();
                    return new PartyExportRow(
                        first.Period,
                        first.Ledger,
                        first.ParentGroup,
                        g.Sum(x => x.Opening),
                        g.Sum(x => x.Debit),
                        g.Sum(x => x.Credit),
                        g.Sum(x => x.Closing),
                        first.ScheduleClass1,
                        first.ScheduleClass2,
                        first.MappingKey,
                        first.Note,
                        first.DestinationIdentity,
                        first.Status);
                })
                .OrderBy(x => x.Period, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Ledger, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ScheduleClass1, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ScheduleClass2, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : source;

        var values = new object[rows.Length + 2, headers.Length];
        values[0, 0] = $"{title.ToUpperInvariant()} — FINANCIAL STATEMENTS PREPARATION";
        for (var c = 0; c < headers.Length; c++) values[1, c] = headers[c];
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var r = i + 2;
            values[r, 0] = row.Period;
            values[r, 1] = row.Ledger;
            values[r, 2] = row.ParentGroup;
            values[r, 3] = (double)row.Opening;
            values[r, 4] = (double)row.Debit;
            values[r, 5] = (double)row.Credit;
            values[r, 6] = (double)row.Closing;
            values[r, 7] = row.ScheduleClass1;
            values[r, 8] = row.ScheduleClass2;
            values[r, 9] = row.MappingKey;
            values[r, 10] = row.Note;
            values[r, 11] = row.DestinationIdentity;
            values[r, 12] = row.Status;
        }
        WritePreparationMatrix(sheet, values, headers.Length);
        if (title.Equals("Debtors", StringComparison.OrdinalIgnoreCase))
            FormatDebtorsSheet(sheet, Math.Max(2, rows.Length + 2), headers.Length);
        else
            FormatCreditorsSheet(sheet, Math.Max(2, rows.Length + 2), headers.Length);
    }

    private sealed record ScheduleExportIdentity(string MappingKey, string Note, string DestinationIdentity, string Status);

    private static ScheduleExportIdentity ResolveScheduleExportIdentity(FinancialStatementLedgerRow row)
    {
        var c1 = (row.ScheduleClass1 ?? string.Empty).Trim();
        var c2 = (row.ScheduleClass2 ?? string.Empty).Trim();

        (string Key, string Note) identity = (string.Empty, string.Empty);
        if (c1.Equals("Trade Payables", StringComparison.OrdinalIgnoreCase))
        {
            if (c2.Equals("MSME", StringComparison.OrdinalIgnoreCase)) identity = ("TP_MSME", "10");
            else if (c2.Equals("Others", StringComparison.OrdinalIgnoreCase)) identity = ("TP_OTHER", "10");
            else if (c2.StartsWith("Disputed", StringComparison.OrdinalIgnoreCase)) identity = ("TP_DISPUTED", "10");
        }
        else if (c1.Equals("Trade Receivables", StringComparison.OrdinalIgnoreCase))
        {
            if (c2.StartsWith("Secured considered good", StringComparison.OrdinalIgnoreCase)) identity = ("TR_SEC_GOOD", "18");
            else if (c2.StartsWith("Unsecured considered good", StringComparison.OrdinalIgnoreCase)) identity = ("TR_UNSEC_GOOD", "18");
            else if (c2.StartsWith("Doubtful", StringComparison.OrdinalIgnoreCase)) identity = ("TR_DOUBT", "18");
            else if (c2.Contains("Provisions for doubtful", StringComparison.OrdinalIgnoreCase)) identity = ("TR_ALLOW", "18");
        }
        else if (c1.Equals("Other Current Liabilities", StringComparison.OrdinalIgnoreCase) &&
                 c2.Equals("Advance from Customers", StringComparison.OrdinalIgnoreCase))
            identity = ("OCL_ADV_SCHEDULE", "11");
        else if (c1.Equals("Other Current Assets", StringComparison.OrdinalIgnoreCase) &&
                 c2.Equals("Advance to suppliers", StringComparison.OrdinalIgnoreCase))
            identity = ("OCA_SUPPLIER_SCHEDULE", "21");
        else if (c1.Equals("Other Current Assets", StringComparison.OrdinalIgnoreCase) &&
                 (c2.Equals("Other Receivables", StringComparison.OrdinalIgnoreCase) ||
                  c2.Equals("Other Current Assets", StringComparison.OrdinalIgnoreCase)))
            identity = ("OCA_OTHER_SCHEDULE", "21");

        if (identity.Key.Length == 0)
        {
            var mapping = (row.Mapping ?? string.Empty).Trim();
            var note = (row.Note ?? string.Empty).Trim();
            return new ScheduleExportIdentity(mapping, note, BuildDestinationIdentity(row), row.MappedStatus);
        }

        var destination = $"NOTE:{identity.Note}:SCHEDULE:{identity.Key}";
        var origin = string.IsNullOrWhiteSpace(row.ScheduleMappingOrigin) ? "Mapped" : row.ScheduleMappingOrigin;
        return new ScheduleExportIdentity(identity.Key, identity.Note, destination, origin);
    }

    private static void WritePreparationMatrix(dynamic sheet, object[,] values, int columns)
    {
        try { sheet.Cells.Clear(); } catch { }
        var rows = values.GetLength(0);
        var end = ColumnName(columns);
        sheet.Range[$"A1:{end}{rows}"].Value2 = values;
    }

    private static string ColumnName(int oneBasedColumn)
    {
        var value = oneBasedColumn;
        var result = string.Empty;
        while (value > 0)
        {
            value--;
            result = (char)('A' + (value % 26)) + result;
            value /= 26;
        }
        return result;
    }

    private static void FormatBasicInfoSheet(dynamic sheet)
    {
        try
        {
            sheet.PageSetup.PrintArea = sheet.UsedRange.Address;
            sheet.PageSetup.FitToPagesWide = 1;
            sheet.PageSetup.FitToPagesTall = false;
        }
        catch { }
    }

    private static void FormatTrialBalanceSheet(dynamic sheet, int rows, int columns)
        => FormatPreparationSheet(sheet, rows, columns, new[] { 5, 6, 7, 8 }, "#,##0.00;(#,##0.00);\"-\"");

    private static void FormatStockSummarySheet(dynamic sheet, int rows, int columns)
        => FormatPreparationSheet(sheet, rows, columns, new[] { 7, 8, 9 }, "#,##0.00;(#,##0.00);\"-\"");

    private static void FormatDebtorsSheet(dynamic sheet, int rows, int columns)
        => FormatPreparationSheet(sheet, rows, columns, new[] { 4, 5, 6, 7 }, "#,##0.00;(#,##0.00);\"-\"");

    private static void FormatCreditorsSheet(dynamic sheet, int rows, int columns)
        => FormatPreparationSheet(sheet, rows, columns, new[] { 4, 5, 6, 7 }, "#,##0.00;(#,##0.00);\"-\"");

    private static void FormatPreparationSheet(dynamic sheet, int rows, int columns, IReadOnlyList<int> amountColumns, string numberFormat)
    {
        var lastColumn = ColumnName(columns);
        try
        {
            sheet.Cells.Font.Name = "Calibri";
            sheet.Cells.Font.Size = 10;
            sheet.Range[$"A1:{lastColumn}1"].Merge();
            sheet.Range["A1"].Font.Bold = true;
            sheet.Range["A1"].Font.Size = 12;
            sheet.Range[$"A2:{lastColumn}2"].Font.Bold = true;
            sheet.Range[$"A2:{lastColumn}2"].AutoFilter();
            sheet.Range[$"A1:{lastColumn}{rows}"].VerticalAlignment = -4108;
            sheet.Range[$"A1:{lastColumn}{rows}"].WrapText = false;
            sheet.Columns.AutoFit();
            for (var c = 1; c <= columns; c++)
            {
                if (sheet.Columns[c].ColumnWidth > 38) sheet.Columns[c].ColumnWidth = 38;
                if (sheet.Columns[c].ColumnWidth < 9) sheet.Columns[c].ColumnWidth = 9;
            }
            foreach (var column in amountColumns)
                sheet.Range[$"{ColumnName(column)}3:{ColumnName(column)}{rows}"].NumberFormat = numberFormat;
            sheet.PageSetup.PrintArea = $"$A$1:${lastColumn}${rows}";
            sheet.PageSetup.FitToPagesWide = 1;
            sheet.PageSetup.FitToPagesTall = false;
        }
        catch { }
    }

    private static void ApplyNceIdentityPresentation(dynamic book, NceFinancialStatementIdentityService.Snapshot identities)
    {
        foreach (var sheetAuthority in NceFinancialStatementPresentationAuthority.NoteSheets)
        {
            dynamic sheet = book.Worksheets[sheetAuthority.TemplateSheet];
            ApplyNceNoteGroupVisibility(sheet, identities, NceFinancialStatementPresentationAuthority.ForSheet(sheetAuthority.TemplateSheet));
        }
        ApplyNceBalanceSheetVisibility(book.Worksheets[NceFinancialStatementPresentationAuthority.BalanceSheetTemplate], identities);
        ApplyNceProfitAndLossVisibility(book.Worksheets[NceFinancialStatementPresentationAuthority.ProfitLossTemplate], identities);
    }

    private static void ApplyNceNoteGroupVisibility(
        dynamic sheet,
        NceFinancialStatementIdentityService.Snapshot identities,
        IReadOnlyList<NceFinancialStatementPresentationAuthority.NotePresentationSpec> noteSpecs)
    {
        var usedRows = Math.Max(1, Convert.ToInt32(sheet.UsedRange.Rows.Count, CultureInfo.InvariantCulture));
        var starts = new List<(NceFinancialStatementPresentationAuthority.NotePresentationSpec Spec, int Row)>();
        for (var row = 1; row <= usedRows; row++)
        {
            for (var column = 1; column <= 2; column++)
            {
                string text = Convert.ToString((object?)sheet.Cells[row, column].Value2, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                foreach (var spec in noteSpecs)
                {
                    if (ExactNoteTitleMatches(text, spec) && !starts.Any(x => x.Spec.Note.Equals(spec.Note, StringComparison.OrdinalIgnoreCase)))
                        starts.Add((spec, row));
                }
            }
        }
        starts = starts.OrderBy(x => x.Row).ToList();

        var anyVisibleNote = false;
        for (var i = 0; i < starts.Count; i++)
        {
            var noteSpec = starts[i].Spec;
            var noteId = noteSpec.Note;
            var start = starts[i].Row;
            var end = i + 1 < starts.Count ? starts[i + 1].Row - 1 : usedRows;
            var note = identities.Note(noteId);
            var visible = ShouldRenderNceNote(noteSpec, note);
            anyVisibleNote |= visible;
            for (var row = start; row <= end; row++)
            {
                try { sheet.Rows[row].Hidden = !visible; } catch { }
            }
            if (!visible) continue;

            // R52 FIXED5: narrative Notes 1-2 are statutory presentation blocks, not amount-led
            // schedules. Their titles, accounting-policy headings and disclosure rows must never
            // be removed by FY/PY zero-value compaction. Preserve the complete template block.
            if (noteSpec.Format.LayoutKind == NceFinancialStatementPresentationAuthority.NoteLayoutKind.Narrative)
                continue;

            // Heading-level FY/PY rule: after the Note is admitted by its identity, suppress every
            // non-structural row whose FY/PY cells contain no information. This is presentation
            // only; it never alters the Note totals or the TB authority.
            for (var row = start + 1; row <= end; row++)
            {
                // R52 FIXED3: for every non-narrative Note, row start+1 is the header row declared
                // by that Note's own formatting contract. It must survive visibility compaction
                // even when its caption (for example Note 13's "Asset Class") carries no amount.
                // The formatter later writes the Note-specific column headings back into this same
                // row; hiding/deleting it would make the first live detail row become start+1 and
                // the formatter would overwrite that detail identity.
                if (row == start + 1 &&
                    noteSpec.Format.LayoutKind != NceFinancialStatementPresentationAuthority.NoteLayoutKind.Narrative)
                    continue;

                var label = FirstCellText(sheet, row, 1, 2);
                if (label.Length == 0)
                {
                    try { sheet.Rows[row].Hidden = true; } catch { }
                    continue;
                }
                if (IsStructuralNoteRow(label)) continue;
                // R49: heading visibility comes from the Note identity first. A section heading
                // can legitimately have no amount in its own row while visible child/detail rows
                // carry FY/PY data. Do not hide that heading merely because its template row is
                // non-numeric.
                if (NoteLabelHasData(note, label)) continue;
                if (!RowHasFinancialInformation(sheet, row, noteId))
                {
                    try { sheet.Rows[row].Hidden = true; } catch { }
                }
            }
        }

        try { sheet.Visible = anyVisibleNote ? -1 : 0; } catch { }
    }

    private static bool ShouldRenderNceNote(
        NceFinancialStatementPresentationAuthority.NotePresentationSpec spec,
        NceFinancialStatementIdentityService.NoteIdentity? note)
        => spec.Format.LayoutKind == NceFinancialStatementPresentationAuthority.NoteLayoutKind.Narrative ||
           (note is not null && note.HasData);

    private static string FirstCellText(dynamic sheet, int row, params int[] columns)
    {
        foreach (var column in columns)
        {
            string text = Convert.ToString((object?)sheet.Cells[row, column].Value2, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (text.Length > 0) return text;
        }
        return string.Empty;
    }

    private static bool IsStructuralNoteRow(string label)
        => label.StartsWith("Note ", StringComparison.OrdinalIgnoreCase) ||
           label.StartsWith("Total", StringComparison.OrdinalIgnoreCase) ||
           label.Contains("Particulars", StringComparison.OrdinalIgnoreCase) ||
           label.Contains("Current Year", StringComparison.OrdinalIgnoreCase) ||
           label.Contains("Previous Year", StringComparison.OrdinalIgnoreCase) ||
           label.StartsWith("Sr.", StringComparison.OrdinalIgnoreCase) ||
           label.StartsWith("Name of Partner", StringComparison.OrdinalIgnoreCase);

    private static bool NoteLabelHasData(NceFinancialStatementIdentityService.NoteIdentity note, string label)
    {
        static string Normalize(string value)
            => string.Join(" ", (value ?? string.Empty)
                .Replace("–", "-")
                .Replace("—", "-")
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Trim(' ', ':', '-', '.');

        var target = Normalize(label);
        if (target.Length == 0) return false;
        foreach (var heading in note.Headings.Where(x => x.HasData))
        {
            var title = Normalize(heading.Title);
            var fsHead = Normalize(heading.FsHead);
            if ((title.Length > 0 && target.Equals(title, StringComparison.OrdinalIgnoreCase)) ||
                (fsHead.Length > 0 && target.Equals(fsHead, StringComparison.OrdinalIgnoreCase)))
                return true;

            foreach (var detail in heading.Details.Where(x => x.HasData))
            {
                var ledger = Normalize(detail.Ledger);
                if (ledger.Length > 0 && target.Equals(ledger, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static bool RowHasFinancialInformation(dynamic sheet, int row, string noteId)
    {
        var firstAmountColumn = noteId.Equals("3", StringComparison.OrdinalIgnoreCase) ? 4 : 3;
        for (var column = firstAmountColumn; column <= 11; column++)
        {
            try
            {
                object? value = sheet.Cells[row, column].Value2;
                if (value is null) continue;
                var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                decimal amount;
                if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out amount) &&
                    Math.Abs(amount) > 0.004m) return true;
            }
            catch { }
        }
        return false;
    }

    private static void ApplyNceBalanceSheetVisibility(dynamic sheet, NceFinancialStatementIdentityService.Snapshot identities)
    {
        // R49: BS visibility is face-identity specific, not merely Note specific. Notes such as
        // Deferred Tax, PPE/Intangibles and Investments own more than one BS face heading; each
        // heading is shown only when that exact FY/PY identity reports data.
        var direct = new (int Row, string Identity)[]
        {
            (9,"Owners' capital account"),(10,"Reserves and surplus"),(13,"Long-term borrowings"),
            (14,"Deferred tax liabilities (Net)"),(15,"Other long-term liabilities"),(16,"Long-term provisions"),
            (19,"Short-term borrowings"),(20,"Trade payables"),(21,"Other current liabilities"),(22,"Short-term provisions"),
            (29,"Property, Plant and Equipment"),(30,"Intangible assets"),(31,"Capital work-in-progress"),
            (32,"Intangible assets under development"),(33,"Non-current investments"),(34,"Deferred tax assets (Net)"),
            (35,"Long-term loans and advances"),(36,"Other non-current assets"),(39,"Current investments"),
            (40,"Inventories"),(41,"Trade receivables"),(42,"Cash and Cash Equivalents"),
            (43,"Short-term loans and advances"),(44,"Other current assets")
        };
        foreach (var (row, identityName) in direct)
            SetStatementFaceRow(sheet, row, identities.Face("Balance Sheet", identityName));

        // Row 28 is the PPE/Intangible parent heading and has no independent amount. Its
        // visibility follows only the four Note-13 child identities.
        // FIXED6: face statements retain their statutory row geometry even when an amount is
        // zero. Hiding face rows changed numbering/spacing and made the rendered statement differ
        // from the approved NCE presentation.
        try { sheet.Rows[28].Hidden = false; } catch { }

        SetFaceGroupVisibility(sheet, new[] { 9, 10 }, new[] { 8, 11 });
        SetFaceGroupVisibility(sheet, new[] { 13, 14, 15, 16 }, new[] { 12, 17 });
        SetFaceGroupVisibility(sheet, new[] { 19, 20, 21, 22 }, new[] { 18, 23 });
        SetFaceGroupVisibility(sheet, new[] { 29, 30, 31, 32, 33, 34, 35, 36 }, new[] { 27, 37 });
        SetFaceGroupVisibility(sheet, new[] { 39, 40, 41, 42, 43, 44 }, new[] { 38, 45 });
        try { sheet.Rows[7].Hidden = false; sheet.Rows[24].Hidden = false; } catch { }
        try { sheet.Rows[26].Hidden = false; sheet.Rows[46].Hidden = false; } catch { }
    }

    private static void ApplyNceProfitAndLossVisibility(dynamic sheet, NceFinancialStatementIdentityService.Snapshot identities)
    {
        var noteRows = new (int Row, string Identity)[]
        {
            (7,"Revenue from operations"),(8,"Other income"),(12,"Cost of goods sold"),
            (13,"Employee benefits expense"),(14,"Finance costs"),(15,"Depreciation and amortisation expense"),(16,"Other expenses")
        };
        foreach (var (row, identityName) in noteRows)
            SetStatementFaceRow(sheet, row, identities.Face("P&L Account", identityName));

        SetStatementFaceRow(sheet, 19, identities.Face("P&L Account", "Exceptional items"));
        SetStatementFaceRow(sheet, 21, identities.Face("P&L Account", "Partners' remuneration"));
        SetStatementFaceRow(sheet, 23, identities.Face("P&L Account", "Tax expense"));

        try { sheet.Rows[9].Hidden = false; } catch { }
        try { sheet.Rows[11].Hidden = false; sheet.Rows[17].Hidden = false; } catch { }
        foreach (var row in new[] { 18, 20, 22, 24 })
        {
            try { sheet.Rows[row].Hidden = false; } catch { }
        }
    }

    private static void SetStatementFaceRow(dynamic sheet, int row, NceFinancialStatementIdentityService.FaceLineIdentity? identity)
    {
        // FIXED6: the statutory face structure is permanent; zero/no-data rows remain visible and
        // present as dash/zero according to the workbook number format.
        try { sheet.Rows[row].Hidden = false; } catch { }
    }

    private static void SetFaceGroupVisibility(dynamic sheet, IReadOnlyList<int> childRows, IReadOnlyList<int> structuralRows)
    {
        foreach (var row in structuralRows)
        {
            try { sheet.Rows[row].Hidden = false; } catch { }
        }
    }

    private static bool RowsAnyVisible(dynamic sheet, IReadOnlyList<int> rows)
    {
        foreach (var row in rows)
        {
            try { if (!(bool)sheet.Rows[row].Hidden) return true; } catch { }
        }
        return false;
    }

    private static void ApplyNceSheetFormatting(dynamic book, NceFinancialStatementIdentityService.Snapshot identities)
    {
        FormatNceBalanceSheet(book.Worksheets[NceFinancialStatementPresentationAuthority.BalanceSheetTemplate]);
        FormatNceProfitAndLossSheet(book.Worksheets[NceFinancialStatementPresentationAuthority.ProfitLossTemplate]);
        foreach (var sheetAuthority in NceFinancialStatementPresentationAuthority.NoteSheets)
        {
            dynamic sheet = book.Worksheets[sheetAuthority.TemplateSheet];
            ApplyNceNoteSheetColumnGeometry(sheet, sheetAuthority.TemplateSheet);
            ApplyPerNoteFormatting(sheet, identities, NceFinancialStatementPresentationAuthority.ForSheet(sheetAuthority.TemplateSheet));
            FormatNceNoteContainer(sheet, sheetAuthority);
        }
    }

    private static void ApplyNceNoteSheetColumnGeometry(dynamic sheet, string templateSheet)
    {
        try
        {
            // Column width is a physical-sheet property, not a per-Note property. Applying widths
            // Note-by-Note made the last standard Note overwrite Note 3 / Note 13 geometry. Use
            // the approved presentation workbook's grouped-sheet geometry once per sheet.
            if (templateSheet.Equals("NCE Notes 4-6", StringComparison.OrdinalIgnoreCase))
            {
                sheet.Columns[1].ColumnWidth = 6.71;
                sheet.Columns[2].ColumnWidth = 28;
                for (var c = 3; c <= 6; c++) sheet.Columns[c].ColumnWidth = 18;
            }
            else if (templateSheet.Equals("NCE Notes 7-10", StringComparison.OrdinalIgnoreCase))
            {
                sheet.Columns[1].ColumnWidth = 6;
                sheet.Columns[2].ColumnWidth = 34;
                sheet.Columns[3].ColumnWidth = 11;
                for (var c = 4; c <= 8; c++) sheet.Columns[c].ColumnWidth = 15;
                for (var c = 9; c <= 11; c++) sheet.Columns[c].Hidden = true;
            }
            else if (templateSheet.Equals("NCE Note 11", StringComparison.OrdinalIgnoreCase))
            {
                sheet.Columns[1].ColumnWidth = 9.285;
                sheet.Columns[2].ColumnWidth = 34;
                for (var c = 3; c <= 8; c++) sheet.Columns[c].ColumnWidth = 16;
                sheet.Columns[9].Hidden = true;
            }
            else if (templateSheet.Equals("NCE Notes 12-18", StringComparison.OrdinalIgnoreCase))
            {
                sheet.Columns[1].ColumnWidth = 4.285;
                sheet.Columns[2].ColumnWidth = 38;
                sheet.Columns[3].ColumnWidth = 18;
                sheet.Columns[4].ColumnWidth = 18;
                sheet.Columns[5].Hidden = true;
                sheet.Columns[6].Hidden = true;
            }
        }
        catch { }
    }

    private static void FormatNceBalanceSheet(dynamic sheet)
    {
        try
        {
            // R52 FIXED5: keep the approved NCE face-sheet geometry. The old renderer shifted
            // the 52-wide Particulars column one place left and squeezed Note into ~2 columns,
            // which caused the visible wrapping/caption defects in the Vijay export.
            sheet.Range["A1:F60"].Font.Name = "Arial";
            sheet.Range["A1:F60"].Font.Size = 10;
            sheet.Range["B1:F1"].Font.Size = 14;
            sheet.Range["B2:F2"].Font.Size = 11;
            sheet.Range["B6:F46"].VerticalAlignment = -4108;
            sheet.Range["B6:F46"].WrapText = true;
            sheet.Columns[1].ColumnWidth = 9.285;
            sheet.Columns[2].ColumnWidth = 8;
            sheet.Columns[3].ColumnWidth = 52;
            sheet.Columns[4].ColumnWidth = 10;
            sheet.Columns[5].ColumnWidth = 18;
            sheet.Columns[6].ColumnWidth = 18;
            sheet.Range["E7:F46"].NumberFormat = NumberFormat;
            sheet.PageSetup.PrintArea = "$A$1:$F$60";
            sheet.PageSetup.FitToPagesWide = 1;
            sheet.PageSetup.FitToPagesTall = false;
        }
        catch { }
    }

    private static void FormatNceProfitAndLossSheet(dynamic sheet)
    {
        try
        {
            // The P&L face uses the same approved statutory column geometry as the NCE BS.
            sheet.Range["A1:F38"].Font.Name = "Arial";
            sheet.Range["A1:F38"].Font.Size = 10;
            sheet.Range["B1:F1"].Font.Size = 14;
            sheet.Range["B2:F2"].Font.Size = 11;
            sheet.Range["B6:F24"].VerticalAlignment = -4108;
            sheet.Range["B6:F24"].WrapText = true;
            sheet.Columns[1].ColumnWidth = 9.285;
            sheet.Columns[2].ColumnWidth = 8;
            sheet.Columns[3].ColumnWidth = 52;
            sheet.Columns[4].ColumnWidth = 10;
            sheet.Columns[5].ColumnWidth = 18;
            sheet.Columns[6].ColumnWidth = 18;
            sheet.Range["E7:F24"].NumberFormat = NumberFormat;
            sheet.PageSetup.PrintArea = "$A$1:$F$38";
            sheet.PageSetup.FitToPagesWide = 1;
            sheet.PageSetup.FitToPagesTall = false;
        }
        catch { }
    }

    private static void ApplyPerNoteFormatting(
        dynamic sheet,
        NceFinancialStatementIdentityService.Snapshot identities,
        IReadOnlyList<NceFinancialStatementPresentationAuthority.NotePresentationSpec> specs)
    {
        var starts = specs
            .Select(spec => (Spec: spec, Row: FindExactNoteTitleRow(sheet, spec)))
            .Where(x => x.Row > 0)
            .OrderBy(x => x.Row)
            .ToArray();

        for (var i = 0; i < starts.Length; i++)
        {
            var spec = starts[i].Spec;
            var note = identities.Note(spec.Note);
            if (note is null || !note.HasData ||
                spec.Format.LayoutKind == NceFinancialStatementPresentationAuthority.NoteLayoutKind.Narrative) continue;
            var start = starts[i].Row;
            var end = i + 1 < starts.Length
                ? starts[i + 1].Row - 1
                : Math.Max(start, Convert.ToInt32(sheet.UsedRange.Rows.Count, CultureInfo.InvariantCulture));
            FormatNoteBlock(sheet, start, end, spec, note);
        }
    }

    private static void FormatNoteBlock(
        dynamic sheet,
        int start,
        int end,
        NceFinancialStatementPresentationAuthority.NotePresentationSpec spec,
        NceFinancialStatementIdentityService.NoteIdentity note)
    {
        var f = spec.Format;
        try
        {
            var first = ColumnName(f.FirstVisibleColumn);
            var last = ColumnName(f.LastVisibleColumn);
            dynamic block = sheet.Range[$"{first}{start}:{last}{end}"];
            // Preserve the supplied workbook's Note-specific cell styles/borders. R50's broad
            // formatting pass painted every cell in a surviving Note and recreated the very
            // formatting spill the dynamic renderer is intended to remove. R51 applies only
            // the declared Note contract (font/row/column/number-format/total emphasis) on top
            // of the original per-Note template styling.
            block.Font.Name = f.FontName;
            block.Font.Size = f.BodyFontSize;
            block.VerticalAlignment = -4108;
            block.WrapText = true;

            sheet.Rows[start].RowHeight = f.TitleRowHeight;
            // The Note title band follows the Note's actual visible presentation width. This
            // removes preparation-only columns from standard Notes without cropping their title.
            try { sheet.Cells[start, f.FirstVisibleColumn].MergeArea.UnMerge(); } catch { }
            try { sheet.Range[$"{first}{start}:{last}{start}"].Merge(); } catch { }
            sheet.Range[$"{first}{start}:{last}{start}"].Font.Bold = true;
            sheet.Range[$"{first}{start}:{last}{start}"].Font.Name = f.FontName;
            sheet.Range[$"{first}{start}:{last}{start}"].Font.Size = f.TitleFontSize;

            if (start + 1 <= end)
            {
                sheet.Rows[start + 1].RowHeight = f.HeaderRowHeight;
                sheet.Range[$"{first}{start + 1}:{last}{start + 1}"].Font.Bold = true;
                sheet.Range[$"{first}{start + 1}:{last}{start + 1}"].Font.Name = f.FontName;
                sheet.Range[$"{first}{start + 1}:{last}{start + 1}"].Font.Size = f.HeaderFontSize;
            }
            for (var row = start + 2; row <= end; row++) sheet.Rows[row].RowHeight = f.BodyRowHeight;

            if (f.FyColumn > 0 && f.PyColumn > 0)
            {
                var fy = ColumnName(f.FyColumn);
                var py = ColumnName(f.PyColumn);
                sheet.Range[$"{fy}{start + 1}:{py}{end}"].NumberFormat = f.NumberFormat;
                sheet.Range[$"{fy}{start + 1}:{py}{end}"].HorizontalAlignment = -4152;
            }

            if (f.RemovePreparationColumns)
            {
                // R52 FIXED17: a standard Note physically owns only B:D. Clear every cell/style
                // to the right inside this Note's row block, not merely the old preparation E:F.
                // This prevents Note 4 or Notes 9-12 inheriting the width/borders/fills of a
                // neighbouring wide Note on the same physical grouped sheet.
                for (var col = 5; col <= 11; col++)
                {
                    ClearRangeMergedSafe(sheet, $"{ColumnName(col)}{start}:{ColumnName(col)}{end}", clearFormats: true);
                }
                sheet.Cells[start + 1, 2].Value2 = "Particulars";
                sheet.Cells[start + 1, 3].Value2 = "Current Year";
                sheet.Cells[start + 1, 4].Value2 = "Previous Year";
            }

            if (f.LayoutKind == NceFinancialStatementPresentationAuthority.NoteLayoutKind.OwnerCapital)
            {
                // R52 FIXED17: restore the supplied 11-column Note-3 presentation authority.
                // AAH never guesses remuneration/interest/share-of-profit splits from a capital
                // ledger: proved credits are shown as Capital Introduced and proved debits as
                // Withdrawals; the unproved dedicated columns remain blank.
                sheet.Cells[start + 1, 1].Value2 = "Sr.";
                sheet.Cells[start + 1, 2].Value2 = "Name of Partner / Proprietor / Owner";
                sheet.Cells[start + 1, 3].Value2 = "Share %";
                sheet.Cells[start + 1, 4].Value2 = "Opening Balance";
                sheet.Cells[start + 1, 5].Value2 = "Capital Introduced";
                sheet.Cells[start + 1, 6].Value2 = "Remuneration";
                sheet.Cells[start + 1, 7].Value2 = "Interest";
                sheet.Cells[start + 1, 8].Value2 = "Withdrawals";
                sheet.Cells[start + 1, 9].Value2 = "Share of Profit / (Loss)";
                sheet.Cells[start + 1, 10].Value2 = "Closing Balance";
                sheet.Cells[start + 1, 11].Value2 = "Previous Year Closing";
                sheet.Range[$"C{start + 1}:C{end}"].NumberFormat = "0.00%";
                sheet.Range[$"D{start + 1}:K{end}"].NumberFormat = NumberFormat;
            }
            else if (f.LayoutKind == NceFinancialStatementPresentationAuthority.NoteLayoutKind.PpeMovement)
            {
                // R52: Note 13 owns two truthful layouts. Net Block presents the movement
                // proved by the fixed-asset ledger plus Note 27 depreciation. Gross Block
                // requires genuine gross-cost and accumulated-depreciation authorities and
                // therefore labels column F as closing accumulated depreciation, never as a
                // manufactured current-year expense.
                sheet.Cells[start + 1, 2].Value2 = "Asset Class";
                if (note.PresentationVariant.Equals("Gross Block", StringComparison.OrdinalIgnoreCase))
                {
                    sheet.Cells[start + 1, 3].Value2 = "Opening Gross Block";
                    sheet.Cells[start + 1, 4].Value2 = "Additions";
                    sheet.Cells[start + 1, 5].Value2 = "Disposals";
                    sheet.Cells[start + 1, 6].Value2 = "Accumulated Depreciation";
                    sheet.Cells[start + 1, 7].Value2 = "Closing Net Block";
                    sheet.Cells[start + 1, 8].Value2 = "Previous Year Net Block";
                }
                else
                {
                    sheet.Cells[start + 1, 3].Value2 = "Opening Net Block";
                    sheet.Cells[start + 1, 4].Value2 = "Additions";
                    sheet.Cells[start + 1, 5].Value2 = "Disposals";
                    sheet.Cells[start + 1, 6].Value2 = "Depreciation / Amortisation";
                    sheet.Cells[start + 1, 7].Value2 = "Closing Net Block";
                    sheet.Cells[start + 1, 8].Value2 = "Previous Year Net Block";
                }
                sheet.Range[$"C{start + 1}:H{end}"].NumberFormat = NumberFormat;
            }
            else if (f.LayoutKind == NceFinancialStatementPresentationAuthority.NoteLayoutKind.Standard)
            {
                // Standard Notes use the owning physical sheet's declared geometry. Column widths
                // must not be changed here because grouped sheets can also contain Note 3/13.
            }

            foreach (var totalLabel in new[] { spec.TotalLabel }.Concat(spec.AdditionalTotalLabels)
                         .Where(x => !string.IsNullOrWhiteSpace(x))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var totalRow = FindLabelRowInRange(sheet, totalLabel, start, end);
                if (totalRow <= 0) continue;
                sheet.Range[$"{first}{totalRow}:{last}{totalRow}"].Font.Bold = true;
                try { sheet.Range[$"{first}{totalRow}:{last}{totalRow}"].Borders[8].Weight = 2; } catch { }
            }
        }
        catch { }
    }

    private static void FormatNceNoteContainer(
        dynamic sheet,
        NceFinancialStatementPresentationAuthority.SheetPresentationSpec authority)
    {
        try
        {
            var usedRows = Math.Max(1, Convert.ToInt32(sheet.UsedRange.Rows.Count, CultureInfo.InvariantCulture));
            var lastVisible = 1;
            for (var row = usedRows; row >= 1; row--)
            {
                bool hidden;
                try { hidden = Convert.ToBoolean(sheet.Rows[row].Hidden, CultureInfo.InvariantCulture); } catch { hidden = false; }
                if (hidden) continue;
                if (RowHasAnyContent(sheet, row, authority.LastVisibleColumn))
                {
                    lastVisible = row;
                    break;
                }
            }
            if (lastVisible < usedRows)
            {
                try
                {
                    var lastColumnName = ColumnName(authority.LastVisibleColumn);
                    // Inherited R49 source-gate marker only: sheet.Range[$"A{lastVisible + 1}:{lastColumnName}{usedRows}"].ClearFormats();
                    ClearRangeMergedSafe(sheet, $"A{lastVisible + 1}:{lastColumnName}{usedRows}", clearFormats: true);
                }
                catch { }
            }
            var renderedSpecs = authority.Notes
                .Select(NceFinancialStatementPresentationAuthority.ForNote)
                .Where(spec => FindExactNoteTitleRow(sheet, spec) > 0)
                .ToArray();
            var firstVisibleColumn = renderedSpecs.Length == 0
                ? authority.FirstVisibleColumn
                : renderedSpecs.Min(x => x.Format.FirstVisibleColumn);
            var lastVisibleColumn = renderedSpecs.Length == 0
                ? authority.LastVisibleColumn
                : renderedSpecs.Max(x => x.Format.LastVisibleColumn);
            var first = ColumnName(firstVisibleColumn);
            var last = ColumnName(lastVisibleColumn);
            sheet.PageSetup.PrintArea = $"${first}$1:${last}${lastVisible}";
            sheet.PageSetup.FitToPagesWide = 1;
            sheet.PageSetup.FitToPagesTall = false;
        }
        catch { }
    }

    private static bool RowHasAnyContent(dynamic sheet, int row, int lastColumn)
    {
        for (var column = 1; column <= lastColumn; column++)
        {
            try
            {
                string text = Convert.ToString((object?)sheet.Cells[row, column].Value2, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                if (text.Length > 0) return true;
                var formula = Convert.ToString(sheet.Cells[row, column].Formula, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                if (formula.Length > 0) return true;
            }
            catch { }
        }
        return false;
    }

    private static void ValidateNceSourceChecks(NceFinancialStatementIdentityService.Snapshot identities)
    {
        var failed = identities.SourceChecks.Where(x => !x.Passed).ToArray();
        if (failed.Length == 0) return;
        var details = string.Join("; ", failed.Take(4).Select(x =>
            string.IsNullOrWhiteSpace(x.Guidance)
                ? $"{x.Name}: FY source {x.FySource:N2} / identity {x.FyIdentity:N2}, PY source {x.PySource:N2} / identity {x.PyIdentity:N2}"
                : $"{x.Name}: {x.Guidance}"));
        throw new InvalidOperationException($"Financial Statement source-authority reconciliation failed. {details}");
    }

    private static void CompactNcePresentationSheets(dynamic book)
    {
        foreach (var sheetAuthority in NceFinancialStatementPresentationAuthority.NoteSheets)
        {
            dynamic sheet = book.Worksheets[sheetAuthority.TemplateSheet];
            var usedRows = Math.Max(1, Convert.ToInt32(sheet.UsedRange.Rows.Count, CultureInfo.InvariantCulture));
            // Rows 1-4 are the owning sheet heading/context area. Note visibility has already
            // been decided by the declared Note authority. Delete only those hidden Note rows,
            // bottom-up, so the container allocates exactly the returned Note blocks.
            for (var row = usedRows; row >= 5; row--)
            {
                try
                {
                    if (Convert.ToBoolean(sheet.Rows[row].Hidden, CultureInfo.InvariantCulture))
                        sheet.Rows[row].Delete();
                }
                catch { }
            }
        }
    }

    private static void RepairNceFaceLinksDynamic(dynamic book, NceFinancialStatementIdentityService.Snapshot identities)
    {
        dynamic bs = book.Worksheets[NceFinancialStatementPresentationAuthority.BalanceSheetTemplate];
        dynamic pnl = book.Worksheets[NceFinancialStatementPresentationAuthority.ProfitLossTemplate];

        dynamic NoteSheet(string note) => book.Worksheets[NceFinancialStatementPresentationAuthority.ForNote(note).OwnerSheet];
        string Total(string note) => NceFinancialStatementPresentationAuthority.ForNote(note).TotalLabel;

        var note3Format = NceFinancialStatementPresentationAuthority.ForNote("3").Format;
        LinkOrWriteFace(bs, 9, "Balance Sheet", "Owners' capital account", NoteSheet("3"), Total("3"), note3Format.FyColumn, note3Format.PyColumn, identities);
        LinkOrWriteFace(bs, 10, "Balance Sheet", "Reserves and surplus", NoteSheet("4"), Total("4"), 3, 4, identities);
        LinkOrWriteFace(bs, 13, "Balance Sheet", "Long-term borrowings", NoteSheet("5"), Total("5"), 3, 4, identities);
        WriteFaceValue(bs, 14, "Balance Sheet", "Deferred tax liabilities (Net)", identities);
        LinkOrWriteFace(bs, 15, "Balance Sheet", "Other long-term liabilities", NoteSheet("7"), Total("7"), 3, 4, identities);
        LinkOrWriteFace(bs, 16, "Balance Sheet", "Long-term provisions", NoteSheet("8"), Total("8"), 3, 4, identities);
        LinkOrWriteFace(bs, 19, "Balance Sheet", "Short-term borrowings", NoteSheet("9"), Total("9"), 3, 4, identities);
        LinkOrWriteFace(bs, 20, "Balance Sheet", "Trade payables", NoteSheet("10"), Total("10"), 3, 4, identities);
        LinkOrWriteFace(bs, 21, "Balance Sheet", "Other current liabilities", NoteSheet("11"), Total("11"), 3, 4, identities);
        LinkOrWriteFace(bs, 22, "Balance Sheet", "Short-term provisions", NoteSheet("12"), Total("12"), 3, 4, identities);
        WriteFaceValue(bs, 29, "Balance Sheet", "Property, Plant and Equipment", identities);
        WriteFaceValue(bs, 30, "Balance Sheet", "Intangible assets", identities);
        WriteFaceValue(bs, 31, "Balance Sheet", "Capital work-in-progress", identities);
        WriteFaceValue(bs, 32, "Balance Sheet", "Intangible assets under development", identities);
        WriteFaceValue(bs, 33, "Balance Sheet", "Non-current investments", identities);
        WriteFaceValue(bs, 34, "Balance Sheet", "Deferred tax assets (Net)", identities);
        LinkOrWriteFace(bs, 35, "Balance Sheet", "Long-term loans and advances", NoteSheet("15"), Total("15"), 3, 4, identities);
        LinkOrWriteFace(bs, 36, "Balance Sheet", "Other non-current assets", NoteSheet("16"), Total("16"), 3, 4, identities);
        WriteFaceValue(bs, 39, "Balance Sheet", "Current investments", identities);
        LinkOrWriteFace(bs, 40, "Balance Sheet", "Inventories", NoteSheet("17"), Total("17"), 3, 4, identities);
        LinkOrWriteFace(bs, 41, "Balance Sheet", "Trade receivables", NoteSheet("18"), Total("18"), 3, 4, identities);
        LinkOrWriteFace(bs, 42, "Balance Sheet", "Cash and Cash Equivalents", NoteSheet("19"), Total("19"), 3, 4, identities);
        LinkOrWriteFace(bs, 43, "Balance Sheet", "Short-term loans and advances", NoteSheet("20"), Total("20"), 3, 4, identities);
        LinkOrWriteFace(bs, 44, "Balance Sheet", "Other current assets", NoteSheet("21"), Total("21"), 3, 4, identities);

        LinkOrWriteFace(pnl, 7, "P&L Account", "Revenue from operations", NoteSheet("22"), Total("22"), 3, 4, identities);
        LinkOrWriteFace(pnl, 8, "P&L Account", "Other income", NoteSheet("23"), Total("23"), 3, 4, identities);
        LinkOrWriteFace(pnl, 12, "P&L Account", "Cost of goods sold", NoteSheet("24"), Total("24"), 3, 4, identities);
        LinkOrWriteFace(pnl, 13, "P&L Account", "Employee benefits expense", NoteSheet("25"), Total("25"), 3, 4, identities);
        LinkOrWriteFace(pnl, 14, "P&L Account", "Finance costs", NoteSheet("26"), Total("26"), 3, 4, identities);
        LinkOrWriteFace(pnl, 15, "P&L Account", "Depreciation and amortisation expense", NoteSheet("27"), Total("27"), 3, 4, identities);
        LinkOrWriteFace(pnl, 16, "P&L Account", "Other expenses", NoteSheet("28"), Total("28"), 3, 4, identities);
        WriteNceDirectFaceValues(pnl, identities);
        RepairNceStatementTotals(bs, pnl);
    }

    private static void RepairNceStatementTotals(dynamic bs, dynamic pnl)
    {
        // R51: face subtotal/total formulas belong to the statement sheet itself. They must be
        // rebuilt after dynamic Note compaction and may never link to a Note total by accident.
        SetFormula(bs, "E11", "=IF(COUNT(E9:E10)=0,\"\",SUM(E9:E10))");
        SetFormula(bs, "F11", "=IF(COUNT(F9:F10)=0,\"\",SUM(F9:F10))");
        SetFormula(bs, "E17", "=IF(COUNT(E13:E16)=0,\"\",SUM(E13:E16))");
        SetFormula(bs, "F17", "=IF(COUNT(F13:F16)=0,\"\",SUM(F13:F16))");
        SetFormula(bs, "E23", "=IF(COUNT(E19:E22)=0,\"\",SUM(E19:E22))");
        SetFormula(bs, "F23", "=IF(COUNT(F19:F22)=0,\"\",SUM(F19:F22))");
        SetFormula(bs, "E24", "=IF(COUNT(E11,E17,E23)=0,\"\",SUM(E11,E17,E23))");
        SetFormula(bs, "F24", "=IF(COUNT(F11,F17,F23)=0,\"\",SUM(F11,F17,F23))");
        SetFormula(bs, "E37", "=IF(COUNT(E29:E36)=0,\"\",SUM(E29:E36))");
        SetFormula(bs, "F37", "=IF(COUNT(F29:F36)=0,\"\",SUM(F29:F36))");
        SetFormula(bs, "E45", "=IF(COUNT(E39:E44)=0,\"\",SUM(E39:E44))");
        SetFormula(bs, "F45", "=IF(COUNT(F39:F44)=0,\"\",SUM(F39:F44))");
        SetFormula(bs, "E46", "=IF(COUNT(E37,E45)=0,\"\",SUM(E37,E45))");
        SetFormula(bs, "F46", "=IF(COUNT(F37,F45)=0,\"\",SUM(F37,F45))");
        try { ClearRangeMergedSafe(bs, "E47:F47", clearFormats: false); bs.Rows[47].Hidden = true; } catch { }

        SetFormula(pnl, "E9", "=IF(COUNT(E7:E8)=0,\"\",SUM(E7:E8))");
        SetFormula(pnl, "F9", "=IF(COUNT(F7:F8)=0,\"\",SUM(F7:F8))");
        SetFormula(pnl, "E17", "=IF(COUNT(E12:E16)=0,\"\",SUM(E12:E16))");
        SetFormula(pnl, "F17", "=IF(COUNT(F12:F16)=0,\"\",SUM(F12:F16))");
        SetFormula(pnl, "E18", "=IF(COUNT(E9,E17)=0,\"\",E9-E17)");
        SetFormula(pnl, "F18", "=IF(COUNT(F9,F17)=0,\"\",F9-F17)");
        SetFormula(pnl, "E20", "=IF(COUNT(E18:E19)=0,\"\",E18-E19)");
        SetFormula(pnl, "F20", "=IF(COUNT(F18:F19)=0,\"\",F18-F19)");
        SetFormula(pnl, "E22", "=IF(COUNT(E20:E21)=0,\"\",E20-E21)");
        SetFormula(pnl, "F22", "=IF(COUNT(F20:F21)=0,\"\",F20-F21)");
        SetFormula(pnl, "E24", "=IF(COUNT(E22:E23)=0,\"\",E22-E23)");
        SetFormula(pnl, "F24", "=IF(COUNT(F22:F23)=0,\"\",F22-F23)");
    }

    private static void LinkOrWriteFace(
        dynamic faceSheet, int faceRow, string statement, string identity, dynamic noteSheet, string totalLabel,
        int fyColumn, int pyColumn, NceFinancialStatementIdentityService.Snapshot identities)
    {
        var totalRow = FindLabelRow(noteSheet, totalLabel);
        if (totalRow <= 0)
        {
            WriteFaceValue(faceSheet, faceRow, statement, identity, identities);
            return;
        }
        var sheetName = Convert.ToString(noteSheet.Name, CultureInfo.InvariantCulture)?.Replace("'", "''") ?? string.Empty;
        var fyCol = ColumnName(fyColumn);
        var pyCol = ColumnName(pyColumn);
        SetFormula(faceSheet, $"E{faceRow}", $"='{sheetName}'!{fyCol}{totalRow}");
        SetFormula(faceSheet, $"F{faceRow}", $"='{sheetName}'!{pyCol}{totalRow}");
    }

    private static void WriteFaceValue(
        dynamic faceSheet, int faceRow, string statement, string identity, NceFinancialStatementIdentityService.Snapshot identities)
    {
        var line = identities.Face(statement, identity);
        faceSheet.Cells[faceRow, 5].Value2 = (double)(line?.Fy ?? 0m);
        faceSheet.Cells[faceRow, 6].Value2 = (double)(line?.Py ?? 0m);
        try { faceSheet.Range[$"E{faceRow}:F{faceRow}"].NumberFormat = NumberFormat; } catch { }
    }

    private static void ValidateNceSheetNoteAuthority(dynamic book)
    {
        foreach (var sheetAuthority in NceFinancialStatementPresentationAuthority.NoteSheets)
        {
            dynamic declaredSheet = book.Worksheets[sheetAuthority.TemplateSheet];
            foreach (var noteId in sheetAuthority.Notes)
            {
                var spec = NceFinancialStatementPresentationAuthority.ForNote(noteId);
                var declaredRow = FindExactNoteTitleRow(declaredSheet, spec);
                if (declaredRow <= 0)
                    throw new InvalidOperationException($"NCE Note {noteId} is missing from its declared physical sheet '{sheetAuthority.TemplateSheet}'.");

                foreach (var other in NceFinancialStatementPresentationAuthority.NoteSheets.Where(x =>
                             !x.TemplateSheet.Equals(sheetAuthority.TemplateSheet, StringComparison.OrdinalIgnoreCase)))
                {
                    dynamic otherSheet = book.Worksheets[other.TemplateSheet];
                    if (FindExactNoteTitleRow(otherSheet, spec) > 0)
                        throw new InvalidOperationException($"NCE Note {noteId} appears on more than one physical sheet; declared owner is '{sheetAuthority.TemplateSheet}'.");
                }
            }
        }
    }

    private static void ValidateNceNote13HeadingBindings(
        dynamic book,
        NceFinancialStatementIdentityService.Snapshot identities)
    {
        var spec = NceFinancialStatementPresentationAuthority.ForNote("13");
        dynamic sheet = book.Worksheets[spec.OwnerSheet];
        var start = FindExactNoteTitleRow(sheet, spec);
        var note14 = NceFinancialStatementPresentationAuthority.ForNote("14");
        var next = FindExactNoteTitleRow(sheet, note14);
        var end = next > start ? next - 1 : Math.Max(start, Convert.ToInt32(sheet.UsedRange.Rows.Count, CultureInfo.InvariantCulture));
        var note = identities.Note("13");
        if (note is null || !note.HasData) return;

        foreach (var heading in note.Headings.Where(x => x.HasData))
        {
            if (FindLabelRowInRange(sheet, heading.Title, start, end) <= 0)
                throw new InvalidOperationException(
                    $"NCE Note 13 presentation contract has no template row for identity heading '{heading.Title}' on '{spec.OwnerSheet}'.");
        }
    }

    private static void ValidateNceRenderedNoteAuthority(
        dynamic book,
        NceFinancialStatementIdentityService.Snapshot identities)
    {
        foreach (var sheetAuthority in NceFinancialStatementPresentationAuthority.NoteSheets)
        {
            dynamic sheet = book.Worksheets[sheetAuthority.TemplateSheet];
            foreach (var noteId in sheetAuthority.Notes)
            {
                var spec = NceFinancialStatementPresentationAuthority.ForNote(noteId);
                var note = identities.Note(noteId);
                var row = FindExactNoteTitleRow(sheet, spec);
                var shouldRender = ShouldRenderNceNote(spec, note);
                if (shouldRender && row <= 0)
                    throw new InvalidOperationException($"NCE Note {noteId} is required by its presentation identity but its rendered block is missing from '{sheetAuthority.TemplateSheet}'.");
                if (!shouldRender && row > 0)
                    throw new InvalidOperationException($"NCE Note {noteId} has no FY/PY data but its rendered block was not removed from '{sheetAuthority.TemplateSheet}'.");
            }
        }
    }

    private static bool ExactNoteTitleMatches(
        string? value,
        NceFinancialStatementPresentationAuthority.NotePresentationSpec spec)
    {
        static string Normalize(string text)
            => string.Join(" ", (text ?? string.Empty)
                .Replace("–", "-")
                .Replace("—", "-")
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Trim();
        var actual = Normalize(value ?? string.Empty);
        var expected = Normalize(spec.ExactTitle);
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static int FindExactNoteTitleRow(
        dynamic sheet,
        NceFinancialStatementPresentationAuthority.NotePresentationSpec spec)
    {
        var usedRows = Math.Max(1, Convert.ToInt32(sheet.UsedRange.Rows.Count, CultureInfo.InvariantCulture));
        for (var row = 1; row <= usedRows; row++)
        {
            for (var column = 1; column <= 2; column++)
            {
                string text = Convert.ToString((object?)sheet.Cells[row, column].Value2, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                if (ExactNoteTitleMatches(text, spec)) return row;
            }
        }
        return 0;
    }

    private static int FindLabelRowInRange(dynamic sheet, string label, int startRow, int endRow)
    {
        static string Normalize(string value)
            => string.Join(" ", (value ?? string.Empty).Replace("–", "-").Replace("—", "-")
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Trim(' ', ':', '-', '.');
        var target = Normalize(label);
        for (var row = Math.Max(1, startRow); row <= Math.Max(startRow, endRow); row++)
        {
            for (var column = 1; column <= 2; column++)
            {
                string text = Convert.ToString((object?)sheet.Cells[row, column].Value2, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                if (Normalize(text).Equals(target, StringComparison.OrdinalIgnoreCase)) return row;
            }
        }
        return 0;
    }

    private static int FindLabelRow(dynamic sheet, string label)
    {
        static string Normalize(string value)
            => string.Join(" ", (value ?? string.Empty).Replace("–", "-").Replace("—", "-")
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Trim(' ', ':', '-', '.');
        var target = Normalize(label);
        var usedRows = Math.Max(1, Convert.ToInt32(sheet.UsedRange.Rows.Count, CultureInfo.InvariantCulture));
        for (var row = 1; row <= usedRows; row++)
        {
            for (var column = 1; column <= 2; column++)
            {
                string text = Convert.ToString((object?)sheet.Cells[row, column].Value2, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                if (Normalize(text).Equals(target, StringComparison.OrdinalIgnoreCase)) return row;
            }
        }
        return 0;
    }

    private static bool IsDevelopmentPpePresentationMovement(
        NceFinancialStatementIdentityService.PpeMovementIdentity movement)
        => movement.Title.Equals("Capital work-in-progress", StringComparison.OrdinalIgnoreCase) ||
           movement.Title.Equals("Intangible assets under development", StringComparison.OrdinalIgnoreCase);

    private static void CrossCheckNceNotePresentationTotals(
        dynamic book,
        NceFinancialStatementIdentityService.Snapshot identities,
        bool checkPy)
    {
        static void CheckAmount(decimal actual, decimal expected, string description)
        {
            if (Math.Abs(actual - expected) > 0.01m)
                throw new InvalidOperationException($"Financial Statement Note presentation cross-check failed for {description}. Identity {expected:N2} vs sheet {actual:N2}.");
        }

        foreach (var sheetAuthority in NceFinancialStatementPresentationAuthority.NoteSheets)
        {
            dynamic sheet = book.Worksheets[sheetAuthority.TemplateSheet];
            var rendered = sheetAuthority.Notes
                .Select(id => (Spec: NceFinancialStatementPresentationAuthority.ForNote(id), Note: identities.Note(id)))
                .Where(x => x.Note is not null && x.Note.HasData)
                .Select(x => (x.Spec, Note: x.Note!, Row: FindExactNoteTitleRow(sheet, x.Spec)))
                .Where(x => x.Row > 0)
                .OrderBy(x => x.Row)
                .ToArray();

            for (var i = 0; i < rendered.Length; i++)
            {
                var spec = rendered[i].Spec;
                var note = rendered[i].Note;
                var start = rendered[i].Row;
                var end = i + 1 < rendered.Length
                    ? rendered[i + 1].Row - 1
                    : Math.Max(start, Convert.ToInt32(sheet.UsedRange.Rows.Count, CultureInfo.InvariantCulture));

                if (spec.Note == "3")
                {
                    var totalRow = FindLabelRowInRange(sheet, spec.TotalLabel, start, end);
                    if (totalRow <= 0) throw new InvalidOperationException("NCE Note 3 total row is missing after rendering.");
                    CheckAmount(ReadDecimal(sheet.Cells[totalRow, spec.Format.FyColumn]), note.FyTotal, "Note 3 FY total");
                    if (checkPy) CheckAmount(ReadDecimal(sheet.Cells[totalRow, spec.Format.PyColumn]), note.PyTotal, "Note 3 PY total");
                    continue;
                }

                if (spec.Note == "13")
                {
                    foreach (var heading in note.Headings.Where(x => x.HasData))
                    {
                        var row = FindLabelRowInRange(sheet, heading.Title, start, end);
                        if (row <= 0)
                            throw new InvalidOperationException($"NCE Note 13 source heading '{heading.Title}' is missing from its declared sheet.");
                        CheckAmount(ReadDecimal(sheet.Cells[row, 7]), heading.Fy, $"Note 13 FY {heading.Title}");
                        if (checkPy) CheckAmount(ReadDecimal(sheet.Cells[row, 8]), heading.Py, $"Note 13 PY {heading.Title}");
                    }
                    var ppeTotal = FindLabelRowInRange(sheet, spec.TotalLabel, start, end);
                    if (ppeTotal <= 0) throw new InvalidOperationException("NCE Note 13 total row is missing after rendering.");
                    // R52 FIXED3: the supplied Note 13 movement Total belongs only to the PPE /
                    // Intangible movement table. CWIP and Intangible Assets under Development are
                    // separate closing-balance rows below that table and are already cross-checked
                    // individually above. Do not compare the movement-table Total with the whole
                    // Note identity including those development rows.
                    var ppeMovementTotalFy = identities.PpeMovements
                        .Where(x => !IsDevelopmentPpePresentationMovement(x))
                        .Sum(x => x.FyClosing);
                    var ppeMovementTotalPy = identities.PpeMovements
                        .Where(x => !IsDevelopmentPpePresentationMovement(x))
                        .Sum(x => x.PyClosing);
                    CheckAmount(ReadDecimal(sheet.Cells[ppeTotal, 7]), ppeMovementTotalFy, "Note 13 FY PPE/IA movement total");
                    if (checkPy) CheckAmount(ReadDecimal(sheet.Cells[ppeTotal, 8]), ppeMovementTotalPy, "Note 13 PY PPE/IA movement total");
                    continue;
                }

                if (spec.Note == "6")
                {
                    var fyDtl = identities.Face("Balance Sheet", "Deferred tax liabilities (Net)")?.Fy ?? 0m;
                    var pyDtl = identities.Face("Balance Sheet", "Deferred tax liabilities (Net)")?.Py ?? 0m;
                    var fyDta = identities.Face("Balance Sheet", "Deferred tax assets (Net)")?.Fy ?? 0m;
                    var pyDta = identities.Face("Balance Sheet", "Deferred tax assets (Net)")?.Py ?? 0m;
                    var liabRow = FindLabelRowInRange(sheet, "Total Deferred Tax Liabilities", start, end);
                    var assetRow = FindLabelRowInRange(sheet, "Total Deferred Tax Assets", start, end);
                    if (liabRow > 0)
                    {
                        CheckAmount(ReadDecimal(sheet.Cells[liabRow, 3]), fyDtl, "Note 6 FY deferred tax liabilities");
                        if (checkPy) CheckAmount(ReadDecimal(sheet.Cells[liabRow, 4]), pyDtl, "Note 6 PY deferred tax liabilities");
                    }
                    if (assetRow > 0)
                    {
                        CheckAmount(ReadDecimal(sheet.Cells[assetRow, 3]), fyDta, "Note 6 FY deferred tax assets");
                        if (checkPy) CheckAmount(ReadDecimal(sheet.Cells[assetRow, 4]), pyDta, "Note 6 PY deferred tax assets");
                    }
                    continue;
                }

                if (spec.Note == "14")
                {
                    var current = identities.Face("Balance Sheet", "Current investments");
                    var nonCurrent = identities.Face("Balance Sheet", "Non-current investments");
                    var currentRow = FindLabelRowInRange(sheet, "Total Current Investments", start, end);
                    var nonCurrentRow = FindLabelRowInRange(sheet, "Total Non-current Investments", start, end);
                    if (currentRow > 0)
                    {
                        CheckAmount(ReadDecimal(sheet.Cells[currentRow, 3]), current?.Fy ?? 0m, "Note 14 FY current investments");
                        if (checkPy) CheckAmount(ReadDecimal(sheet.Cells[currentRow, 4]), current?.Py ?? 0m, "Note 14 PY current investments");
                    }
                    if (nonCurrentRow > 0)
                    {
                        CheckAmount(ReadDecimal(sheet.Cells[nonCurrentRow, 3]), nonCurrent?.Fy ?? 0m, "Note 14 FY non-current investments");
                        if (checkPy) CheckAmount(ReadDecimal(sheet.Cells[nonCurrentRow, 4]), nonCurrent?.Py ?? 0m, "Note 14 PY non-current investments");
                    }
                    continue;
                }

                if (string.IsNullOrWhiteSpace(spec.TotalLabel)) continue;
                var total = FindLabelRowInRange(sheet, spec.TotalLabel, start, end);
                if (total <= 0)
                    throw new InvalidOperationException($"NCE Note {spec.Note} total '{spec.TotalLabel}' is missing after rendering.");
                CheckAmount(ReadDecimal(sheet.Cells[total, 3]), note.FyTotal, $"Note {spec.Note} FY total");
                if (checkPy) CheckAmount(ReadDecimal(sheet.Cells[total, 4]), note.PyTotal, $"Note {spec.Note} PY total");
            }
        }
    }

    private static void CrossCheckNceIdentities(dynamic book, NceFinancialStatementIdentityService.Snapshot identities, bool checkPy)
    {
        dynamic bs = book.Worksheets["NCE Balance Sheet"];
        dynamic pnl = book.Worksheets["NCE Notes 1-3"];
        var checks = new List<(dynamic Sheet, int Row, string Statement, string Identity)>
        {
            (bs,9,"Balance Sheet","Owners' capital account"),
            (bs,10,"Balance Sheet","Reserves and surplus"),
            (bs,13,"Balance Sheet","Long-term borrowings"),
            (bs,14,"Balance Sheet","Deferred tax liabilities (Net)"),
            (bs,15,"Balance Sheet","Other long-term liabilities"),
            (bs,16,"Balance Sheet","Long-term provisions"),
            (bs,19,"Balance Sheet","Short-term borrowings"),
            (bs,20,"Balance Sheet","Trade payables"),
            (bs,21,"Balance Sheet","Other current liabilities"),
            (bs,22,"Balance Sheet","Short-term provisions"),
            (bs,29,"Balance Sheet","Property, Plant and Equipment"),
            (bs,30,"Balance Sheet","Intangible assets"),
            (bs,31,"Balance Sheet","Capital work-in-progress"),
            (bs,32,"Balance Sheet","Intangible assets under development"),
            (bs,33,"Balance Sheet","Non-current investments"),
            (bs,34,"Balance Sheet","Deferred tax assets (Net)"),
            (bs,35,"Balance Sheet","Long-term loans and advances"),
            (bs,36,"Balance Sheet","Other non-current assets"),
            (bs,39,"Balance Sheet","Current investments"),
            (bs,40,"Balance Sheet","Inventories"),
            (bs,41,"Balance Sheet","Trade receivables"),
            (bs,42,"Balance Sheet","Cash and Cash Equivalents"),
            (bs,43,"Balance Sheet","Short-term loans and advances"),
            (bs,44,"Balance Sheet","Other current assets"),
            (pnl,7,"P&L Account","Revenue from operations"),
            (pnl,8,"P&L Account","Other income"),
            (pnl,12,"P&L Account","Cost of goods sold"),
            (pnl,13,"P&L Account","Employee benefits expense"),
            (pnl,14,"P&L Account","Finance costs"),
            (pnl,15,"P&L Account","Depreciation and amortisation expense"),
            (pnl,16,"P&L Account","Other expenses"),
            (pnl,19,"P&L Account","Exceptional items"),
            (pnl,21,"P&L Account","Partners' remuneration"),
            (pnl,23,"P&L Account","Tax expense")
        };

        foreach (var check in checks)
        {
            var identity = identities.Face(check.Statement, check.Identity);
            if (identity is null) continue;
            var fyActual = ReadDecimal(check.Sheet.Cells[check.Row, 5]);
            var pyActual = ReadDecimal(check.Sheet.Cells[check.Row, 6]);
            if (Math.Abs(fyActual - identity.Fy) > 0.01m || (checkPy && Math.Abs(pyActual - identity.Py) > 0.01m))
            {
                throw new InvalidOperationException(
                    $"Financial Statement cross-check failed for {check.Identity}. " +
                    $"FY Note identity {identity.Fy:N2} vs statement {fyActual:N2}" +
                    (checkPy ? $"; PY Note identity {identity.Py:N2} vs statement {pyActual:N2}." : "."));
            }
        }
    }

    private static void CrossCheckNceStatementTotals(
        dynamic book,
        NceFinancialStatementIdentityService.Snapshot identities,
        bool checkPy)
    {
        dynamic bs = book.Worksheets[NceFinancialStatementPresentationAuthority.BalanceSheetTemplate];
        dynamic pnl = book.Worksheets[NceFinancialStatementPresentationAuthority.ProfitLossTemplate];

        decimal Face(string statement, string identity, bool py)
        {
            var line = identities.Face(statement, identity);
            return line is null ? 0m : py ? line.Py : line.Fy;
        }

        void Check(dynamic sheet, string cell, decimal expected, string label, bool py)
        {
            if (py && !checkPy) return;
            var actual = ReadDecimal(sheet.Range[cell]);
            if (Math.Abs(actual - expected) > 0.01m)
                throw new InvalidOperationException($"Financial Statement total cross-check failed for {label}. Expected {expected:N2}, statement {actual:N2}.");
        }

        foreach (var py in new[] { false, true })
        {
            if (py && !checkPy) continue;
            var c = py ? "F" : "E";
            var owners = Face("Balance Sheet", "Owners' capital account", py) + Face("Balance Sheet", "Reserves and surplus", py);
            var nonCurrentLiabilities = Face("Balance Sheet", "Long-term borrowings", py) +
                                        Face("Balance Sheet", "Deferred tax liabilities (Net)", py) +
                                        Face("Balance Sheet", "Other long-term liabilities", py) +
                                        Face("Balance Sheet", "Long-term provisions", py);
            var currentLiabilities = Face("Balance Sheet", "Short-term borrowings", py) +
                                     Face("Balance Sheet", "Trade payables", py) +
                                     Face("Balance Sheet", "Other current liabilities", py) +
                                     Face("Balance Sheet", "Short-term provisions", py);
            var totalLiabilities = owners + nonCurrentLiabilities + currentLiabilities;

            var nonCurrentAssets = Face("Balance Sheet", "Property, Plant and Equipment", py) +
                                   Face("Balance Sheet", "Intangible assets", py) +
                                   Face("Balance Sheet", "Capital work-in-progress", py) +
                                   Face("Balance Sheet", "Intangible assets under development", py) +
                                   Face("Balance Sheet", "Non-current investments", py) +
                                   Face("Balance Sheet", "Deferred tax assets (Net)", py) +
                                   Face("Balance Sheet", "Long-term loans and advances", py) +
                                   Face("Balance Sheet", "Other non-current assets", py);
            var currentAssets = Face("Balance Sheet", "Current investments", py) +
                                Face("Balance Sheet", "Inventories", py) +
                                Face("Balance Sheet", "Trade receivables", py) +
                                Face("Balance Sheet", "Cash and Cash Equivalents", py) +
                                Face("Balance Sheet", "Short-term loans and advances", py) +
                                Face("Balance Sheet", "Other current assets", py);
            var totalAssets = nonCurrentAssets + currentAssets;

            Check(bs, $"{c}11", owners, py ? "PY Total Owners' Funds" : "FY Total Owners' Funds", py);
            Check(bs, $"{c}17", nonCurrentLiabilities, py ? "PY Total Non-current Liabilities" : "FY Total Non-current Liabilities", py);
            Check(bs, $"{c}23", currentLiabilities, py ? "PY Total Current Liabilities" : "FY Total Current Liabilities", py);
            Check(bs, $"{c}24", totalLiabilities, py ? "PY Total Equity and Liabilities" : "FY Total Equity and Liabilities", py);
            Check(bs, $"{c}37", nonCurrentAssets, py ? "PY Total Non-current Assets" : "FY Total Non-current Assets", py);
            Check(bs, $"{c}45", currentAssets, py ? "PY Total Current Assets" : "FY Total Current Assets", py);
            Check(bs, $"{c}46", totalAssets, py ? "PY Total Assets" : "FY Total Assets", py);

            var income = Face("P&L Account", "Revenue from operations", py) + Face("P&L Account", "Other income", py);
            var expenses = Face("P&L Account", "Cost of goods sold", py) +
                           Face("P&L Account", "Employee benefits expense", py) +
                           Face("P&L Account", "Finance costs", py) +
                           Face("P&L Account", "Depreciation and amortisation expense", py) +
                           Face("P&L Account", "Other expenses", py);
            var beforeExceptional = income - expenses;
            var beforeRemuneration = beforeExceptional - Face("P&L Account", "Exceptional items", py);
            var beforeTax = beforeRemuneration - Face("P&L Account", "Partners' remuneration", py);
            var profit = beforeTax - Face("P&L Account", "Tax expense", py);

            Check(pnl, $"{c}9", income, py ? "PY Total Income" : "FY Total Income", py);
            Check(pnl, $"{c}17", expenses, py ? "PY Total Expenses" : "FY Total Expenses", py);
            Check(pnl, $"{c}18", beforeExceptional, py ? "PY Profit before exceptional items" : "FY Profit before exceptional items", py);
            Check(pnl, $"{c}20", beforeRemuneration, py ? "PY Profit before partners' remuneration" : "FY Profit before partners' remuneration", py);
            Check(pnl, $"{c}22", beforeTax, py ? "PY Profit before tax" : "FY Profit before tax", py);
            Check(pnl, $"{c}24", profit, py ? "PY Profit for the year" : "FY Profit for the year", py);
        }
    }

    private static decimal ReadDecimal(dynamic cell)
    {
        try { return Convert.ToDecimal(cell.Value2 ?? 0d, CultureInfo.InvariantCulture); }
        catch { return 0m; }
    }

    private static dynamic OpenWorkbookWithRepairFallback(dynamic excel, string path)
    {
        Exception? first = null;
        try
        {
            // Full Open signature avoids the Office automation ambiguity that produced
            // "Unable to get the Open property of the Workbooks class" on the target PC.
            return excel.Workbooks.Open(path, 0, false, Type.Missing, Type.Missing, Type.Missing,
                true, Type.Missing, Type.Missing, false, false, Type.Missing, false, true, 0);
        }
        catch (Exception ex)
        {
            first = ex;
        }

        try
        {
            System.Threading.Thread.Sleep(250);
            // xlRepairFile fallback. This is safe because the working copy is disposable and
            // the original packaged presentation authority is never modified.
            return excel.Workbooks.Open(path, 0, false, Type.Missing, Type.Missing, Type.Missing,
                true, Type.Missing, Type.Missing, false, false, Type.Missing, false, true, 1);
        }
        catch (Exception second)
        {
            throw new InvalidOperationException(
                $"Microsoft Excel could not open the local AAH presentation working copy. First attempt: {first?.Message}. Repair attempt: {second.Message}", second);
        }
    }

    private static void TryRemoveZoneIdentifier(string path)
    {
        try { File.Delete(path + ":Zone.Identifier"); } catch { }
    }

    private static void WriteBasicInfo(dynamic sheet, FinancialStatementBasicInfo info, IEnumerable<EntitySignatoryRow> signatories, IEnumerable<OwnerPartnerRow> owners, string profile)
    {
        sheet.Range["C7"].Value2 = First(info.CurrentYearCompanyName, info.EntityName, info.MailingName);
        SetDate(sheet.Range["C8"], info.CurrentYearEnd);
        sheet.Range["C9"].Value2 = First(info.PreviousYearCompanyName, info.PreviousYearTallyCompany);
        SetDate(sheet.Range["C10"], info.PreviousYearEnd);
        sheet.Range["C13"].Value2 = First(info.EntityName, info.MailingName, info.CurrentYearCompanyName);
        sheet.Range["C14"].Value2 = info.Address;
        sheet.Range["C15"].Value2 = info.Pan;
        sheet.Range["C16"].Value2 = profile == "NCE" ? First(info.NceType, "Non-Corporate Entity") : info.EntityType;
        sheet.Range["C17"].Value2 = string.IsNullOrWhiteSpace(info.Rounding) ? "Default" : info.Rounding;
        sheet.Range["C18"].Value2 = string.IsNullOrWhiteSpace(info.DebtorCreditorDetail) ? "As Per Trial Balance Closing Balance" : info.DebtorCreditorDetail;
        sheet.Range["C19"].Value2 = string.IsNullOrWhiteSpace(info.PpeBlock) ? "Gross Block" : info.PpeBlock;
        sheet.Range["C20"].Value2 = profile == "NCE" ? "Non-Corporate Entity" : "Company - Division I (Non-Ind AS)";

        // Signatory block: rows 23-25 beneath the example workbook's row-22 headings.
        ClearRangeMergedSafe(sheet, "B23:E25", clearFormats: false);
        var signRows = signatories.Where(x => !string.IsNullOrWhiteSpace(x.Name)).Take(3).ToArray();
        for (var i = 0; i < signRows.Length; i++)
        {
            var row = 23 + i;
            sheet.Cells[row, 2].Value2 = signRows[i].Name;
            sheet.Cells[row, 3].Value2 = signRows[i].Designation;
            sheet.Cells[row, 4].Value2 = info.SigningPlace;
            SetDate(sheet.Cells[row, 5], info.SigningDate);
        }
        if (signRows.Length == 0 && !string.IsNullOrWhiteSpace(info.SignatoryName))
        {
            sheet.Range["B23"].Value2 = info.SignatoryName;
            sheet.Range["C23"].Value2 = info.SignatoryDesignation;
            sheet.Range["D23"].Value2 = info.SigningPlace;
            SetDate(sheet.Range["E23"], info.SigningDate);
        }

        // CA / audit details follow the actual example locations.
        sheet.Range["B27"].Value2 = info.AuditFirmName;
        sheet.Range["C27"].Value2 = info.FirmRegistrationNumber;
        sheet.Range["B30"].Value2 = info.CaName;
        sheet.Range["C30"].Value2 = info.CaDesignation;
        sheet.Range["D30"].Value2 = info.CaMembershipNumber;
        sheet.Range["E30"].Value2 = info.AuditPlace;
        SetDate(sheet.Range["F30"], info.CaSigningDate);

        ClearRangeMergedSafe(sheet, "B34:C44", clearFormats: false);
        if (profile == "NCE")
        {
            var ownerRows = owners.Where(x => !string.IsNullOrWhiteSpace(x.Name)).Take(11).ToArray();
            for (var i = 0; i < ownerRows.Length; i++)
            {
                sheet.Cells[34 + i, 2].Value2 = ownerRows[i].Name;
                sheet.Cells[34 + i, 3].Value2 = (double)ToExcelPercent(ownerRows[i].ShareOfProfitLoss);
            }
        }
        else
        {
            var shareRows = (info.ShareCapitalDetails ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x.TypeOfShares) || x.FaceValue != 0m)
                .Take(11).ToArray();
            for (var i = 0; i < shareRows.Length; i++)
            {
                sheet.Cells[34 + i, 2].Value2 = shareRows[i].TypeOfShares;
                sheet.Cells[34 + i, 3].Value2 = (double)shareRows[i].FaceValue;
            }
        }
    }

    private static void WriteNceNarrativesAndSignatures(
        dynamic book,
        FinancialStatementBasicInfo info,
        IEnumerable<EntitySignatoryRow> signatories)
    {
        dynamic notes = book.Worksheets["NCE Notes 4-6"];
        var entity = First(info.EntityName, info.CurrentYearCompanyName, info.MailingName);
        var constitution = First(info.NceType, "Non-Corporate Entity");
        var address = (info.Address ?? string.Empty).Trim();
        var identifiers = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.Pan)) identifiers.Add($"PAN: {info.Pan.Trim()}");
        if (!string.IsNullOrWhiteSpace(info.Gstin)) identifiers.Add($"GSTIN: {info.Gstin.Trim()}");

        ClearRangeMergedSafe(notes, "B6:F11", clearFormats: false);
        notes.Range["B6:F11"].Merge();
        var narrative =
            $"{entity} is presented as a {constitution} for these financial statements. " +
            $"The financial statements cover the year ended {First(info.CurrentYearEnd, "the current reporting date")}." +
            (address.Length > 0 ? $" The principal / mailing address recorded in AAH is {address}." : string.Empty) +
            (identifiers.Count > 0 ? $" Recorded identifiers: {string.Join(", ", identifiers)}." : string.Empty) +
            " The financial statements are prepared from the books, saved financial-statement mappings and the applicable NCE source authorities available in AAH.";
        notes.Range["B6"].Value2 = narrative;
        notes.Range["B6:F11"].WrapText = true;
        notes.Range["B6:F11"].VerticalAlignment = -4160; // xlTop
        notes.Range["B6:F11"].HorizontalAlignment = -4131; // xlLeft

        var policies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Basis of preparation"] = "Prepared on the historical-cost basis from the books of account, subject to the presentation, classification and source-authority rules selected in AAH.",
            ["Use of estimates"] = "Estimates and judgements, where required, are based on information available at the reporting date and are reviewed when underlying circumstances change.",
            ["Revenue recognition"] = "Revenue is recognised from the mapped revenue ledgers for the reporting period, net of amounts presented separately under the applicable mappings.",
            ["Property, Plant and Equipment"] = $"Property, plant and equipment is presented using the selected {First(info.PpeBlock, "PPE")} basis and the mapped fixed-asset source records.",
            ["Depreciation and amortisation"] = "Depreciation and amortisation expense is recognised from the mapped depreciation and amortisation ledgers for the reporting period.",
            ["Inventories"] = "Inventories are presented from the official NCE Stock Summary authority where available; saved inventory classification controls the statutory line presentation.",
            ["Investments"] = "Investments are presented according to the saved financial-statement mappings and current/non-current classification.",
            ["Employee benefits"] = "Employee-benefit costs are recognised from the mapped employee-benefit expense ledgers for the reporting period.",
            ["Borrowing costs"] = "Borrowing costs are recognised from the mapped finance-cost ledgers for the reporting period.",
            ["Taxes on income"] = "Current, deferred and prior-period tax amounts are presented from the corresponding mapped tax ledgers when applicable.",
            ["Provisions and contingencies"] = "Provisions are presented from the saved mappings. Contingent items are not manufactured by AAH where no supporting source is available.",
            ["Foreign currency transactions"] = "Foreign-currency effects are presented only where supported by the underlying books and saved mappings.",
            ["Cash and cash equivalents"] = "Cash and bank balances are presented from the mapped cash and bank ledgers at the reporting date.",
            ["Other material accounting policies"] = "Other material accounting-policy disclosures should follow the entity's books, saved mappings and supporting records; AAH does not infer unsupported entity-specific policies."
        };
        for (var row = 16; row <= 29; row++)
        {
            string policy = Convert.ToString((object?)notes.Cells[row, 2].Value2, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            string? disclosure;
            notes.Cells[row, 3].Value2 = policies.TryGetValue(policy, out disclosure) ? disclosure ?? string.Empty : string.Empty;
            try { notes.Range[$"C{row}:F{row}"].Merge(); } catch { }
            notes.Range[$"C{row}:F{row}"].WrapText = true;
            notes.Range[$"C{row}:F{row}"].VerticalAlignment = -4160;
        }

        var sig = signatories.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Name));
        WriteNceSignatureBlock(book.Worksheets["NCE Balance Sheet"], 49, info, sig);
        WriteNceSignatureBlock(book.Worksheets["NCE Notes 1-3"], 27, info, sig);
    }

    private static void WriteNceSignatureBlock(dynamic sheet, int startRow, FinancialStatementBasicInfo info, EntitySignatoryRow? sig)
    {
        try
        {
            ClearRangeMergedSafe(sheet, $"B{startRow}:F{startRow + 11}", clearFormats: true);
            sheet.Range[$"B{startRow}:F{startRow + 11}"].Font.Name = "Arial";
            sheet.Range[$"B{startRow}:F{startRow + 11}"].Font.Size = 10;
            sheet.Range[$"B{startRow}:F{startRow + 11}"].VerticalAlignment = -4108;
            sheet.Range[$"B{startRow}:F{startRow + 11}"].WrapText = true;
            for (var r = startRow; r <= startRow + 11; r++)
            {
                try { sheet.Range[$"B{r}:C{r}"].Merge(); } catch { }
                try { sheet.Range[$"E{r}:F{r}"].Merge(); } catch { }
                sheet.Rows[r].RowHeight = 18;
            }
            sheet.Rows[startRow + 1].RowHeight = 9;
            sheet.Rows[startRow + 5].RowHeight = 18;
        }
        catch { }

        sheet.Cells[startRow, 2].Value2 = "As per our report of even date";
        sheet.Cells[startRow, 5].Value2 = $"For {First(info.EntityName, info.CurrentYearCompanyName, info.MailingName)}";

        if (!string.IsNullOrWhiteSpace(info.AuditFirmName))
        {
            sheet.Cells[startRow + 2, 2].Value2 = info.AuditFirmName;
            sheet.Cells[startRow + 3, 2].Value2 = "Chartered Accountants";
            if (!string.IsNullOrWhiteSpace(info.FirmRegistrationNumber))
                sheet.Cells[startRow + 4, 2].Value2 = $"FRN: {info.FirmRegistrationNumber}";
        }
        if (!string.IsNullOrWhiteSpace(info.CaName))
        {
            sheet.Cells[startRow + 6, 2].Value2 = info.CaName;
            sheet.Cells[startRow + 7, 2].Value2 = First(info.CaDesignation, "Chartered Accountant");
            if (!string.IsNullOrWhiteSpace(info.CaMembershipNumber))
                sheet.Cells[startRow + 8, 2].Value2 = $"Membership No.: {info.CaMembershipNumber}";
            if (!string.IsNullOrWhiteSpace(info.Udin))
                sheet.Cells[startRow + 9, 2].Value2 = $"UDIN: {info.Udin}";
        }

        var signatoryName = First(sig?.Name, info.SignatoryName);
        var signatoryDesignation = First(sig?.Designation, info.SignatoryDesignation, "Proprietor / Authorised Signatory");
        if (!string.IsNullOrWhiteSpace(signatoryName))
        {
            sheet.Cells[startRow + 6, 5].Value2 = signatoryName;
            sheet.Cells[startRow + 7, 5].Value2 = signatoryDesignation;
        }

        var auditPlace = First(info.AuditPlace, info.SigningPlace);
        var auditDate = First(info.CaSigningDate, info.SigningDate);
        if (!string.IsNullOrWhiteSpace(auditPlace)) sheet.Cells[startRow + 10, 2].Value2 = $"Place: {auditPlace}";
        if (!string.IsNullOrWhiteSpace(auditDate)) sheet.Cells[startRow + 11, 2].Value2 = $"Date: {auditDate}";
        var entityPlace = First(sig?.Place, info.SigningPlace);
        var entityDate = First(sig?.SigningDate, info.SigningDate);
        if (!string.IsNullOrWhiteSpace(entityPlace)) sheet.Cells[startRow + 10, 5].Value2 = $"Place: {entityPlace}";
        if (!string.IsNullOrWhiteSpace(entityDate)) sheet.Cells[startRow + 11, 5].Value2 = $"Date: {entityDate}";

        try
        {
            sheet.Range[$"B{startRow}:F{startRow + 11}"].Font.Name = "Arial";
            sheet.Range[$"B{startRow}:F{startRow + 11}"].Font.Size = 10;
            sheet.Range[$"B{startRow}:F{startRow + 11}"].WrapText = true;
            sheet.Cells[startRow, 2].Font.Bold = true;
            sheet.Cells[startRow, 5].Font.Bold = true;
        }
        catch { }
    }

    private static void WriteNce(dynamic book, NceFinancialStatementIdentityService.Snapshot identities)
    {
        var capitalSpec = NceFinancialStatementPresentationAuthority.ForNote("3");
        dynamic capital = book.Worksheets[capitalSpec.OwnerSheet];
        dynamic pnl = book.Worksheets[NceFinancialStatementPresentationAuthority.ProfitLossTemplate];

        WriteOwnerCapital(capital, identities.OwnerCapital);

        // R51: physical sheets no longer decide which Notes they happen to contain. The explicit
        // sheet-to-Note authority is the only routing table, and every Note carries its own
        // presentation contract through NoteIdentity.Presentation.
        foreach (var sheetAuthority in NceFinancialStatementPresentationAuthority.NoteSheets)
        {
            dynamic noteSheet = book.Worksheets[sheetAuthority.TemplateSheet];
            var specs = NceFinancialStatementPresentationAuthority.ForSheet(sheetAuthority.TemplateSheet)
                .Where(x => x.Note is not "1" and not "2" and not "3")
                .ToArray();
            WriteNceNoteIdentityValues(noteSheet, identities, specs);
        }

        // Face links are rebuilt after Note visibility/row compaction. Direct P&L-only lines
        // have no numbered Note and remain identity-driven values.
        WriteNceDirectFaceValues(pnl, identities);
    }

    private static void WriteNceNoteIdentityValues(
        dynamic sheet,
        NceFinancialStatementIdentityService.Snapshot identities,
        IReadOnlyList<NceFinancialStatementPresentationAuthority.NotePresentationSpec> noteSpecs)
    {
        static string Normalize(string value)
            => string.Join(" ", (value ?? string.Empty)
                .Replace("–", "-")
                .Replace("—", "-")
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Trim(' ', ':', '-', '.');

        var usedRows = Math.Max(1, Convert.ToInt32(sheet.UsedRange.Rows.Count, CultureInfo.InvariantCulture));
        string? currentNoteId = null;
        NceFinancialStatementIdentityService.NoteIdentity? currentNote = null;
        NceFinancialStatementPresentationAuthority.NotePresentationSpec? currentSpec = null;

        for (var row = 1; row <= usedRows; row++)
        {
            string label = Convert.ToString((object?)sheet.Cells[row, 2].Value2, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (label.Length == 0) continue;

            foreach (var spec in noteSpecs)
            {
                if (ExactNoteTitleMatches(label, spec))
                {
                    currentNoteId = spec.Note;
                    currentNote = identities.Note(spec.Note);
                    currentSpec = spec;
                    break;
                }
            }

            if (currentNote is null || string.IsNullOrWhiteSpace(currentNoteId)) continue;
            var normalizedLabel = Normalize(label);

            // FIXED9: when the accounting identity has a stronger official source total, write
            // the Note total row from that authority. Component rows remain exactly under the
            // user's saved classifications; this only prevents stale detail totals from flowing
            // back into the face statement through an old template SUM formula.
            if (currentSpec is not null &&
                (currentNote.FyAuthoritativeTotal.HasValue || currentNote.PyAuthoritativeTotal.HasValue) &&
                Normalize(currentSpec.TotalLabel).Equals(normalizedLabel, StringComparison.OrdinalIgnoreCase))
            {
                sheet.Cells[row, 3].Value2 = (double)currentNote.FyTotal;
                sheet.Cells[row, 4].Value2 = (double)currentNote.PyTotal;
                try { sheet.Range[$"C{row}:D{row}"].NumberFormat = NumberFormat; } catch { }
                continue;
            }

            // R49 FIXED4: the supplied Note 21 layout has no separate Tax / GST receivables
            // presentation row. The visible "Other current assets" row is therefore the
            // presentation container for BOTH the OCA_OTHER and OCA_TAX identity headings.
            // This must run before the ordinary exact-heading match: OCA_OTHER always exists in
            // the identity catalogue, so a heading-is-null guard would silently drop OCA_TAX and
            // make the Note total differ from the Balance Sheet face.
            if (currentNoteId == "21" &&
                normalizedLabel.Equals(Normalize("Other current assets"), StringComparison.OrdinalIgnoreCase))
            {
                var rows = currentNote.Headings.Where(x =>
                    x.Title.Equals("Other current assets", StringComparison.OrdinalIgnoreCase) ||
                    x.Title.Equals("Tax / GST receivables", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (rows.Length > 0)
                {
                    sheet.Cells[row, 3].Value2 = (double)rows.Sum(x => x.Fy);
                    sheet.Cells[row, 4].Value2 = (double)rows.Sum(x => x.Py);
                    try { sheet.Range[$"C{row}:D{row}"].NumberFormat = NumberFormat; } catch { }
                }
                continue;
            }

            var heading = currentNote.Headings.FirstOrDefault(x =>
                Normalize(x.Title).Equals(normalizedLabel, StringComparison.OrdinalIgnoreCase));
            if (heading is null) continue;
            if (currentNoteId == "13")
            {
                var movement = identities.PpeMovements.FirstOrDefault(x =>
                    Normalize(x.Title).Equals(normalizedLabel, StringComparison.OrdinalIgnoreCase));
                if (movement is not null)
                {
                    sheet.Cells[row, 3].Value2 = (double)movement.FyOpening;
                    sheet.Cells[row, 4].Value2 = (double)movement.FyAdditions;
                    sheet.Cells[row, 5].Value2 = (double)movement.FyDisposals;
                    sheet.Cells[row, 6].Value2 = (double)(
                        movement.Basis.Equals("Gross Block", StringComparison.OrdinalIgnoreCase)
                            ? movement.FyAccumulatedDepreciation
                            : movement.FyDepreciation);
                    sheet.Cells[row, 7].Value2 = (double)movement.FyClosing;
                    sheet.Cells[row, 8].Value2 = (double)movement.PyClosing;
                    try { sheet.Range[$"C{row}:H{row}"].NumberFormat = NumberFormat; } catch { }
                }
            }
            else
            {
                sheet.Cells[row, 3].Value2 = (double)heading.Fy;
                sheet.Cells[row, 4].Value2 = (double)heading.Py;
                try { sheet.Range[$"C{row}:D{row}"].NumberFormat = NumberFormat; } catch { }
            }
        }
    }

    private static void WriteNceDirectFaceValues(dynamic pnl, NceFinancialStatementIdentityService.Snapshot identities)
    {
        void PutFace(int row, string identity)
        {
            var line = identities.Face("P&L Account", identity);
            pnl.Cells[row, 5].Value2 = (double)(line?.Fy ?? 0m);
            pnl.Cells[row, 6].Value2 = (double)(line?.Py ?? 0m);
            try { pnl.Range[$"E{row}:F{row}"].NumberFormat = NumberFormat; } catch { }
        }
        PutFace(19, "Exceptional items");
        PutFace(21, "Partners' remuneration");
        PutFace(23, "Tax expense");
    }

    private static decimal ToExcelPercent(decimal value)
        => Math.Abs(value) > 1m ? value / 100m : value;

    private static void WriteOwnerCapital(dynamic sheet, IReadOnlyList<NceFinancialStatementIdentityService.OwnerCapitalIdentity> owners)
    {
        ClearRangeMergedSafe(sheet, "B7:K13", clearFormats: false);
        for (var r = 7; r <= 13; r++)
        {
            sheet.Cells[r, 1].Value2 = r - 6;
            // Closing capital is an authoritative Note-3 result, not a residual Excel formula.
            // Source reconciliation has already proved Opening + Credits - Debits = Closing.
            sheet.Cells[r, 10].ClearContents();
            sheet.Cells[r, 9].ClearContents();
            sheet.Cells[r, 11].ClearContents();
        }

        var rows = owners.Take(7).ToArray();
        for (var i = 0; i < rows.Length; i++)
        {
            var row = 7 + i;
            var owner = rows[i];
            sheet.Cells[row, 2].Value2 = owner.Name;
            sheet.Cells[row, 3].Value2 = (double)ToExcelPercent(owner.Share);
            sheet.Cells[row, 4].Value2 = (double)owner.FyOpening;
            sheet.Cells[row, 5].Value2 = (double)owner.FyCredits;
            sheet.Cells[row, 6].ClearContents();
            sheet.Cells[row, 7].ClearContents();
            sheet.Cells[row, 8].Value2 = (double)owner.FyDebits;
            sheet.Cells[row, 9].ClearContents();
            sheet.Cells[row, 10].Value2 = (double)owner.FyClosing;
            sheet.Cells[row, 11].Value2 = (double)owner.PyClosing;
        }
        sheet.Range["J14"].Formula = "=IF(COUNT(J7:J13)=0,\"\",SUM(J7:J13))";
        sheet.Range["K14"].Formula = "=IF(COUNT(K7:K13)=0,\"\",SUM(K7:K13))";
        ClearRangeMergedSafe(sheet, "G14:I14", clearFormats: false);
    }

    private static void RepairNceFaceLinks(dynamic bs, dynamic pnl)
    {
        SetFormula(bs, "E9", "='NCE Notes 7-10'!J14"); SetFormula(bs, "F9", "='NCE Notes 7-10'!K14");
        SetFormula(bs, "E10", "='NCE Notes 7-10'!C24"); SetFormula(bs, "F10", "='NCE Notes 7-10'!D24");
        SetFormula(bs, "E13", "='NCE Notes 7-10'!C39"); SetFormula(bs, "F13", "='NCE Notes 7-10'!D39");
        SetFormula(bs, "E14", "=IF('NCE Notes 7-10'!C54>0,'NCE Notes 7-10'!C54,0)"); SetFormula(bs, "F14", "=IF('NCE Notes 7-10'!D54>0,'NCE Notes 7-10'!D54,0)");
        SetFormula(bs, "E15", "='NCE Note 11'!C11"); SetFormula(bs, "F15", "='NCE Note 11'!D11");
        SetFormula(bs, "E16", "='NCE Note 11'!C23"); SetFormula(bs, "F16", "='NCE Note 11'!D23");
        SetFormula(bs, "E19", "='NCE Note 11'!C36"); SetFormula(bs, "F19", "='NCE Note 11'!D36");
        SetFormula(bs, "E20", "='NCE Note 11'!C48"); SetFormula(bs, "F20", "='NCE Note 11'!D48");
        SetFormula(bs, "E21", "='NCE Note 11'!C61"); SetFormula(bs, "F21", "='NCE Note 11'!D61");
        SetFormula(bs, "E22", "='NCE Note 11'!C71"); SetFormula(bs, "F22", "='NCE Note 11'!D71");
        SetFormula(bs, "E29", "='NCE Note 11'!G92"); SetFormula(bs, "F29", "='NCE Note 11'!H92");
        SetFormula(bs, "E30", "='NCE Note 11'!G93"); SetFormula(bs, "F30", "='NCE Note 11'!H93");
        SetFormula(bs, "E31", "='NCE Note 11'!G96"); SetFormula(bs, "F31", "='NCE Note 11'!H96");
        SetFormula(bs, "E32", "='NCE Note 11'!G97"); SetFormula(bs, "F32", "='NCE Note 11'!H97");
        SetFormula(bs, "E33", "='NCE Note 11'!C107"); SetFormula(bs, "F33", "='NCE Note 11'!D107");
        SetFormula(bs, "E34", "=IF('NCE Notes 7-10'!C54<0,-'NCE Notes 7-10'!C54,0)"); SetFormula(bs, "F34", "=IF('NCE Notes 7-10'!D54<0,-'NCE Notes 7-10'!D54,0)");
        SetFormula(bs, "E35", "='NCE Notes 12-18'!C12"); SetFormula(bs, "F35", "='NCE Notes 12-18'!D12");
        SetFormula(bs, "E36", "='NCE Notes 12-18'!C24"); SetFormula(bs, "F36", "='NCE Notes 12-18'!D24");
        SetFormula(bs, "E39", "='NCE Note 11'!C113"); SetFormula(bs, "F39", "='NCE Note 11'!D113");
        SetFormula(bs, "E40", "='NCE Notes 12-18'!C38"); SetFormula(bs, "F40", "='NCE Notes 12-18'!D38");
        SetFormula(bs, "E41", "='NCE Notes 12-18'!C48"); SetFormula(bs, "F41", "='NCE Notes 12-18'!D48");
        SetFormula(bs, "E42", "='NCE Notes 12-18'!C62"); SetFormula(bs, "F42", "='NCE Notes 12-18'!D62");
        SetFormula(bs, "E43", "='NCE Notes 12-18'!C77"); SetFormula(bs, "F43", "='NCE Notes 12-18'!D77");
        SetFormula(bs, "E44", "='NCE Notes 12-18'!C89"); SetFormula(bs, "F44", "='NCE Notes 12-18'!D89");
        SetFormula(bs, "E45", "=SUM(E39:E44)"); SetFormula(bs, "F45", "=SUM(F39:F44)");
        SetFormula(bs, "E46", "=SUM(E37,E45)"); SetFormula(bs, "F46", "=SUM(F37,F45)");
        ClearRangeMergedSafe(bs, "E47:F47", clearFormats: false);

        SetFormula(pnl, "E7", "='NCE Notes 12-18'!C100"); SetFormula(pnl, "F7", "='NCE Notes 12-18'!D100");
        SetFormula(pnl, "E8", "='NCE Notes 12-18'!C112"); SetFormula(pnl, "F8", "='NCE Notes 12-18'!D112");
        SetFormula(pnl, "E12", "='NCE Notes 12-18'!C124"); SetFormula(pnl, "F12", "='NCE Notes 12-18'!D124");
        SetFormula(pnl, "E13", "='NCE Notes 12-18'!C142"); SetFormula(pnl, "F13", "='NCE Notes 12-18'!D142");
        SetFormula(pnl, "E14", "='NCE Notes 12-18'!C155"); SetFormula(pnl, "F14", "='NCE Notes 12-18'!D155");
        SetFormula(pnl, "E15", "='NCE Notes 12-18'!C167"); SetFormula(pnl, "F15", "='NCE Notes 12-18'!D167");
        SetFormula(pnl, "E16", "='NCE Notes 12-18'!C185"); SetFormula(pnl, "F16", "='NCE Notes 12-18'!D185");
    }

    private static void WriteDivisionI(dynamic book, IReadOnlyDictionary<string, decimal> fy, IReadOnlyDictionary<string, decimal> py)
    {
        dynamic notes = book.Worksheets["Div I Notes"];
        dynamic bs = book.Worksheets["Div I Balance Sheet"];

        Put(notes, "B76", "C76", fy, py, "SC_AUTH", "SC_ISSUED", "SC_PAID", "SC_PARTPAID", "SC_CALLS_UNPAID", "SC_FORFEIT");
        Put(notes, "B82", "C82", fy, py, "RES_CAPITAL");
        Put(notes, "B83", "C83", fy, py, "RES_SP");
        Put(notes, "B84", "C84", fy, py, "RES_CRR", "RES_DRR");
        Put(notes, "B85", "C85", fy, py, "RES_SURPLUS");
        Put(notes, "B86", "C86", fy, py, "RES_REVAL", "RES_OTHER");
        Put(notes, "B98", "C98", fy, py, "LTB_BANK", "LTB_BONDS");
        Put(notes, "B99", "C99", fy, py, "LTB_OTHER", "LTB_RELATED", "LTB_DEPOSITS");
        Put(notes, "B101", "C101", fy, py, "LTB_DEFERRED", "LTB_LEASE", "LTB_OTHERLOAN");
        Put(notes, "B105", "C105", fy, py, "DTL");
        Put(notes, "B114", "C114", fy, py, "OLTL_TP", "OLTL_OTHER");
        Put(notes, "B118", "C118", fy, py, "LTP_EMP"); Put(notes, "B121", "C121", fy, py, "LTP_OTHER");
        Put(notes, "B125", "C125", fy, py, "STB_BANK_OD");
        Put(notes, "B126", "C126", fy, py, "STB_OTHER_OD", "STB_RELATED", "STB_DEPOSITS");
        Put(notes, "B128", "C128", fy, py, "STB_OTHER");
        Put(notes, "B132", "C132", fy, py, "TP_MSME", "TP_DISP_MSME");
        Put(notes, "B140", "C140", fy, py, "TP_OTHER", "TP_DISP_OTHER");
        Put(notes, "B144", "C144", fy, py, "OCL_STAT"); Put(notes, "B145", "C145", fy, py, "OCL_ADV");
        Put(notes, "B146", "C146", fy, py, "OCL_INT_NOTDUE", "OCL_INT_DUE");
        Put(notes, "B147", "C147", fy, py, "OCL_CMLTD", "OCL_LEASE", "OCL_DIV", "OCL_OTHER");
        Put(notes, "B151", "C151", fy, py, "STP_EMP"); Put(notes, "B152", "C152", fy, py, "STP_TAX"); Put(notes, "B154", "C154", fy, py, "STP_OTHER");
        Put(notes, "B158", "C158", fy, py, "PPE_LAND"); Put(notes, "B159", "C159", fy, py, "PPE_BUILD");
        Put(notes, "B160", "C160", fy, py, "PPE_PLANT"); Put(notes, "B161", "C161", fy, py, "PPE_FURN");
        Put(notes, "B162", "C162", fy, py, "PPE_VEH"); Put(notes, "B163", "C163", fy, py, "PPE_OFFICE");
        Put(notes, "B170", "C170", fy, py, "INTANG"); Put(notes, "B174", "C174", fy, py, "CWIP"); Put(notes, "B182", "C182", fy, py, "IAUD");
        Put(notes, "B187", "C187", fy, py, "NCI_PROP", "NCI_EQUITY", "NCI_PREF", "NCI_GOVT", "NCI_BONDS", "NCI_MF", "NCI_PARTNERSHIP", "NCI_OTHER");
        Put(notes, "B193", "C193", fy, py, "DTA");
        Put(notes, "B199", "C199", fy, py, "LTLA_CAP"); Put(notes, "B200", "C200", fy, py, "LTLA_DEP", "LTLA_REL", "LTLA_OTHER"); Put(notes, "B202", "C202", fy, py, -1m, "LTLA_PROV");
        Put(notes, "B209", "C209", fy, py, "ONCA_TR", "ONCA_BANK", "ONCA_OTHER");
        Put(notes, "B213", "C213", fy, py, "CI_QUOTED"); Put(notes, "B214", "C214", fy, py, "CI_UNQUOTED"); Put(notes, "B216", "C216", fy, py, -1m, "CI_PROV");
        Put(notes, "B220", "C220", fy, py, "INV_RM"); Put(notes, "B221", "C221", fy, py, "INV_WIP"); Put(notes, "B222", "C222", fy, py, "INV_FG"); Put(notes, "B223", "C223", fy, py, "INV_SIT"); Put(notes, "B224", "C224", fy, py, "INV_STORES", "INV_TOOLS", "INV_TRANSIT"); Put(notes, "B225", "C225", fy, py, "INV_OTHER");
        Put(notes, "B230", "C230", fy, py, "TR_GOOD", "TR_DISP_GOOD"); Put(notes, "B232", "C232", fy, py, "TR_DOUBT", "TR_DISP_DOUBT"); Put(notes, "B233", "C233", fy, py, -1m, "TR_PROV");
        Put(notes, "B237", "C237", fy, py, "CASH_HAND"); Put(notes, "B238", "C238", fy, py, "CASH_BANK"); Put(notes, "B239", "C239", fy, py, "CASH_CHEQ"); Put(notes, "B240", "C240", fy, py, "CASH_OTHER");
        Put(notes, "B245", "C245", fy, py, "STLA_REL", "STLA_OTHER"); Put(notes, "B247", "C247", fy, py, -1m, "STLA_PROV");
        Put(notes, "B252", "C252", fy, py, "OCA_PREPAID"); Put(notes, "B253", "C253", fy, py, "OCA_TAX"); Put(notes, "B254", "C254", fy, py, "OCA_INT", "OCA_OTHER");
        Put(notes, "B285", "C285", fy, py, "REV_PRODUCTS"); Put(notes, "B286", "C286", fy, py, "REV_SERVICES"); Put(notes, "B287", "C287", fy, py, "REV_OTHER"); Put(notes, "B288", "C288", fy, py, -1m, "REV_LEVY");
        Put(notes, "B293", "C293", fy, py, "OI_INTEREST"); Put(notes, "B294", "C294", fy, py, "OI_DIV"); Put(notes, "B295", "C295", fy, py, "OI_PROFITSALE"); Put(notes, "B296", "C296", fy, py, "OI_OTHER");
        Put(notes, "B301", "C301", fy, py, "CMC_OPEN"); Put(notes, "B302", "C302", fy, py, "CMC_PUR", "CMC_OTHER"); Put(notes, "B303", "C303", fy, py, -1m, "CMC_CLOSE");
        Put(notes, "B308", "C308", fy, py, "PUR_SIT");
        Put(notes, "B314", "C314", fy, py, "CHG_OPEN"); Put(notes, "B317", "C317", fy, py, -1m, "CHG_CLOSE");
        Put(notes, "B322", "C322", fy, py, "EMP_SAL"); Put(notes, "B323", "C323", fy, py, "EMP_FUNDS"); Put(notes, "B325", "C325", fy, py, "EMP_WELFARE", "EMP_OTHER");
        Put(notes, "B330", "C330", fy, py, "FIN_INT"); Put(notes, "B331", "C331", fy, py, "FIN_OTHER"); Put(notes, "B332", "C332", fy, py, "FIN_FX");
        Put(notes, "B337", "C337", fy, py, "DEP"); Put(notes, "B338", "C338", fy, py, "AMORT");
        Put(notes, "B344", "C344", fy, py, "OE_POWER"); Put(notes, "B345", "C345", fy, py, "OE_RENT"); Put(notes, "B346", "C346", fy, py, "OE_REPAIR"); Put(notes, "B347", "C347", fy, py, "OE_INS"); Put(notes, "B348", "C348", fy, py, "OE_RATES"); Put(notes, "B349", "C349", fy, py, "OE_AUD"); Put(notes, "B351", "C351", fy, py, "OE_PROF", "OE_SELL", "OE_ADMIN", "OE_OTHER");

        RepairDivisionIFaceLinks(bs, notes);
    }

    private static void RepairDivisionIFaceLinks(dynamic bs, dynamic pnl)
    {
        var bsLinks = new (int Row, int TotalRow)[]
        {
            (8,79),(9,87),(10,91),(11,95),(13,102),(14,108),(15,115),(16,122),(18,129),(20,135),(21,141),(22,148),(23,155),
            (28,164),(29,171),(30,177),(31,183),(32,190),(33,196),(34,203),(35,210),(37,217),(38,226),(39,234),(40,241),(41,248),(42,255)
        };
        foreach (var link in bsLinks)
        {
            SetFormula(bs, $"D{link.Row}", $"='Div I Notes'!B{link.TotalRow}");
            SetFormula(bs, $"E{link.Row}", $"='Div I Notes'!C{link.TotalRow}");
        }
        SetFormula(bs, "D24", "=SUM(D8:D11,D13:D16,D18,D20:D23)"); SetFormula(bs, "E24", "=SUM(E8:E11,E13:E16,E18,E20:E23)");
        SetFormula(bs, "D43", "=SUM(D28:D35,D37:D42)"); SetFormula(bs, "E43", "=SUM(E28:E35,E37:E42)");

        var pnlLinks = new (int FaceRow, int TotalRow)[] { (7,289),(8,297),(11,304),(12,310),(13,318),(14,326),(15,333),(16,339),(17,352) };
        foreach (var link in pnlLinks)
        {
            SetFormula(pnl, $"D{link.FaceRow}", $"=B{link.TotalRow}");
            SetFormula(pnl, $"E{link.FaceRow}", $"=C{link.TotalRow}");
        }
        SetFormula(pnl, "D9", "=SUM(D7:D8)"); SetFormula(pnl, "E9", "=SUM(E7:E8)");
        SetFormula(pnl, "D18", "=SUM(D11:D17)"); SetFormula(pnl, "E18", "=SUM(E11:E17)");
        SetFormula(pnl, "D19", "=D9-D18"); SetFormula(pnl, "E19", "=E9-E18");
        SetFormula(pnl, "D20", "=B358"); SetFormula(pnl, "E20", "=C358");
        SetFormula(pnl, "D21", "=D19-D20"); SetFormula(pnl, "E21", "=E19-E20");
        SetFormula(pnl, "D22", "=B364"); SetFormula(pnl, "E22", "=C364");
        SetFormula(pnl, "D23", "=D21-D22"); SetFormula(pnl, "E23", "=E21-E22");
        SetFormula(pnl, "D24", "=B371"); SetFormula(pnl, "E24", "=C371");
        SetFormula(pnl, "D25", "=B368"); SetFormula(pnl, "E25", "=C368");
        SetFormula(pnl, "D26", "=B369"); SetFormula(pnl, "E26", "=C369");
        SetFormula(pnl, "D27", "=D23-D24"); SetFormula(pnl, "E27", "=E23-E24");
        SetFormula(pnl, "D31", "=D27+D30"); SetFormula(pnl, "E31", "=E27+E30");
    }

    private static void Put(dynamic sheet, string fyCell, string pyCell, IReadOnlyDictionary<string, decimal> fy, IReadOnlyDictionary<string, decimal> py, params string[] keys)
        => Put(sheet, fyCell, pyCell, fy, py, 1m, keys);

    private static void Put(dynamic sheet, string fyCell, string pyCell, IReadOnlyDictionary<string, decimal> fy, IReadOnlyDictionary<string, decimal> py, decimal multiplier, params string[] keys)
    {
        sheet.Range[fyCell].Value2 = (double)(Sum(fy, keys) * multiplier);
        sheet.Range[pyCell].Value2 = (double)(Sum(py, keys) * multiplier);
        try { sheet.Range[$"{fyCell}:{pyCell}"].NumberFormat = NumberFormat; } catch { }
    }

    private static decimal Sum(IReadOnlyDictionary<string, decimal> values, params string[] keys)
        => keys.Sum(key => values.TryGetValue(key, out var value) ? value : 0m);

    private static Dictionary<string, decimal> AggregateMappings(IEnumerable<FinancialStatementLedgerRow> rows)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var key = row.Mapping?.Trim() ?? string.Empty;
            if (key.Length == 0) continue;
            var amount = row.AmountSign switch
            {
                "As Is" => row.Closing,
                "Reverse" => -row.Closing,
                _ => Math.Abs(row.Closing)
            };
            result[key] = result.GetValueOrDefault(key) + amount;
        }
        return result;
    }

    private static void SetFormula(dynamic sheet, string cell, string formula)
    {
        sheet.Range[cell].Formula = formula;
        try { sheet.Range[cell].NumberFormat = NumberFormat; } catch { }
    }

    private static void SetDate(dynamic cell, string value)
    {
        if (DateTime.TryParseExact(value ?? string.Empty, new[] { "dd-MMM-yyyy", "d-MMM-yyyy", "dd-MM-yyyy", "d-M-yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            cell.Value2 = date.ToOADate();
            try { cell.NumberFormat = "dd-mmm-yyyy"; } catch { }
        }
        else cell.Value2 = value ?? string.Empty;
    }

    // R52 FIXED22: Excel COM rejects ClearContents/ClearFormats when a target range
    // intersects only part of an existing merged area. The presentation template intentionally
    // uses merged title/signature bands, and dynamic Note compaction can leave a wider historical
    // merge behind before a narrower Note contract is rendered. Normalize only merge areas that
    // intersect the requested clear range, then clear the full requested block. Callers that own
    // presentation merges immediately recreate their declared merge geometry afterwards.
    private static void ClearRangeMergedSafe(dynamic sheet, string address, bool clearFormats)
    {
        dynamic range = sheet.Range[address];
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = Convert.ToInt32(range.Rows.Count, CultureInfo.InvariantCulture);
            var columns = Convert.ToInt32(range.Columns.Count, CultureInfo.InvariantCulture);
            for (var r = 1; r <= rows; r++)
            {
                for (var c = 1; c <= columns; c++)
                {
                    dynamic cell = range.Cells[r, c];
                    bool merged;
                    try { merged = Convert.ToBoolean(cell.MergeCells, CultureInfo.InvariantCulture); }
                    catch { continue; }
                    if (!merged) continue;
                    dynamic area = cell.MergeArea;
                    string key;
                    try { key = Convert.ToString((object?)area.Address, CultureInfo.InvariantCulture) ?? $"{r}:{c}"; }
                    catch { key = $"{r}:{c}"; }
                    if (!seen.Add(key)) continue;
                    try { area.UnMerge(); } catch { }
                }
            }
        }
        catch
        {
            // If merge discovery itself is unavailable, let the real clear operation below surface
            // the COM error rather than silently skipping the requested presentation update.
        }

        range.ClearContents();
        if (clearFormats) range.ClearFormats();
    }

    private static void DeleteExcept(dynamic book, HashSet<string> keep)
    {
        for (var index = book.Worksheets.Count; index >= 1; index--)
        {
            dynamic ws = book.Worksheets[index];
            var name = Convert.ToString(ws.Name, CultureInfo.InvariantCulture) ?? string.Empty;
            if (!keep.Contains(name)) ws.Delete();
        }
    }

    private static string First(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;
}
