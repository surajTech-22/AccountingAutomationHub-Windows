namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Exact visible Financial Statements preparation authority extracted from the two original
/// supplied workbooks. Visible TB dropdowns use the workbook A:B and D:E:F tables plus the
/// row's source Nature. Internal mapping keys remain renderer authority.
/// </summary>
public static class FinancialStatementWorkbookRules
{
    private sealed record WorkbookPath(string C1, string C2, string C3);

    private static readonly IReadOnlyDictionary<string, string[]> NceSourceC1 = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["CAPITAL ACCOUNT"] = new[] { "Owner/Partners capital account", "Reserves and Surplus" },
        ["RESERVES & SURPLUS"] = new[] { "Owner/Partners capital account", "Reserves and Surplus" },
        ["LOANS (LIABILITY)"] = new[] { "Borrowings - Secured", "Borrowings - Unsecured", "Borrowings - User Input", "Deferred Tax Liabilities (Net)", "Provisions", "Other Liabilities", "Cash and Cash Equivalents" },
        ["BANK OD A/C"] = new[] { "Borrowings - Secured", "Borrowings - Unsecured", "Borrowings - User Input", "Other Liabilities", "Cash and Cash Equivalents" },
        ["SECURED LOANS"] = new[] { "Borrowings - Secured", "Borrowings - Unsecured", "Borrowings - User Input", "Deferred Tax Liabilities (Net)", "Provisions", "Other Liabilities", "Cash and Cash Equivalents" },
        ["UNSECURED LOANS"] = new[] { "Borrowings - Secured", "Borrowings - Unsecured", "Borrowings - User Input", "Deferred Tax Liabilities (Net)", "Provisions", "Other Liabilities", "Cash and Cash Equivalents" },
        ["CURRENT LIABILITIES"] = new[] { "Provisions", "Other Liabilities", "Deferred Tax Liabilities (Net)", "Borrowings - User Input", "Borrowings - Unsecured", "Borrowings - Secured" },
        ["DUTIES & TAXES"] = new[] { "Provisions", "Other Liabilities", "Other Assets", "Loans and Advances - Secured, considered good", "Loans and Advances - Unsecured, considered good", "Loans and Advances - Doubtful", "Loans and Advances - Provision for Doubtful Debts", "Loans and Advances - User Input", "Deferred Tax Liabilities (Net)", "Deferred Tax Assets (Net)", "Cash and Cash Equivalents" },
        ["PROVISIONS"] = new[] { "Provisions", "Deferred Tax Liabilities (Net)", "Other Liabilities", "Deferred Tax Assets (Net)", "Trade Investments - Unquoted", "Trade Investments - Quoted", "Other Investments - Unquoted", "Other Investments - Quoted", "Investments - User Input", "Loans and Advances - Secured, considered good", "Loans and Advances - Unsecured, considered good", "Loans and Advances - Doubtful", "Loans and Advances - Provision for Doubtful Debts", "Loans and Advances - User Input", "Other Assets" },
        ["SUNDRY CREDITORS"] = new[] { "Trade Payables" },
        ["BRANCH / DIVISIONS"] = new[] { "Provisions", "Other Liabilities", "Other Assets" },
        ["PRIMARY - LIABILITIES"] = new[] { "Owner/Partners capital account", "Reserves and Surplus", "Deferred Tax Liabilities (Net)", "Borrowings - Secured", "Borrowings - Unsecured", "Borrowings - User Input", "Provisions", "Other Liabilities", "PPE (Gross)", "PPE (Accumulated Depreciation)", "PPE (Net) for NCE", "Intangible Assets (Gross)", "Intangible Assets (Accumulated Depreciation)", "Intangible Assets (Net)", "Capital Work-in-Progress", "Intangible Assets under Development", "Trade Investments - Quoted", "Trade Investments - Unquoted", "Other Investments - Quoted", "Other Investments - Unquoted", "Investments - User Input", "Deferred Tax Assets (Net)", "Loans and Advances - Secured, considered good", "Loans and Advances - Unsecured, considered good", "Loans and Advances - Doubtful", "Loans and Advances - Provision for Doubtful Debts", "Loans and Advances - User Input", "Cash and Cash Equivalents", "Other Assets" },
        ["SUSPENSE A/C"] = new[] { "Owner/Partners capital account", "Reserves and Surplus", "Borrowings - Secured", "Borrowings - Unsecured", "Borrowings - User Input", "Deferred Tax Liabilities (Net)", "Deferred Tax Assets (Net)", "Trade Payables", "Provisions", "Other Liabilities", "PPE (Gross)", "PPE (Accumulated Depreciation)", "PPE (Net) for NCE", "Intangible Assets (Gross)", "Intangible Assets (Accumulated Depreciation)", "Intangible Assets (Net)", "Capital Work-in-Progress", "Intangible Assets under Development", "Trade Investments - Quoted", "Trade Investments - Unquoted", "Other Investments - Quoted", "Other Investments - Unquoted", "Investments - User Input", "Loans and Advances - Unsecured, considered good", "Loans and Advances - Secured, considered good", "Loans and Advances - Provision for Doubtful Debts", "Loans and Advances - Doubtful", "Cash and Cash Equivalents", "Other Assets" },
        ["FIXED ASSETS"] = new[] { "PPE (Gross)", "PPE (Accumulated Depreciation)", "PPE (Net) for NCE", "Capital Work-in-Progress", "Intangible Assets (Gross)", "Intangible Assets (Accumulated Depreciation)", "Intangible Assets (Net)", "Intangible Assets under Development" },
        ["INVESTMENTS"] = new[] { "Trade Investments - Quoted", "Trade Investments - Unquoted", "Other Investments - Quoted", "Other Investments - Unquoted", "Investments - User Input" },
        ["CURRENT ASSETS"] = new[] { "Trade Investments - Quoted", "Trade Investments - Unquoted", "Other Investments - Quoted", "Other Investments - Unquoted", "Investments - User Input", "Loans and Advances - Secured, considered good", "Loans and Advances - Unsecured, considered good", "Loans and Advances - Doubtful", "Loans and Advances - Provision for Doubtful Debts", "Loans and Advances - User Input", "Deferred Tax Assets (Net)", "Cash and Cash Equivalents", "Other Assets" },
        ["STOCK-IN-HAND"] = new[] { "Inventories" },
        ["DEPOSITS (ASSET)"] = new[] { "Other Assets", "Cash and Cash Equivalents", "Trade Investments - Quoted", "Trade Investments - Unquoted", "Other Investments - Quoted", "Other Investments - Unquoted", "Investments - User Input" },
        ["LOANS & ADVANCES (ASSET)"] = new[] { "Loans and Advances - Secured, considered good", "Loans and Advances - Unsecured, considered good", "Loans and Advances - Doubtful", "Loans and Advances - Provision for Doubtful Debts", "Loans and Advances - User Input" },
        ["SUNDRY DEBTORS"] = new[] { "Trade Receivables" },
        ["CASH-IN-HAND"] = new[] { "Cash and Cash Equivalents" },
        ["BANK ACCOUNTS"] = new[] { "Cash and Cash Equivalents", "Borrowings - User Input", "Borrowings - Unsecured", "Borrowings - Secured", "Other Liabilities" },
        ["MISC. EXPENSES (ASSET)"] = new[] { "Other Assets" },
        ["PRIMARY - ASSETS"] = new[] { "PPE (Gross)", "PPE (Accumulated Depreciation)", "PPE (Net)", "Intangible Assets (Gross)", "Intangible Assets (Accumulated Depreciation)", "Intangible Assets (Net)", "Capital Work-in-Progress", "Intangible Assets under Development", "Trade Investments - Quoted", "Trade Investments - Unquoted", "Other Investments - Quoted", "Other Investments - Unquoted", "Investments - User Input", "Deferred Tax Assets (Net)", "Loans and Advances - Secured, considered good", "Loans and Advances - Unsecured, considered good", "Loans and Advances - Doubtful", "Loans and Advances - Provision for Doubtful Debts", "Loans and Advances - User Input", "Cash and Cash Equivalents", "Other Assets", "Deferred Tax Liabilities (Net)", "Borrowings - Secured", "Borrowings - Unsecured", "Borrowings - User Input", "Provisions", "Other Liabilities" },
        ["SALES ACCOUNTS"] = new[] { "Revenue from Operations" },
        ["PURCHASE ACCOUNTS"] = new[] { "Purchases", "Purchases - User Input", "Tax Expenses (Income Tax)", "Tax Expenses (Income Tax) - User Input", "Other Profit and Loss Items", "Finance Costs", "Employee Benefits Expense", "Depreciation and Amortisation Expense", "Revenue from Operations", "Other Income" },
        ["PRIMARY - INCOME"] = new[] { "Revenue from Operations", "Other Income", "Purchases", "Purchases - User Input", "Tax Expenses (Income Tax)", "Tax Expenses (Income Tax) - User Input", "Other Profit and Loss Items" },
        ["PRIMARY - EXPENSES"] = new[] { "Other Expenses", "Purchases", "Purchases - User Input", "Tax Expenses (Income Tax)", "Tax Expenses (Income Tax) - User Input", "Other Profit and Loss Items", "Finance Costs", "Employee Benefits Expense", "Depreciation and Amortisation Expense" },
        ["DIRECT INCOMES"] = new[] { "Revenue from Operations", "Other Income", "Purchases - User Input", "Purchases", "Other Profit and Loss Items", "Other Expenses", "Finance Costs", "Employee Benefits Expense", "Depreciation and Amortisation Expense", "Tax Expenses (Income Tax)", "Tax Expenses (Income Tax) - User Input" },
        ["INDIRECT INCOMES"] = new[] { "Revenue from Operations", "Other Income", "Purchases", "Purchases - User Input", "Other Profit and Loss Items", "Other Expenses", "Finance Costs", "Employee Benefits Expense", "Depreciation and Amortisation Expense", "Tax Expenses (Income Tax)", "Tax Expenses (Income Tax) - User Input" },
        ["DIRECT EXPENSES"] = new[] { "Revenue from Operations", "Purchases", "Purchases - User Input", "Other Income", "Other Profit and Loss Items", "Other Expenses", "Finance Costs", "Employee Benefits Expense", "Depreciation and Amortisation Expense", "Tax Expenses (Income Tax)", "Tax Expenses (Income Tax) - User Input" },
        ["INDIRECT EXPENSES"] = new[] { "Revenue from Operations", "Other Income", "Purchases", "Purchases - User Input", "Other Profit and Loss Items", "Other Expenses", "Finance Costs", "Employee Benefits Expense", "Depreciation and Amortisation Expense", "Tax Expenses (Income Tax)", "Tax Expenses (Income Tax) - User Input" },
    };

    private static readonly IReadOnlyDictionary<string, string[]> CompanySourceC1 = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["CAPITAL ACCOUNT"] = new[] { "Share Capital", "Reserves and Surplus", "Money received against Share Warrants", "Share Application Money pending allotment" },
        ["RESERVES & SURPLUS"] = new[] { "Share Capital", "Reserves and Surplus", "Money received against Share Warrants", "Share Application Money pending allotment" },
        ["LOANS (LIABILITY)"] = new[] { "Share Application Money pending allotment", "Money received against Share Warrants", "Borrowings - Secured", "Borrowings - Unsecured", "Borrowings - User Input", "Deferred Tax Liabilities (Net)", "Provisions", "Other Liabilities", "Cash and Cash Equivalents" },
        ["BANK OD A/C"] = new[] { "Borrowings - Secured", "Borrowings - Unsecured", "Borrowings - User Input", "Other Liabilities", "Cash and Cash Equivalents" },
        ["SECURED LOANS"] = new[] { "Borrowings - Secured", "Borrowings - Unsecured", "Borrowings - User Input", "Deferred Tax Liabilities (Net)", "Provisions", "Other Liabilities", "Cash and Cash Equivalents" },
        ["UNSECURED LOANS"] = new[] { "Borrowings - Secured", "Borrowings - Unsecured", "Borrowings - User Input", "Deferred Tax Liabilities (Net)", "Provisions", "Other Liabilities", "Cash and Cash Equivalents" },
        ["CURRENT LIABILITIES"] = new[] { "Provisions", "Other Liabilities", "Deferred Tax Liabilities (Net)", "Borrowings - User Input", "Borrowings - Unsecured", "Borrowings - Secured" },
        ["DUTIES & TAXES"] = new[] { "Provisions", "Other Liabilities", "Other Assets", "Loans and Advances - Secured, considered good", "Loans and Advances - Unsecured, considered good", "Loans and Advances - Doubtful", "Loans and Advances - Provision for Doubtful Debts", "Loans and Advances - User Input", "Deferred Tax Liabilities (Net)", "Deferred Tax Assets (Net)", "Cash and Cash Equivalents" },
        ["PROVISIONS"] = new[] { "Provisions", "Deferred Tax Liabilities (Net)", "Other Liabilities", "Deferred Tax Assets (Net)", "Trade Investments - Unquoted", "Trade Investments - Quoted", "Other Investments - Unquoted", "Other Investments - Quoted", "Investments - User Input", "Loans and Advances - Secured, considered good", "Loans and Advances - Unsecured, considered good", "Loans and Advances - Doubtful", "Loans and Advances - Provision for Doubtful Debts", "Loans and Advances - User Input", "Other Assets" },
        ["SUNDRY CREDITORS"] = new[] { "Trade Payables" },
        ["BRANCH / DIVISIONS"] = new[] { "Provisions", "Other Liabilities", "Other Assets" },
        ["PRIMARY - LIABILITIES"] = new[] { "Share Capital", "Reserves and Surplus", "Money received against Share Warrants", "Share Application Money pending allotment", "Deferred Tax Liabilities (Net)", "Borrowings - Secured", "Borrowings - Unsecured", "Borrowings - User Input", "Provisions", "Other Liabilities", "PPE (Gross)", "PPE (Accumulated Depreciation)", "PPE (Net)", "Intangible Assets (Gross)", "Intangible Assets (Accumulated Depreciation)", "Intangible Assets (Net)", "Capital Work-in-Progress", "Intangible Assets under Development", "Trade Investments - Quoted", "Trade Investments - Unquoted", "Other Investments - Quoted", "Other Investments - Unquoted", "Investments - User Input", "Deferred Tax Assets (Net)", "Loans and Advances - Secured, considered good", "Loans and Advances - Unsecured, considered good", "Loans and Advances - Doubtful", "Loans and Advances - Provision for Doubtful Debts", "Loans and Advances - User Input", "Cash and Cash Equivalents", "Other Assets" },
        ["SUSPENSE A/C"] = new[] { "Share Capital", "Reserves and Surplus", "Money received against Share Warrants", "Share Application Money pending allotment", "Borrowings - Secured", "Borrowings - Unsecured", "Borrowings - User Input", "Deferred Tax Liabilities (Net)", "Deferred Tax Assets (Net)", "Trade Payables", "Provisions", "Other Liabilities", "PPE (Gross)", "PPE (Accumulated Depreciation)", "PPE (Net)", "Intangible Assets (Gross)", "Intangible Assets (Accumulated Depreciation)", "Intangible Assets (Net)", "Capital Work-in-Progress", "Intangible Assets under Development", "Trade Investments - Quoted", "Trade Investments - Unquoted", "Other Investments - Quoted", "Other Investments - Unquoted", "Investments - User Input", "Loans and Advances - Unsecured, considered good", "Loans and Advances - Secured, considered good", "Loans and Advances - Provision for Doubtful Debts", "Loans and Advances - Doubtful", "Cash and Cash Equivalents", "Other Assets" },
        ["FIXED ASSETS"] = new[] { "PPE (Gross)", "PPE (Accumulated Depreciation)", "PPE (Net)", "Capital Work-in-Progress", "Intangible Assets (Gross)", "Intangible Assets (Accumulated Depreciation)", "Intangible Assets (Net)", "Intangible Assets under Development" },
        ["INVESTMENTS"] = new[] { "Trade Investments - Quoted", "Trade Investments - Unquoted", "Other Investments - Quoted", "Other Investments - Unquoted", "Investments - User Input" },
        ["CURRENT ASSETS"] = new[] { "Trade Investments - Quoted", "Trade Investments - Unquoted", "Other Investments - Quoted", "Other Investments - Unquoted", "Investments - User Input", "Loans and Advances - Secured, considered good", "Loans and Advances - Unsecured, considered good", "Loans and Advances - Doubtful", "Loans and Advances - Provision for Doubtful Debts", "Loans and Advances - User Input", "Deferred Tax Assets (Net)", "Cash and Cash Equivalents", "Other Assets" },
        ["STOCK-IN-HAND"] = new[] { "Inventories" },
        ["DEPOSITS (ASSET)"] = new[] { "Other Assets", "Cash and Cash Equivalents", "Trade Investments - Quoted", "Trade Investments - Unquoted", "Other Investments - Quoted", "Other Investments - Unquoted", "Investments - User Input" },
        ["LOANS & ADVANCES (ASSET)"] = new[] { "Loans and Advances - Secured, considered good", "Loans and Advances - Unsecured, considered good", "Loans and Advances - Doubtful", "Loans and Advances - Provision for Doubtful Debts", "Loans and Advances - User Input" },
        ["SUNDRY DEBTORS"] = new[] { "Trade Receivables" },
        ["CASH-IN-HAND"] = new[] { "Cash and Cash Equivalents" },
        ["BANK ACCOUNTS"] = new[] { "Cash and Cash Equivalents", "Borrowings - User Input", "Borrowings - Unsecured", "Borrowings - Secured", "Other Liabilities" },
        ["MISC. EXPENSES (ASSET)"] = new[] { "Other Assets" },
        ["PRIMARY - ASSETS"] = new[] { "PPE (Gross)", "PPE (Accumulated Depreciation)", "PPE (Net)", "Intangible Assets (Gross)", "Intangible Assets (Accumulated Depreciation)", "Intangible Assets (Net)", "Capital Work-in-Progress", "Intangible Assets under Development", "Trade Investments - Quoted", "Trade Investments - Unquoted", "Other Investments - Quoted", "Other Investments - Unquoted", "Investments - User Input", "Deferred Tax Assets (Net)", "Loans and Advances - Secured, considered good", "Loans and Advances - Unsecured, considered good", "Loans and Advances - Doubtful", "Loans and Advances - Provision for Doubtful Debts", "Loans and Advances - User Input", "Cash and Cash Equivalents", "Other Assets", "Deferred Tax Liabilities (Net)", "Borrowings - Secured", "Borrowings - Unsecured", "Borrowings - User Input", "Provisions", "Other Liabilities" },
        ["SALES ACCOUNTS"] = new[] { "Revenue from Operations" },
        ["PURCHASE ACCOUNTS"] = new[] { "Purchases", "Purchases - User Input", "Tax Expenses (Income Tax)", "Tax Expenses (Income Tax) - User Input", "Other Profit and Loss Items", "Finance Costs", "Employee Benefits Expense", "Depreciation and Amortisation Expense", "Other Expenses" },
        ["PRIMARY - INCOME"] = new[] { "Revenue from Operations", "Other Income", "Purchases", "Purchases - User Input", "Tax Expenses (Income Tax)", "Tax Expenses (Income Tax) - User Input", "Other Profit and Loss Items" },
        ["PRIMARY - EXPENSES"] = new[] { "Other Expenses", "Purchases", "Purchases - User Input", "Tax Expenses (Income Tax)", "Tax Expenses (Income Tax) - User Input", "Other Profit and Loss Items", "Finance Costs", "Employee Benefits Expense", "Depreciation and Amortisation Expense" },
        ["DIRECT INCOMES"] = new[] { "Revenue from Operations", "Other Income", "Purchases - User Input", "Purchases", "Other Profit and Loss Items", "Other Expenses", "Finance Costs", "Employee Benefits Expense", "Depreciation and Amortisation Expense", "Tax Expenses (Income Tax)", "Tax Expenses (Income Tax) - User Input" },
        ["INDIRECT INCOMES"] = new[] { "Revenue from Operations", "Other Income", "Purchases", "Purchases - User Input", "Other Profit and Loss Items", "Other Expenses", "Finance Costs", "Employee Benefits Expense", "Depreciation and Amortisation Expense", "Tax Expenses (Income Tax)", "Tax Expenses (Income Tax) - User Input" },
        ["DIRECT EXPENSES"] = new[] { "Revenue from Operations", "Purchases", "Purchases - User Input", "Other Income", "Other Profit and Loss Items", "Other Expenses", "Finance Costs", "Employee Benefits Expense", "Depreciation and Amortisation Expense", "Tax Expenses (Income Tax)", "Tax Expenses (Income Tax) - User Input" },
        ["INDIRECT EXPENSES"] = new[] { "Revenue from Operations", "Other Income", "Purchases", "Purchases - User Input", "Other Profit and Loss Items", "Other Expenses", "Finance Costs", "Employee Benefits Expense", "Depreciation and Amortisation Expense", "Tax Expenses (Income Tax)", "Tax Expenses (Income Tax) - User Input" },
    };

    private static readonly WorkbookPath[] NcePaths = new WorkbookPath[]
    {
        new("Owner/Partners Capital Account", "Capital Account", ""),
        new("Owner/Partners Capital Account", "Current Account", ""),
        new("Reserves and Surplus", "Capital Reserve", ""),
        new("Reserves and Surplus", "Revaluation Reserve", ""),
        new("Reserves and Surplus", "Retained Earnings", ""),
        new("Reserves and Surplus", "Other Reserves", ""),
        new("Reserves and Surplus", "Surplus", ""),
        new("Borrowings - Secured", "Term Loans from Banks", ""),
        new("Borrowings - Secured", "Term Loans from Other Parties", ""),
        new("Borrowings - Secured", "Deferred Payment Liabilities", ""),
        new("Borrowings - Secured", "Deposits", ""),
        new("Borrowings - Secured", "Loans and Advances from Related Parties", ""),
        new("Borrowings - Secured", "Maturities of Finance Lease Obligations", ""),
        new("Borrowings - Secured", "Loans Repayable on Demand from Bank", ""),
        new("Borrowings - Secured", "Loans Repayable on Demand from Other Parties", ""),
        new("Borrowings - Unsecured", "Term Loans from Banks", ""),
        new("Borrowings - Unsecured", "Term Loans from Other Parties", ""),
        new("Borrowings - Unsecured", "Deferred Payment Liabilities", ""),
        new("Borrowings - Unsecured", "Deposits", ""),
        new("Borrowings - Unsecured", "Loans and Advances from Related Parties", ""),
        new("Borrowings - Unsecured", "Maturities of Finance Lease Obligations", ""),
        new("Borrowings - Unsecured", "Loans Repayable on Demand from Bank", ""),
        new("Borrowings - Unsecured", "Loans Repayable on Demand from Other Parties", ""),
        new("Borrowings - User Input", "Term Loans from Banks", ""),
        new("Borrowings - User Input", "Term Loans from Other Parties", ""),
        new("Borrowings - User Input", "Deferred Payment Liabilities", ""),
        new("Borrowings - User Input", "Deposits", ""),
        new("Borrowings - User Input", "Loans and Advances from Related Parties", ""),
        new("Borrowings - User Input", "Maturities of Finance Lease Obligations", ""),
        new("Borrowings - User Input", "Loans Repayable on Demand from Bank", ""),
        new("Borrowings - User Input", "Loans Repayable on Demand from Other Parties", ""),
        new("Deferred Tax Liabilities (Net)", "", ""),
        new("Trade Payables", "As per Creditors/Debtors Sheets", ""),
        new("Other Liabilities", "Salaries Payable", ""),
        new("Other Liabilities", "PT Payable", ""),
        new("Other Liabilities", "EPF Payable", ""),
        new("Other Liabilities", "TDS/TCS Payable", ""),
        new("Other Liabilities", "GST Payable", ""),
        new("Other Liabilities", "ESI Payable", ""),
        new("Other Liabilities", "Income Received in Advance", ""),
        new("Other Liabilities", "Unearned Revenue", ""),
        new("Other Liabilities", "Interest Accrued and due on Borrowings", ""),
        new("Other Liabilities", "Interest Accrued but not due on Borrowings", ""),
        new("Other Liabilities", "Advance from Customers", ""),
        new("Provisions", "Provision for Audit Fees", ""),
        new("Provisions", "Provision for Dividend", ""),
        new("Provisions", "Provision for Income Tax", ""),
        new("Provisions", "Provision for Leave Encashment", ""),
        new("Provisions", "Provision for Gratuity", ""),
        new("Provisions", "Provision for Superannuation", ""),
        new("Provisions", "Provision for Expenses", ""),
        new("PPE (Gross)", "Land", ""),
        new("PPE (Gross)", "Leasehold Land", ""),
        new("PPE (Gross)", "Buildings", ""),
        new("PPE (Gross)", "Plant and Equipment", ""),
        new("PPE (Gross)", "Furniture and Fixtures", ""),
        new("PPE (Gross)", "Vehicles", ""),
        new("PPE (Gross)", "Office Equipment", ""),
        new("PPE (Gross)", "Computers and Peripherals", ""),
        new("PPE (Accumulated Depreciation)", "Land", ""),
        new("PPE (Accumulated Depreciation)", "Leasehold Land", ""),
        new("PPE (Accumulated Depreciation)", "Buildings", ""),
        new("PPE (Accumulated Depreciation)", "Plant and Equipment", ""),
        new("PPE (Accumulated Depreciation)", "Furniture and Fixtures", ""),
        new("PPE (Accumulated Depreciation)", "Vehicles", ""),
        new("PPE (Accumulated Depreciation)", "Office Equipment", ""),
        new("PPE (Accumulated Depreciation)", "Computers and Peripherals", ""),
        new("PPE (Net)", "Land", ""),
        new("PPE (Net)", "Leasehold Land", ""),
        new("PPE (Net)", "Buildings", ""),
        new("PPE (Net)", "Plant and Equipment", ""),
        new("PPE (Net)", "Furniture and Fixtures", ""),
        new("PPE (Net)", "Vehicles", ""),
        new("PPE (Net)", "Office Equipment", ""),
        new("PPE (Net)", "Computers and Peripherals", ""),
        new("Intangible Assets (Gross)", "Goodwill", ""),
        new("Intangible Assets (Gross)", "Brands/Trademarks", ""),
        new("Intangible Assets (Gross)", "Computer Software", ""),
        new("Intangible Assets (Gross)", "Masthead and Publishing Titles", ""),
        new("Intangible Assets (Gross)", "Mining Rights", ""),
        new("Intangible Assets (Gross)", "Copyrights, Patents, Other Intellectual Property Rights, Services, and Operating Rights", ""),
        new("Intangible Assets (Gross)", "Recipes, Formulae, Models, Designs, and Prototypes", ""),
        new("Intangible Assets (Gross)", "License and Franchise", ""),
        new("Intangible Assets (Accumulated Depreciation)", "Goodwill", ""),
        new("Intangible Assets (Accumulated Depreciation)", "Brands/Trademarks", ""),
        new("Intangible Assets (Accumulated Depreciation)", "Computer Software", ""),
        new("Intangible Assets (Accumulated Depreciation)", "Masthead and Publishing Titles", ""),
        new("Intangible Assets (Accumulated Depreciation)", "Mining Rights", ""),
        new("Intangible Assets (Accumulated Depreciation)", "Copyrights, Patents, Other Intellectual Property Rights, Services, and Operating Rights", ""),
        new("Intangible Assets (Accumulated Depreciation)", "Recipes, Formulae, Models, Designs, and Prototypes", ""),
        new("Intangible Assets (Accumulated Depreciation)", "License and Franchise", ""),
        new("Intangible Assets (Net)", "Goodwill", ""),
        new("Intangible Assets (Net)", "Brands/Trademarks", ""),
        new("Intangible Assets (Net)", "Computer Software", ""),
        new("Intangible Assets (Net)", "Masthead and Publishing Titles", ""),
        new("Intangible Assets (Net)", "Mining Rights", ""),
        new("Intangible Assets (Net)", "Copyrights, Patents, Other Intellectual Property Rights, Services, and Operating Rights", ""),
        new("Intangible Assets (Net)", "Recipes, Formulae, Models, Designs, and Prototypes", ""),
        new("Intangible Assets (Net)", "License and Franchise", ""),
        new("Capital Work-in-Progress", "Project in Progress", ""),
        new("Capital Work-in-Progress", "Projects temporarily suspended", ""),
        new("Intangible Assets under Development", "Project in Progress", ""),
        new("Intangible Assets under Development", "Projects temporarily suspended", ""),
        new("Trade Investments - Quoted", "Investment Property", ""),
        new("Trade Investments - Quoted", "Investments in Equity Instruments", ""),
        new("Trade Investments - Quoted", "Investments in Preference Shares", ""),
        new("Trade Investments - Quoted", "Investments in Government or Trust Securities", ""),
        new("Trade Investments - Quoted", "Investments in Bonds/Debentures", ""),
        new("Trade Investments - Quoted", "Investments in Mutual Funds", ""),
        new("Trade Investments - Quoted", "Investments in Partnership Firms", ""),
        new("Trade Investments - Quoted", "Other Investments", ""),
        new("Trade Investments - Quoted", "Provision for Diminution in Value of Investment", ""),
        new("Trade Investments - Unquoted", "Investment Property", ""),
        new("Trade Investments - Unquoted", "Investments in Equity Instruments", ""),
        new("Trade Investments - Unquoted", "Investments in Preference Shares", ""),
        new("Trade Investments - Unquoted", "Investments in Government or Trust Securities", ""),
        new("Trade Investments - Unquoted", "Investments in Bonds/Debentures", ""),
        new("Trade Investments - Unquoted", "Investments in Mutual Funds", ""),
        new("Trade Investments - Unquoted", "Investments in Partnership Firms", ""),
        new("Trade Investments - Unquoted", "Other Investments", ""),
        new("Trade Investments - Unquoted", "Provision for Diminution in Value of Investment", ""),
        new("Other Investments - Quoted", "Investment Property", ""),
        new("Other Investments - Quoted", "Investments in Equity Instruments", ""),
        new("Other Investments - Quoted", "Investments in Preference Shares", ""),
        new("Other Investments - Quoted", "Investments in Government or Trust Securities", ""),
        new("Other Investments - Quoted", "Investments in Bonds/Debentures", ""),
        new("Other Investments - Quoted", "Investments in Mutual Funds", ""),
        new("Other Investments - Quoted", "Investments in Partnership Firms", ""),
        new("Other Investments - Quoted", "Other Investments", ""),
        new("Other Investments - Quoted", "Provision for Diminution in Value of Investment", ""),
        new("Other Investments - Unquoted", "Investment Property", ""),
        new("Other Investments - Unquoted", "Investments in Equity Instruments", ""),
        new("Other Investments - Unquoted", "Investments in Preference Shares", ""),
        new("Other Investments - Unquoted", "Investments in Government or Trust Securities", ""),
        new("Other Investments - Unquoted", "Investments in Bonds/Debentures", ""),
        new("Other Investments - Unquoted", "Investments in Mutual Funds", ""),
        new("Other Investments - Unquoted", "Investments in Partnership Firms", ""),
        new("Other Investments - Unquoted", "Other Investments", ""),
        new("Other Investments - Unquoted", "Provision for Diminution in Value of Investment", ""),
        new("Investments - User Input", "Investment Property", ""),
        new("Investments - User Input", "Investments in Equity Instruments", ""),
        new("Investments - User Input", "Investments in Preference Shares", ""),
        new("Investments - User Input", "Investments in Government or Trust Securities", ""),
        new("Investments - User Input", "Investments in Bonds/Debentures", ""),
        new("Investments - User Input", "Investments in Mutual Funds", ""),
        new("Investments - User Input", "Investments in Partnership Firms", ""),
        new("Investments - User Input", "Other Investments", ""),
        new("Investments - User Input", "Provision for Diminution in Value of Investment", ""),
        new("Deferred Tax Assets (Net)", "", ""),
        new("Inventories", "As per Stock Sheet", ""),
        new("Trade Receivables", "As per Creditors/Debtors Sheets", ""),
        new("Cash and Cash Equivalents", "Cash on Hand", ""),
        new("Cash and Cash Equivalents", "Cheques, Drafts on Hand", ""),
        new("Cash and Cash Equivalents", "Balances with Banks", "Current Accounts"),
        new("Cash and Cash Equivalents", "Balances with Banks", "Cash Credit Account (Debit Balance)"),
        new("Cash and Cash Equivalents", "Balances with Banks", "Fixed Deposits with original maturity of less than 3 months"),
        new("Cash and Cash Equivalents", "Other Bank Balances", "Earmarked Bank Deposits"),
        new("Cash and Cash Equivalents", "Other Bank Balances", "Margin Money or Deposits Under Lien"),
        new("Cash and Cash Equivalents", "Other Bank Balances", "Deposits with original maturity for more than 3 months but less than 12 months from reporting date"),
        new("Loans and Advances - Secured, considered good", "Capital Advances", ""),
        new("Loans and Advances - Secured, considered good", "Loans and Advances to Related Parties", ""),
        new("Loans and Advances - Secured, considered good", "Other Loans and Advances", ""),
        new("Loans and Advances - Unsecured, considered good", "Capital Advances", ""),
        new("Loans and Advances - Unsecured, considered good", "Loans and Advances to Related Parties", ""),
        new("Loans and Advances - Unsecured, considered good", "Other Loans and Advances", ""),
        new("Loans and Advances - Doubtful", "Capital Advances", ""),
        new("Loans and Advances - Doubtful", "Loans and Advances to Related Parties", ""),
        new("Loans and Advances - Doubtful", "Other Loans and Advances", ""),
        new("Loans and Advances - Provision for Doubtful Debts", "Capital Advances", ""),
        new("Loans and Advances - Provision for Doubtful Debts", "Loans and Advances to Related Parties", ""),
        new("Loans and Advances - Provision for Doubtful Debts", "Other Loans and Advances", ""),
        new("Loans and Advances - User Input", "Capital Advances", ""),
        new("Loans and Advances - User Input", "Loans and Advances to Related Parties", ""),
        new("Loans and Advances - User Input", "Other Loans and Advances", ""),
        new("Other Assets", "Advance IT/TDS", ""),
        new("Other Assets", "GST Input Credit Receivable", ""),
        new("Other Assets", "Advance to Suppliers", ""),
        new("Other Assets", "Prepaid Expenses", ""),
        new("Other Assets", "Unbilled Revenue", ""),
        new("Other Assets", "Unamortised Premium on Forward Contracts", ""),
        new("Revenue from Operations", "Sale of Products", ""),
        new("Revenue from Operations", "Sale of Services", ""),
        new("Revenue from Operations", "Grants/Donations Received", ""),
        new("Revenue from Operations", "Other Operating Revenues", ""),
        new("Other Income", "Interest Income", ""),
        new("Other Income", "Dividend Income", ""),
        new("Other Income", "Sale of Scrap", ""),
        new("Other Income", "Net Gain on Sale of Investments", ""),
        new("Other Income", "Other Non-Operating Income", ""),
        new("Other Income", "Miscellaneous Income", ""),
        new("Purchases", "Raw Materials", ""),
        new("Purchases", "Finished Goods", ""),
        new("Purchases", "Stock-in-Trade", ""),
        new("Purchases", "Packing Materials", ""),
        new("Purchases", "Other Materials", ""),
        new("Purchases - User Input", "", ""),
        new("Employee Benefits Expense", "Salaries and Wages", ""),
        new("Employee Benefits Expense", "Staff Welfare Expenses", ""),
        new("Employee Benefits Expense", "Gratuity Expenses", ""),
        new("Employee Benefits Expense", "Contribution to Provident and Other Funds", ""),
        new("Finance Costs", "Interest Expense", ""),
        new("Finance Costs", "Other Borrowing Costs", ""),
        new("Finance Costs", "Loss on Foreign Exchange Transactions and Translations (Net)", ""),
        new("Depreciation and Amortisation Expense", "Depreciation of Tangible Assets", ""),
        new("Depreciation and Amortisation Expense", "Amortisation of Intangible Assets", ""),
        new("Other Expenses", "Advertisement and Publicity", ""),
        new("Other Expenses", "Business Promotion Expenses", ""),
        new("Other Expenses", "Clearing and Forwarding Charges", ""),
        new("Other Expenses", "Commission", ""),
        new("Other Expenses", "Communication Expenses", ""),
        new("Other Expenses", "Consumption of Stores and Spare Parts", ""),
        new("Other Expenses", "Insurance", ""),
        new("Other Expenses", "Labour Charges", ""),
        new("Other Expenses", "Legal and Professional Charges", ""),
        new("Other Expenses", "Loss on Cancellation of Forward Contracts", ""),
        new("Other Expenses", "Loss on Foreign Exchange Transactions (Net)", ""),
        new("Other Expenses", "Loss on Sale of Investments (Net)", ""),
        new("Other Expenses", "Loss on Sale of Property, Plant and Equipment", ""),
        new("Other Expenses", "Payments to the Auditor", ""),
        new("Other Expenses", "Power and Fuel", ""),
        new("Other Expenses", "Printing and Stationery", ""),
        new("Other Expenses", "Provision for Diminution in Value of Investments (P&L)", ""),
        new("Other Expenses", "Provision for Doubtful Advances (P&L)", ""),
        new("Other Expenses", "Provision for Doubtful Debts", ""),
        new("Other Expenses", "Rates and Taxes", ""),
        new("Other Expenses", "Rent", ""),
        new("Other Expenses", "Repairs and Maintenance - Buildings", ""),
        new("Other Expenses", "Repairs and Maintenance - Machinery", ""),
        new("Other Expenses", "Repairs and Maintenance - Others", ""),
        new("Other Expenses", "Travelling and Conveyance", ""),
        new("Other Expenses", "Miscellaneous Expenses", ""),
        new("Tax Expenses (Income Tax)", "Current Tax", ""),
        new("Tax Expenses (Income Tax)", "Deferred Tax", ""),
        new("Tax Expenses (Income Tax)", "Adjustment of Tax relating to earlier periods", ""),
        new("Tax Expenses (Income Tax) - User Input", "", ""),
        new("Other Profit and Loss Items", "Exceptional Items", ""),
        new("Other Profit and Loss Items", "Extraordinary Items", ""),
        new("Other Profit and Loss Items", "Partners’ Remuneration", ""),
        new("Other Profit and Loss Items", "Profit/(Loss) from Discontinuing Operations", ""),
        new("Other Profit and Loss Items", "Tax Expense of Discontinuing Operations", ""),
    };

    private static readonly WorkbookPath[] CompanyPaths = new WorkbookPath[]
    {
        new("Share Capital", "Subscribed and Fully Paid", "User Input"),
        new("Share Capital", "Subscribed but Not Fully Paid", "User Input"),
        new("Share Capital", "Calls in Arrears", ""),
        new("Reserves and Surplus", "Capital Reserve", ""),
        new("Reserves and Surplus", "Capital Redemption Reserve", ""),
        new("Reserves and Surplus", "Securities Premium", ""),
        new("Reserves and Surplus", "Debenture Redemption Reserve", ""),
        new("Reserves and Surplus", "Revaluation Reserve", ""),
        new("Reserves and Surplus", "Retained Earnings", ""),
        new("Reserves and Surplus", "Share Options Outstanding Account", ""),
        new("Reserves and Surplus", "Other Reserves", ""),
        new("Reserves and Surplus", "Surplus", ""),
        new("Reserves and Surplus", "General Reserve", ""),
        new("Money received against Share Warrants", "", ""),
        new("Share Application Money pending allotment", "", ""),
        new("Borrowings - Secured", "Bonds/Debentures", ""),
        new("Borrowings - Secured", "Term Loans from Banks", ""),
        new("Borrowings - Secured", "Term Loans from Other Parties", ""),
        new("Borrowings - Secured", "Deferred Payment Liabilities", ""),
        new("Borrowings - Secured", "Deposits", ""),
        new("Borrowings - Secured", "Loans and Advances from Related Parties", ""),
        new("Borrowings - Secured", "Maturities of Finance Lease Obligations", ""),
        new("Borrowings - Secured", "Loans Repayable on Demand from Bank", ""),
        new("Borrowings - Secured", "Loans Repayable on Demand from Other Parties", ""),
        new("Borrowings - Unsecured", "Bonds/Debentures", ""),
        new("Borrowings - Unsecured", "Term Loans from Banks", ""),
        new("Borrowings - Unsecured", "Term Loans from Other Parties", ""),
        new("Borrowings - Unsecured", "Deferred Payment Liabilities", ""),
        new("Borrowings - Unsecured", "Deposits", ""),
        new("Borrowings - Unsecured", "Loans and Advances from Related Parties", ""),
        new("Borrowings - Unsecured", "Maturities of Finance Lease Obligations", ""),
        new("Borrowings - Unsecured", "Loans Repayable on Demand from Bank", ""),
        new("Borrowings - Unsecured", "Loans Repayable on Demand from Other Parties", ""),
        new("Borrowings - User Input", "Bonds/Debentures", ""),
        new("Borrowings - User Input", "Term Loans from Banks", ""),
        new("Borrowings - User Input", "Term Loans from Other Parties", ""),
        new("Borrowings - User Input", "Deferred Payment Liabilities", ""),
        new("Borrowings - User Input", "Deposits", ""),
        new("Borrowings - User Input", "Loans and Advances from Related Parties", ""),
        new("Borrowings - User Input", "Maturities of Finance Lease Obligations", ""),
        new("Borrowings - User Input", "Loans Repayable on Demand from Bank", ""),
        new("Borrowings - User Input", "Loans Repayable on Demand from Other Parties", ""),
        new("Deferred Tax Liabilities (Net)", "", ""),
        new("Trade Payables", "As per Creditors/Debtors Sheets", ""),
        new("Other Liabilities", "Salaries Payable", ""),
        new("Other Liabilities", "PT Payable", ""),
        new("Other Liabilities", "EPF Payable", ""),
        new("Other Liabilities", "TDS/TCS Payable", ""),
        new("Other Liabilities", "GST Payable", ""),
        new("Other Liabilities", "ESI Payable", ""),
        new("Other Liabilities", "Income Received in Advance", ""),
        new("Other Liabilities", "Unearned Revenue", ""),
        new("Other Liabilities", "Interest Accrued and due on Borrowings", ""),
        new("Other Liabilities", "Interest Accrued but not due on Borrowings", ""),
        new("Other Liabilities", "Advance from Customers", ""),
        new("Other Liabilities", "Expenses Payable", ""),
        new("Other Liabilities", "Unpaid Dividend", ""),
        new("Provisions", "Provision for Audit Fees", ""),
        new("Provisions", "Provision for Dividend", ""),
        new("Provisions", "Provision for Income Tax", ""),
        new("Provisions", "Provision for Leave Encashment", ""),
        new("Provisions", "Provision for Gratuity", ""),
        new("Provisions", "Provision for Superannuation", ""),
        new("Provisions", "Provision for Expenses", ""),
        new("PPE (Gross)", "Land", ""),
        new("PPE (Gross)", "Leasehold Land", ""),
        new("PPE (Gross)", "Buildings", ""),
        new("PPE (Gross)", "Plant and Equipment", ""),
        new("PPE (Gross)", "Furniture and Fixtures", ""),
        new("PPE (Gross)", "Vehicles", ""),
        new("PPE (Gross)", "Office Equipment", ""),
        new("PPE (Gross)", "Computers and Peripherals", ""),
        new("PPE (Accumulated Depreciation)", "Land", ""),
        new("PPE (Accumulated Depreciation)", "Leasehold Land", ""),
        new("PPE (Accumulated Depreciation)", "Buildings", ""),
        new("PPE (Accumulated Depreciation)", "Plant and Equipment", ""),
        new("PPE (Accumulated Depreciation)", "Furniture and Fixtures", ""),
        new("PPE (Accumulated Depreciation)", "Vehicles", ""),
        new("PPE (Accumulated Depreciation)", "Office Equipment", ""),
        new("PPE (Accumulated Depreciation)", "Computers and Peripherals", ""),
        new("PPE (Net)", "Land", ""),
        new("PPE (Net)", "Leasehold Land", ""),
        new("PPE (Net)", "Buildings", ""),
        new("PPE (Net)", "Plant and Equipment", ""),
        new("PPE (Net)", "Furniture and Fixtures", ""),
        new("PPE (Net)", "Vehicles", ""),
        new("PPE (Net)", "Office Equipment", ""),
        new("PPE (Net)", "Computers and Peripherals", ""),
        new("Intangible Assets (Gross)", "Goodwill", ""),
        new("Intangible Assets (Gross)", "Brands/Trademarks", ""),
        new("Intangible Assets (Gross)", "Computer Software", ""),
        new("Intangible Assets (Gross)", "Masthead and Publishing Titles", ""),
        new("Intangible Assets (Gross)", "Mining Rights", ""),
        new("Intangible Assets (Gross)", "Copyrights, Patents, Other Intellectual Property Rights, Services, and Operating Rights", ""),
        new("Intangible Assets (Gross)", "Recipes, Formulae, Models, Designs, and Prototypes", ""),
        new("Intangible Assets (Gross)", "License and Franchise", ""),
        new("Intangible Assets (Accumulated Depreciation)", "Goodwill", ""),
        new("Intangible Assets (Accumulated Depreciation)", "Brands/Trademarks", ""),
        new("Intangible Assets (Accumulated Depreciation)", "Computer Software", ""),
        new("Intangible Assets (Accumulated Depreciation)", "Masthead and Publishing Titles", ""),
        new("Intangible Assets (Accumulated Depreciation)", "Mining Rights", ""),
        new("Intangible Assets (Accumulated Depreciation)", "Copyrights, Patents, Other Intellectual Property Rights, Services, and Operating Rights", ""),
        new("Intangible Assets (Accumulated Depreciation)", "Recipes, Formulae, Models, Designs, and Prototypes", ""),
        new("Intangible Assets (Accumulated Depreciation)", "License and Franchise", ""),
        new("Intangible Assets (Net)", "Goodwill", ""),
        new("Intangible Assets (Net)", "Brands/Trademarks", ""),
        new("Intangible Assets (Net)", "Computer Software", ""),
        new("Intangible Assets (Net)", "Masthead and Publishing Titles", ""),
        new("Intangible Assets (Net)", "Mining Rights", ""),
        new("Intangible Assets (Net)", "Copyrights, Patents, Other Intellectual Property Rights, Services, and Operating Rights", ""),
        new("Intangible Assets (Net)", "Recipes, Formulae, Models, Designs, and Prototypes", ""),
        new("Intangible Assets (Net)", "License and Franchise", ""),
        new("Capital Work-in-Progress", "Project in Progress", ""),
        new("Capital Work-in-Progress", "Projects temporarily suspended", ""),
        new("Intangible Assets under Development", "Project in Progress", ""),
        new("Intangible Assets under Development", "Projects temporarily suspended", ""),
        new("Trade Investments - Quoted", "Investment Property", ""),
        new("Trade Investments - Quoted", "Investments in Equity Instruments", ""),
        new("Trade Investments - Quoted", "Investments in Preference Shares", ""),
        new("Trade Investments - Quoted", "Investments in Government or Trust Securities", ""),
        new("Trade Investments - Quoted", "Investments in Bonds/Debentures", ""),
        new("Trade Investments - Quoted", "Investments in Mutual Funds", ""),
        new("Trade Investments - Quoted", "Investments in Partnership Firms", ""),
        new("Trade Investments - Quoted", "Other Investments", ""),
        new("Trade Investments - Quoted", "Provision for Diminution in Value of Investment", ""),
        new("Trade Investments - Unquoted", "Investment Property", ""),
        new("Trade Investments - Unquoted", "Investments in Equity Instruments", ""),
        new("Trade Investments - Unquoted", "Investments in Preference Shares", ""),
        new("Trade Investments - Unquoted", "Investments in Government or Trust Securities", ""),
        new("Trade Investments - Unquoted", "Investments in Bonds/Debentures", ""),
        new("Trade Investments - Unquoted", "Investments in Mutual Funds", ""),
        new("Trade Investments - Unquoted", "Investments in Partnership Firms", ""),
        new("Trade Investments - Unquoted", "Other Investments", ""),
        new("Trade Investments - Unquoted", "Provision for Diminution in Value of Investment", ""),
        new("Other Investments - Quoted", "Investment Property", ""),
        new("Other Investments - Quoted", "Investments in Equity Instruments", ""),
        new("Other Investments - Quoted", "Investments in Preference Shares", ""),
        new("Other Investments - Quoted", "Investments in Government or Trust Securities", ""),
        new("Other Investments - Quoted", "Investments in Bonds/Debentures", ""),
        new("Other Investments - Quoted", "Investments in Mutual Funds", ""),
        new("Other Investments - Quoted", "Investments in Partnership Firms", ""),
        new("Other Investments - Quoted", "Other Investments", ""),
        new("Other Investments - Quoted", "Provision for Diminution in Value of Investment", ""),
        new("Other Investments - Unquoted", "Investment Property", ""),
        new("Other Investments - Unquoted", "Investments in Equity Instruments", ""),
        new("Other Investments - Unquoted", "Investments in Preference Shares", ""),
        new("Other Investments - Unquoted", "Investments in Government or Trust Securities", ""),
        new("Other Investments - Unquoted", "Investments in Bonds/Debentures", ""),
        new("Other Investments - Unquoted", "Investments in Mutual Funds", ""),
        new("Other Investments - Unquoted", "Investments in Partnership Firms", ""),
        new("Other Investments - Unquoted", "Other Investments", ""),
        new("Other Investments - Unquoted", "Provision for Diminution in Value of Investment", ""),
        new("Investments - User Input", "Investment Property", ""),
        new("Investments - User Input", "Investments in Equity Instruments", ""),
        new("Investments - User Input", "Investments in Preference Shares", ""),
        new("Investments - User Input", "Investments in Government or Trust Securities", ""),
        new("Investments - User Input", "Investments in Bonds/Debentures", ""),
        new("Investments - User Input", "Investments in Mutual Funds", ""),
        new("Investments - User Input", "Investments in Partnership Firms", ""),
        new("Investments - User Input", "Other Investments", ""),
        new("Investments - User Input", "Provision for Diminution in Value of Investment", ""),
        new("Deferred Tax Assets (Net)", "", ""),
        new("Inventories", "As per Stock Sheet", ""),
        new("Trade Receivables", "As per Creditors/Debtors Sheets", ""),
        new("Cash and Cash Equivalents", "Cash on Hand", ""),
        new("Cash and Cash Equivalents", "Cheques, Drafts on Hand", ""),
        new("Cash and Cash Equivalents", "Balances with Banks", "Current Accounts"),
        new("Cash and Cash Equivalents", "Balances with Banks", "Cash Credit Account (Debit Balance)"),
        new("Cash and Cash Equivalents", "Balances with Banks", "Fixed Deposits with original maturity of less than 3 months"),
        new("Cash and Cash Equivalents", "Other Bank Balances", "Earmarked Bank Deposits"),
        new("Cash and Cash Equivalents", "Other Bank Balances", "Margin Money or Deposits Under Lien"),
        new("Cash and Cash Equivalents", "Other Bank Balances", "Deposits with original maturity for more than 3 months but less than 12 months from reporting date"),
        new("Loans and Advances - Secured, considered good", "Capital Advances", ""),
        new("Loans and Advances - Secured, considered good", "Loans and Advances to Related Parties", ""),
        new("Loans and Advances - Secured, considered good", "Other Loans and Advances", ""),
        new("Loans and Advances - Unsecured, considered good", "Capital Advances", ""),
        new("Loans and Advances - Unsecured, considered good", "Loans and Advances to Related Parties", ""),
        new("Loans and Advances - Unsecured, considered good", "Other Loans and Advances", ""),
        new("Loans and Advances - Doubtful", "Capital Advances", ""),
        new("Loans and Advances - Doubtful", "Loans and Advances to Related Parties", ""),
        new("Loans and Advances - Doubtful", "Other Loans and Advances", ""),
        new("Loans and Advances - Provision for Doubtful Debts", "Capital Advances", ""),
        new("Loans and Advances - Provision for Doubtful Debts", "Loans and Advances to Related Parties", ""),
        new("Loans and Advances - Provision for Doubtful Debts", "Other Loans and Advances", ""),
        new("Loans and Advances - User Input", "Capital Advances", ""),
        new("Loans and Advances - User Input", "Loans and Advances to Related Parties", ""),
        new("Loans and Advances - User Input", "Other Loans and Advances", ""),
        new("Other Assets", "Advance IT/TDS", ""),
        new("Other Assets", "GST Input Credit Receivable", ""),
        new("Other Assets", "Advance to Suppliers", ""),
        new("Other Assets", "Prepaid Expenses", ""),
        new("Other Assets", "Unbilled Revenue", ""),
        new("Other Assets", "Unamortised Premium on Forward Contracts", ""),
        new("Revenue from Operations", "Sale of Products", ""),
        new("Revenue from Operations", "Sale of Services", ""),
        new("Revenue from Operations", "Grants/Donations Received", ""),
        new("Revenue from Operations", "Other Operating Revenues", ""),
        new("Other Income", "Interest Income", ""),
        new("Other Income", "Dividend Income", ""),
        new("Other Income", "Sale of Scrap", ""),
        new("Other Income", "Net Gain on Sale of Investments", ""),
        new("Other Income", "Other Non-Operating Income", ""),
        new("Other Income", "Miscellaneous Income", ""),
        new("Purchases", "Raw Materials", ""),
        new("Purchases", "Finished Goods", ""),
        new("Purchases", "Stock-in-Trade", ""),
        new("Purchases", "Packing Materials", ""),
        new("Purchases", "Other Materials", ""),
        new("Purchases - User Input", "", ""),
        new("Employee Benefits Expense", "Salaries and Wages", ""),
        new("Employee Benefits Expense", "Staff Welfare Expenses", ""),
        new("Employee Benefits Expense", "Gratuity Expenses", ""),
        new("Employee Benefits Expense", "Contribution to Provident and Other Funds", ""),
        new("Finance Costs", "Interest Expense", ""),
        new("Finance Costs", "Other Borrowing Costs", ""),
        new("Finance Costs", "Loss on Foreign Exchange Transactions and Translations (Net)", ""),
        new("Depreciation and Amortisation Expense", "Depreciation of Tangible Assets", ""),
        new("Depreciation and Amortisation Expense", "Amortisation of Intangible Assets", ""),
        new("Other Expenses", "Advertisement and Publicity", ""),
        new("Other Expenses", "Business Promotion Expenses", ""),
        new("Other Expenses", "Clearing and Forwarding Charges", ""),
        new("Other Expenses", "Commission", ""),
        new("Other Expenses", "Communication Expenses", ""),
        new("Other Expenses", "Consumption of Stores and Spare Parts", ""),
        new("Other Expenses", "Insurance", ""),
        new("Other Expenses", "Labour Charges", ""),
        new("Other Expenses", "Legal and Professional Charges", ""),
        new("Other Expenses", "Loss on Cancellation of Forward Contracts", ""),
        new("Other Expenses", "Loss on Foreign Exchange Transactions (Net)", ""),
        new("Other Expenses", "Loss on Sale of Investments (Net)", ""),
        new("Other Expenses", "Loss on Sale of Property, Plant and Equipment", ""),
        new("Other Expenses", "Payments to the Auditor", ""),
        new("Other Expenses", "Power and Fuel", ""),
        new("Other Expenses", "Printing and Stationery", ""),
        new("Other Expenses", "Provision for Diminution in Value of Investments (P&L)", ""),
        new("Other Expenses", "Provision for Doubtful Advances (P&L)", ""),
        new("Other Expenses", "Provision for Doubtful Debts", ""),
        new("Other Expenses", "Rates and Taxes", ""),
        new("Other Expenses", "Rent", ""),
        new("Other Expenses", "Repairs and Maintenance - Buildings", ""),
        new("Other Expenses", "Repairs and Maintenance - Machinery", ""),
        new("Other Expenses", "Repairs and Maintenance - Others", ""),
        new("Other Expenses", "Travelling and Conveyance", ""),
        new("Other Expenses", "Miscellaneous Expenses", ""),
        new("Tax Expenses (Income Tax)", "Current Tax", ""),
        new("Tax Expenses (Income Tax)", "Deferred Tax", ""),
        new("Tax Expenses (Income Tax)", "Adjustment of Tax relating to earlier periods", ""),
        new("Tax Expenses (Income Tax) - User Input", "", ""),
        new("Other Profit and Loss Items", "Exceptional Items", ""),
        new("Other Profit and Loss Items", "Extraordinary Items", ""),
        new("Other Profit and Loss Items", "Profit/(Loss) from Discontinuing Operations", ""),
        new("Other Profit and Loss Items", "Tax Expense of Discontinuing Operations", ""),
    };

    private static readonly IReadOnlyDictionary<string, string> NceSide = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Owner/Partners Capital Account"] = "Liabilities",
        ["Reserves and Surplus"] = "Liabilities",
        ["Borrowings - Secured"] = "Liabilities",
        ["Borrowings - Unsecured"] = "Liabilities",
        ["Borrowings - User Input"] = "Liabilities",
        ["Deferred Tax Liabilities (Net)"] = "Liabilities",
        ["Trade Payables"] = "Liabilities",
        ["Other Liabilities"] = "Liabilities",
        ["Provisions"] = "Liabilities",
        ["PPE (Gross)"] = "Assets",
        ["PPE (Accumulated Depreciation)"] = "Assets",
        ["PPE (Net)"] = "Assets",
        ["Intangible Assets (Gross)"] = "Assets",
        ["Intangible Assets (Accumulated Depreciation)"] = "Assets",
        ["Intangible Assets (Net)"] = "Assets",
        ["Capital Work-in-Progress"] = "Assets",
        ["Intangible Assets under Development"] = "Assets",
        ["Trade Investments - Quoted"] = "Assets",
        ["Trade Investments - Unquoted"] = "Assets",
        ["Other Investments - Quoted"] = "Assets",
        ["Other Investments - Unquoted"] = "Assets",
        ["Investments - User Input"] = "Assets",
        ["Deferred Tax Assets (Net)"] = "Assets",
        ["Inventories"] = "Assets",
        ["Trade Receivables"] = "Assets",
        ["Cash and Cash Equivalents"] = "Assets",
        ["Loans and Advances - Secured, considered good"] = "Assets",
        ["Loans and Advances - Unsecured, considered good"] = "Assets",
        ["Loans and Advances - Doubtful"] = "Assets",
        ["Loans and Advances - Provision for Doubtful Debts"] = "Assets",
        ["Loans and Advances - User Input"] = "Assets",
        ["Other Assets"] = "Assets",
        ["Revenue from Operations"] = "Income",
        ["Other Income"] = "Income",
        ["Purchases"] = "Expenses",
        ["Purchases - User Input"] = "Expenses",
        ["Employee Benefits Expense"] = "Expenses",
        ["Finance Costs"] = "Expenses",
        ["Depreciation and Amortisation Expense"] = "Expenses",
        ["Other Expenses"] = "Expenses",
        ["Tax Expenses (Income Tax)"] = "Expenses",
        ["Tax Expenses (Income Tax) - User Input"] = "Expenses",
        ["Other Profit and Loss Items"] = "Expenses",
    };

    private static readonly IReadOnlyDictionary<string, string> CompanySide = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Share Capital"] = "Liabilities",
        ["Reserves and Surplus"] = "Liabilities",
        ["Money received against Share Warrants"] = "Liabilities",
        ["Share Application Money pending allotment"] = "Liabilities",
        ["Borrowings - Secured"] = "Liabilities",
        ["Borrowings - Unsecured"] = "Liabilities",
        ["Borrowings - User Input"] = "Liabilities",
        ["Deferred Tax Liabilities (Net)"] = "Liabilities",
        ["Trade Payables"] = "Liabilities",
        ["Other Liabilities"] = "Liabilities",
        ["Provisions"] = "Liabilities",
        ["PPE (Gross)"] = "Assets",
        ["PPE (Accumulated Depreciation)"] = "Assets",
        ["PPE (Net)"] = "Assets",
        ["Intangible Assets (Gross)"] = "Assets",
        ["Intangible Assets (Accumulated Depreciation)"] = "Assets",
        ["Intangible Assets (Net)"] = "Assets",
        ["Capital Work-in-Progress"] = "Assets",
        ["Intangible Assets under Development"] = "Assets",
        ["Trade Investments - Quoted"] = "Assets",
        ["Trade Investments - Unquoted"] = "Assets",
        ["Other Investments - Quoted"] = "Assets",
        ["Other Investments - Unquoted"] = "Assets",
        ["Investments - User Input"] = "Assets",
        ["Deferred Tax Assets (Net)"] = "Assets",
        ["Inventories"] = "Assets",
        ["Trade Receivables"] = "Assets",
        ["Cash and Cash Equivalents"] = "Assets",
        ["Loans and Advances - Secured, considered good"] = "Assets",
        ["Loans and Advances - Unsecured, considered good"] = "Assets",
        ["Loans and Advances - Doubtful"] = "Assets",
        ["Loans and Advances - Provision for Doubtful Debts"] = "Assets",
        ["Loans and Advances - User Input"] = "Assets",
        ["Other Assets"] = "Assets",
        ["Revenue from Operations"] = "Income",
        ["Other Income"] = "Income",
        ["Purchases"] = "Expenses",
        ["Purchases - User Input"] = "Expenses",
        ["Employee Benefits Expense"] = "Expenses",
        ["Finance Costs"] = "Expenses",
        ["Depreciation and Amortisation Expense"] = "Expenses",
        ["Other Expenses"] = "Expenses",
        ["Tax Expenses (Income Tax)"] = "Expenses",
        ["Tax Expenses (Income Tax) - User Input"] = "Expenses",
        ["Other Profit and Loss Items"] = "Expenses",
    };

    public static IReadOnlyList<string> NceEntityTypes { get; } =
        new[] { "Individual", "HUF", "Partnership Firm", "LLP" };

    public static IReadOnlyList<string> RoundOffChoices { get; } =
        new[] { "Default", "Default (without decimals)", "Hundreds", "Thousands", "Ten Thousand", "Lakhs", "Ten Lakh", "Millions", "Ten Million", "Crores" };

    public static IReadOnlyList<string> PpeBlockChoices { get; } =
        new[] { "Gross Block", "Net Block" };

    // Original Basic Info C14/C15 report-detail selectors. The source workbook
    // defaults to Party-wise and Item-wise, and its Read Me explicitly supports
    // the paired Bill-wise / Ledger-wise alternatives.
    public static IReadOnlyList<string> DebtorCreditorDetailChoices { get; } =
        new[] { "Party-wise", "Bill-wise" };

    public static IReadOnlyList<string> StockDetailChoices { get; } =
        new[] { "Item-wise", "Ledger-wise" };

    public static IReadOnlyList<string> StockChoices { get; } =
        new[] { "Raw Materials", "Work-in-Progress", "Finished Goods", "Stock-in-Trade", "Packing Materials", "Other Materials" };

    public static IReadOnlyList<string> ScheduleC1Choices { get; } = new[]
    {
        "Trade Payables",
        "Other Non-Current Liabilities - Trade Payables",
        "Other Current Liabilities",
        "Other Non-Current Liabilities",
        "Trade Receivables",
        "Other Non-Current Assets - Trade Receivables",
        "Other Current Assets",
        "Other Non-current Assets"
    };

    public static IReadOnlyList<string> GetScheduleC2Choices(string? c1) => (c1 ?? string.Empty).Trim() switch
    {
        "Trade Payables" or "Other Non-Current Liabilities - Trade Payables"
            => new[] { "MSME", "Others", "Disputed MSME", "Disputed Others" },
        "Other Current Liabilities" or "Other Non-Current Liabilities"
            => new[] { "Other Payables", "Advance from Customers" },
        "Trade Receivables" or "Other Non-Current Assets - Trade Receivables"
            => new[]
            {
                "Secured considered good - Undisputed",
                "Secured considered good - Disputed",
                "Unsecured considered good - Undisputed",
                "Unsecured considered good - Disputed",
                "Doubtful - Undisputed",
                "Doubtful - Disputed",
                "Less: Provisions for doubtful debts"
            },
        "Other Current Assets" or "Other Non-current Assets"
            => new[] { "Advance to suppliers", "Other Receivables" },
        _ => Array.Empty<string>()
    };

    // Older AAH builds exposed user-facing Non-MSME aliases even though the
    // original workbook stores the corresponding creditor classification as
    // "Others". Normalize those saved aliases on reload so the dependent C2
    // dropdown remains populated instead of silently clearing the selection.
    public static string NormalizeScheduleC2(string? c1, string? c2)
    {
        var parent = (c1 ?? string.Empty).Trim();
        var value = (c2 ?? string.Empty).Trim();
        if (parent.Equals("Trade Payables", StringComparison.OrdinalIgnoreCase) ||
            parent.Equals("Other Non-Current Liabilities - Trade Payables", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Equals("Non-MSME", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Non MSME", StringComparison.OrdinalIgnoreCase)) return "Others";
            if (value.Equals("Disputed Non-MSME", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Disputed Non MSME", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Disputed Other", StringComparison.OrdinalIgnoreCase)) return "Disputed Others";
        }
        if ((parent.Equals("Other Current Liabilities", StringComparison.OrdinalIgnoreCase) ||
             parent.Equals("Other Non-Current Liabilities", StringComparison.OrdinalIgnoreCase)) &&
            value.Equals("Advance from Debtors", StringComparison.OrdinalIgnoreCase))
            return "Advance from Customers";
        return value;
    }

    // Mirrors workbook VBA Norm exactly: NBSP->space, trim the ends, uppercase.
    // Punctuation and internal spacing are intentionally preserved.
    public static string Normalize(string? value)
        => (value ?? string.Empty).Replace('\u00A0', ' ').Trim().ToUpperInvariant();

    public static IReadOnlyList<string> GetC1Choices(string profile, string? nature, string? parentGroup, string? primaryGroup)
    {
        var paths = Paths(profile);
        var side = Side(profile);
        var natureValue = (nature ?? string.Empty).Trim();
        var allForNature = paths.Select(x => x.C1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(x => natureValue.Length == 0 || (side.TryGetValue(x, out var s) && s.Equals(natureValue, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        // Keep the exact source-group workbook suggestions first, then append every valid
        // statutory head on the same side. This preserves workbook guidance while allowing
        // genuine reclassification of a mis-grouped Tally ledger.
        var source = Source(profile);
        string[] preferred = Array.Empty<string>();
        if (source.TryGetValue(Normalize(parentGroup), out var p)) preferred = p;
        else if (source.TryGetValue(Normalize(primaryGroup), out var q)) preferred = q;

        var result = new List<string>();
        foreach (var raw in preferred)
        {
            var canonical = CanonicalC1(profile, raw);
            if (canonical.Length == 0 || !allForNature.Contains(canonical, StringComparer.OrdinalIgnoreCase)) continue;
            if (!result.Contains(canonical, StringComparer.OrdinalIgnoreCase)) result.Add(canonical);
        }
        foreach (var value in allForNature)
            if (!result.Contains(value, StringComparer.OrdinalIgnoreCase)) result.Add(value);
        return result;
    }

    public static IReadOnlyList<string> GetC2Choices(string profile, string? c1)
    {
        var key = Normalize(c1);
        if (key.Length == 0) return Array.Empty<string>();
        return Paths(profile)
            .Where(x => Normalize(x.C1) == key)
            .Select(x => x.C2)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> GetC3Choices(string profile, string? c1, string? c2)
    {
        var k1 = Normalize(c1);
        var k2 = Normalize(c2);
        if (k1.Length == 0 || k2.Length == 0) return Array.Empty<string>();
        return Paths(profile)
            .Where(x => Normalize(x.C1) == k1 && Normalize(x.C2) == k2)
            .Select(x => x.C3)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsDynamicNceOwnerC3(string profile, string? c1, string? c2)
        => profile.Equals("NCE", StringComparison.OrdinalIgnoreCase) &&
           Normalize(c1) == Normalize("Owner/Partners Capital Account") &&
           (Normalize(c2) == Normalize("Capital Account") || Normalize(c2) == Normalize("Current Account"));

    public static bool RequiresC3(string profile, string? c1, string? c2)
        => IsDynamicNceOwnerC3(profile, c1, c2) || GetC3Choices(profile, c1, c2).Count > 0;

    public static bool IsValidVisiblePath(string profile, string? c1, string? c2, string? c3)
    {
        var a = (c1 ?? string.Empty).Trim();
        var b = (c2 ?? string.Empty).Trim();
        var c = (c3 ?? string.Empty).Trim();
        if (a.Length == 0) return false;
        if (IsDynamicNceOwnerC3(profile, a, b)) return b.Length > 0 && c.Length > 0;

        var paths = Paths(profile).Where(x => Normalize(x.C1) == Normalize(a)).ToArray();
        if (paths.Length == 0) return false;
        var c2Choices = paths.Select(x => x.C2).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (c2Choices.Length == 0) return b.Length == 0;
        if (b.Length == 0 || !c2Choices.Contains(b, StringComparer.OrdinalIgnoreCase)) return false;

        var c3Choices = paths.Where(x => Normalize(x.C2) == Normalize(b))
            .Select(x => x.C3).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (c3Choices.Length == 0) return c.Length == 0;
        return c.Length > 0 && c3Choices.Contains(c, StringComparer.OrdinalIgnoreCase);
    }

    public static string GetC1Side(string profile, string? c1)
    {
        var value = CanonicalC1(profile, c1);
        return Side(profile).TryGetValue(value, out var result) ? result : string.Empty;
    }

    public static IReadOnlyList<string> GetSourceAllowedC1(string profile, string? sourceGroup)
    {
        if (!Source(profile).TryGetValue(Normalize(sourceGroup), out var raw)) return Array.Empty<string>();
        return raw.Select(x => CanonicalC1(profile, x))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string CanonicalC1(string profile, string? value)
    {
        var key = Normalize(value);
        if (key == Normalize("PPE (Net) for NCE")) key = Normalize("PPE (Net)");
        if (key == Normalize("Intangible Assets (Net) for NCE")) key = Normalize("Intangible Assets (Net)");
        return Paths(profile).Select(x => x.C1).Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => Normalize(x) == key) ?? string.Empty;
    }

    private static WorkbookPath[] Paths(string profile)
        => profile.Equals("NCE", StringComparison.OrdinalIgnoreCase) ? NcePaths : CompanyPaths;

    private static IReadOnlyDictionary<string, string[]> Source(string profile)
        => profile.Equals("NCE", StringComparison.OrdinalIgnoreCase) ? NceSourceC1 : CompanySourceC1;

    private static IReadOnlyDictionary<string, string> Side(string profile)
        => profile.Equals("NCE", StringComparison.OrdinalIgnoreCase) ? NceSide : CompanySide;
}
