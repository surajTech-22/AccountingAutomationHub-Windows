namespace AccountingAutomationHub.Domain.Banking;

public static class BankingPreferenceKeys
{
    public const string DefaultEmptyLedgerName = "Suspense";
    public const string LegacyDefaultEmptyLedgerName = "Bank Suspense A/c";
    public const string DefaultEmptyLedgerParent = "Suspense A/c";

    public static string LedgerMapping(string financialYear, string suffix)
    {
        var fy = (financialYear ?? string.Empty).Trim();
        return $"Banking.LedgerMapping.{fy}.{suffix}";
    }

    public static string DefaultEmptyLedger(string financialYear) =>
        LedgerMapping(financialYear, "DefaultEmptyLedger");
}
