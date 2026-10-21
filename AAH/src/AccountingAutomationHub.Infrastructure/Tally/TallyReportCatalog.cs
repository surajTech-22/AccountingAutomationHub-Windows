namespace AccountingAutomationHub.Infrastructure.Tally;

public static class TallyReportCatalog
{
    // Tally report export requires a TDL report name in <ID>. Captions can vary
    // by TallyPrime release, enabled features and company configuration, so
    // each dashboard caption can carry ordered report-name fallbacks. A report
    // that is not enabled for the linked company is shown as unavailable; it
    // never blocks the remaining reports.
    private static readonly IReadOnlyList<TallyReportDefinition> Definitions =
    [
        // Company and financial statements.
        new("Statistics", ["Statistics"]),
        new("Balance Sheet", ["Balance Sheet"]),
        new("Profit & Loss A/c", ["Profit and Loss", "Profit & Loss A/c", "Profit and Loss A/c"]),
        new("Trial Balance", ["Trial Balance"]),
        new("Receipts & Payments", ["Receipts and Payments", "Receipts & Payments", "Receipts & Payment A/c"]),
        new("Cash Flow", ["Cash Flow"]),
        new("Funds Flow", ["Funds Flow", "Fund Flow"]),
        new("Ratio Analysis", ["Ratio Analysis"]),
        new("Schedule VI Balance Sheet", ["Schedule VI Balance Sheet", "Schedule III Balance Sheet"]),
        new("Schedule VI Profit & Loss", ["Schedule VI Profit and Loss", "Schedule III Profit and Loss"]),

        // Books, registers and account reports.
        new("Day Book", ["Day Book"]),
        new("Cash Book", ["Cash Book"]),
        new("Bank Book", ["Bank Book"]),
        new("Cash/Bank Summary", ["Cash/Bank Summary", "Cash and Bank Summary"]),
        new("Bank Reconciliation", ["Bank Reconciliation", "Bank Reconciliation Summary"]),
        new("Group Summary", ["Group Summary"]),
        new("Ledger Vouchers", ["Ledger Vouchers"]),
        new("List of Accounts", ["List of Accounts", "Chart of Accounts"]),
        new("Sales Register", ["Sales Register"]),
        new("Purchase Register", ["Purchase Register"]),
        new("Journal Register", ["Journal Register"]),
        new("Debit Note Register", ["Debit Note Register"]),
        new("Credit Note Register", ["Credit Note Register"]),
        new("Payment Register", ["Payment Register"]),
        new("Receipt Register", ["Receipt Register"]),
        new("Contra Register", ["Contra Register"]),
        new("Memorandum Register", ["Memorandum Register"]),
        new("Reversing Journal Register", ["Reversing Journal Register"]),
        new("Optional Voucher Register", ["Optional Voucher Register", "Optional Vouchers"]),
        new("Post-Dated Voucher Register", ["Post-Dated Voucher Register", "Post Dated Voucher Register"]),

        // Outstandings and management information.
        new("Outstanding Receivables", ["Outstanding Receivables", "Bills Receivable", "Receivables"]),
        new("Outstanding Payables", ["Outstanding Payables", "Bills Payable", "Payables"]),
        new("Ageing - Receivables", ["Ageing Analysis - Receivables", "Receivables Ageing", "Ageing Receivables"]),
        new("Ageing - Payables", ["Ageing Analysis - Payables", "Payables Ageing", "Ageing Payables"]),
        new("Interest Receivable", ["Interest Receivable", "Interest Calculations - Receivables"]),
        new("Interest Payable", ["Interest Payable", "Interest Calculations - Payables"]),
        new("Cost Centre Summary", ["Cost Centre Summary"]),
        new("Cost Centre Break-up", ["Cost Centre Break-up", "Cost Centre Breakup"]),
        new("Budget Variance", ["Budget Variance", "Budgets & Scenarios"]),
        new("Exception Reports", ["Exception Reports", "Exceptions"]),
        new("Import Exceptions", ["Import Exceptions", "Import Exception", "Exceptions"]),
        new("Event Log", ["Event Log"]),
        new("Negative Cash", ["Negative Cash", "Negative Cash Report"]),

        // Inventory and order reports.
        new("Stock Summary", ["Stock Summary"]),
        new("Stock Item Summary", ["Stock Item Summary", "Stock Items"]),
        new("Stock Group Summary", ["Stock Group Summary"]),
        new("Godown Summary", ["Godown Summary", "Location Summary"]),
        new("Batch Summary", ["Batch Summary", "Batch-wise Summary"]),
        new("Movement Analysis", ["Movement Analysis"]),
        new("Stock Ageing Analysis", ["Stock Ageing Analysis", "Stock Ageing"]),
        new("Physical Stock Register", ["Physical Stock Register"]),
        new("Purchase Bills Pending", ["Purchase Bills Pending"]),
        new("Stock Query", ["Stock Query"]),
        new("Negative Stock", ["Negative Stock", "Negative Stock Report"]),
        new("Stock Transfer Register", ["Stock Transfer Register"]),
        new("Sales Order Book", ["Sales Order Book", "Sales Orders"]),
        new("Purchase Order Book", ["Purchase Order Book", "Purchase Orders"]),
        new("Sales Orders Outstanding", ["Sales Orders Outstanding", "Sales Order Outstandings"]),
        new("Purchase Orders Outstanding", ["Purchase Orders Outstanding", "Purchase Order Outstandings"]),
        new("Reorder Status", ["Reorder Status"]),

        // Statutory reports. Availability depends on the company's enabled tax
        // features and the installed TallyPrime release.
        new("GSTR-1", ["GSTR-1"]),
        new("GSTR-3B", ["GSTR-3B"]),
        new("GST Annual Computation", ["GST Annual Computation"]),
        new("e-Invoice Register", ["e-Invoice Register", "E-Invoice Register"]),
        new("e-Way Bill Register", ["e-Way Bill Register", "E-Way Bill Register"]),

        // Payroll reports appear only when payroll is enabled.
        new("Payroll Statement", ["Payroll Statement"]),
        new("Pay Sheet", ["Pay Sheet"]),
        new("Attendance Sheet", ["Attendance Sheet"]),
        new("Employee Profile", ["Employee Profile"])
    ];

    // Conservative ERP 9 live-report set. These are long-standing accounting
    // reports available across ERP 9 generations. Newer/feature-specific Prime
    // reports remain visible in AAH but are not posted to ERP 9 in safe mode.
    private static readonly HashSet<string> Erp9SafeReports = new(StringComparer.OrdinalIgnoreCase)
    {
        "Statistics", "Balance Sheet", "Profit & Loss A/c", "Trial Balance",
        "Receipts & Payments", "Cash Flow", "Funds Flow", "Ratio Analysis",
        "Day Book", "Cash Book", "Bank Book", "Cash/Bank Summary",
        "Bank Reconciliation", "Group Summary", "Ledger Vouchers", "List of Accounts",
        "Sales Register", "Purchase Register", "Journal Register",
        "Debit Note Register", "Credit Note Register", "Payment Register",
        "Receipt Register", "Contra Register", "Outstanding Receivables",
        "Outstanding Payables", "Stock Summary", "Stock Item Summary",
        "Stock Group Summary", "Godown Summary"
    };

    public static IReadOnlyList<string> DisplayNames =>
        Definitions.Select(item => item.DisplayName).ToArray();

    public static IReadOnlyList<string> Erp9DisplayNames =>
        Definitions.Where(item => Erp9SafeReports.Contains(item.DisplayName))
            .Select(item => item.DisplayName)
            .ToArray();

    public static bool IsErp9Safe(string displayName) =>
        Erp9SafeReports.Contains((displayName ?? string.Empty).Trim());

    public static TallyReportDefinition? Find(string displayName) =>
        Definitions.FirstOrDefault(item =>
            item.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase));
}

public sealed record TallyReportDefinition(
    string DisplayName,
    IReadOnlyList<string> ReportIds);
