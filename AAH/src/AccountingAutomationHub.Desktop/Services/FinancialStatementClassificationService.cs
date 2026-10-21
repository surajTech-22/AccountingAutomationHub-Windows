using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Profile-neutral financial-statement classification layer introduced in R15 R10.
/// The visible H1-H5 hierarchy is profile-specific, while the underlying mapping keys
/// remain the stable authority consumed by the statement/note renderer.  Future formats
/// register their own hierarchy against the same canonical concepts instead of rebuilding
/// the Trial Balance UI or exporter.
/// </summary>
public static class FinancialStatementClassificationService
{
    public enum BifurcationKind
    {
        None,
        CurrentNonCurrent,
        ReceivableAge
    }

    public sealed record SplitTarget(string NonCurrentKey, string CurrentKey, bool CurrentOnly = false);

    private static readonly HashSet<string> NceBifurcationGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "Borrowings", "Other Liabilities", "Provisions", "Investments", "Loans and Advances", "Other Assets", "Trade Receivables"
    };

    private static readonly HashSet<string> NceCurrentOnlyLines = new(StringComparer.OrdinalIgnoreCase)
    {
        "Secured Loans Repayable on Demand from Bank",
        "Secured Loans Repayable on Demand from Other Parties",
        "Unsecured Loans Repayable on Demand from Bank",
        "Unsecured Loans Repayable on Demand from Other Parties",
        "Secured Maturities of Finance Lease Obligations",
        "Unsecured Maturities of Finance Lease Obligations",
        "Salaries Payable",
        "Advance from Customers",
        "Income Received in Advance",
        "Unearned Revenue",
        "GST Payable",
        "TDS/TCS Payable",
        "PF/ESIC/PT Payable",
        "Provision for Audit fees",
        "Provision for Expenses",
        "Provision for Income Tax",
        // R15 R14: the supplied trial workbook classifies tax credits/advance-tax balances
        // as current. They must not require a hidden J/K non-current split before export.
        "Secured-Advance Tax and Tax Deducted at Source (TDS)",
        "Secured-VAT Credit Receivable",
        "Secured-GST Input Credit Receivable",
        "Secured-Balance with Government Authorities",
        "Unsecured-Advance Tax and Tax Deducted at Source (TDS)",
        "Unsecured-VAT Credit Receivable",
        "Unsecured-GST Input Credit Receivable",
        "Unsecured-Balance with Government Authorities",
        "Unbilled Receivables",
        "Advance IT/TDS",
        "GST Input Credit Receivable"
    };

    private static readonly IReadOnlyDictionary<string, SplitTarget> NceSplitTargets =
        new Dictionary<string, SplitTarget>(StringComparer.OrdinalIgnoreCase)
        {
            // Borrowings — one H4 description can feed current and non-current face lines.
            ["Secured Term Loans from Banks"] = new("BORR_LT_BANK", "BORR_ST_BANK"),
            ["Secured Term Loans from Other Parties"] = new("BORR_LT_OTHER_PARTY", "BORR_ST_OTHER_PARTY"),
            ["Secured Loans Repayable on Demand from Bank"] = new("BORR_ST_DEMAND_BANK", "BORR_ST_DEMAND_BANK", true),
            ["Secured Loans Repayable on Demand from Other Parties"] = new("BORR_ST_OTHER_PARTY", "BORR_ST_OTHER_PARTY", true),
            ["Secured Deferred Payment Liabilities"] = new("BORR_LT_OTHER", "BORR_ST_OTHER"),
            ["Secured Loans and Advances from Related Parties"] = new("BORR_LT_PARTNER", "BORR_ST_PARTNER"),
            ["Secured Maturities of Finance Lease Obligations"] = new("BORR_ST_OTHER", "BORR_ST_OTHER", true),
            ["Other Secured Loans and Advances (specify nature)"] = new("BORR_LT_OTHER", "BORR_ST_OTHER"),
            ["Unsecured Term Loans from Banks"] = new("BORR_LT_BANK", "BORR_ST_BANK"),
            ["Unsecured Term Loans from Other Parties"] = new("BORR_LT_OTHER_PARTY", "BORR_ST_OTHER_PARTY"),
            ["Unsecured Loans Repayable on Demand from Bank"] = new("BORR_ST_DEMAND_BANK", "BORR_ST_DEMAND_BANK", true),
            ["Unsecured Loans Repayable on Demand from Other Parties"] = new("BORR_ST_OTHER_PARTY", "BORR_ST_OTHER_PARTY", true),
            ["Unsecured Deferred Payment Liabilities"] = new("BORR_LT_OTHER", "BORR_ST_OTHER"),
            ["Unsecured Loans and Advances from Related Parties"] = new("BORR_LT_PARTNER", "BORR_ST_PARTNER"),
            ["Unsecured Maturities of Finance Lease Obligations"] = new("BORR_ST_OTHER", "BORR_ST_OTHER", true),
            ["Other Unsecured Loans and Advances (specify nature)"] = new("BORR_LT_OTHER", "BORR_ST_OTHER"),

            // Other liabilities.
            ["Salaries Payable"] = new("OCL_ACCR", "OCL_ACCR", true),
            ["Advance from Customers"] = new("OCL_ADV", "OCL_ADV", true),
            ["Interest Accrued but not due on Borrowings"] = new("OLTL_INTEREST", "OCL_INT_DUE"),
            ["Interest Accrued and due on Borrowings"] = new("OCL_INT_DUE", "OCL_INT_DUE", true),
            ["Income Received in Advance"] = new("OCL_OTHER", "OCL_OTHER", true),
            ["Unearned Revenue"] = new("OCL_OTHER", "OCL_OTHER", true),
            ["GST Payable"] = new("OCL_STAT", "OCL_STAT", true),
            ["TDS/TCS Payable"] = new("OCL_STAT", "OCL_STAT", true),
            ["PF/ESIC/PT Payable"] = new("OCL_STAT", "OCL_STAT", true),
            ["Other Payables (specify nature)"] = new("OLTL_OTHER", "OCL_OTHER"),

            // Provisions.
            ["Provision for Audit fees"] = new("PROV_ST_EXP", "PROV_ST_EXP", true),
            ["Provision for Expenses"] = new("PROV_ST_EXP", "PROV_ST_EXP", true),
            ["Provision for Gratuity"] = new("PROV_LT_EMP", "PROV_ST_EMP"),
            ["Provision for Leave Encashment"] = new("PROV_LT_EMP", "PROV_ST_EMP"),
            ["Provision for Income Tax"] = new("PROV_ST_TAX", "PROV_ST_TAX", true),
            ["Other Provisions (specify nature)"] = new("PROV_LT_OTHER", "PROV_ST_OTHER"),

            // Investments. Detail nature is retained by the ledger-detail schedule; face split is by J/K.
            ["Quoted investments in Other Entities"] = new("INV_NC_OTHER", "INV_CURR_OTHER"),
            ["Quoted Investment in Partnership Firms"] = new("INV_NC_OTHER", "INV_CURR_OTHER"),
            ["Quoted Investments in Preference Shares"] = new("INV_NC_PREF", "INV_CURR_EQUITY_DEBT"),
            ["Quoted Investments in Equity Instruments"] = new("INV_NC_EQUITY", "INV_CURR_EQUITY_DEBT"),
            ["Quoted Investments in Government or Trust Securities"] = new("INV_NC_DEBT", "INV_CURR_EQUITY_DEBT"),
            ["Quoted Investments in Debentures or Bonds"] = new("INV_NC_DEBT", "INV_CURR_EQUITY_DEBT"),
            ["Quoted Investments in Mutual Funds"] = new("INV_NC_MF", "INV_CURR_MF"),
            ["Quoted Investments Property"] = new("INV_NC_OTHER", "INV_CURR_OTHER"),
            ["Other Quoted Investments"] = new("INV_NC_OTHER", "INV_CURR_OTHER"),
            ["Others Quoted Investments (specify nature)"] = new("INV_NC_OTHER", "INV_CURR_OTHER"),
            ["Less: Provision for Diminution in Value of Quoted Investment"] = new("INV_NC_OTHER", "INV_CURR_OTHER"),
            ["Unquoted Investments in Other Entities"] = new("INV_NC_OTHER", "INV_CURR_OTHER"),
            ["Unquoted Investment in Partnership Firms"] = new("INV_NC_OTHER", "INV_CURR_OTHER"),
            ["Unquoted Investments in Preference Shares"] = new("INV_NC_PREF", "INV_CURR_EQUITY_DEBT"),
            ["Unquoted Investments in Equity Instruments"] = new("INV_NC_EQUITY", "INV_CURR_EQUITY_DEBT"),
            ["Unquoted Investments in Government or Trust Securities"] = new("INV_NC_DEBT", "INV_CURR_EQUITY_DEBT"),
            ["Unquoted Investments in Debentures or Bonds"] = new("INV_NC_DEBT", "INV_CURR_EQUITY_DEBT"),
            ["Unquoted Investments in Mutual Funds"] = new("INV_NC_MF", "INV_CURR_MF"),
            ["Unquoted Investments Property"] = new("INV_NC_OTHER", "INV_CURR_OTHER"),
            ["Other Unquoted Investments"] = new("INV_NC_OTHER", "INV_CURR_OTHER"),
            ["Other Unquoted Investments (specify nature)"] = new("INV_NC_OTHER", "INV_CURR_OTHER"),
            ["Less: Provision for Diminution in Value of Unquoted Investment"] = new("INV_NC_OTHER", "INV_CURR_OTHER"),

            // Loans and advances.
            ["Secured-Capital Advances Considered Good"] = new("LA_LT_CAPITAL", "LA_ST_CAPITAL"),
            ["Secured-Capital Advances Considered Doubtful"] = new("LA_LT_CAPITAL", "LA_ST_CAPITAL"),
            ["Less: Provision for Doubtful Advances-Secured (Balance Sheet)"] = new("LA_LT_OTHER", "LA_ST_OTHER"),
            ["Secured-Loans and Advances to Partners or Relative of Partners"] = new("LA_LT_PARTNER", "LA_ST_PARTNER"),
            ["Secured-Prepaid Expenses"] = new("LA_LT_OTHER", "LA_ST_PREPAID"),
            ["Secured-Advance Tax and Tax Deducted at Source (TDS)"] = new("LA_LT_TAX", "LA_ST_TAX", true),
            ["Secured-VAT Credit Receivable"] = new("LA_LT_TAX", "LA_ST_TAX", true),
            ["Secured-GST Input Credit Receivable"] = new("LA_LT_TAX", "LA_ST_TAX", true),
            ["Secured-Balance with Government Authorities"] = new("LA_LT_OTHER", "LA_ST_GOVT", true),
            ["Other Secured Loans and Advances (specify nature)"] = new("LA_LT_OTHER", "LA_ST_OTHER"),
            ["Unsecured-Capital Advances Considered Good"] = new("LA_LT_CAPITAL", "LA_ST_CAPITAL"),
            ["Unsecured-Capital Advances Considered Doubtful"] = new("LA_LT_CAPITAL", "LA_ST_CAPITAL"),
            ["Less: Provision for Doubtful Advances-Unsecured (Balance Sheet)"] = new("LA_LT_OTHER", "LA_ST_OTHER"),
            ["Unsecured-Loans and Advances to Partners or Relative of Partners"] = new("LA_LT_PARTNER", "LA_ST_PARTNER"),
            ["Unsecured-Prepaid Expenses"] = new("LA_LT_OTHER", "LA_ST_PREPAID"),
            ["Unsecured-Advance Tax and Tax Deducted at Source (TDS)"] = new("LA_LT_TAX", "LA_ST_TAX", true),
            ["Unsecured-VAT Credit Receivable"] = new("LA_LT_TAX", "LA_ST_TAX", true),
            ["Unsecured-GST Input Credit Receivable"] = new("LA_LT_TAX", "LA_ST_TAX", true),
            ["Unsecured-Balance with Government Authorities"] = new("LA_LT_OTHER", "LA_ST_GOVT", true),
            ["Other Unsecured Loans and Advances (specify nature)"] = new("LA_LT_OTHER", "LA_ST_OTHER"),

            // Other assets. The source NCE workbook places GST input credits and advance IT/TDS
            // directly under Other Assets and treats them as current-only balances.
            ["Advance IT/TDS"] = new("OCA_TAX", "OCA_TAX", true),
            ["GST Input Credit Receivable"] = new("OCA_TAX", "OCA_TAX", true),
            ["Security Deposits"] = new("ONCA_SECURITY", "OCA_SECURITY"),
            ["Prepaid Expenses"] = new("ONCA_PREPAID", "OCA_PRE"),
            ["Advance to Suppliers"] = new("ONCA_OTHER", "OCA_SUPPLIER_ADV"),
            ["Interest accrued but not due on Deposits"] = new("ONCA_INTEREST", "OCA_INT_NOT_DUE"),
            ["Interest accrued and due on Deposits"] = new("ONCA_INTEREST", "OCA_INT_DUE"),
            ["Other Assets (specify nature)"] = new("ONCA_OTHER", "OCA_OTHER")
        };

    public static IReadOnlyList<FinancialStatementMappingOption> NormalizeProfileCatalog(
        string entityType,
        IReadOnlyList<FinancialStatementMappingOption> baseCatalog)
    {
        var profile = FinancialStatementWorkbookService.ResolveProfileSheet(entityType);
        if (profile.Equals("C_AS", StringComparison.OrdinalIgnoreCase))
            return NormalizeCompanyCatalog(baseCatalog);
        if (!profile.Equals("NCE", StringComparison.OrdinalIgnoreCase))
            return baseCatalog.Select(x => string.IsNullOrWhiteSpace(x.StableHierarchyId)
                ? x with { StableHierarchyId = StableHierarchyId(x.MappingKey, x.Class1, x.Class2, x.Class3, x.Class4, x.Class5) }
                : x).ToArray();

        var byKey = baseCatalog.GroupBy(x => x.MappingKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<FinancialStatementMappingOption>();

        void AddFrom(string key, string c1, string c2, string c3, string c4 = "", string c5 = "")
        {
            if (!byKey.TryGetValue(key, out var o)) return;
            var stableId = StableHierarchyId(key, c1, c2, c3, c4, c5);
            result.Add(o with { Class1 = c1, Class2 = c2, Class3 = c3, Class4 = c4, Class5 = c5, StableHierarchyId = stableId });
        }

        const string BS = "Balance Sheet";
        const string PL = "P&L Account";
        const string Assets = "Assets";
        const string Liabilities = "Liabilities";
        const string Income = "Income";
        const string Expenses = "Expenses";

        // R15 R14: these visible paths reproduce the clean factory D:E:F table from
        // FSforNCE.xlsm/Dropdownmasters NCE. Internal MappingKey values remain the stable
        // renderer authority; they are deliberately not exposed as a second classification.

        // Owner/partner capital. The factory workbook has Capital Account / Current Account;
        // C5 carries the selected owner/partner name in AAH so multiple owners remain distinct.
        for (var i = 1; i <= 11; i++)
        {
            AddFrom("CAP_OPEN", BS, Liabilities, "Owner/Partners Capital Account", "Capital Account", $"Capital A/c {i}");
            AddFrom("CURR_OPEN", BS, Liabilities, "Owner/Partners Capital Account", "Current Account", $"Current A/c {i}");
        }
        AddFrom("CAP_OPEN", BS, Liabilities, "Owner/Partners Capital Account", "Capital Account", "Other Capital A/c (specify nature)");
        AddFrom("CURR_OPEN", BS, Liabilities, "Owner/Partners Capital Account", "Current Account", "Other Current A/c (specify nature)");

        AddFrom("RES_CAP", BS, Liabilities, "Reserves and Surplus", "Capital Reserve");
        AddFrom("RES_OTHER", BS, Liabilities, "Reserves and Surplus", "Revaluation Reserve");
        AddFrom("RES_SURPLUS", BS, Liabilities, "Reserves and Surplus", "Retained Earnings");
        AddFrom("RES_OTHER", BS, Liabilities, "Reserves and Surplus", "Other Reserves");
        AddFrom("RES_SURPLUS", BS, Liabilities, "Reserves and Surplus", "Surplus");

        var borrowingLines = new (string Line, string Key)[]
        {
            ("Term Loans from Banks", "BORR_LT_BANK"),
            ("Term Loans from Other Parties", "BORR_LT_OTHER_PARTY"),
            ("Deferred Payment Liabilities", "BORR_LT_OTHER"),
            ("Deposits", "BORR_LT_DEPOSIT"),
            ("Loans and Advances from Related Parties", "BORR_LT_PARTNER"),
            ("Maturities of Finance Lease Obligations", "BORR_LT_LEASE"),
            ("Loans Repayable on Demand from Bank", "BORR_ST_DEMAND_BANK"),
            ("Loans Repayable on Demand from Other Parties", "BORR_ST_OTHER_PARTY")
        };
        foreach (var group in new[] { "Borrowings - Secured", "Borrowings - Unsecured", "Borrowings - User Input" })
            foreach (var x in borrowingLines) AddFrom(x.Key, BS, Liabilities, group, x.Line);

        AddFrom("DTL_OTHER", BS, Liabilities, "Deferred Tax Liabilities (Net)");

        // The original TB stops at C2 "As per Creditors/Debtors Sheets". MSME/debtor
        // detail belongs to the linked schedule, not to a fourth/fifth TB dropdown. Keep
        // detailed internal keys for existing schedule authority, plus one blank factory path
        // so a manual TB selection resolves without inventing schedule detail.
        AddFrom("TP_OTHER", BS, Liabilities, "Trade Payables", "As per Creditors/Debtors Sheets");
        AddFrom("TP_MSME", BS, Liabilities, "Trade Payables", "As per Creditors/Debtors Sheets", "MSME");
        AddFrom("TP_OTHER", BS, Liabilities, "Trade Payables", "As per Creditors/Debtors Sheets", "Non-MSME");
        AddFrom("TP_MSME_INTEREST", BS, Liabilities, "Trade Payables", "As per Creditors/Debtors Sheets", "Interest due under MSMED Act");
        AddFrom("TP_UNBILLED", BS, Liabilities, "Trade Payables", "As per Creditors/Debtors Sheets", "Unbilled Trade Payables");
        AddFrom("TP_BAL_OTHER", BS, Liabilities, "Trade Payables", "As per Creditors/Debtors Sheets", "Other Trade Payable Balances");

        var otherLiabilities = new (string Line, string Key)[]
        {
            ("Salaries Payable", "OCL_ACCR"), ("PT Payable", "OCL_STAT"), ("EPF Payable", "OCL_STAT"),
            ("TDS/TCS Payable", "OCL_STAT"), ("GST Payable", "OCL_STAT"), ("ESI Payable", "OCL_STAT"),
            ("Income Received in Advance", "OCL_OTHER"), ("Unearned Revenue", "OCL_OTHER"),
            ("Interest Accrued and due on Borrowings", "OCL_INT_DUE"),
            ("Interest Accrued but not due on Borrowings", "OLTL_INTEREST"), ("Advance from Customers", "OCL_ADV")
        };
        foreach (var x in otherLiabilities) AddFrom(x.Key, BS, Liabilities, "Other Liabilities", x.Line);

        var provisions = new (string Line, string Key)[]
        {
            ("Provision for Audit Fees", "PROV_ST_EXP"), ("Provision for Dividend", "PROV_ST_OTHER"),
            ("Provision for Income Tax", "PROV_ST_TAX"), ("Provision for Leave Encashment", "PROV_LT_EMP"),
            ("Provision for Gratuity", "PROV_LT_EMP"), ("Provision for Superannuation", "PROV_LT_EMP"),
            ("Provision for Expenses", "PROV_ST_EXP")
        };
        foreach (var x in provisions) AddFrom(x.Key, BS, Liabilities, "Provisions", x.Line);

        var ppe = new (string Line, string Key)[]
        {
            ("Land", "PPE_LAND"), ("Leasehold Land", "PPE_LAND"), ("Buildings", "PPE_BUILDING"),
            ("Plant and Equipment", "PPE_PLANT"), ("Furniture and Fixtures", "PPE_FURNITURE"),
            ("Vehicles", "PPE_VEHICLES"), ("Office Equipment", "PPE_OFFICE"),
            ("Computers and Peripherals", "PPE_COMPUTERS")
        };
        foreach (var group in new[] { "PPE (Gross)", "PPE (Accumulated Depreciation)", "PPE (Net)" })
            foreach (var x in ppe) AddFrom(x.Key, BS, Assets, group, x.Line);

        var intangible = new (string Line, string Key)[]
        {
            ("Goodwill", "INTANG_GOODWILL"), ("Brands/Trademarks", "INTANG_OTHER"),
            ("Computer Software", "INTANG_SOFTWARE"), ("Masthead and Publishing Titles", "INTANG_OTHER"),
            ("Mining Rights", "INTANG_OTHER"),
            ("Copyrights, Patents, Other Intellectual Property Rights, Services, and Operating Rights", "INTANG_OTHER"),
            ("Recipes, Formulae, Models, Designs, and Prototypes", "INTANG_OTHER"),
            ("License and Franchise", "INTANG_OTHER")
        };
        foreach (var group in new[] { "Intangible Assets (Gross)", "Intangible Assets (Accumulated Depreciation)", "Intangible Assets (Net)" })
            foreach (var x in intangible) AddFrom(x.Key, BS, Assets, group, x.Line);
        AddFrom("CWIP", BS, Assets, "Capital Work-in-Progress", "Project in Progress");
        AddFrom("CWIP", BS, Assets, "Capital Work-in-Progress", "Projects temporarily suspended");
        AddFrom("IAUD", BS, Assets, "Intangible Assets under Development", "Project in Progress");
        AddFrom("IAUD", BS, Assets, "Intangible Assets under Development", "Projects temporarily suspended");

        var investmentLines = new (string Line, string NonCurrentKey)[]
        {
            ("Investment Property", "INV_NC_OTHER"), ("Investments in Equity Instruments", "INV_NC_EQUITY"),
            ("Investments in Preference Shares", "INV_NC_PREF"), ("Investments in Government or Trust Securities", "INV_NC_DEBT"),
            ("Investments in Bonds/Debentures", "INV_NC_DEBT"), ("Investments in Mutual Funds", "INV_NC_MF"),
            ("Investments in Partnership Firms", "INV_NC_OTHER"), ("Other Investments", "INV_NC_OTHER"),
            ("Provision for Diminution in Value of Investment", "INV_NC_OTHER")
        };
        foreach (var group in new[] { "Trade Investments - Quoted", "Trade Investments - Unquoted", "Other Investments - Quoted", "Other Investments - Unquoted", "Investments - User Input" })
            foreach (var x in investmentLines) AddFrom(x.NonCurrentKey, BS, Assets, group, x.Line);
        AddFrom("DTA_OTHER", BS, Assets, "Deferred Tax Assets (Net)");

        // Stock/Debtor detail is linked from dedicated schedules. C5 retains AAH's exact detail
        // without changing the factory C3/C4 dependency.
        AddFrom("INV_SIT", BS, Assets, "Inventories", "As per Stock Sheet");
        AddFrom("INV_RM", BS, Assets, "Inventories", "As per Stock Sheet", "Raw Materials");
        AddFrom("INV_WIP", BS, Assets, "Inventories", "As per Stock Sheet", "Work-in-Progress");
        AddFrom("INV_FG", BS, Assets, "Inventories", "As per Stock Sheet", "Finished Goods");
        AddFrom("INV_SIT", BS, Assets, "Inventories", "As per Stock Sheet", "Stock-in-Trade");
        AddFrom("INV_STORES", BS, Assets, "Inventories", "As per Stock Sheet", "Stores and Spares");
        AddFrom("INV_PACKING", BS, Assets, "Inventories", "As per Stock Sheet", "Packing Materials");
        AddFrom("INV_OTHER", BS, Assets, "Inventories", "As per Stock Sheet", "Other Inventories");

        AddFrom("TR_UNSEC_GOOD", BS, Assets, "Trade Receivables", "As per Creditors/Debtors Sheets");
        AddFrom("TR_SEC_GOOD", BS, Assets, "Trade Receivables", "As per Creditors/Debtors Sheets", "Secured Considered Good");
        AddFrom("TR_UNSEC_GOOD", BS, Assets, "Trade Receivables", "As per Creditors/Debtors Sheets", "Unsecured Considered Good");
        AddFrom("TR_DOUBT", BS, Assets, "Trade Receivables", "As per Creditors/Debtors Sheets", "Doubtful");
        AddFrom("TR_ALLOW", BS, Assets, "Trade Receivables", "As per Creditors/Debtors Sheets", "Less: Allowance for Doubtful Debts");
        AddFrom("TR_UNBILLED", BS, Assets, "Trade Receivables", "As per Creditors/Debtors Sheets", "Unbilled Receivables");

        AddFrom("CASH_HAND", BS, Assets, "Cash and Cash Equivalents", "Cash on Hand");
        AddFrom("CASH_HAND", BS, Assets, "Cash and Cash Equivalents", "Cheques, Drafts on Hand");
        AddFrom("CASH_BANK_CURRENT", BS, Assets, "Cash and Cash Equivalents", "Balances with Banks", "Current Accounts");
        AddFrom("CASH_BANK_CURRENT", BS, Assets, "Cash and Cash Equivalents", "Balances with Banks", "Cash Credit Account (Debit Balance)");
        AddFrom("CASH_DEP_3M", BS, Assets, "Cash and Cash Equivalents", "Balances with Banks", "Fixed Deposits with original maturity of less than 3 months");
        AddFrom("BANK_EARMARKED", BS, Assets, "Cash and Cash Equivalents", "Other Bank Balances", "Earmarked Bank Deposits");
        AddFrom("BANK_EARMARKED", BS, Assets, "Cash and Cash Equivalents", "Other Bank Balances", "Margin Money or Deposits Under Lien");
        AddFrom("BANK_OTHER", BS, Assets, "Cash and Cash Equivalents", "Other Bank Balances", "Deposits with original maturity for more than 3 months but less than 12 months from reporting date");

        var loanGroups = new[]
        {
            "Loans and Advances - Secured, considered good", "Loans and Advances - Unsecured, considered good",
            "Loans and Advances - Doubtful", "Loans and Advances - Provision for Doubtful Debts", "Loans and Advances - User Input"
        };
        foreach (var group in loanGroups)
        {
            AddFrom("LA_LT_CAPITAL", BS, Assets, group, "Capital Advances");
            AddFrom("LA_LT_PARTNER", BS, Assets, group, "Loans and Advances to Related Parties");
            AddFrom("LA_LT_OTHER", BS, Assets, group, "Other Loans and Advances");
        }

        var otherAssets = new (string Line, string Key)[]
        {
            ("Advance IT/TDS", "OCA_TAX"), ("GST Input Credit Receivable", "OCA_TAX"),
            ("Advance to Suppliers", "OCA_SUPPLIER_ADV"), ("Prepaid Expenses", "OCA_PRE"),
            ("Unbilled Revenue", "OCA_OTHER"), ("Unamortised Premium on Forward Contracts", "OCA_OTHER")
        };
        foreach (var x in otherAssets) AddFrom(x.Key, BS, Assets, "Other Assets", x.Line);

        // P&L D:E paths exactly from the clean NCE factory list.
        AddFrom("REV_GOODS", PL, Income, "Revenue from Operations", "Sale of Products");
        AddFrom("REV_SERV", PL, Income, "Revenue from Operations", "Sale of Services");
        AddFrom("REV_GRANTS", PL, Income, "Revenue from Operations", "Grants/Donations Received");
        AddFrom("REV_OTHER", PL, Income, "Revenue from Operations", "Other Operating Revenues");

        AddFrom("OI_INT", PL, Income, "Other Income", "Interest Income");
        AddFrom("OI_DIV", PL, Income, "Other Income", "Dividend Income");
        AddFrom("OI_OTHER", PL, Income, "Other Income", "Sale of Scrap");
        AddFrom("OI_GAIN_INV", PL, Income, "Other Income", "Net Gain on Sale of Investments");
        AddFrom("OI_OTHER", PL, Income, "Other Income", "Other Non-Operating Income");
        AddFrom("OI_OTHER", PL, Income, "Other Income", "Miscellaneous Income");

        foreach (var line in new[] { "Raw Materials", "Finished Goods", "Stock-in-Trade", "Packing Materials", "Other Materials" })
            AddFrom("PUR_SIT", PL, Expenses, "Purchases", line);
        AddFrom("PUR_SIT", PL, Expenses, "Purchases - User Input");

        AddFrom("EMP_SALARY", PL, Expenses, "Employee Benefits Expense", "Salaries and Wages");
        AddFrom("EMP_WELFARE", PL, Expenses, "Employee Benefits Expense", "Staff Welfare Expenses");
        AddFrom("EMP_OTHER", PL, Expenses, "Employee Benefits Expense", "Gratuity Expenses");
        AddFrom("EMP_PF", PL, Expenses, "Employee Benefits Expense", "Contribution to Provident and Other Funds");

        AddFrom("FIN_INTEREST", PL, Expenses, "Finance Costs", "Interest Expense");
        AddFrom("FIN_OTHER", PL, Expenses, "Finance Costs", "Other Borrowing Costs");
        AddFrom("FIN_OTHER", PL, Expenses, "Finance Costs", "Loss on Foreign Exchange Transactions and Translations (Net)");

        AddFrom("DEP", PL, Expenses, "Depreciation and Amortisation Expense", "Depreciation of Tangible Assets");
        AddFrom("AMORT", PL, Expenses, "Depreciation and Amortisation Expense", "Amortisation of Intangible Assets");

        var otherExpenseLines = new (string Line, string Key)[]
        {
            ("Advertisement and Publicity", "OE_OTHER"), ("Business Promotion Expenses", "OE_OTHER"),
            ("Clearing and Forwarding Charges", "OE_OTHER"), ("Commission", "OE_OTHER"),
            ("Communication Expenses", "OE_OTHER"), ("Consumption of Stores and Spare Parts", "OE_STORES"),
            ("Insurance", "OE_INSURANCE"), ("Labour Charges", "OE_OTHER"),
            ("Legal and Professional Charges", "OE_PROFESSIONAL"), ("Loss on Cancellation of Forward Contracts", "OE_OTHER"),
            ("Loss on Foreign Exchange Transactions (Net)", "OE_OTHER"), ("Loss on Sale of Investments (Net)", "OE_OTHER"),
            ("Loss on Sale of Property, Plant and Equipment", "OE_OTHER"), ("Payments to the Auditor", "OE_AUDITOR"),
            ("Power and Fuel", "OE_POWER"), ("Printing and Stationery", "OE_OTHER"),
            ("Provision for Diminution in Value of Investments (P&L)", "OE_OTHER"),
            ("Provision for Doubtful Advances (P&L)", "OE_OTHER"), ("Provision for Doubtful Debts", "OE_OTHER"),
            ("Rates and Taxes", "OE_RATES_TAX"), ("Rent", "OE_RENT"),
            ("Repairs and Maintenance - Buildings", "OE_REPAIRS"), ("Repairs and Maintenance - Machinery", "OE_REPAIRS"),
            ("Repairs and Maintenance - Others", "OE_REPAIRS"), ("Travelling and Conveyance", "OE_OTHER"),
            ("Miscellaneous Expenses", "OE_OTHER")
        };
        foreach (var x in otherExpenseLines) AddFrom(x.Key, PL, Expenses, "Other Expenses", x.Line);

        AddFrom("TAX_CURR", PL, Expenses, "Tax Expenses (Income Tax)", "Current Tax");
        AddFrom("TAX_DEFER", PL, Expenses, "Tax Expenses (Income Tax)", "Deferred Tax");
        AddFrom("TAX_PRIOR", PL, Expenses, "Tax Expenses (Income Tax)", "Adjustment of Tax relating to earlier periods");
        AddFrom("TAX_CURR", PL, Expenses, "Tax Expenses (Income Tax) - User Input");

        // Original trial-workbook spelling retained as an authority marker: Exceptional items
        AddFrom("EXC", PL, Expenses, "Other Profit and Loss Items", "Exceptional Items");
        AddFrom("EXTRAORDINARY", PL, Expenses, "Other Profit and Loss Items", "Extraordinary Items");
        AddFrom("PARTNER_REMUN", PL, Expenses, "Other Profit and Loss Items", "Partners’ Remuneration");
        AddFrom("OE_OTHER", PL, Expenses, "Other Profit and Loss Items", "Profit/(Loss) from Discontinuing Operations");
        AddFrom("TAX_CURR", PL, Expenses, "Other Profit and Loss Items", "Tax Expense of Discontinuing Operations");

        return result
            .GroupBy(x => StableHierarchyId(x.MappingKey, x.Class1, x.Class2, x.Class3, x.Class4, x.Class5), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToArray();
    }

    private static IReadOnlyList<FinancialStatementMappingOption> NormalizeCompanyCatalog(
        IReadOnlyList<FinancialStatementMappingOption> baseCatalog)
    {
        var byKey = baseCatalog.GroupBy(x => x.MappingKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<FinancialStatementMappingOption>();

        void AddFrom(string key, string c1, string c2, string c3, string c4 = "", string c5 = "")
        {
            if (!byKey.TryGetValue(key, out var o)) return;
            result.Add(o with
            {
                Class1 = c1,
                Class2 = c2,
                Class3 = c3,
                Class4 = c4,
                Class5 = c5,
                StableHierarchyId = StableHierarchyId(key, c1, c2, c3, c4, c5)
            });
        }

        const string BS = "Balance Sheet";
        const string PL = "P&L Account";
        const string Assets = "Assets";
        const string Liabilities = "Liabilities";
        const string Income = "Income";
        const string Expenses = "Expenses";

        // Schedule III Division I factory D:E:F hierarchy from FSforSchIIIDivI.xlsm.
        AddFrom("SC_PAID", BS, Liabilities, "Share Capital", "Subscribed and Fully Paid", "User Input");
        AddFrom("SC_PARTPAID", BS, Liabilities, "Share Capital", "Subscribed but Not Fully Paid", "User Input");
        AddFrom("SC_CALLS_UNPAID", BS, Liabilities, "Share Capital", "Calls in Arrears");

        AddFrom("RES_CAPITAL", BS, Liabilities, "Reserves and Surplus", "Capital Reserve");
        AddFrom("RES_CRR", BS, Liabilities, "Reserves and Surplus", "Capital Redemption Reserve");
        AddFrom("RES_SP", BS, Liabilities, "Reserves and Surplus", "Securities Premium");
        AddFrom("RES_DRR", BS, Liabilities, "Reserves and Surplus", "Debenture Redemption Reserve");
        AddFrom("RES_REVAL", BS, Liabilities, "Reserves and Surplus", "Revaluation Reserve");
        AddFrom("RES_SURPLUS", BS, Liabilities, "Reserves and Surplus", "Retained Earnings");
        AddFrom("RES_OTHER", BS, Liabilities, "Reserves and Surplus", "Other Reserves");
        AddFrom("RES_OTHER", BS, Liabilities, "Reserves and Surplus", "Share Options Outstanding Account");

        AddFrom("SHARE_WARRANTS", BS, Liabilities, "Money received against Share Warrants");
        AddFrom("SHARE_APP_MONEY", BS, Liabilities, "Share Application Money pending allotment");

        var borrowingLines = new (string Key, string Line)[]
        {
            ("LTB_BONDS", "Bonds/Debentures"),
            ("LTB_BANK", "Term Loans from Banks"),
            ("LTB_OTHER", "Term Loans from Other Parties"),
            ("LTB_DEFERRED", "Deferred Payment Liabilities"),
            ("LTB_DEPOSITS", "Deposits"),
            ("LTB_RELATED", "Loans and Advances from Related Parties"),
            ("LTB_LEASE", "Maturities of Finance Lease Obligations"),
            ("LTB_OTHERLOAN", "Other Loans and Advances (specify nature)"),
            ("STB_BANK_OD", "Loans Repayable on Demand from Bank"),
            ("STB_OTHER_OD", "Loans Repayable on Demand from Other Parties"),
            ("STB_RELATED", "Loans and Advances from Related Parties"),
            ("STB_DEPOSITS", "Deposits"),
            ("STB_OTHER", "Other Loans and Advances (specify nature)")
        };
        foreach (var group in new[] { "Borrowings - Secured", "Borrowings - Unsecured", "Borrowings - User Input" })
            foreach (var x in borrowingLines) AddFrom(x.Key, BS, Liabilities, group, x.Line);

        AddFrom("DTL", BS, Liabilities, "Deferred Tax Liabilities (Net)");
        AddFrom("DTA", BS, Assets, "Deferred Tax Assets (Net)");

        // Schedule III Division I active workbook C1/C2 choices. Long-term/current
        // schedule identity remains internal; the TB itself exposes only these note heads.
        AddFrom("OCL_OTHER", BS, Liabilities, "Other Liabilities", "Salaries Payable");
        AddFrom("OCL_STAT", BS, Liabilities, "Other Liabilities", "PT Payable");
        AddFrom("OCL_STAT", BS, Liabilities, "Other Liabilities", "EPF Payable");
        AddFrom("OCL_STAT", BS, Liabilities, "Other Liabilities", "TDS/TCS Payable");
        AddFrom("OCL_STAT", BS, Liabilities, "Other Liabilities", "GST Payable");
        AddFrom("OCL_STAT", BS, Liabilities, "Other Liabilities", "ESI Payable");
        AddFrom("OCL_ADV", BS, Liabilities, "Other Liabilities", "Income Received in Advance");
        AddFrom("OCL_OTHER", BS, Liabilities, "Other Liabilities", "Unearned Revenue");
        AddFrom("OCL_INT_DUE", BS, Liabilities, "Other Liabilities", "Interest Accrued and due on Borrowings");
        AddFrom("OCL_INT_NOTDUE", BS, Liabilities, "Other Liabilities", "Interest Accrued but not due on Borrowings");
        AddFrom("OCL_ADV", BS, Liabilities, "Other Liabilities", "Advance from Customers");
        AddFrom("OCL_OTHER", BS, Liabilities, "Other Liabilities", "Expenses Payable");
        AddFrom("OCL_DIV", BS, Liabilities, "Other Liabilities", "Unpaid Dividend");

        AddFrom("STP_OTHER", BS, Liabilities, "Provisions", "Provision for Audit Fees");
        AddFrom("STP_OTHER", BS, Liabilities, "Provisions", "Provision for Dividend");
        AddFrom("STP_TAX", BS, Liabilities, "Provisions", "Provision for Income Tax");
        AddFrom("STP_EMP", BS, Liabilities, "Provisions", "Provision for Leave Encashment");
        AddFrom("LTP_EMP", BS, Liabilities, "Provisions", "Provision for Gratuity");
        AddFrom("LTP_EMP", BS, Liabilities, "Provisions", "Provision for Superannuation");
        AddFrom("STP_OTHER", BS, Liabilities, "Provisions", "Provision for Expenses");

        AddFrom("TP_OTHER", BS, Liabilities, "Trade Payables", "As per Creditors/Debtors Sheets");
        AddFrom("TP_MSME", BS, Liabilities, "Trade Payables", "As per Creditors/Debtors Sheets", "MSME");
        AddFrom("TP_OTHER", BS, Liabilities, "Trade Payables", "As per Creditors/Debtors Sheets", "Non-MSME");
        AddFrom("TP_DISP_MSME", BS, Liabilities, "Trade Payables", "As per Creditors/Debtors Sheets", "Disputed MSME");
        AddFrom("TP_DISP_OTHER", BS, Liabilities, "Trade Payables", "As per Creditors/Debtors Sheets", "Disputed Other");

        var ppe = new (string Key, string Line)[]
        {
            ("PPE_LAND", "Land"), ("PPE_BUILD", "Buildings"), ("PPE_PLANT", "Plant and Equipment"),
            ("PPE_FURN", "Furniture and Fixtures"), ("PPE_VEH", "Vehicles"), ("PPE_OFFICE", "Office Equipment")
        };
        foreach (var group in new[] { "PPE (Gross)", "PPE (Accumulated Depreciation)", "PPE (Net)" })
            foreach (var x in ppe) AddFrom(x.Key, BS, Assets, group, x.Line);

        foreach (var group in new[] { "Intangible Assets (Gross)", "Intangible Assets (Accumulated Depreciation)", "Intangible Assets (Net)" })
            AddFrom("INTANG", BS, Assets, group, "Other Intangible Assets");
        AddFrom("CWIP", BS, Assets, "Capital Work-in-Progress", "Project in Progress");
        AddFrom("IAUD", BS, Assets, "Intangible Assets under Development", "Project in Progress");

        var investmentKeys = new (string Key, string Line)[]
        {
            ("NCI_PROP", "Investment Property"), ("NCI_EQUITY", "Investments in Equity Instruments"),
            ("NCI_PREF", "Investments in Preference Shares"), ("NCI_GOVT", "Investments in Government or Trust Securities"),
            ("NCI_BONDS", "Investments in Bonds/Debentures"), ("NCI_MF", "Investments in Mutual Funds"),
            ("NCI_PARTNERSHIP", "Investments in Partnership Firms"), ("NCI_OTHER", "Other Investments"),
            ("CI_QUOTED", "Other Investments"), ("CI_UNQUOTED", "Other Investments"),
            ("CI_PROV", "Provision for Diminution in Value of Investment")
        };
        foreach (var group in new[] { "Trade Investments - Quoted", "Trade Investments - Unquoted", "Other Investments - Quoted", "Other Investments - Unquoted", "Investments - User Input" })
            foreach (var x in investmentKeys) AddFrom(x.Key, BS, Assets, group, x.Line);

        var loanKeys = new (string Key, string Line)[]
        {
            ("LTLA_CAP", "Capital Advances"), ("LTLA_DEP", "Other Loans and Advances"), ("LTLA_REL", "Loans and Advances to Related Parties"),
            ("LTLA_OTHER", "Other Loans and Advances"), ("LTLA_PROV", "Other Loans and Advances"),
            ("STLA_REL", "Loans and Advances to Related Parties"), ("STLA_OTHER", "Other Loans and Advances"), ("STLA_PROV", "Other Loans and Advances")
        };
        foreach (var group in new[] { "Loans and Advances - Secured, considered good", "Loans and Advances - Unsecured, considered good",
                                     "Loans and Advances - Doubtful", "Loans and Advances - Provision for Doubtful Debts", "Loans and Advances - User Input" })
            foreach (var x in loanKeys) AddFrom(x.Key, BS, Assets, group, x.Line);

        AddFrom("OCA_TAX", BS, Assets, "Other Assets", "Advance IT/TDS");
        AddFrom("OCA_TAX", BS, Assets, "Other Assets", "GST Input Credit Receivable");
        AddFrom("OCA_OTHER", BS, Assets, "Other Assets", "Advance to Suppliers");
        AddFrom("OCA_PREPAID", BS, Assets, "Other Assets", "Prepaid Expenses");
        AddFrom("ONCA_TR", BS, Assets, "Other Assets", "Unbilled Revenue");
        AddFrom("OCA_OTHER", BS, Assets, "Other Assets", "Unamortised Premium on Forward Contracts");

        AddFrom("INV_SIT", BS, Assets, "Inventories", "As per Stock Sheet");
        foreach (var key in new[] { "INV_RM", "INV_WIP", "INV_FG", "INV_SIT", "INV_STORES", "INV_TOOLS", "INV_TRANSIT", "INV_OTHER" })
            AddFrom(key, BS, Assets, "Inventories", "As per Stock Sheet", byKey.TryGetValue(key, out var inv) ? inv.NoteSubHead : key);

        AddFrom("TR_GOOD", BS, Assets, "Trade Receivables", "As per Creditors/Debtors Sheets");
        foreach (var key in new[] { "TR_GOOD", "TR_DOUBT", "TR_DISP_GOOD", "TR_DISP_DOUBT", "TR_PROV" })
            AddFrom(key, BS, Assets, "Trade Receivables", "As per Creditors/Debtors Sheets", byKey.TryGetValue(key, out var tr) ? tr.NoteSubHead : key);

        AddFrom("CASH_HAND", BS, Assets, "Cash and Cash Equivalents", "Cash on Hand");
        AddFrom("CASH_CHEQ", BS, Assets, "Cash and Cash Equivalents", "Cheques, Drafts on Hand");
        AddFrom("CASH_BANK", BS, Assets, "Cash and Cash Equivalents", "Balances with Banks", "Current Accounts");
        AddFrom("CASH_BANK", BS, Assets, "Cash and Cash Equivalents", "Balances with Banks", "Cash Credit Account (Debit Balance)");
        AddFrom("CASH_OTHER", BS, Assets, "Cash and Cash Equivalents", "Balances with Banks", "Fixed Deposits with original maturity of less than 3 months");
        AddFrom("CASH_OTHER", BS, Assets, "Cash and Cash Equivalents", "Other Bank Balances", "Earmarked Bank Deposits");
        AddFrom("CASH_OTHER", BS, Assets, "Cash and Cash Equivalents", "Other Bank Balances", "Margin Money or Deposits Under Lien");
        AddFrom("CASH_OTHER", BS, Assets, "Cash and Cash Equivalents", "Other Bank Balances", "Deposits with original maturity for more than 3 months but less than 12 months from reporting date");

        AddFrom("REV_PRODUCTS", PL, Income, "Revenue from Operations", "Sale of Products");
        AddFrom("REV_SERVICES", PL, Income, "Revenue from Operations", "Sale of Services");
        AddFrom("REV_OTHER", PL, Income, "Revenue from Operations", "Grants/Donations Received");
        AddFrom("REV_OTHER", PL, Income, "Revenue from Operations", "Other Operating Revenues");

        AddFrom("OI_INTEREST", PL, Income, "Other Income", "Interest Income");
        AddFrom("OI_DIV", PL, Income, "Other Income", "Dividend Income");
        AddFrom("OI_OTHER", PL, Income, "Other Income", "Sale of Scrap");
        AddFrom("OI_PROFITSALE", PL, Income, "Other Income", "Net Gain on Sale of Investments");
        AddFrom("OI_OTHER", PL, Income, "Other Income", "Other Non-Operating Income");
        AddFrom("OI_OTHER", PL, Income, "Other Income", "Miscellaneous Income");

        AddFrom("CMC_PUR", PL, Expenses, "Purchases", "Raw Materials");
        AddFrom("CMC_PUR", PL, Expenses, "Purchases", "Finished Goods");
        AddFrom("PUR_SIT", PL, Expenses, "Purchases", "Stock-in-Trade");
        AddFrom("CMC_OTHER", PL, Expenses, "Purchases", "Packing Materials");
        AddFrom("CMC_OTHER", PL, Expenses, "Purchases", "Other Materials");
        AddFrom("CMC_OTHER", PL, Expenses, "Purchases - User Input");

        AddFrom("EMP_SAL", PL, Expenses, "Employee Benefits Expense", "Salaries and Wages");
        AddFrom("EMP_FUNDS", PL, Expenses, "Employee Benefits Expense", "Contribution to Provident and Other Funds");
        AddFrom("EMP_WELFARE", PL, Expenses, "Employee Benefits Expense", "Staff Welfare Expenses");
        AddFrom("EMP_OTHER", PL, Expenses, "Employee Benefits Expense", "Gratuity Expenses");

        AddFrom("FIN_INT", PL, Expenses, "Finance Costs", "Interest Expense");
        AddFrom("FIN_OTHER", PL, Expenses, "Finance Costs", "Other Borrowing Costs");
        AddFrom("FIN_FX", PL, Expenses, "Finance Costs", "Loss on Foreign Exchange Transactions and Translations (Net)");

        AddFrom("DEP", PL, Expenses, "Depreciation and Amortisation Expense", "Depreciation of Tangible Assets");
        AddFrom("AMORT", PL, Expenses, "Depreciation and Amortisation Expense", "Amortisation of Intangible Assets");

        var otherExpenseLines = new (string Line, string Key)[]
        {
            ("Advertisement and Publicity", "OE_OTHER"), ("Business Promotion Expenses", "OE_SELL"),
            ("Clearing and Forwarding Charges", "OE_OTHER"), ("Commission", "OE_OTHER"),
            ("Communication Expenses", "OE_OTHER"), ("Consumption of Stores and Spare Parts", "OE_OTHER"),
            ("Insurance", "OE_INS"), ("Labour Charges", "OE_OTHER"),
            ("Legal and Professional Charges", "OE_PROF"), ("Loss on Cancellation of Forward Contracts", "OE_OTHER"),
            ("Loss on Foreign Exchange Transactions (Net)", "OE_OTHER"), ("Loss on Sale of Investments (Net)", "OE_OTHER"),
            ("Loss on Sale of Property, Plant and Equipment", "OE_OTHER"), ("Payments to the Auditor", "OE_AUD"),
            ("Power and Fuel", "OE_POWER"), ("Printing and Stationery", "OE_OTHER"),
            ("Provision for Diminution in Value of Investments (P&L)", "OE_OTHER"),
            ("Provision for Doubtful Advances (P&L)", "OE_OTHER"), ("Provision for Doubtful Debts", "OE_OTHER"),
            ("Rates and Taxes", "OE_RATES"), ("Rent", "OE_RENT"),
            ("Repairs and Maintenance - Buildings", "OE_REPAIR"), ("Repairs and Maintenance - Machinery", "OE_REPAIR"),
            ("Repairs and Maintenance - Others", "OE_REPAIR"), ("Travelling and Conveyance", "OE_OTHER"),
            ("Miscellaneous Expenses", "OE_OTHER")
        };
        foreach (var x in otherExpenseLines) AddFrom(x.Key, PL, Expenses, "Other Expenses", x.Line);

        AddFrom("TAX_CURR", PL, Expenses, "Tax Expenses (Income Tax)", "Current Tax");
        AddFrom("TAX_DEFER", PL, Expenses, "Tax Expenses (Income Tax)", "Deferred Tax");
        AddFrom("TAX_PRIOR", PL, Expenses, "Tax Expenses (Income Tax)", "Adjustment of Tax relating to earlier periods");
        AddFrom("EXC", PL, Expenses, "Other Profit and Loss Items", "Exceptional Items");
        AddFrom("EXTRAORD", PL, Expenses, "Other Profit and Loss Items", "Extraordinary Items");
        AddFrom("DISCONT_PROFIT", PL, Expenses, "Other Profit and Loss Items", "Profit/(Loss) from Discontinuing Operations");
        AddFrom("DISCONT_TAX", PL, Expenses, "Other Profit and Loss Items", "Tax Expense of Discontinuing Operations");

        // Regulatory notes are kept available for Notes Preparation without polluting the
        // Balance Sheet/P&L statutory dropdowns with a second statement profile.
        foreach (var key in byKey.Keys.Where(x => x.StartsWith("REG_", StringComparison.OrdinalIgnoreCase)))
            AddFrom(key, "Notes / Disclosures", "Disclosure", "Additional Regulatory Information",
                byKey[key].NoteSubHead);

        return result
            .GroupBy(x => StableHierarchyId(x.MappingKey, x.Class1, x.Class2, x.Class3, x.Class4, x.Class5), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToArray();
    }

    public static IReadOnlyList<FinancialStatementMappingOption> ApplyOwnerAccountLabels(
        IReadOnlyList<FinancialStatementMappingOption> catalog,
        IEnumerable<string> ownerNames)
    {
        var names = ownerNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(11)
            .ToArray();
        if (catalog.Count == 0) return catalog;

        // Stable hierarchy identity is profile-neutral. Future formats can reuse the same
        // persistence/selection engine even when their visible C1-C5 labels are different.
        var stableCatalog = catalog.Select(x => string.IsNullOrWhiteSpace(x.StableHierarchyId)
            ? x with { StableHierarchyId = StableHierarchyId(x.MappingKey, x.Class1, x.Class2, x.Class3, x.Class4, x.Class5) }
            : x).ToArray();
        if (names.Length == 0) return stableCatalog;

        var result = new List<FinancialStatementMappingOption>(stableCatalog.Length);
        foreach (var option in stableCatalog)
        {
            if (!option.Class3.Equals("Owner/Partners Capital Account", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(option);
                continue;
            }

            // The factory path is C3 Owner/Partners Capital Account -> C4 Capital/Current
            // Account. AAH uses C5 only to identify the actual owner/partner ledger slot.
            var class5 = option.Class5;
            if (TryAccountSlot(class5, "Capital A/c", out var capitalSlot))
            {
                if (capitalSlot > names.Length) continue;
                // Original NCE TB C3 displays the Owner/Partner name itself. C2 already
                // identifies Capital Account vs Current Account, so do not append an A/c suffix.
                class5 = names[capitalSlot - 1];
            }
            else if (TryAccountSlot(class5, "Current A/c", out var currentSlot))
            {
                if (currentSlot > names.Length) continue;
                class5 = names[currentSlot - 1];
            }

            result.Add(option with { Class5 = class5 });
        }
        return result;
    }

    public static string StableHierarchyId(string mappingKey, string c1, string c2, string c3, string c4, string c5)
        => string.Join("|", new[] { mappingKey, c1, c2, c3, c4, c5 }.Select(x => (x ?? string.Empty).Trim().ToUpperInvariant()));

    private static bool TryAccountSlot(string value, string prefix, out int slot)
    {
        slot = 0;
        var text = (value ?? string.Empty).Trim();
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = text[prefix.Length..].Trim();
        return int.TryParse(suffix, out slot) && slot is >= 1 and <= 11;
    }

    public static BifurcationKind GetBifurcationKind(string profile, string class3)
    {
        if (!string.Equals(profile, "NCE", StringComparison.OrdinalIgnoreCase) || !NceBifurcationGroups.Contains(class3 ?? string.Empty)) return BifurcationKind.None;
        return string.Equals(class3, "Trade Receivables", StringComparison.OrdinalIgnoreCase)
            ? BifurcationKind.ReceivableAge
            : BifurcationKind.CurrentNonCurrent;
    }

    public static bool IsCurrentOnlyLine(string profile, string class4)
        => string.Equals(profile, "NCE", StringComparison.OrdinalIgnoreCase) && NceCurrentOnlyLines.Contains(class4 ?? string.Empty);

    public static bool TryValidateBifurcation(decimal closing, decimal? firstAmount, out string error)
    {
        error = string.Empty;
        if (!firstAmount.HasValue) return true;
        var value = firstAmount.Value;
        if (closing > 0m && value < 0m) { error = "Non-current / >6 months amount cannot be negative when Closing Balance is positive."; return false; }
        if (closing < 0m && value > 0m) { error = "Non-current / >6 months amount cannot be positive when Closing Balance is negative."; return false; }
        if (Math.Abs(value) > Math.Abs(closing) + 0.004m) { error = "Non-current / >6 months amount cannot exceed Closing Balance."; return false; }
        return true;
    }

    public static SplitTarget? ResolveSplitTarget(string profile, string class3, string class4, string fallbackKey)
    {
        if (!string.Equals(profile, "NCE", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(class3, "Trade Receivables", StringComparison.OrdinalIgnoreCase)) return new(fallbackKey, fallbackKey);
        return NceSplitTargets.TryGetValue(class4 ?? string.Empty, out var target) ? target : null;
    }

    public static string ResolveCanonicalId(string profile, FinancialStatementLedgerRow row)
    {
        if (!string.Equals(profile, "NCE", StringComparison.OrdinalIgnoreCase)) return row.Mapping ?? string.Empty;
        var c3 = row.Classification3 ?? string.Empty;
        var nc = row.NonCurrentAmount.GetValueOrDefault();
        var current = row.Closing - nc;
        return c3 switch
        {
            "Borrowings" => Math.Abs(nc) > 0.004m && Math.Abs(current) <= 0.004m ? "NCL_BORROW" : Math.Abs(nc) <= 0.004m ? "CL_BORROW" : "SPLIT_BORROW",
            "Provisions" => Math.Abs(nc) > 0.004m && Math.Abs(current) <= 0.004m ? "NCL_PROV" : Math.Abs(nc) <= 0.004m ? "CL_PROV" : "SPLIT_PROV",
            "Other Liabilities" => Math.Abs(nc) > 0.004m && Math.Abs(current) <= 0.004m ? "NCL_OTHER" : Math.Abs(nc) <= 0.004m ? "CL_OTHER" : "SPLIT_OTHER_LIAB",
            "Investments" => Math.Abs(nc) > 0.004m && Math.Abs(current) <= 0.004m ? "NCA_INVEST" : Math.Abs(nc) <= 0.004m ? "CA_INVEST" : "SPLIT_INVEST",
            "Loans and Advances" => Math.Abs(nc) > 0.004m && Math.Abs(current) <= 0.004m ? "NCA_LOANS" : Math.Abs(nc) <= 0.004m ? "CA_LOANS" : "SPLIT_LOANS",
            "Other Assets" => Math.Abs(nc) > 0.004m && Math.Abs(current) <= 0.004m ? "NCA_OTHER" : Math.Abs(nc) <= 0.004m ? "CA_OTHER" : "SPLIT_OTHER_ASSET",
            "Trade Receivables" => "CA_TR",
            _ => row.Mapping ?? string.Empty
        };
    }

    private static string InferGroupForSplitLine(string line)
    {
        if (line.Contains("Term Loan", StringComparison.OrdinalIgnoreCase) || line.Contains("Loans Repayable", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Deferred Payment", StringComparison.OrdinalIgnoreCase) || line.Contains("Loans and Advances from Related", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Maturities of Finance Lease", StringComparison.OrdinalIgnoreCase) || line.Contains("Secured Loans and Advances", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Unsecured Loans and Advances", StringComparison.OrdinalIgnoreCase)) return "Borrowings";
        if (line.StartsWith("Provision for", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Other Provisions", StringComparison.OrdinalIgnoreCase)) return "Provisions";
        if (line.Contains("Investment", StringComparison.OrdinalIgnoreCase)) return "Investments";
        if (line.Contains("Capital Advances", StringComparison.OrdinalIgnoreCase) || line.Contains("Loans and Advances to", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Doubtful Advances", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Secured-", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Unsecured-", StringComparison.OrdinalIgnoreCase)) return "Loans and Advances";
        if (line is "Security Deposits" or "Prepaid Expenses" or "Advance to Suppliers" or "Advance IT/TDS" or "GST Input Credit Receivable" ||
            line.Contains("Deposits", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Other Assets", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Unbilled Revenue", StringComparison.OrdinalIgnoreCase) || line.Contains("Unamortised", StringComparison.OrdinalIgnoreCase)) return "Other Assets";
        return "Other Liabilities";
    }
}
