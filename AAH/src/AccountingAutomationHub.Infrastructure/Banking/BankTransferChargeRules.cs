namespace AccountingAutomationHub.Infrastructure.Banking;

internal static class BankTransferChargeRules
{
    private const decimal Tolerance = 0.01m;

    public static bool TryMatch(
        string sourceBank,
        string paymentMethod,
        decimal transferAmount,
        string sourceNarration,
        decimal actualDifference,
        out decimal expectedCharge,
        out string rule)
    {
        expectedCharge = 0m;
        rule = string.Empty;

        var bankCode = GetBankCode(sourceBank);
        var method = paymentMethod.Trim().ToUpperInvariant();
        if (bankCode.Length == 0 || method is not ("NEFT" or "RTGS")) return false;
        if (IsExplicitOnlineTransfer(sourceNarration)) return false;

        switch (bankCode, method)
        {
            case ("SBI", "NEFT"):
                if (transferAmount <= 10_000m) { expectedCharge = 2.36m; rule = "SBI BRANCH NEFT UP TO 10000"; }
                else if (transferAmount <= 100_000m) { expectedCharge = 4.72m; rule = "SBI BRANCH NEFT ABOVE 10000 TO 100000"; }
                else if (transferAmount <= 200_000m) { expectedCharge = 14.16m; rule = "SBI BRANCH NEFT ABOVE 100000 TO 200000"; }
                else { expectedCharge = 23.60m; rule = "SBI BRANCH NEFT ABOVE 200000"; }
                break;
            case ("SBI", "RTGS"):
                if (transferAmount < 200_000m) return false;
                if (transferAmount <= 500_000m) { expectedCharge = 23.60m; rule = "SBI BRANCH RTGS 200000 TO 500000"; }
                else { expectedCharge = 47.20m; rule = "SBI BRANCH RTGS ABOVE 500000"; }
                break;
            case ("IDBI", "NEFT"):
                if (transferAmount <= 10_000m) { expectedCharge = 2.36m; rule = "IDBI BRANCH NEFT UP TO 10000"; }
                else if (transferAmount <= 100_000m) { expectedCharge = 5.90m; rule = "IDBI BRANCH NEFT ABOVE 10000 TO 100000"; }
                else if (transferAmount <= 200_000m) { expectedCharge = 17.70m; rule = "IDBI BRANCH NEFT ABOVE 100000 TO 200000"; }
                else { expectedCharge = 29.50m; rule = "IDBI BRANCH NEFT ABOVE 200000"; }
                break;
            case ("IDBI", "RTGS"):
                if (transferAmount < 200_000m) return false;
                if (transferAmount <= 500_000m) { expectedCharge = 28.91m; rule = "IDBI BRANCH RTGS 200000 TO 500000"; }
                else { expectedCharge = 58.41m; rule = "IDBI BRANCH RTGS ABOVE 500000"; }
                break;
            case ("HDFC", "NEFT"):
                if (transferAmount <= 100_000m) { expectedCharge = 2.36m; rule = "HDFC BRANCH NEFT UP TO 100000"; }
                else { expectedCharge = 11.80m; rule = "HDFC BRANCH NEFT ABOVE 100000"; }
                break;
            case ("HDFC", "RTGS"):
                if (transferAmount < 200_000m) return false;
                expectedCharge = 17.70m;
                rule = "HDFC BRANCH RTGS 200000 AND ABOVE";
                break;
        }

        return expectedCharge > 0m && Math.Abs(Math.Round(actualDifference, 2) - expectedCharge) <= Tolerance;
    }

    private static string GetBankCode(string bankName)
    {
        var text = bankName.Trim().ToUpperInvariant();
        if (text.Contains("STATE BANK OF INDIA", StringComparison.Ordinal) || text.Contains("SBI", StringComparison.Ordinal)) return "SBI";
        if (text.Contains("IDBI", StringComparison.Ordinal)) return "IDBI";
        if (text.Contains("HDFC", StringComparison.Ordinal)) return "HDFC";
        return string.Empty;
    }

    private static bool IsExplicitOnlineTransfer(string narration)
    {
        var text = narration.Trim().ToUpperInvariant();
        return new[] { "NETBANKING", "ONLINE", "YONO", "CINB", "INB ", "MOBILE BANKING", "I-CASH", "ICASH" }
            .Any(token => text.Contains(token, StringComparison.Ordinal));
    }
}
