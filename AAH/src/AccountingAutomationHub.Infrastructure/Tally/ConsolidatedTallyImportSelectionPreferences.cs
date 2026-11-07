using System;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// UI-only selection preferences for the original consolidated Tally Import panel.
/// Scoped by the existing ClientPreferences ClientId plus the active accounting FY.
/// These keys never overlap ordinary/manual/automatic Tally export settings.
/// </summary>
public static class ConsolidatedTallyImportSelectionPreferences
{
    public static string ModulesKey(string financialYear) =>
        Prefix(financialYear) + ".Modules";

    public static string SeparateVoucherTypesKey(string financialYear) =>
        Prefix(financialYear) + ".SeparateVoucherTypes";

    public static string SeparateCashLedgersKey(string financialYear) =>
        Prefix(financialYear) + ".SeparateCashLedgers";

    private static string Prefix(string financialYear)
    {
        var fy = (financialYear ?? string.Empty).Trim();
        if (fy.Length == 0)
            throw new ArgumentException("Consolidated import selection requires a financial year.", nameof(financialYear));
        return "TallyExport." + fy + ".ConsolidatedImport";
    }

    // Five fixed positions: Bank, Purchase, Sales, Cash, Journal.
    // "00000" is deliberately distinct from missing/invalid preferences.
    public static string EncodeModules(
        bool bank, bool purchase, bool sales, bool cash, bool journal) =>
        string.Concat(bank ? "1" : "0", purchase ? "1" : "0",
                      sales ? "1" : "0", cash ? "1" : "0", journal ? "1" : "0");

    public static bool TryDecodeModules(string? stored,
        out (bool Bank, bool Purchase, bool Sales, bool Cash, bool Journal) selection)
    {
        var value = (stored ?? string.Empty).Trim();
        selection = default;
        if (value.Length != 5)
            return false;
        foreach (var digit in value)
            if (digit != '0' && digit != '1')
                return false;
        selection = (value[0] == '1', value[1] == '1', value[2] == '1',
                     value[3] == '1', value[4] == '1');
        return true;
    }
}
